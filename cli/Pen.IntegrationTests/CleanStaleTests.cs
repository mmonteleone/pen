using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Pen.IntegrationTests;

/// <summary>pen clean --stale: a deleted project's volumes, layers and runtime dir go, a live one's stay, and so do
/// volumes it doesn't know; tools images on an old base go, by repository.</summary>
/// <remarks>
/// It runs under the throwaway HOME, whose state holds only this class's projects: the user's own projects'
/// volumes show up there as unknown, which pen clean --stale never removes.
/// </remarks>
public sealed class CleanStaleTests(CleanStaleTests.Fixture fixture) : IClassFixture<CleanStaleTests.Fixture>
{
    private string GoneKey => fixture.GoneKey;

    [Fact(DisplayName = "both projects have volumes, and the one to delete a project layer")]
    public void BothProjectsHaveVolumes() => Assert.True(fixture.SetUpComplete);

    [Fact(DisplayName = "a stacked layer does inherit the label")]
    public void StackedLayerInheritsTheLabel() => Assert.Equal("sha256:integration-old-base", fixture.StackedLabel);

    [Fact(DisplayName = "pen clean --stale lists the deleted project as missing, with its volumes and layer")]
    public void ListsTheDeletedProject() =>
        Assert.Multiple(
            () => AssertHas(fixture.AnsweredNo, "to remove, missing"),
            () => AssertHas(fixture.AnsweredNo, $"  {fixture.GoneDir}  ({GoneKey})"),
            () => AssertHas(
                fixture.AnsweredNo,
                $"{GoneKey}_home, {GoneKey}_nuget; {GoneKey}:{fixture.GoneToolsHash}, {GoneKey}:latest"));

    [Fact(DisplayName = "...noting it may be on a volume that isn't mounted")]
    public void NotesAnUnmountedVolume() => AssertHas(fixture.AnsweredNo, "isn't mounted");

    [Fact(DisplayName = "...and the live one as kept")]
    public void ListsTheLiveOneAsKept() =>
        Assert.Multiple(
            () => Assert.True(fixture.AnsweredNo.HasLineStartingWith("kept, in use:"), fixture.AnsweredNo.Describe()),
            () => AssertHas(fixture.AnsweredNo, $"  {fixture.LiveDir}  ({fixture.LiveKey})"));

    [Fact(DisplayName = "...and the tools image on an old base")]
    public void ListsTheStaleToolsImage() =>
        Assert.Multiple(
            () => AssertHas(fixture.AnsweredNo, "tools images on an old base"),
            () => AssertHas(fixture.AnsweredNo, $"  {fixture.Stale}"));

    [Fact(DisplayName = "...asking first")]
    public void AsksFirst() => AssertHas(fixture.AnsweredNo, "[y/N] n");

    [Fact(DisplayName = "...leaving the image there for a no")]
    public void NoLeavesTheImage() => Assert.True(fixture.StaleAfterNo);

    [Fact(DisplayName = "answering no removes nothing")]
    public void NoRemovesNothing() =>
        Assert.Multiple(
            () => AssertHas(fixture.AnsweredNo, "nothing removed"),
            () => Assert.True(fixture.GoneVolumeAfterNo, "the home volume is gone"),
            () => Assert.True(fixture.GoneRuntimeAfterNo, "the runtime dir is gone"));

    [Fact(DisplayName = "a key whose project.lock a session holds is skipped")]
    public void LockedKeyIsSkipped() =>
        Assert.Multiple(
            () => Assert.True(
                fixture.WhileLocked.Stderr.Contains($"skipping {GoneKey}", StringComparison.Ordinal)
                    && fixture.WhileLocked.ExitCode == 1,
                fixture.WhileLocked.Describe()),
            () => Assert.True(fixture.GoneVolumeAfterLocked, "the home volume is gone"),
            () => Assert.True(fixture.GoneRuntimeAfterLocked, "the runtime dir is gone"));

    [Fact(DisplayName = "answering yes removes the deleted project's volumes")]
    public void YesRemovesTheVolumes() =>
        Assert.Multiple(
            () => AssertHas(fixture.AnsweredYes, $"removed {GoneKey}"),
            () => Assert.False(fixture.GoneVolumesAfterYes, "a volume is still there"));

    [Fact(DisplayName = "...its project layers")]
    public void YesRemovesTheLayers() => Assert.False(fixture.GoneLayersAfterYes);

