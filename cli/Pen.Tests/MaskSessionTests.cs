using System.Text;
using System.Text.Json;
using Pen.Masking;

namespace Pen.Tests;

/// <summary>
/// A session writes the files it mounts over what it hides and replaces to its masks dir, mounts them read-only after
/// the git-protection mounts in the agent and dind, says what it hides, warns where that may not hold, and its files
/// are still removed with a 0555 dir and 0444 files among them.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public class MaskSessionTests
{
    private const UnixFileMode Mode444 = (UnixFileMode)0b100_100_100;
    private const UnixFileMode Mode555 = (UnixFileMode)0b101_101_101;
    private const UnixFileMode Mode700 = (UnixFileMode)0b111_000_000;

    private static UnixFileMode ModeOf(string path) => File.GetUnixFileMode(path) & (UnixFileMode)0b111_111_111;

    private static MaskedPath M(string relative) => new("/p/" + relative, relative, 0, 0);

    private static Replacement R(string target, string content, UnixFileMode mode) =>
        new(M(target), Encoding.UTF8.GetBytes(content), mode);

    private static MaskPlan Plan(string[] files, string[] dirs, params Replacement[] replacements) =>
        new([.. files.Select(M)], [.. dirs.Select(M)], replacements, []);

    private static SessionDir NewSession(TempDir t) =>
        new("pen-test-12345678", t.Sub("runtime", "sessions", "12345678"));

    private static MaskPlan Sample() =>
        Plan([".env", "apps/web/.env"], ["secrets"],
            R("config.env", "A=1\n", Mode444), R("run.sh", "#!/bin/sh\n", Mode555));

    // ---- the files ----

    [Fact]
    public void AnEmptyPlanWritesNothingAndMountsNothing()
    {
        using var t = new TempDir();
        var session = NewSession(t);

        Assert.Empty(MaskFiles.Write(session.MasksDir, MaskPlan.Empty));
        Assert.False(Directory.Exists(session.MasksDir));
        Assert.Equal([new Mount("/p/.pen", true)], Session.WriteMasks(session, [new("/p/.pen", true)], MaskPlan.Empty));
        Assert.False(Directory.Exists(session.MasksDir));
    }

    [Fact]
    public void TheMasksDirHoldsAnEmptyFileAnEmptyDirAndACopyPerReplacement_NoneWritable()
    {
        using var t = new TempDir();
        var session = NewSession(t);

        MaskFiles.Write(session.MasksDir, Sample());

        var masks = session.MasksDir;
        Assert.Equal(Mode700, ModeOf(masks));
        Assert.Equal(["empty", "empty-dir", "replace-1", "replace-2"],
            Directory.EnumerateFileSystemEntries(masks).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        var empty = Path.Combine(masks, "empty");
        Assert.Equal(0, new FileInfo(empty).Length);
        Assert.Equal(Mode444, ModeOf(empty));
        var emptyDir = Path.Combine(masks, "empty-dir");
        Assert.True(Directory.Exists(emptyDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(emptyDir));
        Assert.Equal(Mode555, ModeOf(emptyDir));
        Assert.Equal("A=1\n", File.ReadAllText(Path.Combine(masks, "replace-1")));
        Assert.Equal(Mode444, ModeOf(Path.Combine(masks, "replace-1")));
        Assert.Equal("#!/bin/sh\n", File.ReadAllText(Path.Combine(masks, "replace-2")));
        Assert.Equal(Mode555, ModeOf(Path.Combine(masks, "replace-2")));
    }

    [Fact]
    public void AReplacementWithOnlyTheOwnersReadBitKeepsJustThat()
    {
        using var t = new TempDir();
        var session = NewSession(t);

        MaskFiles.Write(session.MasksDir, Plan([], [], R(".env", "x", UnixFileMode.UserRead)));

        Assert.Equal(UnixFileMode.UserRead, ModeOf(Path.Combine(session.MasksDir, "replace-1")));
        Assert.Equal("x", File.ReadAllText(Path.Combine(session.MasksDir, "replace-1")));
    }

    [Theory]
    [InlineData(0b100_100_100)]
    [InlineData(0b101_101_101)]
    [InlineData(0b100_000_000)]
    [InlineData(0b101_000_000)]
    [InlineData(0b110_110_110)] // wider than the usual umask lets a new file have
    public void AWrittenFileGetsExactlyItsModeAndBytes(int mode)
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "copy");

        MaskFiles.WriteNew(path, Encoding.UTF8.GetBytes("A=1\n"), (UnixFileMode)mode);

        Assert.False(PathChecks.IsLink(path));
        Assert.Equal((UnixFileMode)mode, ModeOf(path));
        Assert.Equal("A=1\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASymlinkWhereAFileIsWritten_IsRefused_AndWhatItLeadsToIsLeftAlone(bool dangling)
    {
        using var t = new TempDir();
        var victim = Path.Combine(t.Path, "victim");
        if (!dangling)
        {
            File.WriteAllText(victim, "the user's file");
            File.SetUnixFileMode(victim, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var path = Path.Combine(t.Path, "copy");
        File.CreateSymbolicLink(path, victim);

        Assert.Throws<IOException>(() => MaskFiles.WriteNew(path, Encoding.UTF8.GetBytes("A=1\n"), Mode555));

        Assert.True(PathChecks.IsLink(path));
        if (dangling)
        {
            Assert.False(PathChecks.ExistsOrIsLink(victim));
            return;
        }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(victim));
        Assert.Equal("the user's file", File.ReadAllText(victim));
    }

    [Fact]
    public void ASymlinkSwappedInWhileACopyIsWritten_DoesntGetItsMode()
    {
        using var t = new TempDir();
        var session = NewSession(t);
        var victim = Path.Combine(t.Path, "victim");
        File.WriteAllText(victim, "the user's file");
        File.SetUnixFileMode(victim, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(t.Path, "link");
        File.CreateSymbolicLink(link, victim);
        var copy = Path.Combine(session.MasksDir, MaskFiles.ReplacementPrefix + "1");
        // Big enough that the copy is still being written when the swap lands, between its creation and its mode.
        var plan = Plan([], [], new Replacement(M("big"), new byte[64 << 20], Mode555));
        var swap = new Thread(() =>
        {
            var deadline = Environment.TickCount64 + 30_000;
            while (!PathChecks.ExistsOrIsLink(copy))
            {
                if (Environment.TickCount64 > deadline)
                    return;
            }
            File.Move(link, copy, overwrite: true);
        });
        swap.Start();

        MaskFiles.Write(session.MasksDir, plan);

        Assert.True(swap.Join(TimeSpan.FromSeconds(60)), "hung");
        Assert.True(PathChecks.IsLink(copy), "the swap didn't happen");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, ModeOf(victim));
        Assert.Equal("the user's file", File.ReadAllText(victim));
    }

    [Fact]
    public void AMasksDirThatsThereAlreadyIsRefused()
    {
        using var t = new TempDir();
        var session = NewSession(t);
        Directory.CreateDirectory(session.MasksDir);
        File.WriteAllText(Path.Combine(session.MasksDir, "empty"), "not empty");

        Assert.Throws<IOException>(() => MaskFiles.Write(session.MasksDir, Sample()));
        Assert.Equal("not empty", File.ReadAllText(Path.Combine(session.MasksDir, "empty")));
    }

    [Fact]
    public void EachMaskIsAReadOnlyMountOfAMaskFileOverItsPath_FilesThenDirsThenReplacements()
    {
        using var t = new TempDir();
        var session = NewSession(t);

        var mounts = MaskFiles.Write(session.MasksDir, Sample());

        var masks = session.MasksDir;
        Assert.Equal(
        [
            new Mount("/p/.env", true, Source: Path.Combine(masks, "empty")),
            new Mount("/p/apps/web/.env", true, Source: Path.Combine(masks, "empty")),
            new Mount("/p/secrets", true, Source: Path.Combine(masks, "empty-dir")),
            new Mount("/p/config.env", true, Source: Path.Combine(masks, "replace-1")),
            new Mount("/p/run.sh", true, Source: Path.Combine(masks, "replace-2")),
        ], mounts);
        Assert.All(mounts, mount => Assert.NotEqual(mount.Path, mount.Source));
        Assert.All(mounts, mount => Assert.False(mount.Verify));
    }

    [Fact]
    public void ThePlannersReplacementReachesTheCopyByteForByte()
    {
        using var t = new TempDir();
        var project = t.Sub("project");
        File.WriteAllText(Path.Combine(project, ".env"), "SECRET=real\n");
        File.WriteAllText(Path.Combine(t.Sub("project", "secrets"), "key"), "k");
        File.WriteAllText(Path.Combine(t.Sub("project", ".pen"), "agent.env"), "SECRET=\n");
        Assert.True(HidePattern.TryParse(".env", out var env, out _));
        Assert.True(HidePattern.TryParse("secrets/", out var secrets, out _));
        var plan = new MaskPlanner(project, null, [], [env, secrets], [new(".env", ".pen/agent.env")]).Plan();
        var session = NewSession(t);

        var mounts = MaskFiles.Write(session.MasksDir, plan);

        Assert.Equal(
        [
            new Mount(Path.Combine(project, "secrets"), true, Source: Path.Combine(session.MasksDir, "empty-dir")),
            new Mount(Path.Combine(project, ".env"), true, Source: Path.Combine(session.MasksDir, "replace-1")),
        ], mounts);
        Assert.Equal("SECRET=\n", File.ReadAllText(Path.Combine(session.MasksDir, "replace-1")));
        Assert.Equal("SECRET=real\n", File.ReadAllText(Path.Combine(project, ".env")));
    }

    // ---- looking again before the mounts are written ----

    /// <summary>
    /// Plans a project hiding apps/web/.env and secrets/ and replacing config.env with .pen/agent.env.
    /// </summary>
    private static (string Project, MaskPlan Plan) PlanProject(TempDir t)
    {
        var project = t.Sub("project");
        File.WriteAllText(Path.Combine(t.Sub("project", "apps", "web"), ".env"), "A=1\n");
        File.WriteAllText(Path.Combine(t.Sub("project", "secrets"), "key"), "k");
        File.WriteAllText(Path.Combine(project, "config.env"), "B=1\n");
        File.WriteAllText(Path.Combine(t.Sub("project", ".pen"), "agent.env"), "B=\n");
        Assert.True(HidePattern.TryParse(".env", out var env, out _));
        Assert.True(HidePattern.TryParse("secrets/", out var secrets, out _));
        var plan = new MaskPlanner(project, null, [], [env, secrets], [new("config.env", ".pen/agent.env")]).Plan();
        Assert.Equal(["apps/web/.env"], plan.HiddenFiles.Select(file => file.RelativePath));
        Assert.Equal(["secrets"], plan.HiddenDirs.Select(dir => dir.RelativePath));
        Assert.Equal(["config.env"], plan.Replacements.Select(replacement => replacement.Target.RelativePath));
        return (project, plan);
    }

    private static string Refusal(MaskPlan plan) => Assert.Throws<PenException>(plan.ThrowIfChanged).Message;

    [Fact]
    public void AnUnchangedProjectPassesTheSecondLook_EvenWithItsFilesRewritten()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        File.WriteAllText(Path.Combine(project, "apps", "web", ".env"), "A=2\n");
        File.WriteAllText(Path.Combine(project, "secrets", "new"), "n");

        plan.ThrowIfChanged();
        MaskPlan.Empty.ThrowIfChanged();
    }

    [Fact]
    public void AHiddenFileThatsGoneIsRefused_AsIsOneWhoseDirectoryIsGone()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        File.Delete(Path.Combine(project, "apps", "web", ".env"));

        Assert.Equal("not starting: apps/web/.env, which this session hides, is gone: the project changed while the " +
            "session was being prepared, so run pen again", Refusal(plan));

        Directory.Delete(Path.Combine(project, "apps"), recursive: true);

        Assert.Contains("apps/web/.env, which this session hides, is gone", Refusal(plan));
        Assert.False(Directory.Exists(Path.Combine(project, "apps")));
    }

    [Fact]
    public void AHiddenDirThatsGoneIsRefused()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        Directory.Delete(Path.Combine(project, "secrets"), recursive: true);

        Assert.Contains("secrets, which this session hides, is gone", Refusal(plan));
    }

    [Fact]
    public void AReplacedFileThatsGoneIsRefused()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        File.Delete(Path.Combine(project, "config.env"));

        Assert.Contains("config.env, which this session replaces, is gone", Refusal(plan));
    }

    [Fact]
    public void AHiddenFileNowADirectoryIsRefused()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        var env = Path.Combine(project, "apps", "web", ".env");
        File.Delete(env);
        Directory.CreateDirectory(env);

        Assert.Contains("apps/web/.env, which this session hides, is no longer a file", Refusal(plan));
    }

    [Fact]
    public void AHiddenDirNowAFileIsRefused()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        Directory.Delete(Path.Combine(project, "secrets"), recursive: true);
        File.WriteAllText(Path.Combine(project, "secrets"), "");

        Assert.Contains("secrets, which this session hides, is no longer a directory", Refusal(plan));
    }

    [Fact]
    public void AReplacedFileNowASymlinkIsRefused_EvenToItsOwnCopy()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        var target = Path.Combine(project, "config.env");
        File.Move(target, Path.Combine(project, "moved.env"));
        File.CreateSymbolicLink(target, "moved.env");

        Assert.Contains("config.env, which this session replaces, is a symlink now", Refusal(plan));
    }

    [Fact]
    public void AHiddenFileThatsAnotherFileNowIsRefused()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        // Made while the first still exists, so it can't get the first's inode back.
        var other = Path.Combine(project, "other");
        File.WriteAllText(other, "A=1\n");
        File.Move(other, Path.Combine(project, "apps", "web", ".env"), overwrite: true);

        Assert.Contains("apps/web/.env, which this session hides, is another file now", Refusal(plan));
    }

    [Fact]
    public void AHiddenDirThatsAnotherDirNowIsRefused()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        Directory.Move(Path.Combine(project, "secrets"), Path.Combine(project, "old-secrets"));
        Directory.CreateDirectory(Path.Combine(project, "secrets"));

        Assert.Contains("secrets, which this session hides, is another directory now", Refusal(plan));
    }

    [Fact]
    public void AReplacedFileThatsAnotherFileNowIsRefused()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        var other = Path.Combine(project, "other");
        File.WriteAllText(other, "B=1\n");
        File.Move(other, Path.Combine(project, "config.env"), overwrite: true);

        Assert.Contains("config.env, which this session replaces, is another file now", Refusal(plan));
    }

    [Fact]
    public void AHiddenFileReachedThroughASymlinkedParentNowIsRefused_ThoughItsTheSameFile()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        // The same file and directory, by device and inode: only the way to them changed.
        Directory.Move(Path.Combine(project, "apps"), Path.Combine(project, "elsewhere"));
        Directory.CreateSymbolicLink(Path.Combine(project, "apps"), "elsewhere");

        Assert.Contains("apps/web/.env, which this session hides, is reached by another path now", Refusal(plan));
    }

    [Fact]
    public void AHiddenFileRenamedByCaseOnlyIsRefusedAsSpelledDifferently()
    {
        using var t = new TempDir();
        var (project, plan) = PlanProject(t);
        var web = Path.Combine(project, "apps", "web");
        var upper = Path.Combine(web, ".ENV");
        File.Move(Path.Combine(web, ".env"), upper);
        Assert.SkipUnless(File.Exists(Path.Combine(web, ".env")), "file system is case-sensitive");

        Assert.Contains("apps/web/.env, which this session hides, is spelled differently now", Refusal(plan));
    }

    [Fact]
    public void TheSecondLooksRefusalEscapesTheName()
    {
        using var t = new TempDir();
        var project = t.Sub("project");
        File.WriteAllText(Path.Combine(project, "a\nb.env"), "");
        Assert.True(HidePattern.TryParse("*.env", out var pattern, out _));
        var plan = new MaskPlanner(project, null, [], [pattern], []).Plan();
        File.Delete(Path.Combine(project, "a\nb.env"));

        var message = Refusal(plan);

        Assert.StartsWith(@"not starting: a\nb.env, which", message);
        Assert.DoesNotContain(message, c => Out.IsControl(c));
    }

    [Fact]
    public void ASessionWhoseFilesCantAllBeWrittenIsDeleted_AndNotOpen()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root removes anything, so this would prove nothing");
        using var t = new TempDir();
        var session = NewSession(t);
        File.WriteAllText(session.LockFile, "");

        var thrown = Assert.Throws<PenException>(() => Session.WriteSessionDirOrDelete<int>(session, () =>
        {
            File.WriteAllText(session.EnvFile, "A=1");
            Session.WriteMasks(session, [], Sample());
            throw new PenException("not starting");
        }));

        Assert.Equal("not starting", thrown.Message);
        Assert.False(session.IsOpen);
        Assert.Empty(Directory.EnumerateFileSystemEntries(session.Path));
    }

    [Fact]
    public void ASessionWhoseFilesAreWrittenIsKept()
    {
        using var t = new TempDir();
        var session = NewSession(t);

        var result = Session.WriteSessionDirOrDelete(session, () =>
        {
            File.WriteAllText(session.ComposeFile, "stack");
            return 7;
        });

        Assert.Equal(7, result);
        Assert.True(session.IsOpen);
    }

    // ---- the override ----

    [Fact]
    public void TheMaskMountsComeAfterTheProtectionMounts()
    {
        using var t = new TempDir();
        var session = NewSession(t);
        Mount[] protection = [new("/p/.git", false), new("/p/.git/config", true, Verify: true), new("/p/.pen", true)];

        var mounts = Session.WriteMasks(session, protection, Sample());

        Assert.Equal(protection, mounts.Take(3));
        Assert.Equal(["/p/.env", "/p/apps/web/.env", "/p/secrets", "/p/config.env", "/p/run.sh"],
            mounts.Skip(3).Select(m => m.Path));
        Assert.All(mounts.Skip(3), mount => Assert.StartsWith(session.MasksDir + "/", mount.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheOverrideMountsTheMasksInTheAgentAndInDind(bool docker)
    {
        using var t = new TempDir();
        var session = NewSession(t);
        var mounts = Session.WriteMasks(session, [new("/p/.git", false), new("/p/.pen", true)], Sample());

        var json = SessionCompose.GenerateOverride(mounts, readOnlyRoot: false,
            dindContainer: docker ? "x-dind" : null, ports: null);

        var services = JsonDocument.Parse(json).RootElement.GetProperty("services");
        var names = docker ? new[] { "agent", "dind" } : ["agent"];
        Assert.Equal(docker, services.TryGetProperty("dind", out _));
        var masks = session.MasksDir;
        string[] expected =
        [
            "/p/.git>/p/.git:False", "/p/.pen>/p/.pen:True",
            $"{masks}/empty>/p/.env:True", $"{masks}/empty>/p/apps/web/.env:True",
            $"{masks}/empty-dir>/p/secrets:True",
            $"{masks}/replace-1>/p/config.env:True", $"{masks}/replace-2>/p/run.sh:True",
        ];
        foreach (var name in names)
        {
            var volumes = services.GetProperty(name).GetProperty("volumes").EnumerateArray()
                .Select(v => $"{v.GetProperty("source").GetString()}>{v.GetProperty("target").GetString()}:" +
                    $"{v.GetProperty("read_only").GetBoolean()}");
            Assert.Equal(expected, volumes);
        }
    }

    // ---- removal ----

    private static SessionDir OpenSessionWithMasks(TempDir t)
    {
        var session = NewSession(t);
        File.WriteAllText(session.ComposeFile, "stack");
        File.WriteAllText(session.LockFile, "");
        File.WriteAllText(session.ProxyLog, "");
        Session.WriteMasks(session, [], Sample());
        return session;
    }

    [Fact]
    public void DeletingASessionsFilesRemovesItsMasks()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root removes anything, so this would prove nothing");
        using var t = new TempDir();
        var session = OpenSessionWithMasks(t);

        Session.DeleteSessionFiles(session);

        Assert.Equal([session.ProxyLog], Directory.EnumerateFileSystemEntries(session.Path));
    }

    [Fact]
    public void AnEndedSessionWithMasksIsRemovedWhenItExpires()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root removes anything, so this would prove nothing");
        using var t = new TempDir();
        var session = OpenSessionWithMasks(t);
        // Ended with its files kept, as for a retry that later gave up: only the masks and the log are left.
        File.Delete(session.ComposeFile);
        File.Delete(session.LockFile);
        var old = DateTime.UtcNow - Session.KeepEndedSessions - TimeSpan.FromHours(1);
        File.SetLastWriteTimeUtc(session.ProxyLog, old);
        Directory.SetLastWriteTimeUtc(session.Path, old);

        Assert.True(Session.FinishDeadSessions(session.ProjectKey, Path.Combine(t.Path, "runtime"), TimeProvider.System));

        Assert.False(Directory.Exists(session.Path));
    }

    [Fact]
    public void ARuntimeDirHoldingASessionWithMasksCanBeRemoved()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root removes anything, so this would prove nothing");
        using var t = new TempDir();
        OpenSessionWithMasks(t);

        // What pen clean --stale does to a project's runtime dir.
        Directory.Delete(Path.Combine(t.Path, "runtime"), recursive: true);

        Assert.False(Directory.Exists(Path.Combine(t.Path, "runtime")));
    }

    // ---- the summary ----

    [Fact]
    public void AnEmptyPlanHasNoSummary() => Assert.Null(MaskMessages.FormatSummary(MaskPlan.Empty));

    [Fact]
    public void TheSummaryNamesHiddenFilesAndDirsTogetherInOrder_DirsWithASlash()
    {
        var plan = Plan([".env", "apps/web/.env", "z.ps1"], ["secrets", "apps/private"]);

        Assert.Equal("hidden from the agent: .env, apps/private/, apps/web/.env, secrets/, z.ps1",
            MaskMessages.FormatSummary(plan));
    }

    [Fact]
    public void TheSummaryNamesTheFirstFiveAndCountsTheRest()
    {
        var plan = Plan(["a", "b", "c", "d", "f", "g"], ["e"],
            R("r1", "", Mode444), R("r2", "", Mode444), R("r3", "", Mode444), R("r4", "", Mode444),
            R("r5", "", Mode444), R("r6", "", Mode444), R("r7", "", Mode444));

        Assert.Equal("hidden from the agent: a, b, c, d, e/ and 2 more; replaced: r1, r2, r3, r4, r5 and 2 more",
            MaskMessages.FormatSummary(plan));
    }

    [Fact]
    public void TheSummaryNamesExactlyFiveWithoutACount() =>
        Assert.Equal("hidden from the agent: a, b, c, d, e",
            MaskMessages.FormatSummary(Plan(["a", "b", "c", "d", "e"], [])));

    [Fact]
    public void AReplacementAloneIsSummarizedAsReplaced() =>
        Assert.Equal("replaced: .env", MaskMessages.FormatSummary(Plan([], [], R(".env", "", Mode444))));

    [Fact]
    public void HiddenAndReplacedShareOneLine() =>
        Assert.Equal("hidden from the agent: .env.local; replaced: .env",
            MaskMessages.FormatSummary(Plan([".env.local"], [], R(".env", "", Mode444))));

    [Fact]
    public void TheSummaryEscapesNamesFromTheDisk()
    {
        var plan = Plan(["a\nb", "c\u001b[2J"], ["d\re"], R("f\tg", "", Mode444));

        var summary = MaskMessages.FormatSummary(plan)!;

        Assert.Equal(@"hidden from the agent: a\nb, c\x1b[2J, d\re/; replaced: f\tg", summary);
        Assert.DoesNotContain(summary, c => Out.IsControl(c));
    }

    // ---- the warnings ----

    private static List<string> Warnings(MaskPlan plan, bool docker, bool folds, List<string> probed,
        bool isMacOS = false) =>
        MaskMessages.ListWarnings(plan, "/p/.pen", docker, isMacOS, dir =>
        {
            probed.Add(dir);
            return folds;
        });

    [Fact]
    public void AHiddenFileOnAFoldingFilesystemWarnsAboutOtherSpellings()
    {
        var probed = new List<string>();

        var warnings = Warnings(Plan([".env"], []), docker: false, folds: true, probed);

        Assert.Equal(["/p/.pen"], probed);
        Assert.Equal([MaskMessages.CaseWarning], warnings);
        Assert.StartsWith("warning: this project's filesystem isn't known to be case-sensitive: the agent may read "
            + "a hidden or replaced file under another spelling (.ENV for .env); hide its directory",
            MaskMessages.CaseWarning);
    }

    [Fact]
    public void AReplacedFileIsProbedToo()
    {
        var probed = new List<string>();

        var warnings = Warnings(Plan([], [], R(".env", "", Mode444)), docker: false, folds: true, probed);

        Assert.Equal(["/p/.pen"], probed);
        Assert.Equal([MaskMessages.CaseWarning], warnings);
    }

    [Fact]
    public void ACaseSensitiveFilesystemGetsNoCaseWarning()
    {
        var probed = new List<string>();

        Assert.Empty(Warnings(Plan([".env"], ["secrets"]), docker: false, folds: false, probed));
        Assert.Equal(["/p/.pen"], probed);
    }

    [Fact]
    public void OnlyHiddenDirsArentProbed()
    {
        var probed = new List<string>();

        Assert.Empty(Warnings(Plan([], ["secrets"]), docker: false, folds: true, probed));
        Assert.Empty(probed);
    }

    [Fact]
    public void AnEmptyPlanIsntProbedAndHasNoWarnings_WithDockerToo()
    {
        var probed = new List<string>();

        Assert.Empty(Warnings(MaskPlan.Empty, docker: true, folds: true, probed));
        Assert.Empty(probed);
    }

    [Fact]
    public void ThePlansWarningsFollowTheCaseWarning_AndDockersComesLast()
    {
        var plan = Plan([".env"], ["secrets"]) with { Warnings = ["couldn't look in x for files to hide", "y"] };

        var warnings = Warnings(plan, docker: true, folds: true, []);

        Assert.Equal([MaskMessages.CaseWarning, "couldn't look in x for files to hide", "y",
            MaskMessages.DockerWarning], warnings);
    }

    [Fact]
    public void ADockerSessionHidingOnlyADirWarnsThatItIsntEnforced()
    {
        var warnings = Warnings(Plan([], ["secrets"]), docker: true, folds: true, []);

        Assert.Equal([MaskMessages.DockerWarning], warnings);
        Assert.Contains("aren't enforced with --docker", MaskMessages.DockerWarning);
    }

    [Fact]
    public void ANonAsciiNameOnMacOSWarnsAboutOtherUnicodeSpellings()
    {
        Assert.Equal([MaskMessages.UnicodeWarning],
            Warnings(Plan(["caf\u00e9.env"], []), docker: false, folds: false, [], isMacOS: true));
        Assert.Equal([MaskMessages.UnicodeWarning],
            Warnings(Plan([], [], R("d/caf\u00e9.env", "", Mode444)), docker: false, folds: false, [], isMacOS: true));
    }

    [Fact]
    public void AnAsciiNameOrAnotherPlatformGetsNoUnicodeWarning()
    {
        Assert.Empty(Warnings(Plan([".env"], []), docker: false, folds: false, [], isMacOS: true));
        Assert.Empty(Warnings(Plan(["caf\u00e9.env"], []), docker: false, folds: false, [], isMacOS: false));
        Assert.Empty(Warnings(Plan([], ["caf\u00e9"]), docker: false, folds: false, [], isMacOS: true));
    }

    [Fact]
    public void TheCaseWarningIsNotDuplicatedByTheUnicodeWarning()
    {
        Assert.Equal([MaskMessages.CaseWarning],
            Warnings(Plan(["caf\u00e9.env"], []), docker: false, folds: true, [], isMacOS: true));
    }
}
