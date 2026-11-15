using System.Diagnostics;

namespace Pen.Tests;

/// <summary>
/// An entry whose name isn't valid UTF-8 can't be looked inside through .NET's
/// listing, while host git can enter it and run what was planted there. Scans
/// refuse it at the start and report "couldn't check" at the end. The bad names
/// need a file system that takes them; these tests skip on one that doesn't (APFS).
/// </summary>
public class UndecodableNameTests
{
    /// <summary>Runs a script in <paramref name="dir"/>, with $b as the bad name; skips if the file system refuses that name.</summary>
    private static void Plant(string dir, string script, string badName = "bad\\377dir")
    {
        var psi = new ProcessStartInfo("sh", ["-c", $"b=$(printf '{badName}') && {script}"])
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var r = Process.RunAndCaptureText(psi);
        Assert.SkipWhen(r.ExitStatus.ExitCode != 0 && r.StandardError.Contains("llegal byte sequence", StringComparison.Ordinal), "the file system refuses names that aren't valid UTF-8");
        Assert.True(r.ExitStatus.ExitCode == 0, r.StandardError);
    }

    /// <summary>The attack: a repo in a badly named directory, whose config runs a command when git looks at it.</summary>
    private const string PlantRepo =
        "mkdir \"$b\" && git init -q \"$b\" && git -C \"$b\" config core.fsmonitor 'touch /tmp/CANARY_RAN; false'";

    private static string FreshRepo(TempDir t) => Git.Repo(t.Sub("repo"));

    private static void AssertCouldNotCheck(CheckResult check)
    {
        Assert.Empty(check.Moved);
        Assert.NotNull(check.Failure);
        Assert.Contains("valid UTF-8", check.Failure);
        Assert.Contains("bad\\377dir", check.Failure);
    }

    [Fact]
    public void ARepoInABadlyNamedDirectory_IsRefused_AndCouldntBeCheckedAtTheEnd()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        Directory.CreateDirectory(Path.Combine(repo, "sub"));
        Plant(Path.Combine(repo, "sub"), PlantRepo);

        var e = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        Assert.Contains(@"sub/bad\377dir", e.Message);
        Assert.Contains("rename or remove", e.Message);

        var check = GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);
        AssertCouldNotCheck(check);
        Assert.Contains("don't run git here", GitCheck.NotCheckedWarning(repo, check.Failure!));
    }

    [Fact]
    public void ARealEntryWithTheDecodedName_DoesntHideTheBadOne()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        Directory.CreateDirectory(Path.Combine(repo, "bad�dir"));
        Plant(repo, PlantRepo);
        var e = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        Assert.Contains(@"bad\377dir", e.Message);
    }

    [Fact]
    public void ABadlyNamedSymlinkIsRefused_SinceItsTargetCantBeChecked()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        Plant(repo, "ln -s /nowhere \"$b\"");
        Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
    }

    [Fact]
    public void ABadlyNamedPlainFileIsAllowed_SinceItCantHoldARepo()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        Plant(repo, "echo x > \"$b\"", "caf\\351.txt");

        RepoGuard.CheckAtStart(repo);
        var check = GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);
        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
    }

    [Fact]
    public void AGoodEntryNextToABadlyNamedFile_IsStillScanned()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        Plant(repo, "echo x > \"$b\"", "caf\\351.txt");
        var nested = Git.Repo(Path.Combine(repo, "nested"));
        Assert.Equal(Path.Combine(nested, ".git"), Assert.Single(RepoScan.FindNested(repo)).Path);
    }

    [Fact]
    public void ABadNameTooLongOnceDecoded_IsStillNamed()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        var name = string.Concat(Enumerable.Repeat("\\377", 200));
        Plant(repo, "mkdir \"$b\"", name);
        var e = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        Assert.Contains(name, e.Message);
    }

    [Fact]
    public void AGenuineReplacementCharacterInANameIsFine_AndWhatsInsideIsScanned()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        var dir = Path.Combine(repo, "real�name");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plain�.txt"), "x");

        RepoGuard.CheckAtStart(repo);
        Assert.Null(GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Failure);

        var nested = Git.Repo(Path.Combine(dir, "nested"));
        Assert.Equal(Path.Combine(nested, ".git"), Assert.Single(RepoScan.FindNested(repo)).Path);
    }

    [Theory]
    [InlineData("modules")]
    [InlineData("modules/level")]
    [InlineData("worktrees")]
    public void ABadlyNamedEntryWhereGitKeepsRepos_IsRefused_AndCouldntBeCheckedAtTheEnd(string where)
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        var parent = Path.Combine(repo, ".git", where);
        Directory.CreateDirectory(parent);
        Plant(parent, PlantRepo);

        var e = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        Assert.Contains(@"bad\377dir", e.Message);
        AssertCouldNotCheck(GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null));
    }

    [Fact]
    public void AGoodModuleRepoNextToABadName_IsStillFound()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        var modules = Path.Combine(repo, ".git", "modules");
        Directory.CreateDirectory(modules);
        Plant(modules, "mkdir \"$b\"");
        var good = Path.Combine(modules, "zgood");
        Git.Run(repo, "init", "-q", "--bare", good);
        var scan = RepoScan.ScanModules(Path.Combine(repo, ".git"));
        Assert.Equal([good], scan.Repos);
        Assert.Empty(scan.UnreadableDirs);
        Assert.Single(scan.BadNames);
    }

    [Fact]
    public void ALeadingDashIsEscaped_SoPrintfDoesntTakeItForAnOption()
    {
        Assert.Equal(@"\055x-y\377", new UndecodableEntry([(byte)'-', (byte)'x', (byte)'-', (byte)'y', 0xff], true).EscapedName);

        using var t = new TempDir();
        var repo = FreshRepo(t);
        Plant(repo, "mkdir -- \"$b\"", @"\055x\377");
        var e = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        Assert.Contains(@"\055x\377", e.Message);

        // The printed remedy works.
        Plant(repo, "mv -- \"$b\" renamed", @"\055x\377");
        RepoGuard.CheckAtStart(repo);
    }

    [Fact]
    public void ANameEndingInANewlineGetsNoCommand_ThatCouldntNameIt()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        // The _ keeps the newline through this script's own command substitution.
        Plant(repo, "b=${b%_} && mkdir \"$b\"", @"trail\377\012_");
        var e = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        Assert.Contains(@"trail\377\012 (it ends in a newline", e.Message);
        Assert.Contains("rename it by hand", e.Message);
        Assert.DoesNotContain("printf", e.Message);
        Assert.Contains("rename it by hand", GitCheck.RunWithoutOpening(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Failure);
    }

    [Fact]
    public void OnlyANameUnderTheGitDirGetsTheAdviceToPreferRemoving()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        Plant(repo, "mkdir \"$b\"");
        Assert.DoesNotContain("prefer removing", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message);

        var modules = Path.Combine(repo, ".git", "modules");
        Directory.CreateDirectory(modules);
        Plant(modules, "mkdir \"$b\"");
        var e = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        Assert.Contains("inspect any under .git first and prefer removing", e.Message);
    }

    [Fact]
    public void TheSessionEndCheckReportsItToo()
    {
        using var t = new TempDir();
        var repo = FreshRepo(t);
        Plant(repo, PlantRepo);
        var session = new SessionDir("pen-test-12345678", t.Sub("runtime", "sessions", "12345678"));
        File.WriteAllText(session.StartedWithGit, Path.Combine(repo, ".git") + "\n");
        File.WriteAllText(session.ModulesFile, "");
        AssertCouldNotCheck(GitCheck.RunAtSessionEnd(repo, session));
    }
}
