namespace Pen.Tests;

/// <summary>
/// Paths in a git dir that Pen can't read (an agent's chmod 000) are refused at
/// the start, quarantined at the end, and never leave a project stuck.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public class UnreadablePathTests
{
    private const UnixFileMode Rwx = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Takes <paramref name="path"/>'s mode away; on dispose restores it, under its quarantined name too, for temp cleanup.</summary>
    private sealed class Chmod : IDisposable
    {
        private readonly string _path;

        public Chmod(string path, UnixFileMode mode = UnixFileMode.None)
        {
            _path = path;
            File.SetUnixFileMode(path, mode);
        }

        public void Dispose()
        {
            foreach (var p in new[] { _path, _path + GitQuarantine.Suffix })
                if (Path.Exists(p)) File.SetUnixFileMode(p, Rwx);
        }
    }

    private static string RepoWithModule(TempDir t, out string foo)
    {
        var repo = Git.Repo(t.Sub("repo"));
        foo = t.Sub("repo", ".git", "modules", "foo");
        Git.Run(foo, "init", "-q", "--bare");
        return repo;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GitCheckQuarantinesAnUnreadableModuleEntry(bool thereAtStart)
    {
        using var t = new TempDir();
        var repo = RepoWithModule(t, out var foo);
        using var _ = new Chmod(foo);

        var moved = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: thereAtStart ? [foo] : []).Moved;

        var q = Assert.IsType<Quarantined.Moved>(Assert.Single(moved));
        Assert.Equal(foo, q.From);
        Assert.Equal(foo + GitQuarantine.Suffix, q.To);
        Assert.Contains("can't be read", q.Why);
        Assert.False(Path.Exists(foo));
        Assert.Empty(GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: []).Moved);
        RepoGuard.CheckAtStart(repo); // the next pen command works
    }

    [Fact]
    public void GitCheckWithNoStartListQuarantinesToo()
    {
        using var t = new TempDir();
        var repo = RepoWithModule(t, out var foo);
        using var _ = new Chmod(foo);
        Assert.Equal(foo, Assert.Single(GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved).From);
    }

    [Fact]
    public void AnUnreadableSubmoduleRepoTakesItsCheckoutWithIt()
    {
        using var t = new TempDir();
        var lib = Git.Repo(t.Sub("lib"));
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "-c", "protocol.file.allow=always", "submodule", "add", "-q", lib, "lib");
        var module = Path.Combine(repo, ".git", "modules", "lib");
        var atStart = RepoScan.FindModuleRepos(Path.Combine(repo, ".git"));
        Assert.Equal([module], atStart);
        using var _ = new Chmod(module);

        var moved = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: atStart).Moved.Select(q => PathChecks.FormatPath(repo, q.From)).ToList();

        Assert.Equal([".git/modules/lib", "lib/.git"], moved);
    }

    [Fact]
    public void RepoGuardRefusesAnUnreadableModuleEntry()
    {
        using var t = new TempDir();
        var repo = RepoWithModule(t, out var foo);
        using var _ = new Chmod(foo);
        var msg = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message;
        Assert.Contains("  .git/modules/foo", msg);
    }

    [Theory]
    [InlineData("modules")]
    [InlineData("worktrees")]
    public void AnUnreadableTopLevelIsRefusedAndQuarantined(string name)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var dir = t.Sub("repo", ".git", name);
        using var _ = new Chmod(dir);

        Assert.Contains($"  .git/{name}", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message);
        Assert.Equal(dir, Assert.Single(GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: []).Moved).From);
        RepoGuard.CheckAtStart(repo);
    }

    [Fact]
    public void ADirectoryThatCanBeListedButNotEnteredCountsAsUnreadable()
    {
        using var t = new TempDir();
        var repo = RepoWithModule(t, out var foo);
        using var _ = new Chmod(foo, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal([foo], RepoScan.ScanModules(Path.Combine(repo, ".git")).UnreadableDirs);
        Assert.Contains("  .git/modules/foo", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message);
    }

    [Fact]
    public void FinishDeadSessionsEndsTheSessionAndTheNextCommandWorks()
    {
        using var t = new TempDir();
        var repo = RepoWithModule(t, out var foo);
        var session = new SessionDir("pen-test-12345678", t.Sub("runtime", "sessions", "12345678"));
        File.WriteAllText(session.ComposeFile, "stack");
        File.WriteAllText(session.EnvFile, $"PROJECT_DIR={repo}\n");
        File.WriteAllText(session.StartedWithGit, Path.Combine(repo, ".git") + "\n");
        File.WriteAllText(session.ModulesFile, foo + "\n");
        using var fake = new FakeDocker(t, """case "$1" in rm|compose) exit 0 ;; *) exit 99 ;; esac""" + "\n");
        using var _ = new Chmod(foo);

        Session.FinishDeadSessions(session.Id, Path.Combine(t.Path, "runtime"), TimeProvider.System);

        Assert.False(session.IsOpen);
        Assert.False(Path.Exists(foo));
        Assert.True(Directory.Exists(foo + GitQuarantine.Suffix));
        RepoGuard.CheckAtStart(repo);
    }

    [Fact]
    public void AnUnreadableConfigIsAPenException()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "config");
        File.WriteAllText(file, "[core]\n");
        using var _ = new Chmod(file);
        Assert.Contains(file, Assert.Throws<PenException>(() => GitConfigFile.ParseFile(file)).Message);
    }

    [Fact]
    public void AnUnreadableMainConfigIsAPenExceptionFromAWorktree()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        using var _ = new Chmod(Path.Combine(repo, ".git", "config"));
        Assert.Throws<PenException>(() => Project.FindRepo(wt));
    }

    [Fact]
    public void ASubmoduleWithAnUnreadableConfigLosesItsCheckoutWithoutThrowing()
    {
        using var t = new TempDir();
        var lib = Git.Repo(t.Sub("lib"));
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "-c", "protocol.file.allow=always", "submodule", "add", "-q", lib, "lib");
        var atStart = RepoScan.FindModuleRepos(Path.Combine(repo, ".git"));
        using var _ = new Chmod(Path.Combine(repo, ".git", "modules", "lib", "config"));
        var moved = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: atStart).Moved;
        Assert.Equal("lib/.git", PathChecks.FormatPath(repo, Assert.Single(moved).From));
    }

    [Theory]
    [InlineData("UnauthorizedAccess_IODenied_Path, /a/b", "permission denied: /a/b")]
    [InlineData("Access to the path '/a/b' is denied.", "permission denied: /a/b")]
    [InlineData("UnauthorizedAccess_IODenied_NoPathName", "permission denied")]
    public void PermissionDeniedShowsOnlyThePath(string message, string expected) =>
        Assert.Equal(expected, Out.DescribePermissionDenied(new UnauthorizedAccessException(message)));

    [Fact]
    public void AnUnreadableGitmodulesOnlyLosesTheAdvice()
    {
        using var t = new TempDir();
        var root = t.Sub("root");
        var gitmodules = Path.Combine(root, ".gitmodules");
        File.WriteAllText(gitmodules, "[submodule \"a\"]\n\tpath = a\n");
        using var _ = new Chmod(gitmodules);
        Assert.False(RepoGuard.IsSubmodulePath(root, Path.Combine(root, "a")));
    }
}
