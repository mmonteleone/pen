namespace Pen.Tests;

/// <summary>
/// The locks sessions, pen sign and pen reset take, for a project with two
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
        _runtime = _host.RuntimeDir(Key);
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
        Assert.NotEqual(Sessions.WorktreeLockFile(_runtime, _main.Dir), Sessions.WorktreeLockFile(_runtime, _wt.Dir));
        Assert.Equal(Sessions.WorktreeLockFile(_runtime, _main.Dir), Sessions.WorktreeLockFile(_runtime, _main.Dir));
        Assert.Equal(_runtime, Path.GetDirectoryName(Sessions.WorktreeLockFile(_runtime, _wt.Dir)));
        Assert.Matches("^worktree-[0-9a-f]{8}\\.lock$", Path.GetFileName(Sessions.WorktreeLockFile(_runtime, _wt.Dir)));
    }

    [Fact]
    public void SessionsShareBothLocks_EvenInTheSameDir()
    {
        using var a = Sessions.TakeForSession(_runtime, _main);
        using var b = Sessions.TakeForSession(_runtime, _main);
        using var c = Sessions.TakeForSession(_runtime, _wt);
        Assert.Equal(2, a.Held.Count);
        Assert.Equal(
            [Sessions.ProjectLockFile(_runtime), Sessions.WorktreeLockFile(_runtime, _main.Dir)],
            a.Held.Select(l => l.Path));
    }

    [Fact]
    public void SignRefusedWhileASessionRunsInItsDir_ListingOnlyThatDirsSessions()
    {
        using var lm = Sessions.TakeForSession(_runtime, _main);
        using var lw = Sessions.TakeForSession(_runtime, _wt);
        using var sm = OpenSession("aaaa0001", _main);
        using var sw = OpenSession("aaaa0002", _wt);

        var msg = Refused(() => Sessions.TakeForSign(_host, _main));
        Assert.Contains("refusing to sign while a session is running in this directory:", msg);
        Assert.Contains($"  {Key}-aaaa0001-agent", msg);
        Assert.DoesNotContain("aaaa0002", msg);
    }

    [Fact]
    public void SignAllowedWhileOnlyAnotherWorktreeHasASession()
    {
        using var lw = Sessions.TakeForSession(_runtime, _wt);
        using var sw = OpenSession("aaaa0002", _wt);
        using var sign = Sessions.TakeForSign(_host, _main);
        Assert.Equal(2, sign.Held.Count);
    }

    [Fact]
    public void WhileSignRuns_SessionsInItsDirAreRefused_OthersStart()
    {
        using var sign = Sessions.TakeForSign(_host, _main);

        Assert.Equal("a pen sign is running in this directory: try again once it's done",
            Refused(() => Sessions.TakeForSession(_runtime, _main)));
        using (Sessions.TakeForSession(_runtime, _wt)) { }

        Assert.Contains("another pen sign is running in this directory", Refused(() => Sessions.TakeForSign(_host, _main)));
        using (Sessions.TakeForSign(_host, _wt)) { }
        Assert.Contains("a pen sign or another pen reset is running", Refused(() => Sessions.TakeForReset(_host, _main)));
    }

    [Fact]
    public void ARefusedSessionLeavesProjectLockFree()
    {
        using (Sessions.TakeForSign(_host, _main))
            Assert.Throws<PenException>(() => Sessions.TakeForSession(_runtime, _main));
        // Nothing left held by the refused take: reset gets project.lock exclusive.
        using var reset = Sessions.TakeForReset(_host, _main);
    }

    [Fact]
    public void WhileResetRuns_SessionsAndSignAreRefused()
    {
        using var reset = Sessions.TakeForReset(_host, _main);
        Assert.Equal("a pen reset is running in this project: try again once it's done",
            Refused(() => Sessions.TakeForSession(_runtime, _wt)));
        Assert.Contains("refusing to sign: a pen reset is running", Refused(() => Sessions.TakeForSign(_host, _main)));
        // The refused sign released nothing it didn't take, and took nothing: the worktree lock is free.
        using var w = FileLock.TryTake(Sessions.WorktreeLockFile(_runtime, _main.Dir), exclusive: true);
        Assert.NotNull(w);
    }

    [Fact]
    public void ResetRefusedWhileASessionRunsInAnyDir_ListingAll()
    {
        using var lm = Sessions.TakeForSession(_runtime, _main);
        using var lw = Sessions.TakeForSession(_runtime, _wt);
        using var sm = OpenSession("aaaa0001", _main);
        using var sw = OpenSession("aaaa0002", _wt);

        var msg = Refused(() => Sessions.TakeForReset(_host, _wt));
        Assert.Contains("refusing to reset while 2 sessions are running in this project:", msg);
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

        Assert.Equal(["aaaa0001"], Sessions.RunningIn(Key, _runtime, _main.Dir).Select(s => s.Id));
        Assert.Equal(["aaaa0002"], Sessions.RunningIn(Key, _runtime, _wt.Dir).Select(s => s.Id));
        Assert.Empty(Sessions.RunningIn(Key, _runtime, _t.Sub("elsewhere")));
        Assert.Equal(2, Sessions.Running(Key, _runtime).Count);
    }
}
