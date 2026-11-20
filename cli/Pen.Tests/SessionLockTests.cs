namespace Pen.Tests;

/// <summary>
/// The locks sessions, pen sign and pen clean take, for a project with two
/// dirs (its main checkout and a linked worktree sharing its key).
/// Takes locks: runs alone, with the other lock tests.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public sealed class SessionLockTests : IDisposable
{
    private const string Key = "pen-repo-00000000";
    private readonly TempDir _t = new();
    private readonly HostEnv _host;
    private readonly string _runtime;
    private readonly Project _main;
    private readonly Project _wt;

    public SessionLockTests()
    {
        _host = _t.Host();
        _runtime = _host.GetRuntimeDir(Key);
        Directory.CreateDirectory(_runtime);
        var mainDir = _t.Sub("repo");
        var wtDir = _t.Sub("repo-feat");
        var common = Path.Combine(mainDir, ".git");
        _main = new Project(mainDir, Key, common, mainDir);
        _wt = new Project(wtDir, Key, common, mainDir);
    }

    public void Dispose() => _t.Dispose();

    /// <summary>A session as pen ps sees it: open, PROJECT_DIR set, its session.lock held.</summary>
    private FileLock OpenSession(string id, Project p)
    {
        var s = new SessionDir(Key, Path.Combine(_runtime, "sessions", id));
        Directory.CreateDirectory(s.Path);
        File.WriteAllText(s.EnvFile, $"PROJECT_DIR={p.Dir}\nPEN_PROJECT={Key}\n");
        File.WriteAllText(s.ComposeFile, "");
        return FileLock.TryTake(s.LockFile, exclusive: true)!;
    }

    private static string Refused(Func<IDisposable> take) => Assert.Throws<PenException>(() => take().Dispose()).Message;

    [Fact]
    public void WorktreeLockIsPerDir()
    {
        Assert.NotEqual(SessionRegistry.GetWorktreeLockFile(_runtime, _main.Dir), SessionRegistry.GetWorktreeLockFile(_runtime, _wt.Dir));
        Assert.Equal(SessionRegistry.GetWorktreeLockFile(_runtime, _main.Dir), SessionRegistry.GetWorktreeLockFile(_runtime, _main.Dir));
        Assert.Equal(_runtime, Path.GetDirectoryName(SessionRegistry.GetWorktreeLockFile(_runtime, _wt.Dir)));
        Assert.Matches("^worktree-[0-9a-f]{8}\\.lock$", Path.GetFileName(SessionRegistry.GetWorktreeLockFile(_runtime, _wt.Dir)));
    }

    [Fact]
    public void SessionsShareBothLocks_EvenInTheSameDir()
    {
        using var a = SessionRegistry.TakeForSession(_runtime, _main);
        using var b = SessionRegistry.TakeForSession(_runtime, _main);
        using var c = SessionRegistry.TakeForSession(_runtime, _wt);
        Assert.Equal(2, a.Held.Count);
        Assert.Equal(
            [SessionRegistry.GetProjectLockFile(_runtime), SessionRegistry.GetWorktreeLockFile(_runtime, _main.Dir)],
            a.Held.Select(l => l.Path));
    }

    [Fact]
    public void SignRefusedWhileASessionRunsInItsDir_ListingOnlyThatDirsSessions()
    {
        using var lm = SessionRegistry.TakeForSession(_runtime, _main);
        using var lw = SessionRegistry.TakeForSession(_runtime, _wt);
        using var sm = OpenSession("aaaa0001", _main);
        using var sw = OpenSession("aaaa0002", _wt);

        var msg = Refused(() => SessionRegistry.TakeForSign(_host, _main));
        Assert.Contains("refusing to sign while a session is running in this directory:", msg);
        Assert.Contains($"  {Key}-aaaa0001-agent", msg);
        Assert.DoesNotContain("aaaa0002", msg);
    }

    [Fact]
    public void SignAllowedWhileOnlyAnotherWorktreeHasASession()
    {
        using var lw = SessionRegistry.TakeForSession(_runtime, _wt);
        using var sw = OpenSession("aaaa0002", _wt);
        using var sign = SessionRegistry.TakeForSign(_host, _main);
        Assert.Equal(2, sign.Held.Count);
    }

    [Fact]
    public void WhileSignRuns_SessionsInItsDirAreRefused_OthersStart()
    {
        using var sign = SessionRegistry.TakeForSign(_host, _main);

        Assert.Equal("a pen sign is running in this directory: try again once it's done",
            Refused(() => SessionRegistry.TakeForSession(_runtime, _main)));
        using (SessionRegistry.TakeForSession(_runtime, _wt)) { }

        Assert.Contains("another pen sign is running in this directory", Refused(() => SessionRegistry.TakeForSign(_host, _main)));
        using (SessionRegistry.TakeForSign(_host, _wt)) { }
        Assert.Contains("a pen sign or another pen clean is running", Refused(() => SessionRegistry.TakeForClean(_host, _main)));
    }

    [Fact]
    public void ARefusedSessionLeavesProjectLockFree()
    {
        using (SessionRegistry.TakeForSign(_host, _main))
            Assert.Throws<PenException>(() => SessionRegistry.TakeForSession(_runtime, _main));
        // Nothing left held by the refused take: clean gets project.lock exclusive.
        using var clean = SessionRegistry.TakeForClean(_host, _main);
    }

    [Fact]
    public void WhileCleanRuns_SessionsAndSignAreRefused()
    {
        using var clean = SessionRegistry.TakeForClean(_host, _main);
        Assert.Equal("a pen clean is running in this project: try again once it's done",
            Refused(() => SessionRegistry.TakeForSession(_runtime, _wt)));
        Assert.Contains("refusing to sign: a pen clean is running", Refused(() => SessionRegistry.TakeForSign(_host, _main)));
        // The refused sign released nothing it didn't take, and took nothing: the worktree lock is free.
        using var w = FileLock.TryTake(SessionRegistry.GetWorktreeLockFile(_runtime, _main.Dir), exclusive: true);
        Assert.NotNull(w);
    }

    [Fact]
    public void CleanRefusedWhileASessionRunsInAnyDir_ListingAll()
    {
        using var lm = SessionRegistry.TakeForSession(_runtime, _main);
        using var lw = SessionRegistry.TakeForSession(_runtime, _wt);
        using var sm = OpenSession("aaaa0001", _main);
        using var sw = OpenSession("aaaa0002", _wt);

        var msg = Refused(() => SessionRegistry.TakeForClean(_host, _wt));
        Assert.Contains("refusing to clean while 2 sessions are running in this project:", msg);
        Assert.Contains($"  {Key}-aaaa0001-agent", msg);
        Assert.Contains($"  {Key}-aaaa0002-agent", msg);
    }

    [Fact]
    public void RunningIn_FiltersByProjectDir()
    {
        using var sm = OpenSession("aaaa0001", _main);
        using var sw = OpenSession("aaaa0002", _wt);
        var dead = new SessionDir(Key, Path.Combine(_runtime, "sessions", "aaaa0003"));
        Directory.CreateDirectory(dead.Path);
        File.WriteAllText(dead.EnvFile, $"PROJECT_DIR={_main.Dir}\n");
        File.WriteAllText(dead.ComposeFile, "");

        Assert.Equal(["aaaa0001"], SessionRegistry.FindRunningIn(Key, _runtime, _main.Dir).Select(s => s.Id));
        Assert.Equal(["aaaa0002"], SessionRegistry.FindRunningIn(Key, _runtime, _wt.Dir).Select(s => s.Id));
        Assert.Empty(SessionRegistry.FindRunningIn(Key, _runtime, _t.Sub("elsewhere")));
        Assert.Equal(2, SessionRegistry.FindRunning(Key, _runtime).Count);
    }

    [Fact]
    public void AllowTargets_ThisDirsSessions_OrEveryProjectsWithGlobal()
    {
        using var sm = OpenSession("aaaa0001", _main);
        using var sw1 = OpenSession("aaaa0002", _wt);
        using var sw2 = OpenSession("aaaa0003", _wt);

        const string otherKey = "pen-other-11111111";
        var other = new SessionDir(otherKey, Path.Combine(_host.GetRuntimeDir(otherKey), "sessions", "bbbb0001"));
        Directory.CreateDirectory(other.Path);
        File.WriteAllText(other.EnvFile, $"PROJECT_DIR={_t.Sub("other")}\n");
        File.WriteAllText(other.ComposeFile, "");
        using var so = FileLock.TryTake(other.LockFile, exclusive: true)!;

        Assert.Equal(["aaaa0001"], new EgressTarget.InProject(_main).FindRunningSessions(_host).Select(s => s.Id));
        Assert.Equal(["aaaa0002", "aaaa0003"], new EgressTarget.InProject(_wt).FindRunningSessions(_host).Select(s => s.Id));
        Assert.Equal(["aaaa0001", "aaaa0002", "aaaa0003", "bbbb0001"],
            new EgressTarget.Global(_wt).FindRunningSessions(_host).Select(s => s.Id).Order(StringComparer.Ordinal));
        Assert.Equal(4, new EgressTarget.Global(null).FindRunningSessions(_host).Count);
    }

    /// <summary>
    /// A second --docker session for the project is refused, before it publishes anything or creates a volume, while
    /// the first is running and holds the dind lock; the first then ends normally.
    /// </summary>
    [Fact]
    public void ASecondDockerSessionIsRefusedWhileTheFirstHoldsTheDindLock()
    {
        var repo = Git.Repo(_t.Sub("docker-repo"));
        var project = Project.Resolve(repo, _host);
        var sessions = Path.Combine(_host.GetRuntimeDir(project.Key), "sessions");
        var running = Path.Combine(_t.Path, "first-running");
        var release = Path.Combine(_t.Path, "release-first");
        // Only the first compose run waits, so a second session that isn't refused fails the test at once.
        using var docker = new FakeDocker(_t, $$"""
            case " $* " in *" compose "*" run "*)
              if mkdir '{{running}}' 2>/dev/null; then
                i=0; while [ ! -e '{{release}}' ] && [ $i -lt 600 ]; do sleep 0.05; i=$((i+1)); done
              fi ;;
            esac
            exit 0

            """);
        SessionRunner Runner() => new(new ProjectContext(_host, project, UserConfiguration.Empty, ProjectConfiguration.Empty),
            new Answers(new StringReader(""), IsTerminal: false), RecipeCatalog.Embedded, TimeProvider.System);
        var firstExit = -1;
        var first = new Thread(() => firstExit = Runner().Run(new(["true"]) { Docker = true }));
        first.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => Directory.Exists(running), TimeSpan.FromSeconds(30)), "the first session never ran");
            var callsBefore = File.ReadAllLines(Path.Combine(_t.Path, "calls")).Length;

            var error = Assert.Throws<PenException>(() => Runner().Run(new(["true"]) { Docker = true }));

            Assert.Equal("another --docker session is running in this project, and only one can run at a time " +
                "(they'd share its Docker volume): exit it, or start this one without --docker", error.Message);
            Assert.Single(Directory.GetDirectories(sessions));
            Assert.DoesNotContain(File.ReadAllLines(Path.Combine(_t.Path, "calls")).Skip(callsBefore),
                call => call.StartsWith("volume ", StringComparison.Ordinal) || call.StartsWith("compose ", StringComparison.Ordinal));
        }
        finally
        {
            File.WriteAllText(release, "");
            first.Join();
        }
        Assert.Equal(0, firstExit);
        Assert.Empty(SessionRegistry.FindOpen(project.Key, _host.GetRuntimeDir(project.Key)));
    }
}
