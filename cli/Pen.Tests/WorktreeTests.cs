namespace Pen.Tests;

/// <summary>B1: a repo modeled apart from its dir (RepoScan.ResolveWorktree, Project's repo fields). B4: sessions in a linked worktree.</summary>
public class WorktreeTests
{
    /// <summary>A repo with a linked worktree outside it: (repo, worktree dir, the worktree's admin dir).</summary>
    private static (string Repo, string Wt, string Admin) RepoWithWorktree(TempDir t, string name = "wt")
    {
        var repo = Git.Repo(t.Sub("repo"));
        var wt = Path.Combine(t.Path, name);
        Git.Run(repo, "worktree", "add", "-q", wt);
        return (repo, wt, Path.Combine(repo, ".git", "worktrees", name));
    }

    private static string? Resolve(string wt) => RepoScan.ResolveWorktree(Path.Combine(wt, ".git"));

    [Fact]
    public void AValidWorktreeResolvesToTheCommonDir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        Assert.Equal(Path.Combine(repo, ".git"), Resolve(wt));
    }

    [Fact]
    public void AWorktreeInsideTheRepoResolves_AndIsTheProjects()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", ".claude/worktrees/x");
        var gitFile = Path.Combine(repo, ".claude", "worktrees", "x", ".git");
        Assert.Equal(Path.Combine(repo, ".git"), RepoScan.ResolveWorktree(gitFile));
        Assert.True(RepoScan.IsProjectWorktree(repo, gitFile));
        Assert.False(RepoScan.IsProjectWorktree(t.Sub("other"), gitFile));
    }

    [Fact]
    public void RelativePathsResolve()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: ../repo/.git/worktrees/wt\n");
        File.WriteAllText(Path.Combine(admin, "gitdir"), "../../../../wt/.git\n");
        Assert.Equal(Path.Combine(t.Path, "repo", ".git"), Resolve(wt));
    }

    [Fact]
    public void RefusesAGitdirWhoseParentIsntWorktrees()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        var moved = Path.Combine(repo, ".git", "elsewhere", "wt");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(admin, moved);
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {moved}\n");
        Assert.Null(Resolve(wt));
    }

    [Theory]
    [InlineData("..\n")]
    [InlineData("../../\n")]
    [InlineData("/tmp/evil\n")]
    [InlineData("")]
    public void RefusesACommondirOtherThanDotDotDotDot(string commondir)
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(admin, "commondir"), commondir);
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesAMissingCommondir()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.Delete(Path.Combine(admin, "commondir"));
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesAMissingBackPointer()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.Delete(Path.Combine(admin, "gitdir"));
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesAWrongBackPointer()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        // Another worktree's gitfile copied here: its admin dir points back to the other one.
        var other = Path.Combine(t.Path, "other");
        Git.Run(repo, "worktree", "add", "-q", other);
        File.WriteAllText(Path.Combine(admin, "gitdir"), Path.Combine(other, ".git") + "\n");
        Assert.Null(Resolve(wt));
        Assert.NotNull(Resolve(other));
    }

    [Fact]
    public void RefusesASymlinkedAdminDir()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        // The same files under another worktrees/ dir, reached through a symlink.
        var elsewhere = Path.Combine(t.Sub("evil", ".git", "worktrees"), "wt");
        Directory.Move(admin, elsewhere);
        Directory.CreateSymbolicLink(admin, elsewhere);
        Assert.Null(Resolve(wt));
        Assert.True(Directory.Exists(Path.Combine(repo, ".git", "worktrees", "wt")));
    }

    [Fact]
    public void RefusesASymlinkedWorktreesDir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var worktrees = Path.Combine(repo, ".git", "worktrees");
        var elsewhere = Path.Combine(t.Sub("evil"), "worktrees");
        Directory.Move(worktrees, elsewhere);
        Directory.CreateSymbolicLink(worktrees, elsewhere);
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesASymlinkedAncestorInTheGitdir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var link = Path.Combine(t.Path, "link");
        Directory.CreateSymbolicLink(link, repo);
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {Path.Combine(link, ".git", "worktrees", "wt")}\n");
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesSymlinkedLinkFiles()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        var gitFile = Path.Combine(wt, ".git");
        var copy = Path.Combine(t.Path, "gitfile-copy");
        File.Move(gitFile, copy);
        File.CreateSymbolicLink(gitFile, copy);
        Assert.Null(Resolve(wt));

        File.Delete(gitFile);
        File.Move(copy, gitFile);
        Assert.NotNull(Resolve(wt));
        var commondir = Path.Combine(admin, "commondir");
        File.Move(commondir, Path.Combine(t.Path, "commondir-copy"));
        File.CreateSymbolicLink(commondir, Path.Combine(t.Path, "commondir-copy"));
        Assert.Null(Resolve(wt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("gitdir: \n")]
    [InlineData("gitdir:/x/.git/worktrees/wt\n")]
    [InlineData("gitdir: a\nb\n")]
    public void RefusesMalformedGitFiles(string content)
    {
        using var t = new TempDir();
        var (_, wt, _) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(wt, ".git"), content);
        Assert.Null(Resolve(wt));
    }

    // --- Project ------------------------------------------------------------------

    [Fact]
    public void ProjectForAMainCheckout()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var p = Project.Resolve(repo, t.Host());
        Assert.True(p.IsRepo);
        Assert.False(p.IsWorktree);
        Assert.Equal(Path.Combine(repo, ".git"), p.CommonGitDir);
        Assert.Equal(repo, p.RepoDir);
    }

    [Fact]
    public void ProjectForALinkedWorktree_KeyFromTheMainCheckout()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var p = Project.Resolve(wt, t.Host());
        Assert.True(p.IsRepo);
        Assert.True(p.IsWorktree);
        Assert.Equal(Path.Combine(repo, ".git"), p.CommonGitDir);
        Assert.Equal(repo, p.RepoDir);
        Assert.Equal(wt, p.Dir);
        Assert.Equal(Project.ComputeKey(repo), p.Key);
        Assert.Equal(Project.Resolve(repo, t.Host()).Key, p.Key);
    }

    [Fact]
    public void ProjectForAPlainDirOrAForgedGitFile()
    {
        using var t = new TempDir();
        var plain = Project.Resolve(t.Sub("plain"), t.Host());
        Assert.False(plain.IsRepo);
        Assert.Null(plain.CommonGitDir);
        Assert.Equal(plain.Dir, plain.RepoDir);

        var forged = t.Sub("forged");
        File.WriteAllText(Path.Combine(forged, ".git"), "gitdir: /tmp/nowhere/.git/worktrees/x\n");
        var p = Project.Resolve(forged, t.Host());
        Assert.False(p.IsRepo);
        Assert.Equal(forged, p.RepoDir);
    }

    [Fact]
    public void GitHubOwnerOfAWorktreeComesFromTheCommonConfig()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        Git.Run(repo, "remote", "add", "origin", "git@github.com:acme/widgets.git");
        Assert.Equal("acme", GitHub.FindProjectOwner(Project.Resolve(wt, t.Host()), Config.Empty));
        Assert.Null(GitHub.FindProjectOwner(Project.Resolve(t.Sub("plain"), t.Host()), Config.Empty));
    }

    [Fact]
    public void SignRunsInAWorktree_AndRefusesAPlainDir()
    {
        using var t = new TempDir();
        var (_, wt, _) = RepoWithWorktree(t);
        var host = t.Host();
        // Past the repo checks and the locks, to the base: there's no upstream here.
        Assert.Contains("has no upstream", Assert.Throws<PenException>(() => Sign.Run(host, Project.Resolve(wt, host), null)).Message);
        Assert.Contains("isn't a git repo", Assert.Throws<PenException>(() => Sign.Run(host, Project.Resolve(t.Sub("plain"), host), null)).Message);
    }

    // --- B4: running in a linked worktree ------------------------------------------

    [Fact]
    public void CheckAtStartAcceptsAValidWorktree()
    {
        using var t = new TempDir();
        var (_, wt, _) = RepoWithWorktree(t);
        RepoGuard.CheckAtStart(wt);
    }

    [Fact]
    public void AWorktreeInsideTheMainCheckoutIsAllowed_WithTheMainKey()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", ".claude/worktrees/x");
        var wt = Path.Combine(repo, ".claude", "worktrees", "x");
        var p = Project.Resolve(wt, t.Host()); // CheckAllowed: not "inside the git repo at …"
        Assert.Equal(wt, p.Dir);
        Assert.Equal(repo, p.RepoDir);
        Assert.Equal(Project.ComputeKey(repo), p.Key);
        RepoGuard.CheckAtStart(wt);
    }

    [Fact]
    public void CheckAtStartRefusesAForgedGitFile()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(admin, "gitdir"), "/elsewhere/.git\n");
        Assert.Contains("isn't a valid linked worktree", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
    }

    [Fact]
    public void CheckAtStartRefusesAWorktreeOfABareRepo()
    {
        using var t = new TempDir();
        var source = Git.Repo(t.Sub("source"));
        var bare = Path.Combine(t.Path, "bare.git");
        Git.Run(t.Path, "clone", "-q", "--bare", source, bare);
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(bare, "worktree", "add", "-q", wt);
        Assert.Contains("bare repo", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
        Assert.Equal(wt, Project.Resolve(wt, t.Host()).RepoDir); // no main checkout: the dir stands in

        // A bare repo named .git (core.bare = true) counts as bare too.
        var dotgitParent = t.Sub("dotgit");
        var dotgit = Path.Combine(dotgitParent, ".git");
        Git.Run(t.Path, "clone", "-q", "--bare", source, dotgit);
        var wt2 = Path.Combine(t.Path, "wt2");
        Git.Run(dotgit, "worktree", "add", "-q", wt2);
        Assert.False(RepoGuard.IsMainCheckoutGitDir(dotgit));
        Assert.Contains("bare repo", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt2)).Message);
    }

    [Fact]
    public void CheckAtStartRunsTheGitDirChecksOnTheMainRepo()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var commondir = Path.Combine(repo, ".git", "commondir");
        File.WriteAllText(commondir, "/tmp/evil\n");
        Assert.Contains("commondir exists", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
        File.Delete(commondir);
        RepoGuard.CheckAtStart(wt);
    }

    [Fact]
    public void CheckAtStartScansTheWorktree_AcceptingWorktreesOfTheSameRepo()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        // A worktree of the main repo inside this worktree is the project's own.
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(wt, ".claude", "worktrees", "y"));
        RepoGuard.CheckAtStart(wt);

        Git.Run(wt, "init", "-q", "libs/sub");
        Assert.Contains("libs/sub", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
    }

    private static List<(string, bool)> Mounts(string dir) =>
        RepoGuard.PrepareMounts(dir, "/nonexistent-home").Select(m => (m.Path, m.ReadOnly)).ToList();

    [Fact]
    public void MountsForAWorktree()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "sibling"));
        var git = Path.Combine(repo, ".git");
        var m = Mounts(wt);
        Assert.Equal(
            [
                (git, false), // the common dir, read-write: objects, refs, this worktree's entry
                (Path.Combine(git, "config"), true),
                (Path.Combine(git, "hooks"), true),
                (Path.Combine(git, "worktrees"), false),
                (Path.Combine(git, "worktrees", "sibling"), true), // not visible in the sandbox
                (Path.Combine(admin, "commondir"), true), // this worktree's entry: writable but for commondir
                (Path.Combine(git, "HEAD"), true), // the main checkout's, which isn't visible either
                (Path.Combine(git, "index"), true),
                (Path.Combine(wt, ".git"), true), // the gitfile
                (Path.Combine(wt, ".pen"), true),
            ],
            m);
        Assert.DoesNotContain(m, x => x.Item1 == repo); // the main checkout's files aren't mounted
        Assert.True(Directory.Exists(Path.Combine(wt, ".pen")));
        Assert.False(Directory.Exists(Path.Combine(repo, ".pen")));
    }

    [Fact]
    public void MountsForAWorktree_GoToDindToo()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var yaml = SessionCompose.GenerateOverride(RepoGuard.PrepareMounts(wt, "/nonexistent-home"), dindContainer: "x-dind");
        var dind = yaml[yaml.IndexOf("\"dind\"", StringComparison.Ordinal)..];
        Assert.Contains($"\"source\": \"{Path.Combine(repo, ".git")}\"", dind);
        Assert.Contains($"\"source\": \"{Path.Combine(wt, ".git")}\"", dind);
    }

    [Fact]
    public void MountsCreateTheMainReposHooksWhenMissing()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var hooks = Path.Combine(repo, ".git", "hooks");
        Directory.Delete(hooks, recursive: true);
        Assert.Contains((hooks, true), Mounts(wt));
        Assert.True(Directory.Exists(hooks));
    }

    [Fact]
    public void EndingAWorktreeSessionDoesntQuarantineItsGitFile()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var common = Path.Combine(repo, ".git");
        Git.Run(wt, "commit", "-q", "--allow-empty", "-m", "in wt");
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(wt, ".claude", "worktrees", "y"));
        Assert.Empty(GitCheck.Run(wt, startedWithGit: true, common, modulesAtStart: null));
        Assert.True(File.Exists(Path.Combine(wt, ".git")));
        // Without the common dir, the inner worktree would look foreign.
        Assert.Single(RepoScan.FindNested(wt));
    }

    [Fact]
    public void TheEndCheckRunsTheGitDirChecksOnTheCommonDir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var common = Path.Combine(repo, ".git");
        File.WriteAllText(Path.Combine(common, "commondir"), "x\n");
        var m = Directory.CreateDirectory(Path.Combine(common, "modules", "m")).FullName;
        Git.Run(m, "init", "-q", "--bare");
        Git.Run(wt, "init", "-q", "sub");
        var moved = GitCheck.Run(wt, startedWithGit: true, common, modulesAtStart: []).Select(q => q.From).ToList();
        Assert.Equal([Path.Combine(common, "commondir"), Path.Combine(common, "modules", "m"), Path.Combine(wt, "sub", ".git")], moved);
        Assert.True(File.Exists(Path.Combine(wt, ".git")));
    }

    [Fact]
    public void SessionDirSavesTheCommonDir()
    {
        using var t = new TempDir();
        var s = new SessionDir("pen-x-00000000", t.Sub("s"));
        Assert.Equal((false, null), s.ReadGit());
        File.WriteAllText(s.StartedWithGit, ""); // written by an older Pen
        Assert.Equal((true, null), s.ReadGit());
        File.WriteAllText(s.StartedWithGit, "/r/.git\n");
        Assert.Equal((true, "/r/.git"), s.ReadGit());
    }

    [Fact]
    public void ProjectLayerNamePerDir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var main = Project.Resolve(repo, t.Host());
        var w = Project.Resolve(wt, t.Host());
        Assert.Equal($"{main.Key}:latest", Image.GetProjectLayerName(main));
        Assert.Equal($"{main.Key}-{Project.HashPath(wt)}:latest", Image.GetProjectLayerName(w));
        Assert.Equal($"{Project.ComputeKey(t.Sub("plain"))}:latest", Image.GetProjectLayerName(Project.Resolve(t.Sub("plain"), t.Host())));
    }

    [Fact]
    public void ProjectLayerNameOnAToolsImage()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var main = Project.Resolve(repo, t.Host());
        var w = Project.Resolve(wt, t.Host());
        const string one = "pen-tools:0123456789abcdef", other = "pen-tools:fedcba9876543210";
        Assert.Equal($"{main.Key}:0123456789abcdef", Image.GetProjectLayerName(main, one));
        Assert.Equal($"{main.Key}-{Project.HashPath(wt)}:0123456789abcdef", Image.GetProjectLayerName(w, one));
        Assert.Equal($"{main.Key}:fedcba9876543210", Image.GetProjectLayerName(main, other));
        Assert.Equal($"{main.Key}:latest", Image.GetProjectLayerName(main, Image.Name));
        Assert.True(PruneCommand.IsImageOf(main.Key, Image.GetProjectLayerName(main, one)));
        Assert.True(PruneCommand.IsImageOf(main.Key, Image.GetProjectLayerName(w, one)));
    }
}
