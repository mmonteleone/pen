using System.Diagnostics;

namespace Pen.Tests;

/// <summary>
/// Scans don't skip what they can't list: a repo in a chmod 311 directory is
/// refused at the start and quarantined at the end, a FIFO is never opened, and
/// a git dir that can't be read is "couldn't check".
/// </summary>
public class ScanFailClosedTests
{
    private const UnixFileMode Rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Mode311 = UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    /// <summary>Sets a mode; on dispose makes the path usable again, for temp cleanup.</summary>
    private sealed class Chmod : IDisposable
    {
        private readonly string _path;
        public Chmod(string path, UnixFileMode mode)
        {
            _path = path;
            File.SetUnixFileMode(path, mode);
        }
        public void Dispose() => File.SetUnixFileMode(_path, Rwx);
    }

    private static SessionDir Session(TempDir t, string gitDir)
    {
        var session = new SessionDir("pen-test-12345678", t.Sub("runtime", "sessions", "12345678"));
        File.WriteAllText(session.StartedWithGit, gitDir + "\n");
        File.WriteAllText(session.ModulesFile, "");
        return session;
    }

    private static void MkFifo(string path)
    {
        var r = Process.RunAndCaptureText(new ProcessStartInfo("mkfifo", [path]) { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, r.ExitStatus.ExitCode);
    }

    /// <summary>Fails instead of hanging the suite if the code under test blocks opening a FIFO.</summary>
    private static T Within10s<T>(Func<T> f)
    {
        var task = Task.Factory.StartNew(f, TaskCreationOptions.LongRunning);
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "blocked: a FIFO was opened");
        return task.Result;
    }

    private static string RepoWithHiddenRepo(TempDir t, out string sub)
    {
        var repo = Git.Repo(t.Sub("repo"));
        sub = t.Sub("repo", "sub");
        Git.Repo(t.Sub("repo", "sub", "x"));
        return repo;
    }

    [Fact]
    public void AnUnlistableDirectoryIsRefusedAtStart_NamingIt()
    {
        using var t = new TempDir();
        var repo = RepoWithHiddenRepo(t, out var sub);
        using var _ = new Chmod(sub, Mode311);
        var e = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        Assert.Contains("can't list", e.Message);
        Assert.Contains("\n  sub\n", e.Message);
    }

    [Fact]
    public void AnUnlistableDirectoryIsOpenedAndScannedAtSessionEnd()
    {
        using var t = new TempDir();
        var repo = RepoWithHiddenRepo(t, out var sub);
        var session = Session(t, Path.Combine(repo, ".git"));
        using var _ = new Chmod(sub, Mode311);

        var check = GitCheck.RunAtSessionEnd(repo, session);

        Assert.Null(check.Failure);
        Assert.Equal(Path.Combine(sub, "x", ".git"), Assert.Single(check.Moved).From);
        Assert.True(Directory.Exists(Path.Combine(sub, "x", ".git.pen-quarantine")));
        var c = Assert.Single(check.Opened);
        Assert.Equal((sub, Mode311), (c.Path, c.Was));
        Assert.Equal(c.Now, File.GetUnixFileMode(sub));
        Assert.True(c.Now.HasFlag(UnixFileMode.UserRead));
    }

