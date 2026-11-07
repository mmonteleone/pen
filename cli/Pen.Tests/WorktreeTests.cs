namespace Pen.Tests;

/// <summary>B1: a repo modeled apart from its dir (RepoScan.ResolveWorktree, Project's repo fields).</summary>
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
    public void ProjectForALinkedWorktree_KeyStillFromItsOwnDir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var p = Project.Resolve(wt, t.Host());
        Assert.True(p.IsRepo);
        Assert.True(p.IsWorktree);
        Assert.Equal(Path.Combine(repo, ".git"), p.CommonGitDir);
        Assert.Equal(repo, p.RepoDir);
        Assert.Equal(wt, p.Dir);
        Assert.Equal(Project.KeyFor(wt), p.Key); // no behavior change yet (B4 keys by RepoDir)
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
        Assert.Equal("acme", GitHub.ProjectOwner(Project.Resolve(wt, t.Host()), Config.Empty));
        Assert.Null(GitHub.ProjectOwner(Project.Resolve(t.Sub("plain"), t.Host()), Config.Empty));
    }

    [Fact]
    public void SignStillRefusesAWorktree_AndAPlainDir()
    {
        using var t = new TempDir();
        var (_, wt, _) = RepoWithWorktree(t);
        var host = t.Host();
        Assert.Contains("linked worktree", Assert.Throws<PenException>(() => Sign.Run(host, Project.Resolve(wt, host), null)).Message);
        Assert.Contains("isn't a git repo", Assert.Throws<PenException>(() => Sign.Run(host, Project.Resolve(t.Sub("plain"), host), null)).Message);
    }

    [Fact]
    public void CheckAtStartStillRefusesAValidWorktree()
    {
        using var t = new TempDir();
        var (_, wt, _) = RepoWithWorktree(t);
        Assert.Contains("linked worktree", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
    }
}
