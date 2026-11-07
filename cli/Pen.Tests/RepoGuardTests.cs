namespace Pen.Tests;

public class RepoGuardTests
{
    private static string Refusal(string dir) =>
        Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(dir)).Message;

    [Fact]
    public void APlainRepoAndAPlainDirectoryStart()
    {
        using var t = new TempDir();
        RepoGuard.CheckAtStart(Git.Repo(t.Sub("repo")));
        var plain = t.Sub("plain", "src");
        File.WriteAllText(Path.Combine(plain, "HEAD"), "not a repo"); // HEAD alone isn't a bare layout
        RepoGuard.CheckAtStart(t.Sub("plain"));
    }

    [Fact]
    public void RefusesALinkedWorktree()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "wt"));
        Assert.Contains("linked worktree", Refusal(Path.Combine(t.Path, "wt")));
        Assert.Contains("main checkout", Refusal(Path.Combine(t.Path, "wt")));
        RepoGuard.CheckAtStart(repo); // the main checkout itself is fine
    }

    [Fact]
    public void RefusesASymlinkedGitDir()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var proj = t.Sub("proj");
        Directory.CreateSymbolicLink(Path.Combine(proj, ".git"), Path.Combine(repo, ".git"));
        Assert.Contains("symlink", Refusal(proj));
    }

    [Fact]
    public void RefusesSubmodules()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(repo, ".gitmodules"), "");
        Assert.Contains("submodules", Refusal(repo));
        File.Delete(Path.Combine(repo, ".gitmodules"));
        Directory.CreateDirectory(Path.Combine(repo, ".git", "modules"));
        Assert.Contains("submodules", Refusal(repo));
    }

    [Fact]
    public void RefusesACommondir()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(repo, ".git", "commondir"), "/elsewhere");
        Assert.Contains(".git/commondir", Refusal(repo));
    }

    [Fact]
    public void RefusesNestedRepos_NamingEach()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "init", "-q", "libs/sub");
        Git.Run(repo, "init", "-q", "--bare", "sub2");
        var msg = Refusal(repo);
        Assert.Contains("other git repos inside it", msg);
        Assert.Contains("\n  libs/sub\n", msg);
        Assert.Contains("\n  sub2 (a bare repo)", msg);
        Assert.Contains("run pen in each repo instead", msg);
    }

    [Fact]
    public void RefusesAFolderOfRepos()
    {
        using var t = new TempDir();
        var repos = t.Sub("repos");
        Git.Repo(Path.Combine(repos, "a"));
        Git.Repo(Path.Combine(repos, "b"));
        var msg = Refusal(repos);
        Assert.Contains("it contains git repos", msg);
        Assert.Contains("\n  a\n", msg);
        Assert.Contains("\n  b\n", msg);
    }

    [Fact]
    public void RefusesABareLayoutInTheRoot()
    {
        using var t = new TempDir();
        var bare = t.Sub("bare.git");
        Git.Run(bare, "init", "-q", "--bare");
        Assert.Contains("(a bare repo)", Refusal(bare));
    }

    [Fact]
    public void FindsAGitdirLayoutWithCommondir_AGitFileAndAGitSymlink()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var wtLike = t.Sub("repo", "a");
        File.WriteAllText(Path.Combine(wtLike, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(wtLike, "commondir"), "../.git\n");
        File.WriteAllText(Path.Combine(t.Sub("repo", "b"), ".git"), $"gitdir: {t.Path}/elsewhere\n");
        Directory.CreateSymbolicLink(Path.Combine(t.Sub("repo", "c"), ".git"), Path.Combine(repo, ".git"));
        var found = RepoScan.FindNested(repo);
        Assert.Equal(
            [("a/HEAD", NestedKind.Bare), ("b/.git", NestedKind.GitFile), ("c/.git", NestedKind.GitLink)],
            found.Select(n => (RepoGuard.Rel(repo, n.Path), n.Kind)));
    }

    [Fact]
    public void FindsAnUppercaseGit()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateDirectory(Path.Combine(t.Sub("repo", "x"), ".GIT"));
        Assert.Single(RepoScan.FindNested(repo));
    }

    [Fact]
    public void DoesNotFollowSymlinkedDirectories()
    {
        using var t = new TempDir();
        var other = Git.Repo(t.Sub("other"));
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "link"), other);
        Assert.Empty(RepoScan.FindNested(repo));
    }

    [Fact]
    public void AllowsTheProjectsOwnWorktrees()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", ".claude/worktrees/x");
        Git.Run(repo, "worktree", "add", "-q", "--relative-paths", ".claude/worktrees/y");
        RepoGuard.CheckAtStart(repo);
    }

    [Fact]
    public void A_WorktreeWithAChangedCommondirIsNested()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "wt");
        File.WriteAllText(Path.Combine(repo, ".git", "worktrees", "wt", "commondir"), "/tmp/evil\n");
        Assert.Contains("wt (a .git file that isn't one of this repo's worktrees)", Refusal(repo));
    }

    [Fact]
    public void A_WorktreeOfAnotherRepoIsNested()
    {
        using var t = new TempDir();
        var other = Git.Repo(t.Sub("other"));
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(other, "worktree", "add", "-q", Path.Combine(repo, "wt"));
        Assert.Contains("wt (a .git file", Refusal(repo));
    }

    [Fact]
    public void A_WorktreeGitdirThroughASymlinkIsNested()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "wt");
        // Same name under .git/worktrees, but really a directory elsewhere whose ../.. isn't .git.
        var elsewhere = t.Sub("x", "y", "z");
        File.WriteAllText(Path.Combine(elsewhere, "commondir"), "../..\n");
        Directory.Delete(Path.Combine(repo, ".git", "worktrees", "wt"), recursive: true);
        Directory.CreateSymbolicLink(Path.Combine(repo, ".git", "worktrees", "wt"), elsewhere);
        Assert.False(RepoScan.IsProjectWorktree(repo, Path.Combine(repo, "wt", ".git")));
    }

    [Fact]
    public void ScanSkipsTheProjectsOwnGitDir()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "outside"));
        // .git/worktrees/outside has HEAD + commondir, but it's inside the project's .git.
        Assert.Empty(RepoScan.FindNested(repo));
    }

    // --- mounts ---------------------------------------------------------------

    private static List<(string, bool)> Mounts(string dir, string? home = null) =>
        RepoGuard.PrepareMounts(dir, home ?? "/nonexistent-home").Select(m => (RepoGuard.Rel(dir, m.Path), m.ReadOnly)).ToList();

    [Fact]
    public void MountsForARepo()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Directory.Delete(Path.Combine(repo, ".git", "hooks"), recursive: true);
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "wt"));
        Assert.Equal(
            [(".git", false), (".git/config", true), (".git/hooks", true), (".git/worktrees", false), (".git/worktrees/wt", true), (".pen", true)],
            Mounts(repo));
        Assert.True(Directory.Exists(Path.Combine(repo, ".git", "hooks")), ".git/hooks is created");
        Assert.True(Directory.Exists(Path.Combine(repo, ".pen")), ".pen is created");
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(repo, ".pen")));
        Assert.Equal("", Git.Run(repo, "status", "--porcelain")); // an empty .pen/ doesn't show up
    }

    [Fact]
    public void AnExternalWorktreesEntryIsReadOnlyAndAnInternalOnesStaysWritable()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "sibling"));
        Git.Run(repo, "worktree", "add", "-q", "--relative-paths", Path.Combine(t.Path, "relsibling"));
        Git.Run(repo, "worktree", "add", "-q", ".claude/worktrees/inside");
        Git.Run(repo, "worktree", "add", "-q", "--relative-paths", ".claude/worktrees/relinside");
        var m = Mounts(repo);
        Assert.Contains((".git/worktrees", false), m);
        Assert.Contains((".git/worktrees/sibling", true), m);
        Assert.Contains((".git/worktrees/relsibling", true), m);
        // Inside the sandbox those are dangling; the commondir in them needs no mount of its own.
        Assert.DoesNotContain(m, x => x.Item1.StartsWith(".git/worktrees/sibling/", StringComparison.Ordinal));
        // Visible in the sandbox: the entry stays writable (git updates its HEAD and index), its commondir doesn't.
        Assert.DoesNotContain(m, x => x.Item1 is ".git/worktrees/inside" or ".git/worktrees/relinside");
        Assert.Contains((".git/worktrees/inside/commondir", true), m);
        Assert.Contains((".git/worktrees/relinside/commondir", true), m);
        // Parents come before what's mounted inside them.
        Assert.True(m.IndexOf((".git/worktrees", false)) < m.IndexOf((".git/worktrees/sibling", true)));
    }

    [Fact]
    public void A_WorktreesEntryWithAMissingOrOddGitdirFileIsReadOnly()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "a");
        Git.Run(repo, "worktree", "add", "-q", "b");
        Git.Run(repo, "worktree", "add", "-q", "c");
        var entries = Path.Combine(repo, ".git", "worktrees");
        File.Delete(Path.Combine(entries, "a", "gitdir"));
        File.WriteAllText(Path.Combine(entries, "b", "gitdir"), "");
        // A symlinked gitdir file can't be trusted to say where the worktree is.
        var real = Path.Combine(t.Path, "c-gitdir");
        File.Move(Path.Combine(entries, "c", "gitdir"), real);
        File.CreateSymbolicLink(Path.Combine(entries, "c", "gitdir"), real);
        var m = Mounts(repo);
        Assert.Contains((".git/worktrees/a", true), m);
        Assert.Contains((".git/worktrees/b", true), m);
        Assert.Contains((".git/worktrees/c", true), m);
    }

    [Fact]
    public void A_WorktreeInsideTheProjectThroughASymlinkedPathCountsAsInside()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "wt");
        // The gitdir file names the worktree by a path through a symlink to the project.
        var link = Path.Combine(t.Path, "link");
        Directory.CreateSymbolicLink(link, repo);
        File.WriteAllText(Path.Combine(repo, ".git", "worktrees", "wt", "gitdir"), Path.Combine(link, "wt", ".git") + "\n");
        Assert.True(RepoGuard.IsWorktreeInside(repo, Path.Combine(repo, ".git", "worktrees", "wt")));
        // And a symlink inside the project leading out of it counts as outside.
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "out"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "to-out"), Path.Combine(t.Path, "out"));
        File.WriteAllText(Path.Combine(repo, ".git", "worktrees", "out", "gitdir"), Path.Combine(repo, "to-out", ".git") + "\n");
        Assert.False(RepoGuard.IsWorktreeInside(repo, Path.Combine(repo, ".git", "worktrees", "out")));
    }

    [Fact]
    public void A_SymlinkedCommondirIsRefusedInAnyEntry()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "sibling"));
        var commondir = Path.Combine(repo, ".git", "worktrees", "sibling", "commondir");
        File.Delete(commondir);
        File.WriteAllText(Path.Combine(repo, "cd"), "../..\n");
        File.CreateSymbolicLink(commondir, Path.Combine(repo, "cd"));
        Assert.Contains("commondir is a symlink", Assert.Throws<PenException>(() => Mounts(repo)).Message);
    }

    [Fact]
    public void MountsForAPlainDirectory()
    {
        using var t = new TempDir();
        Assert.Equal([(".pen", true)], Mounts(t.Sub("plain")));
    }

    [Fact]
    public void RefusesSymlinkedHooksOrPen()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var hooks = Path.Combine(repo, ".git", "hooks");
        Directory.Delete(hooks, recursive: true);
        Directory.CreateSymbolicLink(hooks, t.Sub("shared-hooks"));
        Assert.Contains(".git/hooks is a symlink", Assert.Throws<PenException>(() => Mounts(repo)).Message);

        var plain = t.Sub("plain");
        Directory.CreateSymbolicLink(Path.Combine(plain, ".pen"), t.Sub("elsewhere"));
        Assert.Contains(".pen is a symlink", Assert.Throws<PenException>(() => Mounts(plain)).Message);
        File.WriteAllText(Path.Combine(t.Sub("plain2"), ".pen"), "");
        Assert.Contains(".pen is a file", Assert.Throws<PenException>(() => Mounts(Path.Combine(t.Path, "plain2"))).Message);
    }

    [Fact]
    public void WorktreeConfigFilesAreCreatedAndMountedWhenEnabled()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", ".claude/worktrees/wt");
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "sibling"));
        Assert.DoesNotContain((".git/config.worktree", true), Mounts(repo));
        Git.Run(repo, "config", "extensions.worktreeConfig", "true");
        var m = Mounts(repo);
        Assert.Contains((".git/config.worktree", true), m);
        Assert.Contains((".git/worktrees/wt/config.worktree", true), m);
        Assert.True(File.Exists(Path.Combine(repo, ".git", "config.worktree")));
        // The sibling's entry is read-only as a whole, so nothing is created in it.
        Assert.Contains((".git/worktrees/sibling", true), m);
        Assert.False(File.Exists(Path.Combine(repo, ".git", "worktrees", "sibling", "config.worktree")));
    }

    [Fact]
    public void IncludedConfigInsideTheProjectIsMountedReadOnly()
    {
        using var t = new TempDir();
        var home = t.Sub("home");
        var repo = Git.Repo(t.Sub("home", "repo"));
        File.WriteAllText(Path.Combine(repo, ".gitconfig"), "[include]\n\tpath = .git/nested.inc\n");
        Git.Run(repo, "config", "--add", "include.path", "../.gitconfig");
        Git.Run(repo, "config", "--add", "include.path", "local.inc");            // missing, inside .git: created
        Git.Run(repo, "config", "--add", "includeIf.onbranch:main.path", "~/repo/other.inc"); // ~ is the host's home
        Git.Run(repo, "config", "--add", "include.path", Path.Combine(t.Path, "outside.inc")); // outside: ignored
        File.WriteAllText(Path.Combine(repo, "other.inc"), "");
        var m = Mounts(repo, home);
        Assert.Contains((".gitconfig", true), m);
        Assert.Contains((".git/nested.inc", true), m);
        Assert.Contains((".git/local.inc", true), m);
        Assert.Contains(("other.inc", true), m);
        Assert.DoesNotContain(m, x => x.Item1.Contains("outside"));
        Assert.True(File.Exists(Path.Combine(repo, ".git", "local.inc")));
    }

    [Fact]
    public void AMissingIncludeInTheWorkingTreeIsRefused()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "config", "include.path", "../shared.gitconfig");
        var msg = Assert.Throws<PenException>(() => Mounts(repo)).Message;
        Assert.Contains("includes shared.gitconfig, which doesn't exist", msg);
    }

    [Fact]
    public void ComposeOverrideQuotesPathsAndEscapesDollars()
    {
        var yaml = RepoGuard.ComposeOverride([new("/p/a \"b\" $HOME é/.git", false), new("/p/.pen", true)]);
        Assert.Contains("\"source\": \"/p/a \\\"b\\\" $$HOME é/.git\"", yaml);
        Assert.Contains("\"read_only\": true", yaml);
        Assert.Contains("\"create_host_path\": false", yaml);
        Assert.StartsWith("{", yaml);
    }
}
