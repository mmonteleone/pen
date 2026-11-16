using System.Diagnostics;
using System.Text;
using Pen.Masking;

namespace Pen.Tests;

/// <summary>
/// MaskPlanner finds what hide patterns match in the project, without following symlinks or entering .git, the
/// top-level .pen or the common git dir, and checks [replace] entries, reading sources only from inside the project.
/// </summary>
public class MaskPlannerTests
{
    private static HidePattern P(string text)
    {
        Assert.Null(HidePattern.TryParse(text, out var pattern));
        return pattern!;
    }

    private static MaskPlan Plan(string dir, params string[] hide) => Plan(dir, null, hide, []);

    private static MaskPlan Plan(string dir, string? commonGitDir, string[] hide, params (string Target, string Source)[] replace) =>
        PlanProtected(dir, commonGitDir, [], hide, replace);

    private static MaskPlan PlanProtected(string dir, string? commonGitDir, string[] protectedPaths, string[] hide, params (string Target, string Source)[] replace) =>
        new MaskPlanner(dir, commonGitDir, protectedPaths, hide.Select(P).ToList(), replace.Select(r => new KeyValuePair<string, string>(r.Target, r.Source)).ToList()).Plan();

    private static MaskPlan Replace(string dir, string[] hide, params (string Target, string Source)[] replace) => Plan(dir, null, hide, replace);

    private static string[] Files(MaskPlan plan) => plan.HiddenFiles.Select(f => f.RelativePath).ToArray();
    private static string[] Dirs(MaskPlan plan) => plan.HiddenDirs.Select(d => d.RelativePath).ToArray();

