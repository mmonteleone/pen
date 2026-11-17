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
    public void RefusesAGitFileThatIsntAValidWorktree()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "wt"));
        File.WriteAllText(Path.Combine(repo, ".git", "worktrees", "wt", "commondir"), "/tmp/evil\n");
        Assert.Contains("isn't a valid linked worktree", Refusal(Path.Combine(t.Path, "wt")));
        // The forged entry is in the main checkout's git dir, where git run in it would take it for a git dir.
        var message = Refusal(repo);
        Assert.Contains("\n  .git/worktrees/wt (a worktree entry whose commondir doesn't lead back to this repo)\n",
            message);
        Assert.Contains("git worktree prune removes an entry whose worktree is gone", message);
        Assert.Contains("git worktree repair mends one whose worktree moved", message);
        Assert.EndsWith("\ninspect any others and remove them, then rerun", message);
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
    public void AcceptsGitmodulesAndAModulesDir()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(repo, ".gitmodules"), "[submodule \"x\"]\n\tpath = x\n\turl = ../x\n");
        Directory.CreateDirectory(Path.Combine(repo, ".git", "modules"));
        RepoGuard.CheckAtStart(repo);
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
    public void RefusesReposInTheGitDir_NamingEach()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        Git.Run(t.Sub("repo", ".git", "a"), "init", "-q");
        Git.Run(t.Sub("repo", ".git", "b"), "init", "-q", "--bare");
        File.WriteAllText(Path.Combine(t.Sub("repo", ".git", "c", "d"), ".git"), $"gitdir: {t.Path}/elsewhere\n");
        Directory.CreateSymbolicLink(Path.Combine(git, ".git"), t.Sub("elsewhere"));

        var message = Refusal(repo);

        Assert.Contains("its git dir has repos where git keeps none", message);
        Assert.Contains("\n  .git (a .git symlink)\n", message);
        Assert.Contains("\n  .git/a (a .git dir)\n", message);
        Assert.Contains("\n  .git/b (laid out as a git dir of its own)\n", message);
        Assert.Contains("\n  .git/c/d (a .git file that isn't one of this repo's worktrees or submodules)\n", message);
        Assert.EndsWith("\ninspect each and remove it, then rerun", message);
    }

    [Fact]
    public void RefusesASymlinkIntoTheGitDir()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateSymbolicLink(Path.Combine(t.Sub("repo", "src"), "docs"), Path.Combine(repo, ".git", "refs"));

        var message = Refusal(repo);

        Assert.Contains("these symlinks lead into its git dir (or would, once a directory they name is made), so " +
            "git run through one would use any repo the sandbox puts there", message);
        Assert.Contains("\n  src/docs\n", message);
        Assert.EndsWith("\nremove them, then rerun", message);
    }

    /// <summary>Remote branches named refs/x and objects/y beside origin/HEAD are ref names git takes for a git dir.</summary>
    [Fact]
    public void ReposMadeByRefNames_AreRefused_WithAdviceToRenameTheRefs()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "commit", "-q", "--allow-empty", "-m", "init");
        Git.Run(repo, "update-ref", "refs/remotes/origin/main", "HEAD");
        Git.Run(repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        Git.Run(repo, "update-ref", "refs/remotes/origin/refs/x", "HEAD");
        Git.Run(repo, "update-ref", "refs/remotes/origin/objects/y", "HEAD");
        var origin = Path.Combine(repo, ".git", "refs", "remotes", "origin");
        Assert.Equal(origin, Native.RealPath(Git.Run(origin, "rev-parse", "--absolute-git-dir").TrimEnd('\n')));

        var message = Refusal(repo);

        Assert.Contains("\n  .git/refs/remotes/origin (ref names that lay it out as a git dir)\n", message);
        Assert.Contains("those in refs/ or logs/ are ref names that git takes for a repo's layout", message);
        Assert.Contains("rather than removing them, rename those branches or refs", message);
        Assert.DoesNotContain("git worktree prune", message);
    }

    [Fact]
    public void RefusesADirectoryInTheGitDirItCantList()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var shut = t.Sub("repo", ".git", "x");
        File.SetUnixFileMode(shut, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var message = Refusal(repo);
            Assert.Contains("pen can't list these directories", message);
            Assert.Contains("\n  .git/x\n", message);
        }
        finally
        {
            File.SetUnixFileMode(shut, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
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
        Directory.CreateSymbolicLink(Path.Combine(t.Sub("repo", "c"), ".git"), t.Sub("elsewhere"));
        // One into the git dir is a symlink into the git dir, which the end check removes rather than renames.
        Directory.CreateSymbolicLink(Path.Combine(t.Sub("repo", "d"), ".git"), Path.Combine(repo, ".git"));
        var scan = NestedScans.Scan(repo, Path.Combine(repo, ".git"));
        Assert.Equal(
            [("a/HEAD", NestedKind.Bare), ("b/.git", NestedKind.GitFile), ("c/.git", NestedKind.GitLink)],
            scan.Repos.Select(n => (PathChecks.FormatPath(repo, n.Path), n.Kind)));
        Assert.Equal([Path.Combine(repo, "d", ".git")], scan.GitDirLinks);
    }

    [Fact]
    public void FindsAnUppercaseGit()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateDirectory(Path.Combine(t.Sub("repo", "x"), ".GIT"));
        Assert.Single(NestedScans.Find(repo));
    }

    [Fact]
    public void DoesNotFollowSymlinkedDirectories()
    {
        using var t = new TempDir();
        var other = Git.Repo(t.Sub("other"));
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "link"), other);
        Assert.Empty(NestedScans.Find(repo));
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
        Assert.Contains("wt (a .git file that isn't one of this repo's worktrees or submodules)", Refusal(repo));
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
        Assert.False(NestedScans.IsProjectWorktree(repo, Path.Combine(repo, "wt", ".git")));
    }

    [Fact]
    public void ScanSkipsTheProjectsOwnGitDir()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "outside"));
        // .git/worktrees/outside has HEAD + commondir, but it's inside the project's .git.
        Assert.Empty(NestedScans.Find(repo));
    }

    // --- mounts ---------------------------------------------------------------

    private static List<(string, bool)> Mounts(string dir, string? home = null) =>
        RepoGuard.PrepareMounts(dir, home ?? "/nonexistent-home").Select(m => (PathChecks.FormatPath(dir, m.Path), m.ReadOnly)).ToList();

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
        Assert.True(RepoScan.IsWorktreeInside(repo, Path.Combine(repo, ".git", "worktrees", "wt")));
        // And a symlink inside the project leading out of it counts as outside.
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "out"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "to-out"), Path.Combine(t.Path, "out"));
        File.WriteAllText(Path.Combine(repo, ".git", "worktrees", "out", "gitdir"), Path.Combine(repo, "to-out", ".git") + "\n");
        Assert.False(RepoScan.IsWorktreeInside(repo, Path.Combine(repo, ".git", "worktrees", "out")));
    }

    [Fact]
    public void A_WorktreeIsClassifiedWhereItsPathLeads_WithDotDotAfterASymlinkLeavingItsTarget()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var outside = Path.Combine(t.Path, "elsewhere", "wt");
        Git.Run(repo, "worktree", "add", "-q", outside);
        // As text, repo/l/../wt/.git is inside; l leads to elsewhere/a, so the path is elsewhere/wt/.git.
        Directory.CreateSymbolicLink(Path.Combine(repo, "l"), t.Sub("elsewhere", "a"));
        var entry = Path.Combine(repo, ".git", "worktrees", "wt");
        File.WriteAllText(Path.Combine(entry, "gitdir"), Path.Combine(repo, "l", "..", "wt", ".git") + "\n");
        Assert.False(RepoScan.IsWorktreeInside(repo, entry));
        // And the other way round: through a symlink out of the project and back into it.
        Directory.CreateSymbolicLink(Path.Combine(t.Path, "elsewhere", "in"), t.Sub("repo", "sub", "deeper"));
        Directory.Move(outside, Path.Combine(repo, "sub", "wt"));
        File.WriteAllText(Path.Combine(entry, "gitdir"), Path.Combine(t.Path, "elsewhere", "in", "..", "wt", ".git") + "\n");
        Assert.True(RepoScan.IsWorktreeInside(repo, entry));
    }

    [Fact]
    public void A_WorktreeThatsGoneIsStillClassifiedByWhereItWas()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var entry = t.Sub("repo", ".git", "worktrees", "wt");
        File.WriteAllText(Path.Combine(entry, "gitdir"), Path.Combine(repo, "gone", "wt", ".git") + "\n");
        Assert.True(RepoScan.IsWorktreeInside(repo, entry));
        File.WriteAllText(Path.Combine(entry, "gitdir"), Path.Combine(t.Path, "gone", "wt", ".git") + "\n");
        Assert.False(RepoScan.IsWorktreeInside(repo, entry));
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "i0")]
    [InlineData(3, "i0|i1|i2")]
    [InlineData(4, "i0|i1|i2|… and 1 more")]
    [InlineData(6, "i0|i1|i2|… and 3 more")]
    public void ListFirst_ListsUpToMax_ThenHowManyMore(int count, string expected)
    {
        var formatted = new List<int>();
        var lines = CappedList.FormatLines(Enumerable.Range(0, count).ToList(), 3, i =>
        {
            formatted.Add(i);
            return $"i{i}";
        });
        Assert.Equal(expected, string.Join('|', lines));
        Assert.Equal(Enumerable.Range(0, Math.Min(count, 3)), formatted); // only those listed, once each, in order
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "i0")]
    [InlineData(3, "i0, i1, i2")]
    [InlineData(4, "i0, i1, i2 and 1 more")]
    [InlineData(5, "i0, i1, i2 and 2 more")]
    public void CappedListJoin_ListsUpToMax_ThenHowManyMore(int count, string expected)
    {
        var items = Enumerable.Range(0, count).Select(i => $"i{i}").ToList();
        Assert.Equal(expected, CappedList.Join(items, 3));
        Assert.Equal(expected, CappedList.Join(items, 3, item => item));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorktreeConfigsFollowRecursiveIncludes(bool linked)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "wt");
        Git.Run(repo, "config", "extensions.worktreeConfig", "true");
        var root = linked ? Path.Combine(repo, ".git", "worktrees", "wt") : Path.Combine(repo, ".git");
        File.WriteAllText(Path.Combine(root, "config.worktree"), "[include]\npath = first.inc\n");
        File.WriteAllText(Path.Combine(root, "first.inc"), "[includeIf \"onbranch:other\"]\npath = last.inc\n");
        File.WriteAllText(Path.Combine(root, "last.inc"), "");
        var mounts = RepoGuard.PrepareMounts(linked ? Path.Combine(repo, "wt") : repo, "/nonexistent-home");
        Assert.Contains(mounts, x => x.Path == Path.Combine(root, "first.inc") && x.ReadOnly);
        Assert.Contains(mounts, x => x.Path == Path.Combine(root, "last.inc") && x.ReadOnly);
    }

    [Fact]
    public void HostOnlyIncludesCanEnableWorktreeConfigAndIncludeWritableFiles()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "wt");
        var outside = Path.Combine(t.Path, "host.inc");
        File.WriteAllText(outside, $"[extensions]\nworktreeConfig = true\n[include]\npath = {repo}/inside.inc\n");
        File.WriteAllText(Path.Combine(repo, "inside.inc"), "");
        Git.Run(repo, "config", "include.path", outside);
        var m = Mounts(repo);
        Assert.Contains((".git/config.worktree", true), m);
        Assert.Contains((".git/worktrees/wt/config.worktree", true), m);
        Assert.Contains(("inside.inc", true), m);
        Assert.DoesNotContain(m, x => x.Item1 == outside);
    }

    [Fact]
    public void AnExternalWorktreeConfigCanIncludeAWritableProjectFile()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "outside"));
        File.WriteAllText(Path.Combine(repo, "shared.inc"), "");
        File.WriteAllText(Path.Combine(repo, ".git", "worktrees", "outside", "config.worktree"),
            $"[include]\npath = {repo}/shared.inc\n");
        Assert.Contains(("shared.inc", true), Mounts(repo));
    }

    [Fact]
    public void AnExternalWorktreeConfigCannotBeASymlinkToWritableConfig()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "outside"));
        var target = Path.Combine(repo, "writable.inc");
        File.WriteAllText(target, "");
        File.CreateSymbolicLink(Path.Combine(repo, ".git", "worktrees", "outside", "config.worktree"), target);
        Assert.Contains("config.worktree is a symlink", Assert.Throws<PenException>(() => Mounts(repo)).Message);
    }

    [Fact]
    public void AMissingWorktreeIncludeIsRefusedAndIncludeCyclesAreBounded()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(repo, ".git", "config.worktree"), "[include]\npath = ../missing.inc\n");
        Assert.Contains("which doesn't exist", Assert.Throws<PenException>(() => Mounts(repo)).Message);
        File.WriteAllText(Path.Combine(repo, "missing.inc"), "[include]\npath = .git/config.worktree\n");
        Assert.Contains(("missing.inc", true), Mounts(repo));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IncludedConfigCannotBeRedirectedThroughAWritableSymlink(bool parentLink)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var target = Path.Combine(t.Sub("config"), "actual.inc");
        File.WriteAllText(target, "");
        var link = Path.Combine(repo, "link");
        if (parentLink) Directory.CreateSymbolicLink(link, Path.GetDirectoryName(target)!);
        else File.CreateSymbolicLink(link, target);
        File.WriteAllText(Path.Combine(repo, ".git", "config.worktree"),
            $"[include]\npath = ../link{(parentLink ? "/actual.inc" : "")}\n");
        Assert.Contains("through a symlink", Assert.Throws<PenException>(() => Mounts(repo)).Message);
    }

    [Fact]
    public void ComposeOverrideQuotesPathsAndEscapesDollars()
    {
        var yaml = SessionCompose.GenerateOverride([new("/p/a \"b\" $HOME é/.git", false), new("/p/.pen", true)], readOnlyRoot: false, dindContainer: null, ports: null);
        Assert.Contains("\"source\": \"/p/a \\\"b\\\" $$HOME é/.git\"", yaml);
        Assert.Contains("\"read_only\": true", yaml);
        Assert.Contains("\"create_host_path\": false", yaml);
        Assert.StartsWith("{", yaml);
    }

    [Fact]
    public void RefusesMoreThanMaxListedSubmodules_WithBothAdviceLines_AndTheCountOfTheRest()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var modules = "";
        for (var i = 0; i <= RepoGuard.MaxListed; i++)
        {
            Git.Run(repo, "init", "-q", $"sub{i:00}");
            modules += $"[submodule \"s{i}\"]\n\tpath = sub{i:00}\n";
        }

        File.WriteAllText(Path.Combine(repo, ".gitmodules"), modules);
        var msg = Refusal(repo);
        Assert.Contains("(a submodule with its own .git dir)", msg);
        Assert.Contains("run git submodule absorbgitdirs here", msg);
        Assert.Contains("run pen in each repo instead", msg);
        Assert.Contains("… and 1 more", msg);
    }
}
