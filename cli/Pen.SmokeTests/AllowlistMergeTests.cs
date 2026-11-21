using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>Where a session's allowlist comes from (defaults, the user, the project, tools), how squid takes a
/// wildcard next to a default, deny under a wildcard and in open egress, and the blocked lines at session
/// end.</summary>
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
    public void WildcardReachesApiGitHub() =>
        Assert.True(fixture.Wildcard.ExitCode == 0
            && Regex.IsMatch(fixture.Wildcard.CapturedStdout, "^CONNECT 200, HTTPS [1-5][0-9]{2}$"),
            fixture.Wildcard.Describe());

    [Fact(DisplayName = "...and the proxy starts with empty deny and open-egress files, warning about each")]
    public void EmptyDenyAndOpenFilesStartFine() =>
        Assert.True(
            fixture.WildcardProxyLog.Contains("WARNING: empty ACL: acl denied dstdomain", StringComparison.Ordinal)
                && fixture.WildcardProxyLog.Contains(
                    "WARNING: empty ACL: acl open dstdom_regex", StringComparison.Ordinal)
                && fixture.Wildcard.ExitCode == 0,
            fixture.WildcardProxyLog);

    [Fact(DisplayName = "a project deny under the user's wildcard blocks that host, in any spelling")]
    public void DenyUnderAWildcardBlocksTheHost()
    {
        var codes = Codes(fixture.DenyUnderWildcard);
        Assert.True(codes.GetValueOrDefault("gist.github.com") == "403"
            && codes.GetValueOrDefault("GIST.GitHub.COM.") == "403", fixture.DenyUnderWildcard.Describe());
    }

    [Fact(DisplayName = "...and a sibling under the wildcard stays reachable")]
    public void DenyUnderAWildcardKeepsTheSibling() =>
        Assert.True(Codes(fixture.DenyUnderWildcard).GetValueOrDefault("education.github.com") == "200",
            fixture.DenyUnderWildcard.Describe());

    [Fact(DisplayName = "...and the session-end line shows it as denied, with no pen allow for it")]
    public void DenyUnderAWildcardIsReportedAsDenied() =>
        Assert.True(
            fixture.DenyUnderWildcard.Output.Contains(
                    "pen: denied by config: gist.github.com\n", StringComparison.Ordinal)
                && !fixture.DenyUnderWildcard.Output.Contains("pen allow", StringComparison.Ordinal),
            fixture.DenyUnderWildcard.Describe());

    [Fact(DisplayName = "allow = [\"*\"] reaches an arbitrary domain, and warns at the start")]
    public void OpenEgressReachesAnyDomain() =>
        Assert.True(
            Codes(fixture.Open).GetValueOrDefault("curl example.org") == "200"
                && Codes(fixture.Open).GetValueOrDefault("example.org") == "200"
                && fixture.Open.Output.Contains(
                    "pen: egress is open: any domain is allowed (allow = [\"*\"] in ", StringComparison.Ordinal),
            fixture.Open.Describe());

    [Theory(DisplayName =
        "...but not a denied one, nor a raw IP or a name that resolves privately or not at all, by CONNECT or HTTP")]
    [InlineData("gist.github.com")]
    [InlineData("GIST.GitHub.COM")]
    [InlineData("gist.github.com.")]
    [InlineData("140.82.112.4")]
    [InlineData("0x8c.0x52.0x70.0x04")]
    [InlineData("2354212868")]
    [InlineData("[2606:4700:4700::1111]")]
    [InlineData("localhost")]
    [InlineData("proxy")] // its own service name, on the session's private network
    [InlineData("GET localhost")]
    [InlineData("GET proxy")]
    // A failed lookup must not let it through: the forwarder would resolve the name again.
    [InlineData("pen-unresolvable.invalid")]
    [InlineData("GET pen-unresolvable.invalid")]
    [InlineData("127.0.0.2.nip.io")]
    public void OpenEgressBlocks(string host) =>
        Assert.True(Codes(fixture.Open).GetValueOrDefault(host) == "403", fixture.Open.Describe());

    [Fact(DisplayName = "...while a domain allowed by name gets past the proxy's rules, though it resolves privately")]
    public void OpenEgressKeepsTheNamedDomains()
    {
        // 127.0.0.1:443 in the proxy's container refuses, so squid answers 503; 403 would mean its rules refused it.
        var code = Codes(fixture.Open).GetValueOrDefault("127.0.0.1.nip.io");
        Assert.True(code != null && code != "403" && Regex.IsMatch(code, "^[0-9]{3}$"), fixture.Open.Describe());
    }

    [Fact(DisplayName = "...and the session-end lines group them, with no pen allow")]
    public void OpenEgressReportsGroups() =>
        Assert.True(
            fixture.Open.Output.Contains("pen: denied by config: gist.github.com\n", StringComparison.Ordinal)
                && fixture.Open.Output.Contains(
                    "pen: blocked (raw IP, private address, unresolvable name, another port or blocked earlier): 140.82.112.4, "
                        + "2606:4700:4700::1111, localhost, proxy, pen-unresolvable.invalid, 127.0.0.2.nip.io\n",
                    StringComparison.Ordinal)
                && !fixture.Open.Output.Contains("pen allow", StringComparison.Ordinal),
            fixture.Open.Describe());

    /// <summary>Reads the "&lt;probe&gt; &lt;status&gt;" lines a <see cref="Fixture.Probe"/> session printed.</summary>
    private static Dictionary<string, string> Codes(CommandResult result) =>
        result.StdoutLines
            .Select(line => line.LastIndexOf(' ') is var space and > 0
                ? (line[..space], line[(space + 1)..])
                : (line, ""))
            .GroupBy(pair => pair.Item1, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Last().Item2, StringComparer.Ordinal);

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

        /// <summary>Gets the session with .github.com allowed by the user and gist.github.com denied by the
        /// project.</summary>
        internal CommandResult DenyUnderWildcard { get; private set; } = null!;

        /// <summary>Gets the session with allow = ["*", "127.0.0.1.nip.io"] from the user and gist.github.com denied
        /// by the project.</summary>
        internal CommandResult Open { get; private set; } = null!;

        /// <summary>Gets the docker logs of the .github.com session's proxy, stdout and stderr together.</summary>
        internal string WildcardProxyLog { get; private set; } = null!;

        /// <summary>
        /// A script that sends a CONNECT for each of <paramref name="hosts"/> to the proxy, exactly as written, or a
        /// plain HTTP GET for one written "GET host", and prints "&lt;host&gt; &lt;status&gt;" for each.
        /// </summary>
        internal static string Probe(params string[] hosts) =>
            """
            probe() {
              exec 3<>/dev/tcp/proxy/3128 || { echo "$1 noconnect"; return; }
              case $1 in
                "GET "*) printf 'GET http://%s/ HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n' \
                  "${1#GET }" "${1#GET }" >&3 ;;
                *) printf 'CONNECT %s:443 HTTP/1.1\r\nHost: %s:443\r\n\r\n' "$1" "$1" >&3 ;;
              esac
              code=none
              read -r -t 20 _ code _ <&3
              echo "$1 $code"
              exec 3>&-
            }
            """ + "\n" + string.Join("\n", hosts.Select(host => $"probe '{host}'")) + "\n";

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
                sleep 5
                curl -sS -o /dev/null -w "CONNECT %{http_connect}, HTTPS %{http_code}" -I https://api.github.com
                """));
            var proxyName = $"^{Key}-.*-proxy-1$";
            if (!Polling.WaitForContainer(proxyName))
                throw new InvalidOperationException($"proxy didn't start: {session.OutputSoFar}");
            Thread.Sleep(TimeSpan.FromSeconds(2));
            var proxy = Docker.ListRunningNames().First(name => Regex.IsMatch(name, proxyName));
            WildcardProxyLog = Docker.Run("logs", proxy).RequireSuccess().Output;
            Wildcard = session.WaitForExit();

            // Deny needs no approval, so no pen trust.
            Directory.CreateDirectory(penDir);
            File.WriteAllText(Path.Combine(penDir, "config.toml"), "deny = [\"gist.github.com\"]\n");
            DenyUnderWildcard = Cli.Run("run", "bash", "-c", Probe(
                "gist.github.com", "GIST.GitHub.COM.", "education.github.com"));

            // 127.0.0.1.nip.io resolves to 127.0.0.1: allowed by name, it gets past the private-address rule.
            Home.WriteConfig("allow = [\"*\", \"127.0.0.1.nip.io\"]");
            Open = Cli.Run("run", "bash", "-c",
                "echo \"curl example.org $(curl -s -o /dev/null -w %{http_code} -I https://example.org)\"\n" + Probe(
                    "example.org", "gist.github.com", "GIST.GitHub.COM", "gist.github.com.", "140.82.112.4",
                    "0x8c.0x52.0x70.0x04", "2354212868", "[2606:4700:4700::1111]", "localhost", "proxy",
                    "GET localhost", "GET proxy", "pen-unresolvable.invalid", "GET pen-unresolvable.invalid",
                    "127.0.0.1.nip.io", "127.0.0.2.nip.io"));
            Files.DeleteTree(penDir);
        }
    }
}