    [Fact(DisplayName = "...but not the shared tools image they were built on")]
    public void YesKeepsTheToolsImage() => Assert.True(fixture.ToolsImageAfterYes);

    [Fact(DisplayName = "...and the tools image on an old base goes")]
    public void YesRemovesTheStaleToolsImage() => Assert.False(fixture.StaleAfterYes);

    [Fact(DisplayName = "...but not a layer stacked on it, with the same label")]
    public void YesKeepsTheStackedLayer() => Assert.True(fixture.StackedAfterYes);

    [Fact(DisplayName = "...or a project named tools, by repository")]
    public void YesKeepsAProjectNamedTools() => Assert.True(fixture.NamedAfterYes);

    [Fact(DisplayName = "...and its runtime dir")]
    public void YesRemovesTheRuntimeDir() => Assert.False(fixture.GoneRuntimeAfterYes);

    [Fact(DisplayName = "the live project keeps its volumes and runtime dir")]
    public void LiveProjectKeepsItsState() => Assert.True(fixture.LiveKeptAfterYes);

    [Fact(DisplayName = "...as does every other volume (unknown to this HOME, so never removed)")]
    public void OtherVolumesAreKept() => Assert.Equal(fixture.OtherVolumesBefore, fixture.OtherVolumesAfter);

    [Fact(DisplayName = "run again, it finds nothing stale to clean")]
    public void SecondRunFindsNothing() =>
        Assert.True(
            fixture.Again.Output.Contains("pen: nothing stale to clean", StringComparison.Ordinal)
                && fixture.Again.OutputComplete && !fixture.Again.Output.Contains(GoneKey, StringComparison.Ordinal),
            fixture.Again.Describe());

    [Fact(DisplayName = "a deleted project's key with a session that hasn't ended is kept, with its protected copies")]
    public void KeyWithAnOpenSessionIsKept() =>
        Assert.Multiple(
            () => AssertHas(fixture.WithOpenSession, "kept, sessions that haven't ended"),
            () => AssertHas(fixture.WithOpenSession, $"  {fixture.GoneDir}  ({GoneKey})"),
            () => AssertHas(
                fixture.WithOpenSession,
                $"    ~/.local/state/pen/{GoneKey}/sessions/{Fixture.OpenId}/protected (manifest.json lists them)"),
            () => AssertHas(fixture.WithOpenSession, "    if you moved the project, move it back"),
            () => AssertHas(
                fixture.WithOpenSession,
                $"    docker rm -f {GoneKey}-{Fixture.OpenId}-agent && "
                    + $"docker compose -p {GoneKey}-{Fixture.OpenId} down -t 1 --remove-orphans &&\n"),
            () => AssertHas(
                fixture.WithOpenSession,
                $"    rm -rf ~/.local/state/pen/{GoneKey}/sessions/{Fixture.OpenId} && pen clean --stale"),
            () => Assert.True(
                fixture.WithOpenSession.Output.Contains("pen: nothing stale to clean", StringComparison.Ordinal)
                    && fixture.WithOpenSession.ExitCode == 0 && fixture.WithOpenSession.OutputComplete
                    && !fixture.WithOpenSession.Output.Contains("[y/N]", StringComparison.Ordinal),
                fixture.WithOpenSession.Describe()),
            () => Assert.True(fixture.ProtectedAfterOpen, "the protected copies are gone"));

    [Fact(DisplayName = "...and removed once that session has ended")]
    public void KeyIsRemovedOnceTheSessionEnded() =>
        Assert.Multiple(
            () => Assert.True(
                fixture.AfterEnded.Output.Contains($"pen: removed {GoneKey}", StringComparison.Ordinal)
                    && fixture.AfterEnded.ExitCode == 0,
                fixture.AfterEnded.Describe()),
            () => Assert.False(fixture.GoneRuntimeAfterEnded, "the runtime dir is still there"));

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    /// <summary>Makes a project with a layer and a tools layer, which it then deletes, a live one, and
    /// tools images on an old base and on it, then runs pen clean --stale answering no, with a held lock, yes,
    /// and again; then with a session that hasn't ended in the deleted project's runtime dir, and once it
    /// has.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        internal string GoneDir { get; private set; } = null!;

        internal string GoneKey { get; private set; } = null!;

        /// <summary>Gets the tag of the gone project's layer on tools: the tools image's hash.</summary>
        internal string GoneToolsHash { get; private set; } = null!;

