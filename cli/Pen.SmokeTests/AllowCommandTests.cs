using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>pen allow &lt;domain…&gt;: what it writes to .pen/config.toml or, with --global, the user's config, and
/// how it reaches running sessions without a restart.</summary>
public sealed class AllowCommandTests(AllowCommandTests.Fixture fixture) : IClassFixture<AllowCommandTests.Fixture>
{
    [Fact(DisplayName = "pen allow applies to both running sessions")]
    public void AppliesToBothSessions() => AssertHas(fixture.Allow, "applied to 2 running sessions");

    [Fact(DisplayName = "...and both reach the domain with no restart")]
    public void BothReachTheDomain() =>
        Assert.Multiple(
            () => AssertHas(fixture.FirstSession, "got-200"),
            () => AssertHas(fixture.SecondSession, "got-200"));

    [Fact(DisplayName = "...whose end line no longer suggests allowing it")]
    public void EndLineNoLongerSuggestsIt() =>
        Assert.True(
            fixture.FirstSession.OutputComplete
                && !fixture.FirstSession.Output.Contains("pen allow example.com", StringComparison.Ordinal),
            fixture.FirstSession.Describe());

    [Fact(DisplayName = "it creates .pen/config.toml with the commented stub")]
    public void CreatesTheStub()
    {
        var lines = fixture.ProjectConfigLines;

        Assert.True(
            lines.Count > 0 && lines[0].StartsWith("# pen project config", StringComparison.Ordinal)
                && lines.Any(line => line.StartsWith("# github_owner", StringComparison.Ordinal)),
            string.Join('\n', lines));
    }

    [Fact(DisplayName = "...plus an allow list with the domain")]
    public void AddsTheDomain() =>
        Assert.Equal(1, fixture.ProjectConfigLines.Count(line => line == "  \"example.com\","));

    [Fact(DisplayName = "running it again adds nothing")]
    public void RunningAgainAddsNothing() =>
        Assert.Multiple(
            () => AssertHas(fixture.AllowAgain, "already in .pen/config.toml"),
            () => Assert.Equal(
                1,
                fixture.ProjectConfigLinesAfterAgain.Count(
                    line => line.Contains("example.com", StringComparison.Ordinal))));

    [Fact(DisplayName = "pen allow applies to a session with a tool")]
    public void AppliesToASessionWithATool() => AssertHas(fixture.AllowWithTool, "applied to 1 running session");

    [Fact(DisplayName = "...which keeps the domains its tool brought")]
    public void ToolSessionKeepsItsDomains() => AssertHas(fixture.ToolSession, "tool-domain-200");

    [Fact(DisplayName = "pen allow --global adds to ~/.config/pen/config.toml, keeping what was there")]
    public void GlobalAddsToTheUserConfig()
    {
        var lines = fixture.UserConfigLines;

        Assert.True(
            lines.Contains("  \"example.net\",") && lines.Contains("# none")
                && lines.Any(line => line.Contains(".github.com", StringComparison.Ordinal))
                && fixture.UserConfigMode == "600",
            $"mode {fixture.UserConfigMode}:\n{string.Join('\n', lines)}");
    }

    [Fact(DisplayName = "...and reaches a running session of another project")]
    public void GlobalReachesAnotherProject() =>
        Assert.Multiple(
            () => AssertHas(fixture.AllowGlobal, "applied to 1 running session"),
            () => AssertHas(fixture.OtherSession, "got-200"));

    [Fact(DisplayName = "...whose reconfigure with .github.com plus api.github.com is clean")]
    public void GlobalReconfigureIsClean()
    {
        var log = fixture.OtherProxyLog;

        // A reconfigured Squid, no longer root, logs "ERROR: Cannot open cache_log (/dev/stderr)" and keeps
        // writing to stderr anyway: harmless.
        Assert.True(
            log.Contains("Reconfiguring Squid Cache", StringComparison.Ordinal)
                && !Regex.IsMatch(log, "is a subdomain of|You should remove|FATAL"),
            log);
    }

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    /// <summary>Runs pen allow while two sessions, then a session with a tool, wait for a domain, then pen allow
    /// --global while another project's session waits; the user's config names no tools.</summary>
    public sealed class Fixture : SmokeFixture
    {
        private const string Loop = """
            for i in $(seq 60); do [ "$(curl -s -o /dev/null -w %{http_code} -I https://example.com)" = 200 ] && { echo got-200; exit 0; }; sleep 0.5; done; echo never-200
            """;

