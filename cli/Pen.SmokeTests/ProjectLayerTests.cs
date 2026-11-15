namespace Pen.SmokeTests;

/// <summary>The project layer from .pen/Dockerfile: approval, the build, its context and a failing build.</summary>
public sealed class ProjectLayerTests(ProjectLayerTests.Fixture fixture) : IClassFixture<ProjectLayerTests.Fixture>
{
    [Fact(DisplayName = "an unapproved .pen/ doesn't start a session without a terminal")]
    public void UnapprovedPenDirIsRefused() =>
        Assert.Multiple(
            () => AssertHas(fixture.Unapproved, "allow example.org"),
            () => AssertHas(fixture.Unapproved, ".pen/Dockerfile"),
            () => AssertHas(fixture.Unapproved, "pen trust"));

    [Fact(DisplayName = "...builds no layer")]
    public void UnapprovedBuildsNoLayer() => Assert.False(fixture.LayerAfterUnapproved);

    [Fact(DisplayName = "...and starts nothing")]
    public void UnapprovedStartsNothing() => Assert.Equal(0, fixture.ContainersAfterUnapproved);

    [Fact(DisplayName = "pen trust --yes has nothing new to approve the second time")]
    public void SecondTrustHasNothingNew() => AssertHas(fixture.SecondTrust, "nothing new");

    [Fact(DisplayName = ".pen/Dockerfile is built and used")]
    public void LayerIsBuiltAndUsed() => AssertHas(fixture.Approved, "layer-ok");

    [Fact(DisplayName = "...as <project key>:latest")]
    public void LayerIsTaggedByTheKey() => Assert.True(fixture.LayerAfterApproved);

    [Fact(DisplayName = "...and the .pen/config.toml allow entry is reachable")]
    public void ProjectAllowIsReachable() => AssertHas(fixture.Approved, "code=200");

    [Fact(DisplayName = "a FROM pen:latest layer picks up a base-image rebuild")]
    public void LayerPicksUpABaseRebuild() => Assert.Equal("rebuilt", fixture.RebuiltLabel);

    [Fact(DisplayName = "a build's context keeps modes and symlinks")]
    public void ContextKeepsModesAndSymlinks() =>
        Assert.Multiple(
            () => AssertHas(fixture.Context, "script-ran"),
            () => AssertHas(fixture.Context, "run.sh"));

    [Fact(DisplayName = "...leaves out config.toml, which isn't hashed")]
    public void ContextLeavesOutConfig() => AssertLacks(fixture.Context, "config-copied");

    [Fact(DisplayName = "...and honors .dockerignore")]
    public void ContextHonorsDockerignore() => AssertLacks(fixture.Context, "secret-copied");

    [Fact(DisplayName = "...and the private copy is gone afterwards")]
    public void PrivateCopyIsGone() => Assert.Empty(fixture.LayerCopies);

    [Fact(DisplayName = "a failing layer build stops the launch")]
    public void FailingBuildStopsTheLaunch() =>
        AssertHas(fixture.Failing, "building the project layer from .pen/Dockerfile failed");

    [Fact(DisplayName = "...showing the build output")]
    public void FailingBuildShowsItsOutput() => AssertHas(fixture.Failing, "smoke-build-error");

    [Fact(DisplayName = "...without starting anything")]
    public void FailingBuildStartsNothing() => Assert.Equal(0, fixture.ContainersAfterFailing);

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    private static void AssertLacks(CommandResult result, string text) =>
        Assert.True(
            result.OutputComplete && !result.Output.Contains(text, StringComparison.Ordinal),
            $"\"{text}\" is there; {result.Describe()}");

    /// <summary>Runs sessions with a .pen/Dockerfile before and after pen trust, after a rebuild of pen:latest,
    /// with a build context to look into, and with a failing Dockerfile; the user's config names
    /// no tools, so the layer is &lt;key&gt;:latest.</summary>
    public sealed class Fixture : SmokeFixture
    {
        private const string BaseImage = "pen:latest";

        private ReplacedImages _replaced = null!;

        internal CommandResult Unapproved { get; private set; } = null!;

        internal bool LayerAfterUnapproved { get; private set; }

        internal int ContainersAfterUnapproved { get; private set; }

        internal CommandResult SecondTrust { get; private set; } = null!;

        internal CommandResult Approved { get; private set; } = null!;

        internal bool LayerAfterApproved { get; private set; }

        /// <summary>Gets the smoke.base label of the layer built on the rebuilt pen:latest.</summary>
        internal string RebuiltLabel { get; private set; } = null!;

        internal CommandResult Context { get; private set; } = null!;

        /// <summary>Gets the runtime dir's layer-* directories after the context session.</summary>
        internal IReadOnlyList<string> LayerCopies { get; private set; } = [];

        internal CommandResult Failing { get; private set; } = null!;

        internal int ContainersAfterFailing { get; private set; }

