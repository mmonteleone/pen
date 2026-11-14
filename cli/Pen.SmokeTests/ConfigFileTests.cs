using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

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

    [Fact(DisplayName = "a TOML error is reported with its file and line")]
    public void TomlErrorIsReported() => AssertHas(fixture.TomlError, "~/.config/pen/config.toml:2:");

    [Fact(DisplayName = "an [env] value never appears in any process's arguments")]
    public void EnvValueIsNotInArguments() =>
        Assert.True(
            !fixture.ProcessArguments.Contains(fixture.Secret, StringComparison.Ordinal),
            "the secret is in a process's arguments");

    [Fact(DisplayName = "...and reaches the agent")]
    public void EnvValueReachesTheAgent() => AssertHas(fixture.EnvSession, $"[{fixture.Secret}]");

    [Fact(DisplayName = "[env] in .pen/config.toml is refused, naming the file")]
    public void EnvInProjectIsRefused() => AssertHas(fixture.ProjectEnv, ".pen/config.toml: \"env\" belongs in");

    [Fact(DisplayName = "[github] in .pen/config.toml is refused, naming the file")]
    public void GitHubInProjectIsRefused() =>
        AssertHas(fixture.ProjectGitHub, ".pen/config.toml: \"github\" belongs in");

    [Fact(DisplayName = "ports in .pen/config.toml is accepted")]
    public void PortsInProjectAreAccepted() => AssertSucceeded(fixture.ProjectPorts);

    [Fact(DisplayName = "an out-of-range port in .pen/config.toml is refused, naming the file")]
    public void OutOfRangePortIsRefused() =>
        AssertHas(fixture.ProjectPortOutOfRange, ".pen/config.toml: ports: 70000 isn't a port number");

    [Fact(DisplayName = "...and so is a quoted one")]
    public void QuotedPortIsRefused() =>
        AssertHas(fixture.ProjectPortQuoted, ".pen/config.toml: ports: \"3000\" isn't a port number");

    [Fact(DisplayName = "ports in ~/.config/pen/config.toml is refused, pointing to .pen/config.toml")]
    public void PortsInUserConfigAreRefused() =>
        AssertHas(
            fixture.UserPorts, "~/.config/pen/config.toml: \"ports\" belongs in .pen/config.toml (or use pen --port)");

    [Fact(DisplayName = "tools in ~/.config/pen/config.toml is accepted")]
    public void ToolsInUserConfigAreAccepted() => AssertSucceeded(fixture.UserTools);

    [Fact(DisplayName = "...and a version --tool can't coexist with is refused")]
    public void ConflictingFlagVersionIsRefused() =>
        AssertHas(fixture.FlagConflict, "pen-test:1 and pen-test:2 can't be used together");

    [Fact(DisplayName = "...as is one from .pen/config.toml")]
    public void ConflictingProjectVersionIsRefused() =>
        AssertHas(fixture.ProjectConflict, "pen-test:1 and pen-test:2 can't be used together");

    [Fact(DisplayName = "...but the same version is fine")]
    public void SameVersionIsFine() => AssertSucceeded(fixture.SameVersion);

    [Fact(DisplayName = "a version form the recipe doesn't want is refused, not rewritten")]
    public void UnwantedVersionFormIsRefused() =>
        AssertHas(fixture.UnwantedVersionForm, ".pen/config.toml: tools: \"01\" isn't a valid version of pen-test");

    [Fact(DisplayName = "an unknown tool in .pen/config.toml is refused, naming the file")]
    public void UnknownToolIsRefused() =>
        AssertHas(fixture.UnknownTool, ".pen/config.toml: tools: unknown tool \"nope\"");

    [Fact(DisplayName = "...and so is a shell-like version")]
    public void ShellLikeVersionIsRefused() =>
        AssertHas(fixture.ShellLikeVersion, ".pen/config.toml: tools: \"1;id\" isn't a valid version of pen-test");

    [Fact(DisplayName = "a legacy .pen/config is refused with a pointer to the new files")]
    public void LegacyConfigIsRefused() => AssertHas(fixture.LegacyConfig, ".pen/config is pen's old config format");

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    private static void AssertLacks(CommandResult result, string text) =>
        Assert.True(
            result.OutputComplete && !result.Output.Contains(text, StringComparison.Ordinal),
            $"\"{text}\" is there; {result.Describe()}");

    private static void AssertSucceeded(CommandResult result) => Assert.True(result.ExitCode == 0, result.Describe());

    /// <summary>Runs pen with a fresh ~/.config/pen twice, then once per config under test: user config.toml
    /// variants with the throwaway HOME, and .pen/config.toml variants in the project.</summary>
    public sealed class Fixture : SmokeFixture
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

        internal CommandResult TomlError { get; private set; } = null!;

        internal string Secret { get; } = $"smoke-{Environment.ProcessId}-{Random.Shared.Next(32768)}-secret";

        /// <summary>Gets every process's arguments (ps -axww -o args=) while the [env] session ran.</summary>
        internal string ProcessArguments { get; private set; } = null!;

        internal CommandResult EnvSession { get; private set; } = null!;

        internal CommandResult ProjectEnv { get; private set; } = null!;

        internal CommandResult ProjectGitHub { get; private set; } = null!;

        internal CommandResult ProjectPorts { get; private set; } = null!;

        internal CommandResult ProjectPortOutOfRange { get; private set; } = null!;

        internal CommandResult ProjectPortQuoted { get; private set; } = null!;

        internal CommandResult UserPorts { get; private set; } = null!;

        internal CommandResult UserTools { get; private set; } = null!;

        internal CommandResult FlagConflict { get; private set; } = null!;

        internal CommandResult ProjectConflict { get; private set; } = null!;

        internal CommandResult SameVersion { get; private set; } = null!;

        internal CommandResult UnwantedVersionForm { get; private set; } = null!;

        internal CommandResult UnknownTool { get; private set; } = null!;

        internal CommandResult ShellLikeVersion { get; private set; } = null!;

        internal CommandResult LegacyConfig { get; private set; } = null!;

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
            Home.WriteConfig("[env]", "X = unquoted");
            TomlError = Cli.Run("run", "true");
        }

        private void RunEnvSession()
        {
            Home.WriteConfig("[env]", $"SMOKE_SECRET = \"{Secret}\"");
            var session = Track(Cli.Start("run", "bash", "-c", "sleep 4; echo \"[$SMOKE_SECRET]\""));
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
            ProjectGitHub = RunWithProjectConfig("[github]\nacme = \"x\"\n");
            ProjectPorts = RunWithProjectConfig("ports = [3000, 5173]\n");
            ProjectPortOutOfRange = RunWithProjectConfig("ports = [70000]\n");
            ProjectPortQuoted = RunWithProjectConfig("ports = [\"3000\"]\n");
            File.Delete(ProjectConfig);
            Home.WriteConfig("ports = [3000]");
            UserPorts = Cli.Run("run", "true");
        }

        private void RunToolConfigs()
        {
            Home.WriteConfig("tools = [\"pen-test:1\"]");
            UserTools = Cli.Run("run", "true");
            FlagConflict = Cli.Run("--tool", "pen-test:2", "run", "true");
            ProjectConflict = RunWithProjectConfig("tools = [\"pen-test:2\"]\n");
            File.WriteAllText(ProjectConfig, "tools = [\"pen-test:1\"]\n");
            SameVersion = Cli.Run("--tool", "pen-test", "run", "true");

            // The rest asserts what .pen/ alone gets, with a user config that names no tools.
            Home.MakeToolFree();
            UnwantedVersionForm = RunWithProjectConfig("tools = [\"pen-test:01\"]\n");
            UnknownTool = RunWithProjectConfig("tools = [\"nope\"]\n");
            ShellLikeVersion = RunWithProjectConfig("tools = [\"pen-test:1;id\"]\n");
            File.Delete(ProjectConfig);
            Home.WriteConfig("# empty");
            File.WriteAllText(Path.Combine(PenDir, "config"), "allow = example.com\n");
            LegacyConfig = Cli.Run("run", "true");
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
