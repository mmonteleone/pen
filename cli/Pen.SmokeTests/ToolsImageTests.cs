using System.Globalization;
using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The tools image: one image per set of tools, shared across projects, tools from .pen/config.toml only
/// once approved, and a project layer stacked on tools.</summary>
/// <remarks>The tool versions are unique to the run, so no earlier image or build cache can stand in.</remarks>
public sealed class ToolsImageTests(ToolsImageTests.Fixture fixture) : IClassFixture<ToolsImageTests.Fixture>
{
    private string V => fixture.Version;

    [Fact(DisplayName = "a session with two tools runs one image, tagged pen-tools:<hash>")]
    public void TwoToolsRunOneImage() =>
        Assert.True(
            Regex.IsMatch(fixture.Tag, "^pen-tools:[0-9a-f]{16}$") && fixture.BuildsAfterFirst == 1,
            $"{fixture.Tag}, {fixture.BuildsAfterFirst} builds");

    [Fact(DisplayName = "...labeled with the base image's ID")]
    public void LabeledWithTheBaseId() => Assert.Equal(fixture.BaseId, fixture.TagBaseLabel);

    [Fact(DisplayName = "...which installed each at its version, as root")]
    public void InstalledEachAsRoot() =>
        Assert.Multiple(
            () => Assert.Contains(V, fixture.Installed, StringComparison.Ordinal),
            () => Assert.Contains("owner=0", fixture.Installed, StringComparison.Ordinal));

    [Fact(DisplayName = "...and kept no recipe scripts")]
    public void KeptNoRecipeScripts() =>
        Assert.DoesNotContain("script-left", fixture.Installed, StringComparison.Ordinal);

    [Fact(DisplayName = "the session can run the tool")]
    public void SessionRunsTheTool() => Assert.Equal(V, fixture.RunTool);

    [Fact(DisplayName = "...and has its recipe's env, literally")]
    public void SessionHasTheRecipesEnv() => Assert.Equal("from \"pen-test\" $HOME \\ here", fixture.RecipeEnv);

    [Fact(DisplayName = "swapping the flags runs the same image")]
    public void SwappedFlagsRunTheSameImage() => Assert.Equal(fixture.Tag, fixture.SwappedTag);

    [Fact(DisplayName = "...without a rebuild")]
    public void SwappedFlagsDontRebuild() => Assert.Equal(1, fixture.BuildsAfterSwap);

    [Fact(DisplayName = "...and the image is untouched")]
    public void SwappedFlagsLeaveTheImage() => Assert.Equal(fixture.TagId, fixture.TagIdAfterSwap);

    [Fact(DisplayName = "a session in another project shares that one image")]
    public void OtherProjectSharesTheImage() => Assert.Equal(fixture.Tag, fixture.OtherProjectTag);

    [Fact(DisplayName = "...also without a rebuild")]
    public void OtherProjectDoesntRebuild() => Assert.Equal(1, fixture.BuildsAfterOtherProject);

    [Fact(DisplayName = "...and runs the tool from it")]
    public void OtherProjectRunsTheTool() => Assert.Equal(V, fixture.OtherProjectRun);

    [Fact(DisplayName = "another set of tools is another image")]
    public void OtherSetIsOtherImage() =>
        Assert.True(
            fixture.Many != fixture.Tag && fixture.BuildsAfterMany == 2,
            $"{fixture.Many} vs {fixture.Tag}, {fixture.BuildsAfterMany} builds");

    [Fact(DisplayName = "...and versions of one tool reach its script as separate arguments")]
    public void VersionsAreSeparateArguments() => Assert.Equal("2", fixture.ManyVersions);

    [Fact(DisplayName = "pen ps shows a session's tools")]
    public void PsShowsTheTools() =>
        Assert.True(
            fixture.Ps.Stdout.Contains("TOOLS", StringComparison.Ordinal)
                && fixture.Ps.Stdout.Contains(fixture.A, StringComparison.Ordinal),
            fixture.Ps.Describe());