        internal CommandResult FirstSession { get; private set; } = null!;

        internal CommandResult SecondSession { get; private set; } = null!;

        internal CommandResult Allow { get; private set; } = null!;

        internal IReadOnlyList<string> ProjectConfigLines { get; private set; } = [];

        internal CommandResult AllowAgain { get; private set; } = null!;

        internal IReadOnlyList<string> ProjectConfigLinesAfterAgain { get; private set; } = [];

        internal CommandResult AllowWithTool { get; private set; } = null!;

        internal CommandResult ToolSession { get; private set; } = null!;

        internal CommandResult AllowGlobal { get; private set; } = null!;

        internal CommandResult OtherSession { get; private set; } = null!;

        internal string OtherProxyLog { get; private set; } = null!;

        internal IReadOnlyList<string> UserConfigLines { get; private set; } = [];

        internal string UserConfigMode { get; private set; } = null!;

        private string ProjectConfig => Path.Combine(Project.Dir, ".pen", "config.toml");

        protected override void Prepare()
        {
            Home.MakeToolFree();
            RunAllowWithTwoSessions();
            RunAllowWithATool();
            RunAllowGlobal();
        }

        private void RunAllowWithTwoSessions()
        {
            var first = Track(Cli.Start("run", "bash", "-c", Loop));
            var second = Track(Cli.Start("run", "bash", "-c", Loop));
            if (!Polling.WaitForContainers($"^{Key}-.*-agent$", 2))
            {
                throw new InvalidOperationException(
                    $"two sessions didn't start: {first.OutputSoFar}{second.OutputSoFar}");
            }
            Allow = Cli.Run("allow", "example.com").RequireSuccess();
            FirstSession = first.WaitForExit();
            SecondSession = second.WaitForExit();
            ProjectConfigLines = File.ReadAllLines(ProjectConfig);
            AllowAgain = Cli.Run("allow", "example.com").RequireSuccess();
            ProjectConfigLinesAfterAgain = File.ReadAllLines(ProjectConfig);
        }

        /// <summary>Runs pen allow while a session with a tool waits: only the session knows about the flag, so its
        /// record has to keep the tool's domains.</summary>
        private void RunAllowWithATool()
        {
            var session = Track(Cli.Start("--tool", "pen-test", "run", "bash", "-c", """
                for i in $(seq 60); do [ "$(curl -s -o /dev/null -w %{http_code} -I https://example.net)" = 200 ] && break; sleep 0.5; done; curl -s -o /dev/null -w "tool-domain-%{http_code}\n" -I https://example.org
                """));
            if (!Polling.WaitForContainers($"^{Key}-.*-agent$", 1))
                throw new InvalidOperationException($"the session with a tool didn't start: {session.OutputSoFar}");
            AllowWithTool = Cli.Run("allow", "example.net").RequireSuccess();
            ToolSession = session.WaitForExit();
        }

        /// <summary>Runs pen allow --global while another project's session waits for example.net, kept up after
        /// that so its proxy's logs can be read.</summary>
        private void RunAllowGlobal()
        {
            var other = CreateEmptyProject(".pen-smoke-other.");
            other.RunGit("init", "-q");
            Home.WriteConfig("# none", "allow = [\".github.com\"]");
            var loop = Loop.Replace("example.com", "example.net", StringComparison.Ordinal);
            var session = Track(Cli.WithWorkingDirectory(other.Dir).Start("run", "bash", "-c", $"{loop}; sleep 4"));
            if (!Polling.WaitForContainer($"^pen-.*-{other.Hash}-.*-agent$"))
                throw new InvalidOperationException($"the other project's session didn't start: {session.OutputSoFar}");
            AllowGlobal = Cli.Run("allow", "--global", "example.net").RequireSuccess();
            var proxyName = new Regex($"^pen-.*-{other.Hash}-.*-proxy-1$");
            var proxy = Docker.ListRunningNames().FirstOrDefault(proxyName.IsMatch);
            Thread.Sleep(TimeSpan.FromSeconds(1));
            OtherProxyLog = proxy == null ? "" : Docker.Run("logs", proxy).Output;
            OtherSession = session.WaitForExit();
            UserConfigLines = File.ReadAllLines(Home.ConfigPath);
            UserConfigMode = Files.GetMode(Home.ConfigPath);
        }
    }
}