    [Fact]
    public void SayingItLeftAModeChanged()
    {
        var opened = new List<ModeChange> { new("/p/sub", Mode311, Mode311 | UnixFileMode.UserRead) };
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { GitCheck.Report("/p", new CheckResult { Moved = [], Failure = null, Opened = opened }); }
        finally { Console.SetError(stderr); }
        Assert.Contains("changed sub from mode 311 to 711", captured.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADirectoryThatStaysUnlistableIsReported(bool claimsOpened)
    {
        using var t = new TempDir();
        var repo = RepoWithHiddenRepo(t, out var sub);
        using var _ = new Chmod(sub, Mode311);
        Assert.Equal([sub], RepoScan.ScanNested(repo, Path.Combine(repo, ".git"), [], p => claimsOpened).UnlistableDirs);
    }

    [Fact]
    public void WithoutOpeningAnUnlistableDirectoryIsAFailure_AndKeepsItsMode()
    {
        using var t = new TempDir();
        var repo = RepoWithHiddenRepo(t, out var sub);
        using var _ = new Chmod(sub, Mode311);
        var check = GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);
        Assert.Contains("can't list", check.Failure);
        Assert.Contains("sub", check.Failure);
        Assert.Equal(Mode311, File.GetUnixFileMode(sub));
    }

    [Theory]
    [InlineData(UnixFileMode.None)]
    [InlineData(UnixFileMode.UserRead)]
    public void ADirectoryThatCantBeEnteredStaysAllowed(UnixFileMode mode)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var db = t.Sub("repo", "db");
        File.WriteAllText(Path.Combine(db, "data"), "");
        var session = Session(t, Path.Combine(repo, ".git"));
        using var _ = new Chmod(db, mode);

        RepoGuard.CheckAtStart(repo);
        var check = GitCheck.RunAtSessionEnd(repo, session);

        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
        Assert.Empty(check.Opened);
        Assert.Equal(mode, File.GetUnixFileMode(db));
    }

    [Fact]
    public void AnUnreadableGitDirIsCouldntCheck()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        var session = Session(t, git);
        using var _ = new Chmod(git, UnixFileMode.None);

        Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        var check = GitCheck.RunAtSessionEnd(repo, session);