    [Fact(DisplayName = "an unapproved tool in .pen/ refuses a session without a terminal, showing its domains")]
    public void UnapprovedToolIsRefused() =>
        AssertRefused(
            fixture.Unapproved,
            $"tool pen-test:{fixture.U}: sessions run with it installed, and the sandbox can reach example.org",
            "pen trust");

    [Fact(DisplayName = "...and builds nothing")]
    public void UnapprovedBuildsNothing() =>
        Assert.Equal(fixture.BuildsBeforeUnapproved, fixture.BuildsAfterUnapproved);

    [Fact(DisplayName = "pen trust --list shows it")]
    public void TrustListShowsIt() =>
        Assert.Contains($"tool pen-test:{fixture.U}", fixture.TrustList.Stdout, StringComparison.Ordinal);

    [Fact(DisplayName = "...and approves nothing")]
    public void TrustListApprovesNothing() =>
        Assert.True(fixture.AfterList.ExitCode != 0, fixture.AfterList.Describe());

    [Fact(DisplayName = "a --tool for the same entry doesn't ask")]
    public void SameEntryAsFlagDoesntAsk() => Assert.Equal(fixture.U, fixture.FlagForEntry.CapturedStdout);

    [Fact(DisplayName = "...and doesn't approve the entry")]
    public void FlagDoesntApproveTheEntry() =>
        Assert.True(fixture.AfterFlag.ExitCode != 0, fixture.AfterFlag.Describe());

    [Fact(DisplayName = "once approved, a session runs with it")]
    public void ApprovedToolRuns() => Assert.Equal(fixture.U, fixture.ApprovedRun.CapturedStdout);

    [Fact(DisplayName = "...and reaches its domain")]
    public void ApprovedToolReachesItsDomain() => Assert.Equal("200", fixture.ApprovedDomain.CapturedStdout);

    [Fact(DisplayName = "a domain the approval doesn't record asks again")]
    public void UnrecordedDomainAsksAgain() =>
        AssertRefused(
            fixture.UnrecordedDomain,
            $"tool pen-test:{fixture.U} now brings more domains: the sandbox can reach example.org");

    [Fact(DisplayName = "...and pen trust --list shows it")]
    public void TrustListShowsTheDomain() =>
        Assert.Contains("now brings more domains", fixture.UnrecordedList.Stdout, StringComparison.Ordinal);

    [Fact(DisplayName = "changing the version asks again")]
    public void ChangedVersionAsksAgain() => AssertRefused(fixture.ChangedVersion, $"tool pen-test:{fixture.NextU}");

    [Fact(DisplayName = "an unapproved .pen/ isn't stacked on tools")]
    public void UnapprovedLayerIsntStacked() =>
        AssertRefused(fixture.UnapprovedLayer, ".pen/ asks for things you haven't approved");

    [Fact(DisplayName = "a project layer on tools is <key>:<hash of the tools image>")]
    public void LayerOnToolsIsTaggedByTheToolsHash()
    {
        var layer = fixture.Layer1;
        var hash = layer[(layer.LastIndexOf(':') + 1)..];

        Assert.True(
            layer.StartsWith($"{fixture.Key}:", StringComparison.Ordinal) && Regex.IsMatch(hash, "^[0-9a-f]{16}$")
                && fixture.Layer1ToolsImageExists,
            $"{layer}; pen-tools:{hash} exists: {fixture.Layer1ToolsImageExists}");
    }

    [Fact(DisplayName = "...and its Dockerfile can RUN the tool")]
    public void LayerCanRunTheTool() => Assert.Equal(V, fixture.Layer1Tool);

    [Fact(DisplayName = "another tool set in the project is another image")]
    public void OtherToolSetIsOtherLayer() =>
        Assert.True(
            fixture.Layer1 != fixture.Layer2 && fixture.Layer2.StartsWith($"{fixture.Key}:", StringComparison.Ordinal),
            $"{fixture.Layer1} vs {fixture.Layer2}");