    private static string Write(string root, string relative, string content = "secret")
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static void Link(string root, string relative, string target)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, target);
    }

    private static void Run(string command, params string[] args)
    {
        var r = Process.RunAndCaptureText(new ProcessStartInfo(command, args) { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.True(r.ExitStatus.ExitCode == 0, r.StandardError);
    }

    private static PenException Refused(Func<MaskPlan> plan) => Assert.Throws<PenException>(() => plan());

    // ---- what matches and how it's hidden ----

    [Fact]
    public void NoPatternsAndNoReplacements_PlanNothing()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");

        var plan = Plan(t.Path);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void AnAnchoredLiteral_HidesThatPathOnly_AndANamePattern_HidesItAtAnyDepth()
    {
        using var t = new TempDir();
        var env = Write(t.Path, ".env");
        Write(t.Path, "apps/web/.env");
        Write(t.Path, "apps/web/.envrc");

        var anchored = Plan(t.Path, "/.env");
        Assert.Equal([".env"], Files(anchored));
        Assert.Equal(env, anchored.HiddenFiles[0].Path);
        Assert.Empty(anchored.HiddenDirs);

        Assert.Equal([".env", "apps/web/.env"], Files(Plan(t.Path, ".env")));
        Assert.Equal(["apps/web/.env"], Files(Plan(t.Path, "apps/web/.env")));
    }

    [Fact]
    public void Globs_MatchWithinASegment_AndDoubleStarAcrossDirectories()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        Write(t.Path, ".env.local");
        Write(t.Path, "scripts/env.ps1");
        Write(t.Path, "scripts/deep/setup.PS1");
        Write(t.Path, "apps/web/config.json");
        Write(t.Path, "apps/api/config.json");
        Write(t.Path, "apps/api/sub/config.json");
        Write(t.Path, "readme.md");

        Assert.Equal([".env.local"], Files(Plan(t.Path, ".env.*")));
        Assert.Equal(["scripts/deep/setup.PS1", "scripts/env.ps1"], Files(Plan(t.Path, "*.ps1")));
        Assert.Equal(["apps/api/config.json", "apps/web/config.json"], Files(Plan(t.Path, "apps/*/config.json")));
        Assert.Equal(["apps/api/config.json", "apps/api/sub/config.json", "apps/web/config.json"], Files(Plan(t.Path, "**/config.json")));
        // A trailing ** matches what's inside, so sub/ is hidden whole.
        var inside = Plan(t.Path, "apps/api/**");
        Assert.Equal(["apps/api/config.json"], Files(inside));
        Assert.Equal(["apps/api/sub"], Dirs(inside));
    }

    [Fact]
    public void AHiddenDirectory_IsOneMount_AndItsContentsArentLookedIn()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        Write(t.Path, "secrets/a.env");
        Write(t.Path, "secrets/deep/b.env");
        var locked = t.Sub("secrets", "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var plan = Plan(t.Path, "secrets/", "*.env");

            Assert.Equal(["secrets"], Dirs(plan));
            Assert.Equal(Path.Combine(t.Path, "secrets"), plan.HiddenDirs[0].Path);
            Assert.Equal([".env"], Files(plan));
            // Never listed, so no warning about it.
            Assert.Empty(plan.Warnings);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ADirectoryOnlyPattern_DoesntMatchAFile_AndANamePatternMatchesADirectory()
    {
        using var t = new TempDir();
        Write(t.Path, "secrets");
        Write(t.Path, "sub/secrets/key");

        var plan = Plan(t.Path, "secrets/");
        Assert.Empty(Files(plan));
        Assert.Equal(["sub/secrets"], Dirs(plan));

        var both = Plan(t.Path, "secrets");
        Assert.Equal(["secrets"], Files(both));
        Assert.Equal(["sub/secrets"], Dirs(both));
    }

    [Fact]
    public void AFifo_IsLeftAlone()
    {
        using var t = new TempDir();
        Run("mkfifo", Path.Combine(t.Path, "pipe.env"));
        Write(t.Path, "real.env");

        Assert.Equal(["real.env"], Files(Plan(t.Path, "*.env")));
    }

    // ---- symlinks ----

    [Fact]
    public void ASymlinkToAFileInTheProject_HidesTheFileItLeadsTo()
    {
        using var t = new TempDir();
        var real = Write(t.Path, "real/secret.txt");
        Link(t.Path, "link.env", "real/secret.txt");

        var plan = Plan(t.Path, "*.env");

        Assert.Equal(["real/secret.txt"], Files(plan));
        Assert.Equal(real, plan.HiddenFiles[0].Path);
    }

    [Fact]
    public void ASymlinkToADirectoryInTheProject_HidesTheDirectory_ForADirectoryOnlyPatternToo()
    {
        using var t = new TempDir();
        Write(t.Path, "vault/key");
        Link(t.Path, "secrets", "vault");

        Assert.Equal(["vault"], Dirs(Plan(t.Path, "secrets/")));
        Assert.Equal(["vault"], Dirs(Plan(t.Path, "/secrets/")));
        Assert.Equal(["vault"], Dirs(Plan(t.Path, "secrets")));
        Assert.Empty(Files(Plan(t.Path, "secrets/")));
    }

    [Fact]
    public void ADirectoryOnlyPattern_DoesntMatchASymlinkToAFile()
    {
        using var t = new TempDir();
        Write(t.Path, "vault");
        Link(t.Path, "secrets", "vault");

        Assert.True(Plan(t.Path, "secrets/").IsEmpty);
        Assert.True(Plan(t.Path, "/secrets/").IsEmpty);
    }

    [Fact]
    public void ASymlinkLeadingOutOfTheProject_OrNowhere_IsSkipped()
    {
        using var t = new TempDir();
        var project = t.Sub("project");
        var outside = Write(t.Path, "outside/secret.env");
        Link(project, "abs.env", outside);
        Link(project, "rel.env", "../outside/secret.env");
        Link(project, "dir", Path.Combine(t.Path, "outside"));
        Link(project, "dangling.env", "nothing-here");

        var plan = Plan(project, "*.env", "dir");

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void ASymlinkIntoGitPenOrTheCommonGitDir_IsSkipped()
    {
        using var t = new TempDir();
        Write(t.Path, ".git/config");
        Write(t.Path, ".pen/agent.env");
        Write(t.Path, "sub/.GIT/config");
        Write(t.Path, "gitdir/config");
        Link(t.Path, "a.env", ".git/config");
        Link(t.Path, "b.env", ".git");
        Link(t.Path, "c.env", ".pen/agent.env");
        Link(t.Path, "d.env", "sub/.GIT/config");
        Link(t.Path, "e.env", "gitdir/config");
        Link(t.Path, "f.env", "gitdir");
        Link(t.Path, "g.env", ".pen");

        var plan = Plan(t.Path, Path.Combine(t.Path, "gitdir"), ["*.env"]);

        Assert.True(plan.IsEmpty);
        // Into .pen is left alone quietly; into a git dir is said, since the user may expect it hidden.
        string[] warned = ["a.env", "b.env", "d.env", "e.env", "f.env"];
        Assert.Equal(warned.Select(link => $"couldn't hide {link}: it leads into the repo's git dir, which pen doesn't hide"), plan.Warnings.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASymlinkToANestedPen_HidesIt()
    {
        using var t = new TempDir();
        Write(t.Path, "sub/.pen/agent.env");
        Link(t.Path, "a.secret", "sub/.pen/agent.env");

        Assert.Equal(["sub/.pen/agent.env"], Files(Plan(t.Path, "*.secret")));
    }

    [Fact]
    public void ASymlinkToTheProjectDir_IsSkippedWithAWarning()
    {
        using var t = new TempDir();
        Write(t.Path, "x");
        Link(t.Path, "self", ".");
        Link(t.Path, "sub/up", "..");

        var plan = Plan(t.Path, "self", "up");

        Assert.True(plan.IsEmpty);
        Assert.Contains("self leads to the project dir itself, which can't be hidden", plan.Warnings);
        Assert.Contains("sub/up leads to the project dir itself, which can't be hidden", plan.Warnings);
    }

    [Fact]
    public void APathFoundTwice_IsOneMount()
    {
        using var t = new TempDir();
        Write(t.Path, "real.env");
        Link(t.Path, "a.env", "real.env");
        Link(t.Path, "z.env", "real.env");

        var plan = Plan(t.Path, "*.env");

        Assert.Equal(["real.env"], Files(plan));
    }

    [Theory]
    [InlineData("aaa.env", "zzz")]
    [InlineData("zzz.env", "aaa")]
    public void AFileInAHiddenDirectory_IsntMountedAgain_InWhicheverOrderTheyreFound(string link, string dir)
    {
        using var t = new TempDir();
        Write(t.Path, $"{dir}/secret.txt");
        Link(t.Path, link, $"{dir}/secret.txt");

        var plan = Plan(t.Path, "*.env", $"{dir}/");

        Assert.Empty(Files(plan));
        Assert.Equal([dir], Dirs(plan));
    }

    [Fact]
    public void ADirectoryHiddenThroughASymlink_CoversWhatsFoundInsideIt()
    {
        using var t = new TempDir();
        Write(t.Path, "vault/key.env");
        Write(t.Path, "vault/inner/more.env");
        t.Sub("vault", "inner", "deeper");
        Link(t.Path, "a-link", "vault");
        Link(t.Path, "z-link", "vault/inner");

        var plan = Plan(t.Path, "*.env", "a-link", "z-link", "deeper");

        Assert.Equal(["vault"], Dirs(plan));
        Assert.Empty(Files(plan));
    }

    // ---- places never entered ----

    [Fact]
    public void GitInAnyCaseAtAnyDepth_IsNeverEnteredOrMatched()
    {
        using var t = new TempDir();
        Write(t.Path, ".git/x.env");
        Write(t.Path, "sub/.Git/x.env");
        Write(t.Path, "sub/deeper/.GIT/x.env");
        Write(t.Path, "other/.git");
        Write(t.Path, ".gix");
        Write(t.Path, "kept.env");

        Assert.Equal(["kept.env"], Files(Plan(t.Path, "*.env")));
        var glob = Plan(t.Path, ".gi?");
        Assert.Equal([".gix"], Files(glob));
        Assert.Empty(Dirs(glob));
        Assert.True(Plan(t.Path, "sub/**/.gi?").IsEmpty);
    }

    [Fact]
    public void TheTopLevelPen_IsNeverEnteredOrMatched_ButANestedOneIs()
    {
        using var t = new TempDir();
        Write(t.Path, ".pen/a.env");
        Write(t.Path, "sub/.pen/a.env");

        Assert.Equal(["sub/.pen/a.env"], Files(Plan(t.Path, "*.env")));
        Assert.Equal(["sub/.pen"], Dirs(Plan(t.Path, ".p?n")));
        Assert.Equal(["sub/.pen"], Dirs(Plan(t.Path, "sub/.pen/")));
    }

    [Fact]
    public void TheTopLevelPen_InAnotherCase_IsNeverEntered()
    {
        using var t = new TempDir();
        Write(t.Path, ".PEN/a.env");

        Assert.True(Plan(t.Path, "*.env", ".p?n").IsEmpty);
    }

    [Fact]
    public void TheCommonGitDir_IsNeverEnteredOrMatched()
    {
        using var t = new TempDir();
        Write(t.Path, "gitdir/x.env");
        Write(t.Path, "other/x.env");

        var plan = Plan(t.Path, Path.Combine(t.Path, "gitdir"), ["*.env", "gitdir/"]);

        Assert.Equal(["other/x.env"], Files(plan));
        Assert.Empty(Dirs(plan));
    }

    // ---- warnings ----

    [Fact]
    public void AHardLinkedFile_IsHiddenWithAWarning()
    {
        using var t = new TempDir();
        var env = Write(t.Path, ".env");
        Run("ln", env, Path.Combine(t.Path, "copy.txt"));

        var plan = Plan(t.Path, ".env");

        Assert.Equal([".env"], Files(plan));
        Assert.Equal([".env has 2 hard links: the agent may read it under another name"], plan.Warnings);
    }

    [Fact]
    public void ADirectoryThatCantBeListed_IsWarnedAbout_AndTheWalkGoesOn()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root lists any directory");
        using var t = new TempDir();
        Write(t.Path, "a/locked/x.env");
        Write(t.Path, "z/y.env");
        var locked = Path.Combine(t.Path, "a", "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var plan = Plan(t.Path, "*.env");

            Assert.Equal(["z/y.env"], Files(plan));
            Assert.Equal(["couldn't look in a/locked for files to hide"], plan.Warnings);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void NamesThatArentValidUtf8_AreSkippedWithAWarning()
    {
        using var t = new TempDir();
        Write(t.Path, "ok.env");
        var psi = new ProcessStartInfo("sh", ["-c", "b=$(printf 'bad\\377') && mkdir \"$b\" && : > \"$b/x.env\" && : > \"$b.env\""])
        {
            WorkingDirectory = t.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var r = Process.RunAndCaptureText(psi);
        Assert.SkipWhen(r.ExitStatus.ExitCode != 0 && r.StandardError.Contains("llegal byte sequence", StringComparison.Ordinal), "the file system refuses names that aren't valid UTF-8");
        Assert.True(r.ExitStatus.ExitCode == 0, r.StandardError);

        var plan = Plan(t.Path, "*.env");

        Assert.Equal(["ok.env"], Files(plan));
        Assert.Contains("couldn't hide bad\uFFFD.env: its name isn't valid UTF-8", plan.Warnings);
        Assert.Contains("couldn't look in bad\uFFFD for files to hide: its name isn't valid UTF-8", plan.Warnings);
    }

    [Fact]
    public void ANameWithARealReplacementCharacter_IsHidden()
    {
        using var t = new TempDir();
        Write(t.Path, "odd\uFFFD.env");
        Write(t.Path, "odd\uFFFDdir/x.env");

        var plan = Plan(t.Path, "*.env");

        Assert.Equal(["odd\uFFFD.env", "odd\uFFFDdir/x.env"], Files(plan));
        Assert.Empty(plan.Warnings);
    }

    // ---- the literal lookup finds what the walk finds ----

    /// <summary>Plans with the literal lookup, then again with a pattern that matches nothing, which makes it walk.</summary>
    private static (MaskPlan Fast, MaskPlan Walked) PlanBothWays(string dir, params string[] literals)
    {
        Assert.All(literals, l => Assert.True(P(l).IsLiteral, l));
        return (Plan(dir, literals), Plan(dir, [.. literals, "no-such-name-*"]));
    }

    private static void AssertSame(MaskPlan fast, MaskPlan walked)
    {
        Assert.Equal(walked.HiddenFiles, fast.HiddenFiles);
        Assert.Equal(walked.HiddenDirs, fast.HiddenDirs);
    }

    [Fact]
    public void TheLiteralLookup_DoesntWalk()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root lists any directory");
        using var t = new TempDir();
        Write(t.Path, "secrets/key");
        var locked = t.Sub("elsewhere");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var (fast, walked) = PlanBothWays(t.Path, "/secrets/key");

            AssertSame(fast, walked);
            Assert.Empty(fast.Warnings);
            Assert.Equal(["couldn't look in elsewhere for files to hide"], walked.Warnings);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void TheLiteralLookup_MatchesLikeTheWalk_IgnoringCaseAndNormalization()
    {
        using var t = new TempDir();
        Write(t.Path, "Secrets/Key.env");
        Write(t.Path, "cafe\u0301/token");
        Write(t.Path, "Apps/Web/.ENV");
        Write(t.Path, "apps-other/.env");

        var (fast, walked) = PlanBothWays(t.Path, "/secrets/key.ENV", "/caf\u00e9/TOKEN", "apps/web/.env");

        AssertSame(fast, walked);
        // The on-disk spelling is the one mounted.
        Assert.Equal(["Apps/Web/.ENV", "Secrets/Key.env", "cafe\u0301/token"], Files(fast));
    }

    [Fact]
    public void TheLiteralLookup_HidesEachEntryDifferingOnlyInCase()
    {
        using var t = new TempDir();
        Assert.SkipWhen(RepoGuard.MayFoldCase(t.Path), "this file system folds case, so Secret.env and secret.env are one file");
        Write(t.Path, "Dir/Secret.env");
        Write(t.Path, "dir/secret.env");
        Write(t.Path, "dir/SECRET.ENV");

        var (fast, walked) = PlanBothWays(t.Path, "/DIR/secret.env");

        AssertSame(fast, walked);
        Assert.Equal(["Dir/Secret.env", "dir/SECRET.ENV", "dir/secret.env"], Files(fast));
    }

    [Fact]
    public void TheLiteralLookup_DoesntGoThroughASymlink_ButHidesOneAtTheEnd()
    {
        using var t = new TempDir();
        Write(t.Path, "real/secret.env");
        Write(t.Path, "real/other.env");
        Link(t.Path, "link", "real");
        Link(t.Path, "file-link", "real/other.env");

        var (fast, walked) = PlanBothWays(t.Path, "/link/secret.env", "/file-link");

        AssertSame(fast, walked);
        Assert.Equal(["real/other.env"], Files(fast));
        Assert.Empty(Dirs(fast));
        // Only the symlink on the way is warned about: one followed would hide real/secret.env, or warn of it.
        const string Warning = "couldn't hide link/secret.env: link on the way is a symlink, which pen doesn't follow; hide the path it leads to";
        Assert.Equal([Warning], fast.Warnings);
        Assert.Equal([Warning], walked.Warnings);
    }

    [Fact]
    public void ALiteralPathThroughASymlinkLeadingOutOfTheProject_OrNowhere_IsntWarnedAbout()
    {
        using var t = new TempDir();
        var project = t.Sub("project");
        Write(t.Path, "outside/secret.env");
        Link(project, "out", Path.Combine(t.Path, "outside"));
        Link(project, "nowhere", "missing");

        var (fast, walked) = PlanBothWays(project, "/out/secret.env", "/nowhere/secret.env");

        Assert.True(fast.IsEmpty);
        Assert.Empty(fast.Warnings);
        Assert.Empty(walked.Warnings);
    }

    [Fact]
    public void TheLiteralLookup_StopsAtAHiddenDirectory_AsTheWalkDoes()
    {
        using var t = new TempDir();
        Write(t.Path, "a/b/secret.env");
        Write(t.Path, "outside.env");
        // Inside the hidden a/, so the walk never sees it, and what it leads to stays visible.
        Link(t.Path, "a/b/link", "../../outside.env");

        var (fast, walked) = PlanBothWays(t.Path, "/a", "/a/b/secret.env", "/a/b/link");

        AssertSame(fast, walked);
        Assert.Equal(["a"], Dirs(fast));
        Assert.Empty(Files(fast));
    }

    [Fact]
    public void TheLiteralLookup_NeverEntersGitOrPen_OrTheCommonGitDir()
    {
        using var t = new TempDir();
        Write(t.Path, ".git/x/key");
        Write(t.Path, "sub/.GIT/key");
        Write(t.Path, "sub/.pen/key");
        Write(t.Path, "gitdir/key");

        var fast = Plan(t.Path, Path.Combine(t.Path, "gitdir"), ["/sub/.pen/key", "/gitdir/key", "/gitdir"]);
        var walked = Plan(t.Path, Path.Combine(t.Path, "gitdir"), ["/sub/.pen/key", "/gitdir/key", "/gitdir", "nothing-*"]);

        AssertSame(fast, walked);
        Assert.Equal(["sub/.pen/key"], Files(fast));
        Assert.Empty(Dirs(fast));
    }

    [Fact]
    public void TheLiteralLookup_AndTheWalk_AgreeOnAMixedTree()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        Write(t.Path, "apps/web/.env");
        Write(t.Path, "apps/web/.env.local");
        Write(t.Path, "secrets/key");
        Write(t.Path, "secrets/inner/key");
        Write(t.Path, "vault/token");
        Link(t.Path, "apps/vault-link", "../vault");
        Link(t.Path, "apps/key-link", "../secrets/inner/key");
        Run("mkfifo", Path.Combine(t.Path, "apps", "pipe"));

        var (fast, walked) = PlanBothWays(t.Path,
            "/.env", "/apps/web/.ENV", "/secrets/inner/key", "/secrets/", "/apps/vault-link/", "/apps/key-link", "/apps/pipe", "/missing/file");

        AssertSame(fast, walked);
        Assert.Equal([".env", "apps/web/.env"], Files(fast));
        Assert.Equal(["secrets", "vault"], Dirs(fast));
        Assert.Empty(fast.Warnings);
    }

    // ---- [replace] ----

    [Fact]
    public void AReplacement_KeepsTheSourcesBytes_AndItsModeWithoutWriteBits()
    {
        using var t = new TempDir();
        var env = Write(t.Path, ".env", "KEY=real");
        var source = Write(t.Path, ".pen/agent.env", "KEY=");
        File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);

        var plan = Replace(t.Path, [], (".env", ".pen/agent.env"));

        var replacement = Assert.Single(plan.Replacements);
        Assert.Equal(env, replacement.Target.Path);
        Assert.Equal(".env", replacement.Target.RelativePath);
        Assert.Equal(".pen/agent.env", replacement.Source);
        Assert.Equal("KEY="u8.ToArray(), replacement.Bytes);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead, replacement.Mode);
        Assert.Empty(plan.HiddenFiles);
        Assert.Equal(1, plan.MountCount);
        Assert.Equal("KEY=real", File.ReadAllText(env));
    }

    [Fact]
    public void AReplacement_WinsOverHidingTheSameFile()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        Write(t.Path, "apps/.env");
        Write(t.Path, ".pen/agent.env");
        Link(t.Path, "env-link", ".env");

        var plan = Replace(t.Path, [".env", "env-link"], (".env", ".pen/agent.env"));

        Assert.Equal(["apps/.env"], Files(plan));
        Assert.Equal(".env", Assert.Single(plan.Replacements).Target.RelativePath);
    }

    [Fact]
    public void AReplacementTarget_InAnotherCase_IsTheFileOnDisk_WhereTheFileSystemFoldsCase()
    {
        using var t = new TempDir();
        Assert.SkipUnless(RepoGuard.MayFoldCase(t.Path), "this file system tells case apart");
        var env = Write(t.Path, ".env");
        Write(t.Path, "agent.env");

        var plan = Replace(t.Path, [".env"], (".ENV", "agent.env"));

        Assert.Equal(env, Assert.Single(plan.Replacements).Target.Path);
        Assert.Empty(plan.HiddenFiles);
    }

    [Fact]
    public void TwoReplacementsOfOneFile_AreRefused()
    {
        using var t = new TempDir();
        Write(t.Path, "caf\u00e9.env");
        Write(t.Path, "agent.env");
        Assert.SkipWhen(Native.Stat(Path.Combine(t.Path, "cafe\u0301.env"), follow: false) == null, "this file system tells normalization forms apart");

        var e = Refused(() => Replace(t.Path, [], ("caf\u00e9.env", "agent.env"), ("cafe\u0301.env", "agent.env")));

        Assert.Equal("replace: \"caf\u00e9.env\" and \"cafe\u0301.env\" are the same file", e.Message);
    }

    [Theory]
    [InlineData("link.env", "a symlink")]
    [InlineData("linkdir/agent.env", "isn't a directory (a symlink, say)")]
    [InlineData("outdir/agent.env", "isn't a directory (a symlink, say)")]
    [InlineData("dir", "isn't a regular file")]
    [InlineData("pipe", "isn't a regular file")]
    [InlineData("big", "over 1 MB")]
    [InlineData("missing", "doesn't exist")]
    public void ASourceThatIsntARegularFileReachedWithoutSymlinks_IsRefused(string source, string why)
    {
        using var t = new TempDir();
        var project = t.Sub("project");
        Write(project, ".env");
        Write(project, "real/agent.env");
        Write(t.Path, "outside/agent.env");
        Link(project, "link.env", "real/agent.env");
        Link(project, "linkdir", "real");
        Link(project, "outdir", Path.Combine(t.Path, "outside"));
        Directory.CreateDirectory(Path.Combine(project, "dir"));
        Run("mkfifo", Path.Combine(project, "pipe"));
        File.WriteAllBytes(Path.Combine(project, "big"), new byte[1024 * 1024 + 1]);

        var e = Refused(() => Replace(project, [], (".env", source)));

        Assert.StartsWith($"replace: source \"{source}\" (for \".env\") can't be used: ", e.Message);
        Assert.Contains(why, e.Message);
    }

    [Fact]
    public void ASourceOfExactly1MB_IsRead()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        File.WriteAllBytes(Path.Combine(t.Path, "big"), new byte[1024 * 1024]);

        Assert.Equal(1024 * 1024, Assert.Single(Replace(t.Path, [], (".env", "big")).Replacements).Bytes.Length);
    }

    [Theory]
    [InlineData("missing.env", "doesn't exist")]
    [InlineData("nodir/x.env", "doesn't exist")]
    [InlineData("link.env", "link.env is a symlink")]
    [InlineData("linkdir/real.env", "linkdir is a symlink")]
    [InlineData("gitlink/config", "gitlink is a symlink")]
    [InlineData("dir", "isn't a regular file")]
    [InlineData("pipe", "isn't a regular file")]
    public void ATargetThatIsntARegularFileReachedWithoutSymlinks_IsRefused(string target, string why)
    {
        using var t = new TempDir();
        Write(t.Path, "agent.env");
        Write(t.Path, "real/real.env");
        Write(t.Path, ".git/config");
        Link(t.Path, "link.env", "real/real.env");
        Link(t.Path, "linkdir", "real");
        Link(t.Path, "gitlink", ".git");
        Directory.CreateDirectory(Path.Combine(t.Path, "dir"));
        Run("mkfifo", Path.Combine(t.Path, "pipe"));

        var e = Refused(() => Replace(t.Path, [], (target, "agent.env")));

        Assert.StartsWith($"replace: target \"{target}\"", e.Message);
        Assert.Contains(why, e.Message);
        Assert.False(File.Exists(Path.Combine(t.Path, "missing.env")));
    }

    [Theory]
    [InlineData(".git/config")]
    [InlineData("sub/.GIT/config")]
    [InlineData(".pen/config.toml")]
    [InlineData(".PEN/config.toml")]
    [InlineData("a/../.env")]
    public void ATargetInGitOrPen_IsRefused(string target)
    {
        using var t = new TempDir();
        Write(t.Path, ".git/config");
        Write(t.Path, "sub/.git/config");
        Write(t.Path, ".pen/config.toml");
        Write(t.Path, ".env");
        Write(t.Path, "a/x");
        Write(t.Path, "agent.env");

        var e = Refused(() => Replace(t.Path, [], (target, "agent.env")));

        Assert.Equal($"replace: target \"{target}\" must be a file in the project, outside .git and .pen", e.Message);
    }

    [Fact]
    public void ATargetInTheCommonGitDir_IsRefused()
    {
        using var t = new TempDir();
        Write(t.Path, "gitdir/config");
        Write(t.Path, "agent.env");

        var e = Refused(() => Plan(t.Path, Path.Combine(t.Path, "gitdir"), [], ("gitdir/config", "agent.env")));

        Assert.Equal("replace: target \"gitdir/config\" must be a file in the project, outside .git and .pen", e.Message);
    }

    [Fact]
    public void ASourceInGit_IsRefused()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        Write(t.Path, ".git/config");

        var e = Refused(() => Replace(t.Path, [], (".env", ".git/config")));

        Assert.Equal("replace: source \".git/config\" (for \".env\") can't be in .git", e.Message);
    }

    [Fact]
    public void ASourceInPen_IsRead()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        Write(t.Path, ".pen/agent.env", "x");

        Assert.Single(Replace(t.Path, [], (".env", ".pen/agent.env")).Replacements);
    }

    [Theory]
    [InlineData("secrets/")]
    [InlineData("vault-link")]
    public void ATargetInAHiddenDirectory_IsRefused(string hide)
    {
        using var t = new TempDir();
        Write(t.Path, "secrets/.env");
        Write(t.Path, "agent.env");
        Link(t.Path, "vault-link", "secrets");

        var e = Refused(() => Replace(t.Path, [hide], ("secrets/.env", "agent.env")));

        Assert.StartsWith("replace: target \"secrets/.env\" is in secrets/, which hide hides whole", e.Message);
    }

    [Fact]
    public void AHardLinkedTarget_IsReplacedWithAWarning()
    {
        using var t = new TempDir();
        var env = Write(t.Path, ".env");
        Run("ln", env, Path.Combine(t.Path, "copy"));
        Write(t.Path, "agent.env");

        var plan = Replace(t.Path, [], (".env", "agent.env"));

        Assert.Single(plan.Replacements);
        Assert.Equal([".env has 2 hard links: the agent may read what it holds under another name"], plan.Warnings);
    }

    // ---- the mount cap ----

    private static void WriteMany(string dir, int count)
    {
        var secrets = Directory.CreateDirectory(Path.Combine(dir, "many")).FullName;
        for (var i = 0; i < count; i++)
            File.WriteAllText(Path.Combine(secrets, $"f{i}.secret"), "");
    }

    [Fact]
    public void MoreThan1000Mounts_AreRefused_NamingThePattern()
    {
        using var t = new TempDir();
        WriteMany(t.Path, 1001);

        var e = Refused(() => Plan(t.Path, "*.secret"));

        Assert.Equal("hide and [replace] need 1001 mounts, and pen allows at most 1000 (hide \"*.secret\" matches 1001): " +
            "hide the directory the files are in instead (secrets/ rather than secrets/*.env)", e.Message);
    }

    [Fact]
    public void Exactly1000Mounts_AreAllowed_CountingReplacements()
    {
        using var t = new TempDir();
        WriteMany(t.Path, 999);
        Write(t.Path, ".env");
        Write(t.Path, "other");
        Write(t.Path, "agent.env");

        Assert.Equal(1000, Plan(t.Path, "*.secret", ".env").MountCount);
        Assert.Equal(1000, Replace(t.Path, ["*.secret", ".env"], (".env", "agent.env")).MountCount);
        var e = Refused(() => Replace(t.Path, ["*.secret", ".env"], ("other", "agent.env")));
        Assert.StartsWith("hide and [replace] need 1001 mounts", e.Message);
        // A directory hidden whole is one mount, whatever is in it.
        Assert.Equal(1, Plan(t.Path, "many/", "*.secret").MountCount);
    }

    // ---- a [replace] source that hide hides ----

    [Theory]
    [InlineData("prod.env", "hide \"*.env\" matches prod.env")]
    [InlineData("secrets/key", "hide \"secrets/\" matches secrets/")]
    [InlineData("deep/secrets/inner/key", "hide \"secrets/\" matches deep/secrets/")]
    [InlineData("SECRETS/key", "hide \"secrets/\" matches SECRETS/")]
    public void ASourceAPatternHides_OrThatsInADirectoryOneHides_IsRefused(string source, string why)
    {
        using var t = new TempDir();
        Write(t.Path, "README.md", "readme");
        Write(t.Path, "prod.env", "KEY=prod");
        Write(t.Path, "secrets/key");
        Write(t.Path, "deep/secrets/inner/key");
        // Where the file system tells case apart, SECRETS/ is its own directory, and still matches.
        Write(t.Path, "SECRETS/key");

        var e = Refused(() => Replace(t.Path, ["*.env", "secrets/"], ("README.md", source)));

        Assert.Equal($"replace: source \"{source}\" (for \"README.md\") is hidden: {why}, and [replace] can't show the agent what hide keeps from it", e.Message);
    }

    [Fact]
    public void ASourceThatsAFileHiddenThroughASymlink_IsRefused()
    {
        using var t = new TempDir();
        Write(t.Path, "README.md");
        Write(t.Path, "real/data");
        Link(t.Path, "link.secret", "real/data");

        var e = Refused(() => Replace(t.Path, ["*.secret"], ("README.md", "real/data")));

        Assert.Contains("is hidden: it's real/data, which hide hides,", e.Message);
    }

    [Fact]
    public void ASourceInADirectoryHiddenThroughASymlink_IsRefused()
    {
        using var t = new TempDir();
        Write(t.Path, "README.md");
        Write(t.Path, "vault/inner/key");
        Link(t.Path, "vault-link", "vault");

        var e = Refused(() => Replace(t.Path, ["vault-link"], ("README.md", "vault/inner/key")));

        Assert.Contains("is hidden: it's in vault/, which hide hides whole,", e.Message);
    }

    [Fact]
    public void ASourceThatsAHardLinkToAHiddenFile_IsRefused()
    {
        using var t = new TempDir();
        Write(t.Path, "README.md");
        var env = Write(t.Path, "prod.env");
        Run("ln", env, Path.Combine(t.Path, "public.txt"));

        var e = Refused(() => Replace(t.Path, ["/prod.env"], ("README.md", "public.txt")));

        Assert.Contains("is hidden: it's prod.env, which hide hides,", e.Message);
    }

    [Fact]
    public void ASourceThatsAHardLinkToAFileInAHiddenDirectory_IsRefused()
    {
        using var t = new TempDir();
        Write(t.Path, "README.md");
        var key = Write(t.Path, "secrets/key");
        t.Sub(".pen");
        Run("ln", key, Path.Combine(t.Path, ".pen", "key"));

        var e = Refused(() => Replace(t.Path, ["secrets/"], ("README.md", ".pen/key")));

        Assert.Equal("replace: source \".pen/key\" (for \"README.md\") has another name (a hard link), so pen can't tell whether hide hides it: use a copy instead", e.Message);
    }

    [Fact]
    public void AHardLinkedSource_IsRefused_WithNothingHidden()
    {
        using var t = new TempDir();
        Write(t.Path, "README.md");
        var source = Write(t.Path, "agent.env");
        Run("ln", source, Path.Combine(t.Path, "other"));

        var e = Refused(() => Replace(t.Path, [], ("README.md", "agent.env")));

        Assert.Equal("replace: source \"agent.env\" (for \"README.md\") has another name (a hard link), so pen can't tell whether hide hides it: use a copy instead", e.Message);
    }

    [Fact]
    public void ASourceSpelledInAnotherCase_InADirectoryHiddenThroughASymlink_IsRefused_WhereTheFileSystemFoldsCase()
    {
        using var t = new TempDir();
        Assert.SkipUnless(RepoGuard.MayFoldCase(t.Path), "this file system tells case apart");
        Write(t.Path, "README.md");
        Write(t.Path, "Vault/key");
        Write(t.Path, "Plain.txt");
        Link(t.Path, "vault-link", "Vault");
        Link(t.Path, "plain-link.secret", "Plain.txt");

        var inDir = Refused(() => Replace(t.Path, ["vault-link"], ("README.md", "vAULT/KEY")));
        var file = Refused(() => Replace(t.Path, ["*.secret"], ("README.md", "PLAIN.TXT")));

        Assert.Contains("is hidden: it's in Vault/, which hide hides whole,", inDir.Message);
        Assert.Contains("is hidden: it's Plain.txt, which hide hides,", file.Message);
    }

    [Fact]
    public void ASourceInPen_IsRead_WhateverHideMatches()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        Write(t.Path, ".pen/agent.env", "x");

        var plan = Replace(t.Path, ["*.env", "**/agent.env"], (".env", ".pen/agent.env"));

        Assert.Equal("x"u8.ToArray(), Assert.Single(plan.Replacements).Bytes);
    }

    [Fact]
    public void ASourceThatsAReplacedHiddenFile_IsRefused()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        Write(t.Path, "README.md");
        Write(t.Path, ".pen/agent.env");

        var e = Refused(() => Replace(t.Path, [".env"], (".env", ".pen/agent.env"), ("README.md", ".env")));

        Assert.StartsWith("replace: source \".env\" (for \"README.md\") is hidden: hide \".env\" matches .env,", e.Message);
    }

    // ---- git-protection mounts in the tree ----

    [Theory]
    [InlineData("*.inc", "conf/git.inc, which pen mounts read-only to protect git's config")]
    [InlineData("conf/", "conf, which holds conf/git.inc, which pen mounts read-only to protect git's config")]
    [InlineData("pdir/x", "pdir/x, which is in pdir, which pen mounts read-only to protect git's config")]
    public void HidingAGitProtectionMount_OrWhatsInOrAroundOne_IsRefused(string pattern, string overlap)
    {
        using var t = new TempDir();
        var inc = Write(t.Path, "conf/git.inc");
        Write(t.Path, "pdir/x");
        Write(t.Path, "other.inc.txt");

        var e = Refused(() => PlanProtected(t.Path, null, [inc, Path.Combine(t.Path, "pdir")], [pattern]));

        Assert.Equal($"hide \"{pattern}\" matches {overlap}, so it can't be hidden as well: change the pattern to leave it out", e.Message);
    }

    [Fact]
    public void ReplacingAGitProtectionMount_IsRefused()
    {
        using var t = new TempDir();
        var inc = Write(t.Path, "conf/git.inc");
        Write(t.Path, "agent.env");

        var e = Refused(() => PlanProtected(t.Path, null, [inc], [], ("conf/git.inc", "agent.env")));

        Assert.Equal("replace: target \"conf/git.inc\" is conf/git.inc, which pen mounts read-only to protect git's config, so it can't be replaced as well: replace another file", e.Message);
    }

    [Fact]
    public void GitProtectionMountsElsewhere_OrOutsideTheProject_DontStopAPlan()
    {
        using var t = new TempDir();
        var project = t.Sub("project");
        Write(project, ".env");
        Write(project, "README.md");
        Write(project, "public.txt");
        var inc = Write(project, "conf/git.inc");
        var outside = Write(t.Path, "outside/git.inc");

        var plan = PlanProtected(project, null, [inc, outside, project, Path.Combine(project, ".pen")], ["*.env", "/outside/"], ("README.md", "public.txt"));

        Assert.Equal([".env"], Files(plan));
        Assert.Single(plan.Replacements);
    }

    [Fact]
    public void AnInTreeFileThatGitsConfigIncludes_CantBeHiddenOrReplaced()
    {
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("project"));
        Write(dir, "conf/git.inc", "");
        Write(dir, "agent.env");
        File.AppendAllText(Path.Combine(dir, ".git", "config"), "[include]\n\tpath = ../conf/git.inc\n");
        var mounts = RepoGuard.PrepareMounts(dir, t.Sub("home")).Select(m => m.Path).ToArray();

        var hide = Refused(() => PlanProtected(dir, Path.Combine(dir, ".git"), mounts, ["conf/"]));
        var replace = Refused(() => PlanProtected(dir, Path.Combine(dir, ".git"), mounts, [], ("conf/git.inc", "agent.env")));

        Assert.StartsWith("hide \"conf/\" matches conf, which holds conf/git.inc, which pen mounts read-only", hide.Message);
        Assert.StartsWith("replace: target \"conf/git.inc\" is conf/git.inc, which pen mounts read-only", replace.Message);
        Assert.Equal(["agent.env"], Files(PlanProtected(dir, Path.Combine(dir, ".git"), mounts, ["agent.env"])));
    }

    // ---- names from disk in messages ----

    [Fact]
    public void NamesFromDisk_CantForgeALineOfOutput()
    {
        using var t = new TempDir();
        var name = "x\npen: y.env";
        var env = Write(t.Path, name);
        Run("ln", env, Path.Combine(t.Path, "other"));
        var dir = t.Sub("d\nz");
        Write(dir, "f.env");
        Write(t.Path, "agent.env");

        var plan = Plan(t.Path, "*.env");
        var replaced = Replace(t.Path, [], (name, "agent.env"));
        var e = Refused(() => Replace(t.Path, ["d?z/"], ("d\nz/f.env", "agent.env")));

        Assert.Contains("x\\npen: y.env has 2 hard links: the agent may read it under another name", plan.Warnings);
        Assert.Contains("x\\npen: y.env has 2 hard links: the agent may read what it holds under another name", replaced.Warnings);
        Assert.Equal("replace: target \"d\\nz/f.env\" is in d\\nz/, which hide hides whole: stop hiding the directory, or replace a file outside it", e.Message);
        // The plan keeps the name as it is.
        Assert.Contains(name, Files(plan));
    }

    [Fact]
    public void PatternsSourcesAndNamesFromDisk_CantForgeALineOfOutput_InAnyMessage()
    {
        using var t = new TempDir();
        Write(t.Path, "README.md");
        Write(t.Path, "s\nk");
        Write(t.Path, "real/secret.env");
        Write(t.Path, "p\nq/gitdir/config");
        Write(t.Path, "p\nq/gitdir/x");
        Link(t.Path, "s\nelf", ".");
        Link(t.Path, "l\nink", "real");
        Link(t.Path, "g\nit.env", "p\nq/gitdir/config");
        var gitdir = Path.Combine(t.Path, "p\nq", "gitdir");
        var inc = Write(t.Path, "c\nf/git.inc");
        var linked = Write(t.Path, "h\nl");
        Run("ln", linked, Path.Combine(t.Path, "other"));

        var walked = Plan(t.Path, gitdir, ["p?q/", "s\nelf", "*.env", "/l\nink/secret.env"]);
        var literal = Plan(t.Path, gitdir, ["/p\nq", "/l\nink/secret.env"]);
        var protectedMount = Refused(() => PlanProtected(t.Path, null, [inc], ["c\nf/"]));
        var hiddenSource = Refused(() => Replace(t.Path, ["s\nk"], ("README.md", "s\nk")));
        var inGit = Refused(() => Replace(t.Path, [], ("README.md", "a\nb/.git/config")));
        var missing = Refused(() => Replace(t.Path, [], ("README.md", "m\nissing")));
        var hardLinked = Refused(() => Replace(t.Path, [], ("README.md", "h\nl")));

        Assert.Contains("couldn't hide p\\nq: the repo's git dir is in it, which pen doesn't hide", walked.Warnings);
        Assert.Contains("s\\nelf leads to the project dir itself, which can't be hidden", walked.Warnings);
        Assert.Contains("couldn't hide g\\nit.env: it leads into the repo's git dir, which pen doesn't hide", walked.Warnings);
        const string Through = "couldn't hide l\\nink/secret.env: l\\nink on the way is a symlink, which pen doesn't follow; hide the path it leads to";
        Assert.Contains(Through, walked.Warnings);
        Assert.Equal(["couldn't hide p\\nq: the repo's git dir is in it, which pen doesn't hide", Through], literal.Warnings);
        Assert.Equal("hide \"c\\nf/\" matches c\\nf, which holds c\\nf/git.inc, which pen mounts read-only to protect git's config, so it can't be hidden as well: change the pattern to leave it out", protectedMount.Message);
        Assert.Equal("replace: source \"s\\nk\" (for \"README.md\") is hidden: hide \"s\\nk\" matches s\\nk, and [replace] can't show the agent what hide keeps from it", hiddenSource.Message);
        Assert.Equal("replace: source \"a\\nb/.git/config\" (for \"README.md\") can't be in .git", inGit.Message);
        Assert.StartsWith("replace: source \"m\\nissing\" (for \"README.md\") can't be used: ", missing.Message);
        Assert.EndsWith("m\\nissing doesn't exist", missing.Message);
        Assert.StartsWith("replace: source \"h\\nl\" (for \"README.md\") has another name (a hard link)", hardLinked.Message);
        Assert.All(walked.Warnings.Concat(literal.Warnings), warning => Assert.DoesNotContain('\n', warning));
        Assert.All(new[] { protectedMount, hiddenSource, inGit, missing, hardLinked }, e => Assert.DoesNotContain('\n', e.Message));
    }

    [Fact]
    public void APatternWithANewline_CantForgeALineOfTheMountCapsRefusal()
    {
        using var t = new TempDir();
        var dir = t.Sub("m\nny");
        for (var i = 0; i < 1001; i++)
            File.WriteAllText(Path.Combine(dir, $"f{i}"), "");

        var e = Refused(() => Plan(t.Path, "m\nny/*"));

        Assert.Equal("hide and [replace] need 1001 mounts, and pen allows at most 1000 (hide \"m\\nny/*\" matches 1001): " +
            "hide the directory the files are in instead (secrets/ rather than secrets/*.env)", e.Message);
    }

    [Fact]
    public void NamesThatArentValidUtf8_WithANewline_CantForgeALineOfOutput()
    {
        using var t = new TempDir();
        RunWithBadNames(t.Path, "b=$(printf 'b\\nad\\377') && mkdir \"$b\" && : > \"$b/x\" && : > \"$b.env\" && ln -s \"$b/x\" \"$(printf 'l\\nink.env')\"");

        var plan = Plan(t.Path, "*.env");

        Assert.True(plan.IsEmpty);
        Assert.Contains("couldn't hide b\\nad\uFFFD.env: its name isn't valid UTF-8", plan.Warnings);
        Assert.Contains("couldn't look in b\\nad\uFFFD for files to hide: its name isn't valid UTF-8", plan.Warnings);
        Assert.Contains("couldn't hide l\\nink.env: pen can't look at what it leads to", plan.Warnings);
        Assert.All(plan.Warnings, warning => Assert.DoesNotContain('\n', warning));
    }

    [Fact]
    public void ManyWarnings_AreCut_AfterTheFirst20()
    {
        using var t = new TempDir();
        for (var i = 0; i < 25; i++)
            Run("ln", Write(t.Path, $"f{i:D2}.env"), Path.Combine(t.Path, $"f{i:D2}.copy"));

        var plan = Plan(t.Path, "*.env");

        Assert.Equal(25, Files(plan).Length);
        Assert.Equal(21, plan.Warnings.Count);
        Assert.Equal("f19.env has 2 hard links: the agent may read it under another name", plan.Warnings[19]);
        Assert.Equal("and 5 more warnings", plan.Warnings[20]);
    }

    [Fact]
    public void AnUnlistableDirectoryWithANewlineInItsName_IsWarnedAboutEscaped()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root lists any directory");
        using var t = new TempDir();
        var locked = t.Sub("a\nb");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.Equal(["couldn't look in a\\nb for files to hide"], Plan(t.Path, "*.env").Warnings);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ---- what couldn't be hidden ----

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public void AMatchWhosePathIsTooLongToLookAt_IsWarnedAbout(string newline)
    {
        using var t = new TempDir();
        var limit = OperatingSystem.IsMacOS() ? 1024 : 4096;
        // Directories made one step at a time, so no call is given the whole path, down to about 100 below the limit.
        var relative = new List<string>();
        while (t.Path.Length + relative.Sum(part => part.Length + 1) < limit - 300)
            relative.Add(new string('d', 200));
        relative.Add(new string('e', limit - 100 - t.Path.Length - relative.Sum(part => part.Length + 1) - 1));
        var file = new string('f', 150) + newline + ".env";
        var script = string.Concat(relative.Select(part => $"mkdir {part} && cd {part} && ")) + $": > '{file}'";
        try
        {
            var psi = new ProcessStartInfo("sh", ["-c", script]) { WorkingDirectory = t.Path, RedirectStandardOutput = true, RedirectStandardError = true };
            var r = Process.RunAndCaptureText(psi);
            Assert.True(r.ExitStatus.ExitCode == 0, r.StandardError);
            Write(t.Path, "short.env");

            var plan = Plan(t.Path, "*.env");

            Assert.Equal(["short.env"], Files(plan));
            Assert.Equal([$"couldn't hide {string.Join('/', relative)}/{file.Replace("\n", "\\n")}: pen can't look at it"], plan.Warnings);
        }
        finally
        {
            Run("rm", "-rf", Path.Combine(t.Path, relative[0]));
        }
    }

    /// <summary>Runs a shell script in <paramref name="dir"/>, skipping the test where the file system refuses a name that isn't valid UTF-8.</summary>
    private static void RunWithBadNames(string dir, string script)
    {
        var psi = new ProcessStartInfo("sh", ["-c", script]) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        var r = Process.RunAndCaptureText(psi);
        Assert.SkipWhen(r.ExitStatus.ExitCode != 0 && r.StandardError.Contains("llegal byte sequence", StringComparison.Ordinal), "the file system refuses names that aren't valid UTF-8");
        Assert.True(r.ExitStatus.ExitCode == 0, r.StandardError);
    }

    [Fact]
    public void AnUndecodableNameThatListsAsAValidOne_IsWarnedAbout_AndTheValidOneHidden()
    {
        using var t = new TempDir();
        Write(t.Path, "bad�.env", "valid");
        RunWithBadNames(t.Path, "b=$(printf 'bad\\377') && : > \"$b.env\"");

        var plan = Plan(t.Path, "*.env");

        Assert.Equal(["bad�.env"], Files(plan));
        Assert.Equal(["couldn't hide bad�.env: its name isn't valid UTF-8"], plan.Warnings);
    }

    [Fact]
    public void AnUndecodableDirectoryThatListsAsAValidFile_IsWarnedAbout()
    {
        using var t = new TempDir();
        Write(t.Path, "bad�", "valid");
        RunWithBadNames(t.Path, "b=$(printf 'bad\\377') && mkdir \"$b\" && : > \"$b/x.env\"");

        var plan = Plan(t.Path, "*.env");

        Assert.Empty(Files(plan));
        Assert.Equal(["couldn't look in bad� for files to hide: its name isn't valid UTF-8"], plan.Warnings);
    }

    [Fact]
    public void ASymlinkLeadingToAnUndecodablePath_IsWarnedAbout()
    {
        using var t = new TempDir();
        RunWithBadNames(t.Path, "b=$(printf 'bad\\377') && mkdir \"$b\" && : > \"$b/x\" && ln -s \"$b/x\" link.env");

        var plan = Plan(t.Path, "*.env");

        Assert.True(plan.IsEmpty);
        Assert.Contains("couldn't hide link.env: pen can't look at what it leads to", plan.Warnings);
    }

    [Fact]
    public void AMatchedDirectoryHoldingTheCommonGitDir_IsntHidden_ButIsLookedIn()
    {
        using var t = new TempDir();
        Write(t.Path, "parent/gitdir/x.env");
        Write(t.Path, "parent/x.env");
        Write(t.Path, "parent/sub/y.env");

        var plan = Plan(t.Path, Path.Combine(t.Path, "parent", "gitdir"), ["parent/", "*.env"]);
        var literal = Plan(t.Path, Path.Combine(t.Path, "parent", "gitdir"), ["/parent", "/parent/x.env"]);

        Assert.Empty(Dirs(plan));
        Assert.Equal(["parent/sub/y.env", "parent/x.env"], Files(plan));
        Assert.Equal(["couldn't hide parent: the repo's git dir is in it, which pen doesn't hide"], plan.Warnings);
        Assert.Equal(["parent/x.env"], Files(literal));
        Assert.Equal(["couldn't hide parent: the repo's git dir is in it, which pen doesn't hide"], literal.Warnings);
    }

    [Fact]
    public void AReplacement_DropsSetuidSetgidAndStickyBits()
    {
        using var t = new TempDir();
        Write(t.Path, ".env");
        var source = Write(t.Path, "agent.env");
        File.SetUnixFileMode(source, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.SetUser);
        Assert.SkipUnless(File.GetUnixFileMode(source).HasFlag(UnixFileMode.SetUser), "setuid couldn't be set");

        var plan = Replace(t.Path, [], (".env", "agent.env"));

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupExecute, Assert.Single(plan.Replacements).Mode);
    }

    // ---- places never entered: an unlistable directory in each isn't warned about ----

    [Theory]
    [InlineData(".git")]
    [InlineData("sub/.GIT")]
    [InlineData(".pen")]
    [InlineData("gitdir")]
    public void AnUnlistableDirectoryInAPlaceNeverEntered_IsntWarnedAbout(string place)
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root lists any directory");
        using var t = new TempDir();
        var locked = t.Sub([.. place.Split('/'), "locked"]);
        Write(t.Path, "kept.env");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var walked = Plan(t.Path, Path.Combine(t.Path, "gitdir"), ["*.env", "nothing-*"]);

            Assert.Equal(["kept.env"], Files(walked));
            Assert.Empty(walked.Warnings);
            // Patterns can't name .git or .pen, but the literal lookup must stay out of the common git dir too.
            if (place == "gitdir")
                Assert.Empty(Plan(t.Path, Path.Combine(t.Path, "gitdir"), ["/gitdir/locked/x", "/kept.env"]).Warnings);
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
