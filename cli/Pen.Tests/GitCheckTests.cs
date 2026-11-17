namespace Pen.Tests;

public class GitCheckTests
{
    private static List<(string From, string To)> Check(string dir, bool startedWithGit = true) =>
        GitCheck.RunWithoutSession(dir, startedWithGit, commonGitDir: null, modulesAtStart: null).Moved.Select(q => (PathChecks.FormatPath(dir, q.From), PathChecks.FormatPath(dir, q.To))).ToList();

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
    public void QuarantinesAModuleRepoMadeDuringTheSession()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var x = t.Sub("repo", ".git", "modules", "x");
        Git.Run(x, "init", "-q", "--bare");
        var moved = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: []).Moved;
        Assert.Equal([(".git/modules/x", ".git/modules/x.pen-quarantine")], moved.Select(q => (PathChecks.FormatPath(repo, q.From), PathChecks.FormatPath(repo, q.To))));
        Assert.Equal((".git/modules/x.pen-quarantine/HEAD", ".git/modules/x.pen-quarantine/HEAD.pen-quarantine"),
            (PathChecks.FormatPath(repo, moved[0].Inner!.Value.From), PathChecks.FormatPath(repo, moved[0].Inner!.Value.To)));
        // An empty directory in modules/ isn't a repo, and isn't flagged.
        Directory.CreateDirectory(Path.Combine(repo, ".git", "modules", "empty"));
        Assert.Empty(GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: []).Moved);
    }

    [Fact]
    public void QuarantinesANestedRepo_AndBreaksItsLayout()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "init", "-q", "sub");
        var moved = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved;
        var q = Assert.Single(moved);
        Assert.Equal(Path.Combine(repo, "sub", ".git.pen-quarantine"), q.To);
        Assert.Equal((Path.Combine(q.To, "HEAD"), Path.Combine(q.To, "HEAD.pen-quarantine")), q.Inner);
        // The renamed dir no longer looks like a bare repo either.
        Assert.Empty(Check(repo));
        Assert.Empty(NestedScans.Find(repo));
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
        var moved = GitCheck.RunWithoutSession(dir, startedWithGit: false, commonGitDir: null, modulesAtStart: null).Moved;
        Assert.Equal(Path.Combine(dir, ".git.pen-quarantine"), Assert.Single(moved).To);
        Assert.False(Directory.Exists(Path.Combine(dir, ".git")));
        Assert.Empty(NestedScans.Find(dir));
    }

    /// <summary>Only a session's end treats a dir that's gone as a failure.</summary>
    [Fact]
    public void WithoutASession_ADirThatsGone_FindsNothing()
    {
        using var t = new TempDir();
        var check = GitCheck.RunWithoutSession(Path.Combine(t.Path, "gone"), startedWithGit: true, commonGitDir: null, modulesAtStart: null);
        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
        Assert.False(check.RestoreIncomplete);
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
        var w = GitCheckWording.Warning(repo, GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved);
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