    [Fact(DisplayName = "...with that set's tool")]
    public void OtherLayerHasItsTool() => Assert.Equal(fixture.A2Version, fixture.Layer2Tool);

    [Fact(DisplayName = "...leaving the first as it was")]
    public void FirstLayerIsUnchanged() => Assert.Equal(V, fixture.Layer1ToolAfterLayer2);

    [Fact(DisplayName = "building the first again gives the same image")]
    public void RebuildGivesTheSameImage() => Assert.Equal(fixture.Layer1Built, fixture.Layer1BuiltAgain);

    [Fact(DisplayName = "a changed Dockerfile isn't built on tools until approved")]
    public void ChangedLayerNeedsApproval() =>
        AssertRefused(fixture.ChangedLayer, ".pen/ asks for things you haven't approved");

    [Fact(DisplayName = "...leaving the image as it was")]
    public void ChangedLayerLeavesTheImage() => Assert.Equal(fixture.Layer1Built, fixture.Layer1BuiltAfterChange);

    [Fact(DisplayName = "once approved, it keeps the same tag")]
    public void ApprovedChangeKeepsTheTag() => Assert.Equal(fixture.Layer1, fixture.ChangedLayerTag);

    [Fact(DisplayName = "...which now holds the new layer, not the old one")]
    public void TagHoldsTheNewLayer() => Assert.Equal("changed", fixture.ChangedLayerContent);

    [Fact(DisplayName = "with no tools, a project layer is still <key>:latest, on plain pen:latest")]
    public void LayerWithoutToolsIsLatest() => Assert.Equal($"{fixture.Key}:latest", fixture.PlainLayer);

    [Fact(DisplayName = "in a linked worktree it's <key>-<dir hash>:<hash of the tools image>")]
    public void WorktreeLayerIsTaggedByItsDir() => Assert.Equal(fixture.WorktreeLayerExpected, fixture.WorktreeLayer);

    [Fact(DisplayName = "...and can RUN the tool too")]
    public void WorktreeLayerCanRunTheTool() => Assert.Equal(V, fixture.WorktreeLayerTool);