        internal string LiveDir { get; private set; } = null!;

        internal string LiveKey { get; private set; } = null!;

        internal bool SetUpComplete { get; private set; }

        internal string Stale { get; private set; } = null!;

        internal string Stacked { get; private set; } = null!;

        internal string Named { get; private set; } = null!;

        internal string StackedLabel { get; private set; } = null!;

        internal IReadOnlyList<string> OtherVolumesBefore { get; private set; } = [];

        internal CommandResult AnsweredNo { get; private set; } = null!;

        internal bool StaleAfterNo { get; private set; }

        internal bool GoneVolumeAfterNo { get; private set; }

        internal bool GoneRuntimeAfterNo { get; private set; }

        internal CommandResult WhileLocked { get; private set; } = null!;

        internal bool GoneVolumeAfterLocked { get; private set; }

        internal bool GoneRuntimeAfterLocked { get; private set; }

        internal CommandResult AnsweredYes { get; private set; } = null!;

        internal bool GoneVolumesAfterYes { get; private set; }

        internal bool GoneLayersAfterYes { get; private set; }

        internal bool ToolsImageAfterYes { get; private set; }

        internal bool StaleAfterYes { get; private set; }

        internal bool StackedAfterYes { get; private set; }

        internal bool NamedAfterYes { get; private set; }

        internal bool GoneRuntimeAfterYes { get; private set; }

        internal bool LiveKeptAfterYes { get; private set; }

        internal IReadOnlyList<string> OtherVolumesAfter { get; private set; } = [];

        internal CommandResult Again { get; private set; } = null!;

        /// <summary>Gets the ID of the session left open in the deleted project's runtime dir.</summary>
        internal const string OpenId = "integration-open";

        internal CommandResult WithOpenSession { get; private set; } = null!;

        internal bool ProtectedAfterOpen { get; private set; }

        internal CommandResult AfterEnded { get; private set; } = null!;

        internal bool GoneRuntimeAfterEnded { get; private set; }

        private string GoneRuntimeDir => Home.GetRuntimeDir(GoneKey);

        protected override void Prepare()
        {
            Home.MakeToolFree();
            SetUpProjects();
            SetUpToolsImages();
            AnsweredNo = Cli.WithStdin("n\n").Run("clean", "--stale");
            StaleAfterNo = HasImage(Stale);
            GoneVolumeAfterNo = HasVolume($"{GoneKey}_home");
            GoneRuntimeAfterNo = Directory.Exists(GoneRuntimeDir);

            using (Locks.Hold(Path.Combine(GoneRuntimeDir, "project.lock"), exclusive: false))
                WhileLocked = Cli.WithStdin("y\n").Run("clean", "--stale");
            GoneVolumeAfterLocked = HasVolume($"{GoneKey}_home");
            GoneRuntimeAfterLocked = Directory.Exists(GoneRuntimeDir);

            AnsweredYes = Cli.WithStdin("y\n").Run("clean", "--stale");
            GoneVolumesAfterYes = HasVolume($"{GoneKey}_home") || HasVolume($"{GoneKey}_nuget");
            GoneLayersAfterYes = HasImage($"{GoneKey}:latest") || HasImage($"{GoneKey}:{GoneToolsHash}");
            ToolsImageAfterYes = HasImage($"pen-tools:{GoneToolsHash}");
            StaleAfterYes = HasImage(Stale);
            StackedAfterYes = HasImage(Stacked);
            NamedAfterYes = HasImage(Named);
            GoneRuntimeAfterYes = Path.Exists(GoneRuntimeDir);
            var liveProjectDir = Path.Combine(Home.GetRuntimeDir(LiveKey), "project-dir");
            LiveKeptAfterYes = HasVolume($"{LiveKey}_home") && File.Exists(liveProjectDir)
                && new FileInfo(liveProjectDir).Length > 0;
            OtherVolumesAfter = ListOtherVolumes();
            Again = Cli.WithStdin("n\n").Run("clean", "--stale").RequireSuccess();

            var session = OpenSessionInGone();
            WithOpenSession = Cli.WithStdin("y\n").Run("clean", "--stale");
            ProtectedAfterOpen = File.Exists(Path.Combine(session, "protected", "manifest.json"));
            // The session ends: compose.yaml goes first, as Pen's teardown removes it.
            File.Delete(Path.Combine(session, "compose.yaml"));
            AfterEnded = Cli.WithStdin("y\n").Run("clean", "--stale");
            GoneRuntimeAfterEnded = Path.Exists(GoneRuntimeDir);
        }

