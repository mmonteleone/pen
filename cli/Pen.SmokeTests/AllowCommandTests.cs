using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>pen allow &lt;domain…&gt; and pen deny &lt;domain…&gt;: what they write to .pen/config.toml or, with
/// --global, the user's config, and how they reach running sessions without a restart.</summary>
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

    [Fact(DisplayName = "pen deny applies to a running session")]
    public void DenyAppliesLive() => AssertHas(fixture.Deny, "applied to 1 running session");

    [Fact(DisplayName = "...which can no longer reach the host it reached before")]
    public void DeniedHostIsBlocked() =>
        Assert.True(
            fixture.EgressSession.OutputSoFar.Contains("com-reachable", StringComparison.Ordinal)
                && fixture.EgressSession.OutputSoFar.Contains("com-denied", StringComparison.Ordinal),
            fixture.EgressSession.OutputSoFar);

    [Fact(DisplayName = "pen allow warns when a deny still covers the domain")]
    public void AllowWarnsThatDenyWins() =>
        AssertHas(fixture.AllowDenied, "example.com is also denied by .pen/config.toml; deny wins");

    [Fact(DisplayName = "pen deny lists the denied domain with its file")]
    public void DenyListsIt() =>
        Assert.True(
            fixture.DenyListing.StdoutLines.Any(line => line.StartsWith("  example.com ", StringComparison.Ordinal)
                && line.Contains(".pen/config.toml", StringComparison.Ordinal)),
            fixture.DenyListing.Describe());

    [Fact(DisplayName = "pen allow '*' opens a running session's egress")]
    public void StarOpensLive() =>
        Assert.Multiple(
            () => AssertHas(fixture.AllowStar, "applied to 1 running session"),
            () => Assert.True(
                fixture.EgressSession.OutputSoFar.Contains("org-open", StringComparison.Ordinal),
                fixture.EgressSession.OutputSoFar));

    [Fact(DisplayName = "...and pen ps marks it open")]
    public void PsMarksItOpen() =>
        Assert.True(
            fixture.PsWhileOpen.StdoutLines.Any(line => line.Contains(fixture.EgressSessionId, StringComparison.Ordinal)
                && Regex.IsMatch(line, @"\sopen\s")),
            $"session {fixture.EgressSessionId}; {fixture.PsWhileOpen.Describe()}");

    [Fact(DisplayName = "...and once '*' is out of the config, the next pen deny closes it again")]
    public void TheNextDenyClosesIt() =>
        Assert.Multiple(
            () => AssertHas(fixture.DenyAfterClosing, "applied to 1 running session"),
            () => AssertHas(fixture.EgressSessionResult, "org-closed"));

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

        internal RunningCommand EgressSession { get; private set; } = null!;

        internal string EgressSessionId { get; private set; } = "";

        internal CommandResult EgressSessionResult { get; private set; } = null!;

        internal CommandResult Deny { get; private set; } = null!;

        internal CommandResult AllowDenied { get; private set; } = null!;

        internal CommandResult DenyListing { get; private set; } = null!;

        internal CommandResult AllowStar { get; private set; } = null!;

        internal CommandResult PsWhileOpen { get; private set; } = null!;

        internal CommandResult DenyAfterClosing { get; private set; } = null!;

        internal string UserConfigMode { get; private set; } = null!;

        private string ProjectConfig => Path.Combine(Project.Dir, ".pen", "config.toml");

        protected override void Prepare()
        {
            Home.MakeToolFree();
            RunAllowWithTwoSessions();
            RunAllowWithATool();
            RunAllowGlobal();
            RunDenyAndOpenEgress();
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

        /// <summary>
        /// Runs pen deny on a domain a running session reaches, then pen allow for it, pen allow '*', and once '*'
        /// is taken out of .pen/config.toml by hand, another pen deny, each after the session saw the one before.
        /// </summary>
        private void RunDenyAndOpenEgress()
        {
            var session = Track(Cli.Start("run", "bash", "-c", """
                code() { curl -s --max-time 5 -o /dev/null -w %{http_code} -I "$1"; }
                until_ok() { for i in $(seq 90); do [ "$(code "$1")" = 200 ] && { echo "$2"; return; }; sleep 0.5; done; echo "never-$2"; exit 1; }
                until_blocked() { for i in $(seq 90); do [ "$(code "$1")" != 200 ] && { echo "$2"; return; }; sleep 0.5; done; echo "never-$2"; exit 1; }
                until_ok https://example.com com-reachable
                until_blocked https://example.com com-denied
                until_ok https://example.org org-open
                until_blocked https://example.org org-closed
                """));
            EgressSession = session;
            if (!Polling.WaitForContainer($"^{Key}-.*-agent$"))
                throw new InvalidOperationException($"the egress session didn't start: {session.OutputSoFar}");
            var agent = new Regex($"^{Key}-([0-9a-f]{{8}})-(.*-)?agent$");
            EgressSessionId = Docker.ListRunningNames().Select(name => agent.Match(name))
                .FirstOrDefault(match => match.Success)?.Groups[1].Value ?? "?";

            WaitForOutput(session, "com-reachable");
            Deny = Cli.Run("deny", "example.com").RequireSuccess();
            WaitForOutput(session, "com-denied");
            AllowDenied = Cli.Run("allow", "example.com").RequireSuccess();
            DenyListing = Cli.Run("deny").RequireSuccess();
            AllowStar = Cli.Run("allow", "*").RequireSuccess();
            WaitForOutput(session, "org-open");
            PsWhileOpen = Cli.Run("ps").RequireSuccess();

            File.WriteAllLines(ProjectConfig, File.ReadAllLines(ProjectConfig).Where(line => line.Trim() != "\"*\","));
            DenyAfterClosing = Cli.Run("deny", "pastebin.com").RequireSuccess();
            EgressSessionResult = session.WaitForExit();
        }

        /// <summary>Waits up to 60 s for <paramref name="marker"/> in <paramref name="session"/>'s output.</summary>
        private static void WaitForOutput(RunningCommand session, string marker)
        {
            if (!Polling.WaitUntil(
                    () => session.OutputSoFar.Contains(marker, StringComparison.Ordinal) || session.HasExited,
                    TimeSpan.FromSeconds(60))
                || !session.OutputSoFar.Contains(marker, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"no {marker} from the session: {session.OutputSoFar}");
            }
        }
    }
}
