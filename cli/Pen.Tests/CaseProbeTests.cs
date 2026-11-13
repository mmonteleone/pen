namespace Pen.Tests;

public class CaseProbeTests
{
    private static string[] Listing(string dir) =>
        [.. Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal)];

    /// <summary>Whether this file system folds case, found without the code under test.</summary>
    private static bool Folds(string dir)
    {
        var f = Path.Combine(dir, "folds-check");
        File.WriteAllText(f, "");
        try { return File.Exists(Path.Combine(dir, "FOLDS-CHECK")); }
        finally { File.Delete(f); }
    }

    private static string GitDir(TempDir t) => Path.Combine(Git.Repo(t.Sub("repo")), ".git");

    [Fact]
    public void TheProbeTellsWhatThisFileSystemDoes()
    {
        using var t = new TempDir();
        var git = GitDir(t);
        Assert.Equal(Folds(git), RepoGuard.MayFoldCase(git));
    }

    [Fact]
    public void TheProbeLeavesNothingBehind()
    {
        using var t = new TempDir();
        var git = GitDir(t);
        var before = Listing(git);
        RepoGuard.MayFoldCase(git);
        Assert.Equal(before, Listing(git));
    }

    [Fact]
    public void AGitDirThatCantBeWrittenCountsAsFolding()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root writes to any directory");
        using var t = new TempDir();
        var git = GitDir(t);
        var before = Listing(git);
        File.SetUnixFileMode(git, (UnixFileMode)0b101_101_101);
        try
        {
            Assert.True(RepoGuard.MayFoldCase(git));
            Assert.Equal(before, Listing(git));
        }
        finally { File.SetUnixFileMode(git, (UnixFileMode)0b111_101_101); }
    }

    [Fact]
    public void AMissingGitDirCountsAsFolding()
    {
        using var t = new TempDir();
        Assert.True(RepoGuard.MayFoldCase(Path.Combine(t.Path, "gone")));
    }

    [Fact]
    public void AFileAtTheUpperCaseNameIsLeftAlone()
    {
        using var t = new TempDir();
        var git = GitDir(t);
        var name = RepoGuard.GenerateCaseProbeName();
        var upper = Path.Combine(git, name.ToUpperInvariant());
        File.WriteAllText(upper, "someone else's");
        var before = Listing(git);

        Assert.True(RepoGuard.MayFoldCase(git, name));

        Assert.Equal("someone else's", File.ReadAllText(upper));
        Assert.Equal(before, Listing(git));
    }

    [Fact]
    public void ADirectoryAtEitherNameIsLeftAlone()
    {
        using var t = new TempDir();
        var git = GitDir(t);
        foreach (var upper in new[] { false, true })
        {
            var name = RepoGuard.GenerateCaseProbeName();
            var taken = Path.Combine(git, upper ? name.ToUpperInvariant() : name);
            var inside = Path.Combine(taken, "inside");
            Directory.CreateDirectory(inside);

            Assert.True(RepoGuard.MayFoldCase(git, name));

            Assert.True(Directory.Exists(inside));
            Directory.Delete(taken, recursive: true);
        }
    }

    [Fact]
    public void ASymlinkAtEitherNameIsntFollowedOrRemoved()
    {
        using var t = new TempDir();
        var git = GitDir(t);
        var target = Path.Combine(t.Path, "target");
        foreach (var upper in new[] { false, true })
        {
            var name = RepoGuard.GenerateCaseProbeName();
            var link = Path.Combine(git, upper ? name.ToUpperInvariant() : name);
            File.CreateSymbolicLink(link, target); // dangling, so only lstat sees it

            Assert.True(RepoGuard.MayFoldCase(git, name));

            Assert.True(RepoGuard.IsLink(link));
            Assert.False(File.Exists(target));
            File.Delete(link);
        }
    }

    [Fact]
    public void TheNameIsRandomAndGitTakesItForNothing()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        var name = RepoGuard.GenerateCaseProbeName();
        Assert.NotEqual(name, RepoGuard.GenerateCaseProbeName());
        Assert.NotEqual(name, name.ToUpperInvariant());
        Assert.StartsWith(".", name);

        var refsBefore = Git.Run(repo, "show-ref", "--head");
        foreach (var form in new[] { name, name.ToUpperInvariant() })
        {
            File.WriteAllText(Path.Combine(git, form), Git.Run(repo, "rev-parse", "HEAD"));
            Assert.Throws<InvalidOperationException>(void () => Git.Run(repo, "rev-parse", "--verify", "-q", form));
            Assert.Equal(refsBefore, Git.Run(repo, "show-ref", "--head"));
            File.Delete(Path.Combine(git, form));
        }
        RepoGuard.CheckAtStart(repo);
    }

    [Fact]
    public void APlainDirectoryIsntProbedAndGetsNoWarning()
    {
        using var t = new TempDir();
        var dir = t.Sub("plain");
        var project = Project.Resolve(dir, t.Host());
        Assert.False(project.IsRepo);
        var warnings = new List<string>();

        RepoGuard.WarnIfCaseMayFold(project, warnings.Add, _ => throw new InvalidOperationException("probed"));

        Assert.Empty(warnings);
        Assert.Empty(Listing(dir));
    }

    [Fact]
    public void ALinkedWorktreeProbesTheCommonGitDirAndWarnsOnce()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        var project = Project.Resolve(wt, t.Host());
        Assert.True(project.IsWorktree);
        var probed = new List<string>();
        var warnings = new List<string>();

        RepoGuard.WarnIfCaseMayFold(project, warnings.Add, d => { probed.Add(d); return true; });

        Assert.Equal([Path.Combine(repo, ".git")], probed);
        Assert.Equal([RepoGuard.CaseWarning], warnings);
    }

    [Fact]
    public void AMainCheckoutProbesItsGitDirAndACleanAnswerGivesNoWarning()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var project = Project.Resolve(repo, t.Host());
        var probed = new List<string>();
        var warnings = new List<string>();

        RepoGuard.WarnIfCaseMayFold(project, warnings.Add, d => { probed.Add(d); return false; });

        Assert.Equal([Path.Combine(repo, ".git")], probed);
        Assert.Empty(warnings);
    }
}