        /// <summary>Puts back the deleted project's runtime dir as a Pen that died in a session there would have
        /// left it: its project-dir record, and the session's compose.yaml and protected copies.</summary>
        /// <returns>The session's dir.</returns>
        private string OpenSessionInGone()
        {
            var session = Path.Combine(GoneRuntimeDir, "sessions", OpenId);
            Directory.CreateDirectory(Path.Combine(session, "protected"));
            File.WriteAllText(Path.Combine(GoneRuntimeDir, "project-dir"), GoneDir + "\n");
            File.WriteAllText(Path.Combine(session, "protected", "manifest.json"), "{}");
            File.WriteAllText(Path.Combine(session, "compose.yaml"), "");
            return session;
        }

        /// <summary>Makes a project with a layer and a layer on a tools image, and a live project, each with a
        /// session, then deletes the first.</summary>
        private void SetUpProjects()
        {
            var gone = CreateEmptyProject(".pen-integration-gone.");
            var live = CreateEmptyProject(".pen-integration-live.");
            gone.RunGit("init", "-q");
            live.RunGit("init", "-q");
            Directory.CreateDirectory(Path.Combine(gone.Dir, ".pen"));
            File.WriteAllText(Path.Combine(gone.Dir, ".pen", "Dockerfile"), "FROM pen:latest\n");
            var inGone = Cli.WithWorkingDirectory(gone.Dir);
            inGone.Run("trust", "--yes").RequireSuccess();
            inGone.Run("run", "true").RequireSuccess();
            var shim = new DockerShim(Path.Combine(Scratch, "tools-shim"));
            var epoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            var goneImage = shim.RunSessionImage(inGone, "--tool", $"pen-test:{epoch}");
            GoneToolsHash = goneImage[(goneImage.LastIndexOf(':') + 1)..];
            Cli.WithWorkingDirectory(live.Dir).Run("run", "true").RequireSuccess();
            GoneDir = gone.Dir;
            LiveDir = live.Dir;
            GoneKey = gone.ResolveKey(Home.Path);
            LiveKey = live.ResolveKey(Home.Path);

            SetUpComplete = HasVolume($"{GoneKey}_home") && HasVolume($"{LiveKey}_home")
                && HasImage($"{GoneKey}:latest");
            OtherVolumesBefore = ListOtherVolumes();
            Files.DeleteTree(gone.Dir);
        }

        /// <summary>Builds a tools image on an old base, and what must stay though it has the same label or a like
        /// name: a layer stacked on it, which inherits its label, and a project named tools.</summary>
        private void SetUpToolsImages()
        {
            var run = Guid.NewGuid().ToString("N");
            Stale = $"pen-tools:{HashText("stale" + run)}";
            Stacked = $"{LiveKey}:{HashText("stacked" + run)}";
            Named = $"pen-tools-0123abcd:{HashText("named" + run)}";
            TrackImage(Named);
            TrackImage(Stacked);
            TrackImage(Stale);
            Build($"FROM pen:latest\nLABEL pen.base-id=sha256:integration-old-base\n", "-t", Stale);
            Build($"FROM {Stale}\n", "-t", Stacked, "-t", Named);
            StackedLabel = Docker.Run(
                "image", "inspect", "--format", "{{ index .Config.Labels \"pen.base-id\" }}", Stacked).CapturedStdout;
        }

        private IReadOnlyList<string> ListOtherVolumes() =>
        [
            .. Docker.ReadLines("volume", "ls", "-q")
                .Where(volume => !volume.StartsWith($"{GoneKey}_", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal),
        ];

        private static void Build(string dockerfile, params string[] tags)
        {
            var built = new HostCommand(IntegrationEnvironment.DockerPath, ["build", "-q", .. tags, "-"])
            {
                Stdin = dockerfile,
            }.Run();
            built.RequireSuccess();
        }

        /// <summary>Gets the first 16 hex digits of a text's SHA-256, as an image tag.</summary>
        private static string HashText(string text) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

        private static bool HasImage(string image) => Docker.Run("image", "inspect", image).ExitCode == 0;

        private static bool HasVolume(string volume) => Docker.Run("volume", "inspect", volume).ExitCode == 0;
    }
}