        private string PenDir => Path.Combine(Project.Dir, ".pen");

        private string Layer => $"{Key}:latest";

        protected override void Prepare()
        {
            Home.MakeToolFree();
            _replaced = Track(new ReplacedImages());
            RunApproval();
            RunOnRebuiltBase();
            RunContext();
            RunFailingBuild();
            Files.DeleteTree(PenDir);
            Docker.Run("image", "rm", Layer);
        }

        private void RunApproval()
        {
            Files.DeleteTree(PenDir);
            Directory.CreateDirectory(PenDir);
            WriteDockerfile("FROM pen:latest\nRUN echo layer-ok >/etc/pen-smoke-layer\n");
            File.WriteAllText(Path.Combine(PenDir, "config.toml"), "allow = [\"example.org\"]\n");
            Unapproved = Cli.Run("run", "bash", "-c", "cat /etc/pen-smoke-layer");
            LayerAfterUnapproved = HasImage(Layer);
            ContainersAfterUnapproved = Docker.CountContainers(Key);
            Cli.Run("trust", "--yes").RequireSuccess();
            SecondTrust = Cli.Run("trust", "--yes");
            Approved = Cli.Run("run", "bash", "-c", """
                cat /etc/pen-smoke-layer; curl -s -o /dev/null -w "code=%{http_code}\n" -I https://example.org
                """).RequireSuccess();
            Cli.Run("run", "true").RequireSuccess();
            LayerAfterApproved = HasImage(Layer);
            _replaced.Note(Layer);
        }

        /// <summary>Runs a session on a rebuilt pen:latest (the same hash label, a new image), then puts the real
        /// one back.</summary>
        /// <remarks>
        /// The layer built on the rebuilt image is removed once its label is read, so the rebuilt image is left
        /// untagged with nothing on it, and putting pen:latest back removes it.
        /// </remarks>
        private void RunOnRebuiltBase()
        {
            using var backup = ImageBackup.Create(BaseImage, "pen");
            var rebuilt = new HostCommand(SmokeEnvironment.DockerPath, ["build", "-q", "-t", BaseImage, "-"])
            {
                Stdin = $"FROM {backup.Backup}\nLABEL smoke.base=rebuilt\n",
            }.Run();
            if (rebuilt.ExitCode != 0)
                throw new InvalidOperationException($"building the rebuilt {BaseImage}: {rebuilt.Describe()}");
            backup.RemoveWithRestore(rebuilt.CapturedStdout);
            try
            {
                Cli.Run("run", "true").RequireSuccess();
                RebuiltLabel = Docker.Run(
                    "image", "inspect", "--format", "{{ index .Config.Labels \"smoke.base\" }}", Layer)
                    .CapturedStdout;
            }
            finally
            {
                // The layer sits on the stand-in until it's gone, so it goes before the restore removes the stand-in.
                Docker.Run("image", "rm", Layer);
                backup.Restore();
            }
        }

        /// <summary>Builds from a context with an executable, a symlink, a .dockerignore and a file it ignores,
        /// read through pen's private copy of .pen/.</summary>
        private void RunContext()
        {
            WriteDockerfile("FROM pen:latest\nCOPY . /etc/pen-ctx/\n");
            File.WriteAllText(Path.Combine(PenDir, ".dockerignore"), "secret\n");
            File.WriteAllText(Path.Combine(PenDir, "secret"), "s\n");
            var script = Path.Combine(PenDir, "run.sh");
            File.WriteAllText(script, "#!/bin/sh\necho script-ran\n");
            File.SetUnixFileMode(script, (UnixFileMode)Convert.ToInt32("755", 8));
            File.CreateSymbolicLink(Path.Combine(PenDir, "link"), "run.sh");
            Cli.Run("trust", "--yes").RequireSuccess();
            Context = Cli.Run("run", "bash", "-c", """
                /etc/pen-ctx/run.sh; readlink /etc/pen-ctx/link; [ -e /etc/pen-ctx/config.toml ] && echo config-copied; [ -e /etc/pen-ctx/secret ] && echo secret-copied; true
                """).RequireSuccess();
            LayerCopies = [.. Directory.EnumerateDirectories(RuntimeDir, "layer-*")];
            foreach (var name in new[] { ".dockerignore", "secret", "run.sh", "link" })
                File.Delete(Path.Combine(PenDir, name));
        }

        private void RunFailingBuild()
        {
            WriteDockerfile("FROM pen:latest\nRUN echo smoke-build-error && exit 3\n");
            Cli.Run("trust", "--yes").RequireSuccess();
            Failing = Cli.Run("run", "true");
            ContainersAfterFailing = Docker.CountContainers(Key);
        }

        private void WriteDockerfile(string text) => File.WriteAllText(Path.Combine(PenDir, "Dockerfile"), text);

        private static bool HasImage(string image) => Docker.Run("image", "inspect", image).ExitCode == 0;
    }
}
