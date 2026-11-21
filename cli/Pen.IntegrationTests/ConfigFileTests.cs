using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

/// <summary>The user's config.toml and the seed a first run creates, and which keys each config file takes.</summary>
public sealed class ConfigFileTests(ConfigFileTests.Fixture fixture) : IClassFixture<ConfigFileTests.Fixture>
{
    private static readonly Regex CommentedOutLine = new(@"^[\s]*(#|$|\[[a-z]+\]$|allow = \[$|\]$)");

    [Fact(DisplayName = "first run creates ~/.config/pen/config.toml")]
    public void FirstRunCreatesTheConfig() =>
        Assert.True(File.Exists(fixture.Home.ConfigPath), fixture.FirstRun.Describe());

    [Fact(DisplayName = "...mode 600")]
    public void ConfigIsPrivate() => Assert.Equal("600", fixture.ConfigMode);

    [Fact(DisplayName = "...with every value commented out")]
    public void EveryValueIsCommentedOut() =>
        Assert.DoesNotContain(fixture.ConfigLines, line => !CommentedOutLine.IsMatch(line));

    [Fact(DisplayName = "...and says so in one line")]
    public void SaysSoInOneLine() =>
        Assert.True(
            fixture.FirstRun.OutputLines.Count(
                line => line.Contains("created ~/.config/pen/", StringComparison.Ordinal)) == 1,
            fixture.FirstRun.Describe());

    [Fact(DisplayName = "...and creates home/ (the seed)")]
    public void CreatesTheSeed() =>
        Assert.Equal(
            [".claude/CLAUDE.md", ".claude/settings.json", ".codex/AGENTS.md", ".codex/config.toml", ".gitconfig"],
            fixture.SeedFiles);

    [Fact(DisplayName = "...whose .gitconfig has the host's user.name/user.email")]
    public void SeedHasTheHostsIdentity() =>
        Assert.Equal($"{FakeHome.GitName} <{FakeHome.GitEmail}>", fixture.SeedIdentity);

    [Fact(DisplayName = "a second run says nothing about config")]
    public void SecondRunSaysNothing() => AssertLacks(fixture.SecondRun, "created");

    [Fact(DisplayName = "...and changes neither")]
    public void SecondRunChangesNeither() =>
        Assert.Multiple(
            () => Assert.Equal(fixture.ConfigSumBefore, fixture.ConfigSumAfter),
            () => Assert.Equal(fixture.SeedSumsBefore, fixture.SeedSumsAfter));

    [Fact(DisplayName = "a misspelled key is reported with its file")]
    public void MisspelledKeyIsReported() =>
        AssertHas(fixture.Misspelled, "~/.config/pen/config.toml: unknown key \"allw\"");

    [Fact(DisplayName = "an [env] value never appears in any process's arguments")]
    public void EnvValueIsNotInArguments() =>
        Assert.True(
            !fixture.ProcessArguments.Contains(fixture.Secret, StringComparison.Ordinal),
            "the secret is in a process's arguments");

    [Fact(DisplayName = "...and reaches the agent")]
    public void EnvValueReachesTheAgent() => AssertHas(fixture.EnvSession, $"[{fixture.Secret}]");

    [Fact(DisplayName = "[env] in .pen/config.toml is refused, naming the file")]
    public void EnvInProjectIsRefused() => AssertHas(fixture.ProjectEnv, ".pen/config.toml: \"env\" belongs in");

    [Fact(DisplayName = "ports in .pen/config.toml is accepted")]
    public void PortsInProjectAreAccepted() => AssertSucceeded(fixture.ProjectPorts);

    [Fact(DisplayName = "a tool named at one version in user config, .pen/config.toml and --tool is fine")]
    public void SameVersionIsFine() => AssertSucceeded(fixture.SameVersion);

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    private static void AssertLacks(CommandResult result, string text) =>
        Assert.True(
            result.OutputComplete && !result.Output.Contains(text, StringComparison.Ordinal),
            $"\"{text}\" is there; {result.Describe()}");

    private static void AssertSucceeded(CommandResult result) => Assert.True(result.ExitCode == 0, result.Describe());

    /// <summary>Runs pen with a fresh ~/.config/pen twice, then once per config under test: user config.toml
    /// variants with the throwaway HOME, and .pen/config.toml variants in the project.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        internal CommandResult FirstRun { get; private set; } = null!;

        internal string ConfigMode { get; private set; } = null!;

        internal IReadOnlyList<string> ConfigLines { get; private set; } = [];

        /// <summary>Gets the seed's files, relative to it, sorted.</summary>
        internal IReadOnlyList<string> SeedFiles { get; private set; } = [];

