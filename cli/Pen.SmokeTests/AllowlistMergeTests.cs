using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>Where a session's allowlist comes from (defaults, the user, the project, tools), how squid takes a
/// wildcard next to a default, and the blocked line at session end.</summary>
public sealed class AllowlistMergeTests(AllowlistMergeTests.Fixture fixture)
    : IClassFixture<AllowlistMergeTests.Fixture>
{
    [Fact(DisplayName = "session end prints the blocked line")]
    public void SessionEndPrintsTheBlockedLine() =>
        Assert.True(
            fixture.Blocked.Output.Contains(
                "blocked: example.com → pen allow example.com (add --global for all projects)",
                StringComparison.Ordinal),
            fixture.Blocked.Describe());

    [Fact(DisplayName = "...and nothing when nothing was blocked")]
    public void NothingBlockedPrintsNothing() =>
        Assert.True(
            fixture.NothingBlocked.OutputComplete
                && !fixture.NothingBlocked.Output.Contains("blocked:", StringComparison.Ordinal),
            fixture.NothingBlocked.Describe());

    [Fact(DisplayName = "a domain in .pen/config.toml's allow becomes reachable")]
    public void ProjectAllowIsReachable() => AssertStdout(fixture.ProjectAllowed, "200");

    [Fact(DisplayName = "a domain no one allowed stays blocked")]
    public void UnallowedDomainStaysBlocked() =>
        Assert.True(
            !fixture.NoOneAllowed.Stdout.Contains("200", StringComparison.Ordinal), fixture.NoOneAllowed.Describe());

    [Fact(DisplayName = "...and a --tool's recipe adds its domains to the session's allowlist")]
    public void FlagToolAddsItsDomains() => AssertStdout(fixture.FlagTool, "200");

    [Fact(DisplayName = "...as does one in ~/.config/pen/config.toml")]
    public void UserToolAddsItsDomains() => AssertStdout(fixture.UserTool, "200");

    [Fact(DisplayName = ".github.com plus the default api.github.com: the proxy reports no overlap")]
    public void WildcardGivesNoOverlap() =>
        Assert.True(
            fixture.WildcardProxyLog.Contains("Processing Configuration File", StringComparison.Ordinal)
                && !Regex.IsMatch(fixture.WildcardProxyLog, "is a subdomain of|You should remove"),
            fixture.WildcardProxyLog);

    [Fact(DisplayName = "...and api.github.com is reachable")]
    public void WildcardReachesApiGitHub() => AssertStdout(fixture.Wildcard, "200");

    private static void AssertStdout(CommandResult result, string expected) =>
        Assert.True(result.CapturedStdout == expected, result.Describe());

    /// <summary>Runs sessions that request a domain with it blocked, allowed by .pen/config.toml, by a tool's
    /// recipe from --tool and from the user's config, and one whose proxy log is read with .github.com
    /// allowed.</summary>
    public sealed class Fixture : SmokeFixture
    {
        private const string ToolProbe = "curl -s -o /dev/null -w %{http_code} -I https://example.org";

        internal CommandResult Blocked { get; private set; } = null!;

        internal CommandResult NothingBlocked { get; private set; } = null!;

        internal CommandResult ProjectAllowed { get; private set; } = null!;

        internal CommandResult NoOneAllowed { get; private set; } = null!;

        internal CommandResult FlagTool { get; private set; } = null!;

        internal CommandResult UserTool { get; private set; } = null!;

        internal CommandResult Wildcard { get; private set; } = null!;

        /// <summary>Gets the docker logs of the .github.com session's proxy, stdout and stderr together.</summary>
        internal string WildcardProxyLog { get; private set; } = null!;

        protected override void Prepare()
        {
            Blocked = Cli.Run("run", "bash", "-c", "curl -s -o /dev/null -I https://example.com");
            NothingBlocked = Cli.Run("run", "bash", "-c", "curl -s -o /dev/null -I https://github.com");

            var penDir = Directory.CreateDirectory(Path.Combine(Project.Dir, ".pen")).FullName;
            File.WriteAllText(Path.Combine(penDir, "config.toml"), "allow = [\"example.com\"]\n");
            Cli.Run("trust", "--yes").RequireSuccess();
            ProjectAllowed = Cli.Run(
                "run", "bash", "-c", "curl -s -o /dev/null -w %{http_code} -I https://example.com");
            Files.DeleteTree(penDir);

            NoOneAllowed = Cli.Run("run", "bash", "-c", ToolProbe);
            FlagTool = Cli.Run("--tool", "pen-test", "run", "bash", "-c", ToolProbe);
            Home.WriteConfig("tools = [\"pen-test\"]");
            UserTool = Cli.Run("run", "bash", "-c", ToolProbe);

            Home.WriteConfig("allow = [\".github.com\"]");
            var session = Track(Cli.Start("run", "bash", "-c", """
                sleep 5; curl -s -o /dev/null -w "%{http_code}" -I https://api.github.com
                """));
            var proxyName = $"^{Key}-.*-proxy-1$";
            if (!Polling.WaitForContainer(proxyName))
                throw new InvalidOperationException($"proxy didn't start: {session.OutputSoFar}");
            Thread.Sleep(TimeSpan.FromSeconds(2));
            var proxy = Docker.ListRunningNames().First(name => Regex.IsMatch(name, proxyName));
            WildcardProxyLog = Docker.Run("logs", proxy).RequireSuccess().Output;
            Wildcard = session.WaitForExit();
        }
    }
}