        Assert.Contains("can't read", check.Failure);
        Assert.Empty(check.Moved);
    }

    [Fact]
    public void AProjectDirThatCantBeEnteredIsCouldntCheck()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var session = Session(t, Path.Combine(repo, ".git"));
        using var _ = new Chmod(repo, UnixFileMode.None);
        Assert.Contains("can't enter", GitCheck.RunAtSessionEnd(repo, session).Failure);
    }

    [Fact]
    public void AFifoNamedDotGitIsRefusedAtStart()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        MkFifo(Path.Combine(t.Sub("repo", "sub"), ".git"));
        var e = Within10s(() => Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)));
        Assert.Contains("sub", e.Message);
    }

    [Fact]
    public void AFifoNamedDotGitIsQuarantinedAtSessionEnd()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var fifo = Path.Combine(t.Sub("repo", "sub"), ".git");
        MkFifo(fifo);
        var check = Within10s(() => GitCheck.RunAtSessionEnd(repo, Session(t, Path.Combine(repo, ".git"))));
        Assert.Null(check.Failure);
        Assert.Equal(fifo, Assert.Single(check.Moved).From);
        Assert.False(RepoGuard.Exists(fifo));
    }

    [Fact]
    public void AFifoAsTheProjectsOwnDotGitIsntARepo()
    {
        using var t = new TempDir();
        var dir = t.Sub("dir");
        MkFifo(Path.Combine(dir, ".git"));
        Assert.Null(Within10s(() => Project.FindRepo(dir)).CommonGitDir);
        Within10s(() => Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(dir)));
    }

    [Fact]
    public void AFifoAsAWorktreesFilesOrConfigIsNeverOpened()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var entry = t.Sub("repo", ".git", "worktrees", "w");
        MkFifo(Path.Combine(entry, "gitdir"));
        Assert.False(Within10s(() => RepoScan.IsWorktreeInside(repo, entry)));
        var config = Path.Combine(repo, ".git", "config.worktree");
        MkFifo(config);
        var e = Within10s(() => Assert.Throws<PenException>(() => GitConfigFile.ParseFile(config)));
        Assert.Contains("can't read", e.Message);
    }

    private static void Sparse(string path, long size)
    {
        using var f = File.Create(path);
        f.SetLength(size);
    }

    private const long ThreeGb = 3L * 1024 * 1024 * 1024;

    [Fact]
    public void AHugeSparseDotGitIsRefusedAtStart()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Sparse(Path.Combine(t.Sub("repo", "sub"), ".git"), ThreeGb);
        var e = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        Assert.Contains("  sub (a .git file", e.Message);
    }

    [Fact]
    public void AHugeSparseDotGitIsQuarantinedAtSessionEnd_AlongWithWhatElseWasPlanted()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var huge = Path.Combine(t.Sub("repo", "sub"), ".git");
        Sparse(huge, ThreeGb);
        Git.Repo(t.Sub("repo", "planted"));

        var check = GitCheck.RunAtSessionEnd(repo, Session(t, Path.Combine(repo, ".git")));

        Assert.Null(check.Failure);
        Assert.Equal([Path.Combine(repo, "planted", ".git"), huge], check.Moved.Select(q => q.From).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASizeLimitLetsARealFileThrough_AndStopsOneJustOver()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "f");
        File.WriteAllText(path, new string('x', 1024 * 1024));
        Assert.Equal(1024 * 1024, Native.ReadRegularFile(path)!.Length);
        File.AppendAllText(path, "x");
        Assert.Null(Native.ReadRegularFile(path));
        File.WriteAllText(path, "");
        Assert.Equal("", Native.ReadRegularFile(path));
    }

    [Fact]
    public void AProtectedReadDoesntFollowASymlink()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "f");
        File.WriteAllText(path, "x");
        var link = Path.Combine(t.Path, "link");
        File.CreateSymbolicLink(link, path);
        Assert.Equal("x"u8.ToArray(), Native.ReadProtected(path)!.Bytes);
        Assert.Null(Native.ReadProtected(link));
        Assert.Equal("x", Native.ReadRegularFile(link)); // the plain read does
    }

    [Fact]
    public void AnOversizedGitConfigSaysSo_NotToFixPermissions()
    {
        using var t = new TempDir();
        var config = Path.Combine(t.Path, "config");
        Sparse(config, ThreeGb);
        var e = Assert.Throws<PenException>(() => GitConfigFile.ParseFile(config));
        Assert.Contains("isn't a regular file of a reasonable size", e.Message);
        Assert.DoesNotContain("permissions", e.Message);
    }

    [Fact]
    public void AFifoOrHugeConfigTomlIsRefused_NotHungOn()
    {
        using var t = new TempDir();
        var fifo = Path.Combine(t.Path, "fifo");
        MkFifo(fifo);
        var e = Within10s(() => Assert.Throws<PenException>(() => Config.ReadText(fifo, ".pen/config.toml")));
        Assert.Contains(".pen/config.toml isn't a regular file", e.Message);
        var viaAllow = Within10s(() => Assert.Throws<PenException>(() => AllowCommand.Append(new(fifo, ".pen/config.toml", ConfigKind.Project, ""), "allow", ["a.example"])));
        Assert.Contains(".pen/config.toml isn't a regular file", viaAllow.Message);
        var huge = Path.Combine(t.Path, "huge");
        Sparse(huge, ThreeGb);
        Assert.Throws<PenException>(() => Config.ReadText(huge, ".pen/config.toml"));
    }

    [Fact]
    public void TryOpenWontChmodThroughASymlink()
    {
        using var t = new TempDir();
        var target = t.Sub("target");
        var link = Path.Combine(t.Path, "link");
        File.CreateSymbolicLink(link, target);
        using var _ = new Chmod(target, Mode311);
        Assert.False(GitCheck.TryOpen(link, write: false, out var opened));

        Assert.Null(opened);
        Assert.Equal(Mode311, File.GetUnixFileMode(target));
        Assert.True(GitCheck.TryOpen(target, write: false, out var reopened)); // the check above is what stopped it
    }

    [Fact]
    public void APathTooLongToStatIsUnlistable_NotSkipped()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        // Made a level at a time from inside: the whole path is over PATH_MAX (4096 on Linux, 1024 on macOS).
        // In bash, since dash's cd fails past that length.
        var name = new string('d', 100);
        var script = $"cd '{repo}' && for i in $(seq 1 50); do mkdir {name} && cd {name}; done";
        var r = Process.RunAndCaptureText(new ProcessStartInfo("bash", ["-c", script]) { RedirectStandardOutput = true, RedirectStandardError = true });
        try
        {
            Assert.Equal(0, r.ExitStatus.ExitCode);
            var deep = NestedScans.Scan(repo, Path.Combine(repo, ".git")).UnlistableDirs;
            Assert.StartsWith(Path.Combine(repo, name), Assert.Single(deep));
            Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        }
        finally
        {
            Process.RunAndCaptureText(new ProcessStartInfo("rm", ["-rf", Path.Combine(repo, name)]) { RedirectStandardOutput = true, RedirectStandardError = true });
        }
    }

    private static string Inc(params string[] paths) => string.Concat(paths.Select(p => $"[include]\n\tpath = {p}\n"));

    private static List<Mount> Mounts(TempDir t, string repo) =>
        Within10s(() => RepoGuard.PrepareMounts(repo, t.Path));

    private static bool ReadOnly(List<Mount> mounts, string path) => mounts.Any(m => m.Path == path && m.ReadOnly);

    [Fact]
    public void ASelfIncludingConfigWorktreeDoesntHangStartOrTheChecks()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var file = Path.Combine(repo, ".git", "config.worktree");
        File.WriteAllText(file, string.Concat(Enumerable.Repeat(Inc("config.worktree"), 64)));

        Assert.True(ReadOnly(Mounts(t, repo), file));
        Assert.Empty(Within10s(() => GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved));
        Assert.Null(Within10s(() => GitCheck.RunAtSessionEnd(repo, Session(t, Path.Combine(repo, ".git")))).Failure);
    }

    [Fact]
    public void AMutualIncludeCycleEndsWithBothProtected()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        File.WriteAllText(Path.Combine(git, "a.inc"), Inc("b.inc"));
        File.WriteAllText(Path.Combine(git, "b.inc"), Inc("a.inc"));
        Git.Run(repo, "config", "include.path", "a.inc");
        var mounts = Mounts(t, repo);
        Assert.True(ReadOnly(mounts, Path.Combine(git, "a.inc")));
        Assert.True(ReadOnly(mounts, Path.Combine(git, "b.inc")));
    }

    [Fact]
    public void ADiamondsSharedFileStaysProtected_AndWhatItEnablesCounts()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        File.WriteAllText(Path.Combine(git, "left.inc"), Inc("shared.inc"));
        File.WriteAllText(Path.Combine(git, "right.inc"), Inc("shared.inc"));
        File.WriteAllText(Path.Combine(git, "shared.inc"), Inc("leaf.inc") + "[extensions]\n\tworktreeConfig = true\n");
        File.WriteAllText(Path.Combine(git, "leaf.inc"), "");
        Git.Run(repo, "config", "--add", "include.path", "left.inc");
        Git.Run(repo, "config", "--add", "include.path", "right.inc");

        var mounts = Mounts(t, repo);

        foreach (var f in new[] { "left.inc", "right.inc", "shared.inc", "leaf.inc", "config.worktree" })
            Assert.True(ReadOnly(mounts, Path.Combine(git, f)), f);
    }

    [Fact]
    public void AFileFirstMetDeepKeepsItsIncludesWhenMetAgainHigher()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        // A chain past git's depth limit ends in shared.inc, also included directly.
        for (int i = 0; i < 12; i++) File.WriteAllText(Path.Combine(git, $"c{i}.inc"), Inc($"c{i + 1}.inc"));
        File.WriteAllText(Path.Combine(git, "c12.inc"), Inc("shared.inc"));
        File.WriteAllText(Path.Combine(git, "shared.inc"), Inc("leaf.inc"));
        File.WriteAllText(Path.Combine(git, "leaf.inc"), "");
        File.AppendAllText(Path.Combine(git, "config"), Inc("c0.inc", "shared.inc")); // git config itself refuses the depth
        Assert.True(ReadOnly(Mounts(t, repo), Path.Combine(git, "leaf.inc")));
    }

    [Fact]
    public void AFanOutOverTheLimitIsRefused_NamingTheFile()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var file = Path.Combine(repo, ".git", "config.worktree");
        File.WriteAllText(file, Inc(Enumerable.Range(0, RepoGuard.MaxIncludeFiles + 1).Select(i => $"f{i}.inc").ToArray()));
        var e = Within10s(() => Assert.Throws<PenException>(() => RepoGuard.PrepareMounts(repo, t.Path)));
        Assert.Contains(".git/config.worktree", e.Message);
        Assert.Contains("remove it", e.Message);
    }

    [Theory]
    [InlineData("HEAD/x")] // the parent is a file
    [InlineData("HEAD/x/y.inc")]
    [InlineData("LONG")] // the name is over the file system's limit
    public void AnIncludeInTheGitDirThatCantBeCreatedIsRefused_UntilTheConfigIsRemoved(string include)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var file = Path.Combine(repo, ".git", "config.worktree");
        var path = include == "LONG" ? new string('n', 300) : include;
        File.WriteAllText(file, Inc(path));

        var e = Assert.Throws<PenException>(() => RepoGuard.PrepareMounts(repo, t.Path));
        Assert.Contains(".git/config.worktree includes", e.Message);
        Assert.Contains(path, e.Message);
        Assert.Contains("remove it", e.Message);

        File.Delete(file);
        RepoGuard.PrepareMounts(repo, t.Path);
    }

    [Fact]
    public void AnOpenDirectoryWithTheBitsAlreadySetRecordsNoChange()
    {
        using var t = new TempDir();
        Assert.False(GitCheck.TryOpen(t.Sub("d"), write: false, out var opened));
        Assert.Null(opened);
    }

    [Fact]
    public void TheFailureListsAtMostTheCappedNumberOfDirectories()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var dirs = Enumerable.Range(0, RepoGuard.MaxListed + 2).Select(i => new Chmod(t.Sub("repo", $"d{i}"), Mode311)).ToList();
        try { Assert.Contains("and 2 more", GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Failure); }
        finally { dirs.ForEach(d => d.Dispose()); }
    }

    [Fact]
    public void AConfigThatsGoneReadsAsNone()
    {
        using var t = new TempDir();
        Assert.Null(Config.ReadText(Path.Combine(t.Path, "config.toml"), ".pen/config.toml"));
    }

    [Fact]
    public void AQuarantinedSymlinkIsLeftAloneTheSecondTime()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var foo = Path.Combine(t.Sub("repo", ".git", "modules"), "foo");
        File.CreateSymbolicLink(foo, t.Sub("elsewhere"));

        Assert.Equal(foo + GitCheck.Suffix, Assert.Single(GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved).To);

        Assert.Empty(GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved);
        Assert.Empty(RepoScan.FindModuleRepos(Path.Combine(repo, ".git")));
        Assert.True(RepoGuard.IsLink(foo + GitCheck.Suffix));
    }

    [Fact]
    public void WhatCouldntBeMovedIsCouldntCheck()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        File.WriteAllText(Path.Combine(git, "commondir"), "planted\n");
        var session = Session(t, git);
        using var _ = new Chmod(git, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var check = GitCheck.RunAtSessionEnd(repo, session);

        Assert.Contains("COULD NOT MOVE", Assert.Single(check.Moved).Why);
        Assert.Contains("couldn't be moved", check.Failure);
    }

    [Fact]
    public void AModuleRepoThatCouldntBeMovedIsntKept_SoItsCheckoutIsFlagged()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var modules = t.Sub("repo", ".git", "modules");
        var foo = Path.Combine(modules, "foo");
        Git.Run(modules, "init", "-q", "--bare", "foo");
        Git.Run(foo, "config", "core.worktree", t.Sub("repo", "co"));
        File.WriteAllText(Path.Combine(repo, "co", ".git"), $"gitdir: {foo}\n");
        var session = Session(t, Path.Combine(repo, ".git"));
        using var _ = new Chmod(modules, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        var check = GitCheck.RunAtSessionEnd(repo, session);

        Assert.NotNull(check.Failure);
        Assert.Contains(check.Moved, q => q.From == foo && q.To.Length == 0);
        Assert.Contains(check.Moved, q => q.From == Path.Combine(repo, "co", ".git") && q.To.Length > 0);
    }
}