    private static void AssertRefused(CommandResult result, params string[] messages) =>
        Assert.True(
            result.ExitCode != 0 && messages.All(message => result.Output.Contains(message, StringComparison.Ordinal)),
            $"expected a refusal saying \"{string.Join("\" and \"", messages)}\"; {result.Describe()}");

    /// <summary>Runs sessions with the pen-test tools at versions unique to the run, counting image builds through
    /// a docker shim; the user's config names no tools, so each session names its own.</summary>
    public sealed class Fixture : SmokeFixture
    {
        private DockerShim _shim = null!;

        internal string Version { get; private set; } = null!;

        internal string A => $"pen-test:{Version}";

        internal string B => $"pen-test-b:{Version}";

        /// <summary>Gets the version of the project's tool entry, V + 2.</summary>
        internal string U { get; private set; } = null!;

        /// <summary>Gets U + 1, the entry's changed version.</summary>
        internal string NextU { get; private set; } = null!;

        /// <summary>Gets V + 1, the second tool set's version.</summary>
        internal string A2Version { get; private set; } = null!;

        internal string Tag { get; private set; } = null!;

        internal int BuildsAfterFirst { get; private set; }

        internal string BaseId { get; private set; } = null!;

        internal string TagBaseLabel { get; private set; } = null!;

        internal string TagId { get; private set; } = null!;

        internal string Installed { get; private set; } = null!;

        internal string RunTool { get; private set; } = null!;

        internal string RecipeEnv { get; private set; } = null!;

        internal string SwappedTag { get; private set; } = null!;

        internal int BuildsAfterSwap { get; private set; }

        internal string TagIdAfterSwap { get; private set; } = null!;

        internal string OtherProjectTag { get; private set; } = null!;

        internal int BuildsAfterOtherProject { get; private set; }

        internal string OtherProjectRun { get; private set; } = null!;

        internal string Many { get; private set; } = null!;

        internal int BuildsAfterMany { get; private set; }

        internal string ManyVersions { get; private set; } = null!;

        internal CommandResult Ps { get; private set; } = null!;

        internal int BuildsBeforeUnapproved { get; private set; }

        internal CommandResult Unapproved { get; private set; } = null!;

        internal int BuildsAfterUnapproved { get; private set; }

        internal CommandResult TrustList { get; private set; } = null!;

        internal CommandResult AfterList { get; private set; } = null!;

        internal CommandResult FlagForEntry { get; private set; } = null!;

        internal CommandResult AfterFlag { get; private set; } = null!;

        internal CommandResult ApprovedRun { get; private set; } = null!;

        internal CommandResult ApprovedDomain { get; private set; } = null!;

        internal CommandResult UnrecordedDomain { get; private set; } = null!;

        internal CommandResult UnrecordedList { get; private set; } = null!;

        internal CommandResult ChangedVersion { get; private set; } = null!;

        internal CommandResult UnapprovedLayer { get; private set; } = null!;

        internal string Layer1 { get; private set; } = null!;

        internal bool Layer1ToolsImageExists { get; private set; }

        internal string Layer1Tool { get; private set; } = null!;

        internal string Layer2 { get; private set; } = null!;

        internal string Layer2Tool { get; private set; } = null!;

        internal string Layer1ToolAfterLayer2 { get; private set; } = null!;

        /// <summary>Gets the first layer's creation time and layers: on the containerd image store a fully cached
        /// build gets a new ID, while a step that really runs again makes a new layer.</summary>
        internal string Layer1Built { get; private set; } = null!;

        internal string Layer1BuiltAgain { get; private set; } = null!;

        internal CommandResult ChangedLayer { get; private set; } = null!;

        internal string Layer1BuiltAfterChange { get; private set; } = null!;

        internal string ChangedLayerTag { get; private set; } = null!;

        internal string ChangedLayerContent { get; private set; } = null!;

        internal string PlainLayer { get; private set; } = null!;

        internal string WorktreeLayerExpected { get; private set; } = null!;

        internal string WorktreeLayer { get; private set; } = null!;

        internal string WorktreeLayerTool { get; private set; } = null!;

        private string PenDir => Path.Combine(Project.Dir, ".pen");

        protected override void Prepare()
        {
            Home.MakeToolFree();
            Files.DeleteTree(PenDir);
            _shim = new DockerShim(Path.Combine(Scratch, "tools-shim"));
            var v = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Version = Format(v);
            A2Version = Format(v + 1);
            U = Format(v + 2);
            NextU = Format(v + 3);

            RunTwoTools();
            RunOtherSets();
            RunProjectTools();
            RunLayersOnTools();
            RunWorktreeLayer();
        }

        private void RunTwoTools()
        {
            Tag = _shim.RunSessionImage(Cli, "--tool", A, "--tool", B);
            BuildsAfterFirst = _shim.Builds;
            BaseId = InspectImage("{{.Id}}", "pen:latest");
            TagBaseLabel = InspectImage("{{ index .Config.Labels \"pen.base-id\" }}", Tag);
            TagId = InspectImage("{{.Id}}", Tag);
            Installed = RunInImage(Tag, """
                ls /opt/pen-test /opt/pen-test-b; stat -c 'owner=%u' /opt/pen-test; [ -e /pen-recipe ] && echo script-left; true
                """);
            RunTool = Cli.Run("--tool", A, "--tool", B, "run", "pen-test").RequireSuccess().CapturedStdout;
            RecipeEnv = Cli.Run("--tool", A, "--tool", B, "run", "printenv", "TOOL_TEST_ENV").RequireSuccess()
                .CapturedStdout;
            SwappedTag = _shim.RunSessionImage(Cli, "--tool", B, "--tool", A);
            BuildsAfterSwap = _shim.Builds;
            TagIdAfterSwap = InspectImage("{{.Id}}", Tag);
        }

        private void RunOtherSets()
        {
            var other = CreateEmptyProject(".pen-smoke-tools.");
            other.RunGit("init", "-q");
            var inOther = Cli.WithWorkingDirectory(other.Dir);
            OtherProjectTag = _shim.RunSessionImage(inOther, "--tool", A, "--tool", B);
            BuildsAfterOtherProject = _shim.Builds;
            OtherProjectRun = inOther.Run("--tool", A, "--tool", B, "run", "pen-test").CapturedStdout;

            Many = _shim.RunSessionImage(Cli, "--tool", B, "--tool", $"pen-test-b:{A2Version}");
            BuildsAfterMany = _shim.Builds;
            ManyVersions = RunInImage(Many, "ls /opt/pen-test-b | wc -l").Replace(" ", "", StringComparison.Ordinal);

            var session = Track(Cli.Start("--tool", A, "run", "sleep", "20"));
            var agentName = new Regex($"^{Key}-.*-agent$");
            if (!Polling.WaitForContainer(agentName.ToString()))
                throw new InvalidOperationException($"the session for pen ps didn't start: {session.OutputSoFar}");
            Ps = Cli.Run("ps").RequireSuccess();
            Docker.RemoveContainers(Docker.ListRunningNames().Where(name => agentName.IsMatch(name)).Take(1));
            session.WaitForExit();
        }

        /// <summary>Runs sessions with tools in .pen/config.toml: nothing happens until they're approved.</summary>
        private void RunProjectTools()
        {
            Directory.CreateDirectory(PenDir);
            WriteProjectConfig($"tools = [\"pen-test:{U}\"]\n");
            BuildsBeforeUnapproved = _shim.Builds;
            Unapproved = _shim.Apply(Cli).Run("run", "true");
            BuildsAfterUnapproved = _shim.Builds;
            TrustList = Cli.Run("trust", "--list").RequireSuccess();
            AfterList = Cli.Run("run", "true");
            FlagForEntry = Cli.Run("--tool", $"pen-test:{U}", "run", "pen-test");
            AfterFlag = Cli.Run("run", "true");
            Cli.Run("trust", "--yes").RequireSuccess();
            ApprovedRun = Cli.Run("run", "pen-test");
            ApprovedDomain = Cli.Run(
                "run", "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}", "-I", "https://example.org");

            var store = Path.Combine(RuntimeDir, $"trust-{Project.Hash}");
            var full = File.ReadAllText(store);
            // As grep -v '^tool-allow ' writes it: each line kept, with a newline.
            var text = full.EndsWith('\n') ? full[..^1] : full;
            var lines = full.Length == 0 ? [] : text.Split('\n');
            var kept = lines.Where(line => !line.StartsWith("tool-allow ", StringComparison.Ordinal));
            File.WriteAllText(store, string.Concat(kept.Select(line => line + "\n")));
            UnrecordedDomain = Cli.Run("run", "true");
            UnrecordedList = Cli.Run("trust", "--list").RequireSuccess();
            File.WriteAllText(store, full);

            WriteProjectConfig($"tools = [\"pen-test:{NextU}\"]\n");
            ChangedVersion = Cli.Run("run", "true");
            Files.DeleteTree(PenDir);
        }

        /// <summary>Runs sessions with a project layer on tools images: the Dockerfile says FROM pen:latest and runs
        /// the tool.</summary>
        private void RunLayersOnTools()
        {
            Directory.CreateDirectory(PenDir);
            WriteDockerfile("FROM pen:latest\nRUN pen-test >/etc/pen-smoke-tool\n");
            UnapprovedLayer = Cli.Run("--tool", A, "run", "true");
            Cli.Run("trust", "--yes").RequireSuccess();
            Layer1 = _shim.RunSessionImage(Cli, "--tool", A);
            Layer1ToolsImageExists =
                Docker.Run("image", "inspect", $"pen-tools:{Layer1[(Layer1.LastIndexOf(':') + 1)..]}").ExitCode == 0;
            Layer1Tool = RunInImage(Layer1, "cat /etc/pen-smoke-tool");
            Layer2 = _shim.RunSessionImage(Cli, "--tool", $"pen-test:{A2Version}");
            Layer2Tool = RunInImage(Layer2, "cat /etc/pen-smoke-tool");
            Layer1ToolAfterLayer2 = RunInImage(Layer1, "cat /etc/pen-smoke-tool");
            Layer1Built = InspectBuilt(Layer1);
            Layer1BuiltAgain = InspectBuilt(_shim.RunSessionImage(Cli, "--tool", A));

            WriteDockerfile(
                "FROM pen:latest\nRUN pen-test >/etc/pen-smoke-tool; echo changed >/etc/pen-smoke-changed\n");
            ChangedLayer = Cli.Run("--tool", A, "run", "true");
            Layer1BuiltAfterChange = InspectBuilt(Layer1);
            Cli.Run("trust", "--yes").RequireSuccess();
            ChangedLayerTag = _shim.RunSessionImage(Cli, "--tool", A);
            ChangedLayerContent = RunInImage(Layer1, "cat /etc/pen-smoke-changed");

            WriteDockerfile("FROM pen:latest\nRUN test ! -e /opt/pen-test\n");
            Cli.Run("trust", "--yes").RequireSuccess();
            PlainLayer = _shim.RunSessionImage(Cli);
            Files.DeleteTree(PenDir);
        }

        private void RunWorktreeLayer()
        {
            // Scratch is a real path, and so is the worktree in it.
            var worktree = Path.Combine(Scratch, "wt28");
            Project.RunGit("worktree", "add", "-q", "-b", "smoke-wt28", worktree);
            var penDir = Directory.CreateDirectory(Path.Combine(worktree, ".pen")).FullName;
            File.WriteAllText(
                Path.Combine(penDir, "Dockerfile"), "FROM pen:latest\nRUN pen-test >/etc/pen-smoke-tool\n");
            var inWorktree = Cli.WithWorkingDirectory(worktree);
            inWorktree.Run("trust", "--yes").RequireSuccess();
            WorktreeLayer = _shim.RunSessionImage(inWorktree, "--tool", A);
            WorktreeLayerExpected = $"{Key}-{Files.HashPath(worktree)}:{Layer1[(Layer1.LastIndexOf(':') + 1)..]}";
            WorktreeLayerTool = RunInImage(WorktreeLayer, "cat /etc/pen-smoke-tool");
            Project.RunGit("worktree", "remove", "--force", worktree);
            Project.RunGit("branch", "-qD", "smoke-wt28");
        }

        private void WriteProjectConfig(string text) => File.WriteAllText(Path.Combine(PenDir, "config.toml"), text);

        private void WriteDockerfile(string text) => File.WriteAllText(Path.Combine(PenDir, "Dockerfile"), text);

        private static string Format(long number) => number.ToString(CultureInfo.InvariantCulture);

        private static string InspectImage(string format, string image) =>
            Docker.Run("image", "inspect", "--format", format, image).CapturedStdout;

        private static string InspectBuilt(string image) =>
            InspectImage("{{.Created}} {{json .RootFS.Layers}}", image);

        /// <summary>Runs a script in an image with sh and gets its stdout.</summary>
        private static string RunInImage(string image, string script) =>
            Docker.Run("run", "--rm", "--entrypoint", "sh", image, "-c", script).RequireSuccess().CapturedStdout;
    }
}
