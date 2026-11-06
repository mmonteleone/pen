namespace Pen.Tests;

public class GitCheckTests
{
    private static List<(string From, string To)> Check(string dir, bool startedWithGit = true) =>
        GitCheck.Run(dir, startedWithGit).Select(q => (RepoGuard.Rel(dir, q.From), RepoGuard.Rel(dir, q.To))).ToList();

    [Fact]
    public void NothingFlaggedForCommitsBranchesAndWorktrees()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "commit", "-q", "--allow-empty", "-m", "two");
        Git.Run(repo, "branch", "feature");
        Git.Run(repo, "worktree", "add", "-q", ".claude/worktrees/x");
        Git.Run(repo, "stash", "list");
        Assert.Empty(Check(repo));
    }

    [Fact]
    public void NothingFlaggedInAPlainDirectory()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Sub("plain"), "file"), "");
        Assert.Empty(Check(Path.Combine(t.Path, "plain"), startedWithGit: false));
    }

    [Fact]
    public void QuarantinesCommondir()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(repo, ".git", "commondir"), "/elsewhere");
        Assert.Equal([(".git/commondir", ".git/commondir.pen-quarantine")], Check(repo));
        Assert.False(File.Exists(Path.Combine(repo, ".git", "commondir")));
        Assert.Empty(Check(repo)); // and the quarantined name isn't flagged again
    }

    [Fact]
    public void QuarantinesADanglingCommondirSymlink()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.CreateSymbolicLink(Path.Combine(repo, ".git", "commondir"), "/nonexistent");
        Assert.Equal([(".git/commondir", ".git/commondir.pen-quarantine")], Check(repo));
    }

    [Fact]
    public void QuarantinesModules()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateDirectory(Path.Combine(repo, ".git", "modules", "x"));
        Assert.Equal([(".git/modules", ".git/modules.pen-quarantine")], Check(repo));
    }

    [Fact]
    public void QuarantinesANestedRepo_AndBreaksItsLayout()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "init", "-q", "sub");
        var moved = GitCheck.Run(repo, startedWithGit: true);
        var q = Assert.Single(moved);
        Assert.Equal(Path.Combine(repo, "sub", ".git.pen-quarantine"), q.To);
        Assert.Equal((Path.Combine(q.To, "HEAD"), Path.Combine(q.To, "HEAD.pen-quarantine")), q.Inner);
        // The renamed dir no longer looks like a bare repo either.
        Assert.Empty(Check(repo));
        Assert.Empty(RepoScan.FindNested(repo));
    }

    [Fact]
    public void QuarantinesABareRepo()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "init", "-q", "--bare", "sub2");
        Assert.Equal([("sub2/HEAD", "sub2/HEAD.pen-quarantine")], Check(repo));
    }

    [Fact]
    public void QuarantinesAGitFileOrSymlink()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(t.Sub("repo", "a"), ".git"), "gitdir: /elsewhere\n");
        Directory.CreateSymbolicLink(Path.Combine(t.Sub("repo", "b"), ".git"), "/elsewhere");
        Assert.Equal([("a/.git", "a/.git.pen-quarantine"), ("b/.git", "b/.git.pen-quarantine")], Check(repo));
    }

    [Fact]
    public void QuarantinesAGitCreatedDuringTheSession()
    {
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("proj"));
        var moved = GitCheck.Run(dir, startedWithGit: false);
        Assert.Equal(Path.Combine(dir, ".git.pen-quarantine"), Assert.Single(moved).To);
        Assert.False(Directory.Exists(Path.Combine(dir, ".git")));
        Assert.Empty(RepoScan.FindNested(dir));
    }

    [Fact]
    public void PicksAFreeName()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var c = Path.Combine(repo, ".git", "commondir");
        File.WriteAllText(c, "1");
        Check(repo);
        File.WriteAllText(c, "2");
        Assert.Equal([(".git/commondir", ".git/commondir.pen-quarantine-2")], Check(repo));
        Assert.Equal("1", File.ReadAllText(c + ".pen-quarantine"));
    }

    [Fact]
    public void WarningListsEachItemAndHowToRestoreIt()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(repo, ".git", "commondir"), "x");
        Git.Run(repo, "init", "-q", "it's");
        var w = GitCheck.Warning(repo, GitCheck.Run(repo, startedWithGit: true));
        Assert.Contains("pen: WARNING", w);
        Assert.Contains("  .git/commondir → .git/commondir.pen-quarantine", w);
        Assert.Contains($"  mv '{repo}/.git/commondir.pen-quarantine' '{repo}/.git/commondir'", w);
        Assert.Contains($"  mv '{repo}/it'\\''s/.git.pen-quarantine' '{repo}/it'\\''s/.git' && mv '{repo}/it'\\''s/.git/HEAD.pen-quarantine' '{repo}/it'\\''s/.git/HEAD'", w);
    }

    [Fact]
    public void AMissingProjectIsNotAnError()
    {
        using var t = new TempDir();
        Assert.Empty(Check(Path.Combine(t.Path, "gone")));
    }
}