        /// <summary>Gets "name &lt;email&gt;" from the seed's .gitconfig.</summary>
        internal string SeedIdentity { get; private set; } = null!;

        internal CommandResult SecondRun { get; private set; } = null!;

        internal string ConfigSumBefore { get; private set; } = null!;

        internal string ConfigSumAfter { get; private set; } = null!;

        internal IReadOnlyList<string> SeedSumsBefore { get; private set; } = [];

        internal IReadOnlyList<string> SeedSumsAfter { get; private set; } = [];

        internal CommandResult Misspelled { get; private set; } = null!;

        internal string Secret { get; } = $"integration-{Environment.ProcessId}-{Random.Shared.Next(32768)}-secret";

        /// <summary>Gets every process's arguments (ps -axww -o args=) while the [env] session ran.</summary>
        internal string ProcessArguments { get; private set; } = null!;

        internal CommandResult EnvSession { get; private set; } = null!;

        internal CommandResult ProjectEnv { get; private set; } = null!;

        internal CommandResult ProjectPorts { get; private set; } = null!;

        internal CommandResult SameVersion { get; private set; } = null!;

        private string PenDir => Path.Combine(Project.Dir, ".pen");

        private string ProjectConfig => Path.Combine(PenDir, "config.toml");

        protected override void Prepare()
        {
            RunFirstAndSecond();
            RunUserConfigErrors();
            RunEnvSession();
            RunProjectConfigs();
            RunToolConfigs();
        }

        private void RunFirstAndSecond()
        {
            // The class's first session made ~/.config/pen; this run makes it again.
            Files.DeleteTree(Path.Combine(Home.Path, ".config"));
            FirstRun = Cli.Run("run", "true").RequireSuccess();
            ConfigMode = Files.GetMode(Home.ConfigPath);
            ConfigLines = File.ReadAllLines(Home.ConfigPath);
            SeedFiles =
            [
                .. Directory.EnumerateFiles(Home.SeedDir, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(Home.SeedDir, file)).Order(StringComparer.Ordinal),
            ];
            var gitconfig = Path.Combine(Home.SeedDir, ".gitconfig");
            SeedIdentity = $"{ReadGitConfig(gitconfig, "user.name")} <{ReadGitConfig(gitconfig, "user.email")}>";

            ConfigSumBefore = HashFile(Home.ConfigPath);
            SeedSumsBefore = HashSeed();
            SecondRun = Cli.Run("run", "true").RequireSuccess();
            ConfigSumAfter = HashFile(Home.ConfigPath);
            SeedSumsAfter = HashSeed();
        }

        private void RunUserConfigErrors()
        {
            Home.WriteConfig("# comment", "allw = 1");
            Misspelled = Cli.Run("run", "true");
        }

        private void RunEnvSession()
        {
            Home.WriteConfig("[env]", $"INTEGRATION_SECRET = \"{Secret}\"");
            var session = Track(Cli.Start("run", "bash", "-c", "sleep 4; echo \"[$INTEGRATION_SECRET]\""));
            if (!Polling.WaitForContainer($"^{Key}-.*-agent$"))
                throw new InvalidOperationException($"session didn't start: {session.OutputSoFar}");
            ProcessArguments = new HostCommand("ps", ["-axww", "-o", "args="]).Run().Stdout;
            EnvSession = session.WaitForExit();
        }

        private void RunProjectConfigs()
        {
            Home.MakeToolFree();
            Directory.CreateDirectory(PenDir);
            ProjectEnv = RunWithProjectConfig("[env]\nFOO = \"x\"\n");
            ProjectPorts = RunWithProjectConfig("ports = [3000, 5173]\n");
        }

        private void RunToolConfigs()
        {
            // The user names the tool too, so the project's entry doesn't ask for approval.
            Home.WriteConfig("tools = [\"pen-test:1\"]");
            File.WriteAllText(ProjectConfig, "tools = [\"pen-test:1\"]\n");
            SameVersion = Cli.Run("--tool", "pen-test", "run", "true");
            Files.DeleteTree(PenDir);
        }

        private CommandResult RunWithProjectConfig(string text)
        {
            File.WriteAllText(ProjectConfig, text);
            return Cli.Run("run", "true");
        }

        private IReadOnlyList<string> HashSeed() =>
        [
            .. Directory.EnumerateFiles(Home.SeedDir, "*", SearchOption.AllDirectories)
                .Select(file => $"{Path.GetRelativePath(Home.SeedDir, file)} {HashFile(file)}")
                .Order(StringComparer.Ordinal),
        ];

        private static string HashFile(string path) =>
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        private static string ReadGitConfig(string file, string key) =>
            new HostCommand("git", ["config", "--file", file, key]).Run().CapturedStdout;
    }
}
