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

        Assert.Equal(["aaaa0001"], AllowCommand.FindTargets(_host, _main, global: false).Select(s => s.Id));
        Assert.Equal(["aaaa0002", "aaaa0003"], AllowCommand.FindTargets(_host, _wt, global: false).Select(s => s.Id));
        Assert.Equal(["aaaa0001", "aaaa0002", "aaaa0003", "bbbb0001"],
            AllowCommand.FindTargets(_host, _wt, global: true).Select(s => s.Id).Order(StringComparer.Ordinal));
        Assert.Equal(4, AllowCommand.FindTargets(_host, project: null, global: true).Count);
    }
}
