using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The squid proxy's allowlist and the sandbox's network: what gets out, what doesn't, and the log.</summary>
public sealed class ProxyNetworkTests(ProxyNetworkTests.Fixture fixture) : IClassFixture<ProxyNetworkTests.Fixture>
{
    private ProbeResult Probes => fixture.Probes;

    [Fact(DisplayName = "github.com → 200 through the proxy")]
    public void GitHubIsAllowed() => AssertCode("github", "200", connect: false);

    [Fact(DisplayName = "example.com → 403 from the proxy")]
    public void ExampleComIsDenied() => AssertCode("example", "403", connect: true);

    [Fact(DisplayName = "plain http://example.com → 403 from the proxy")]
    public void PlainHttpIsDenied() => AssertCode("plain", "403", connect: false);

    [Fact(DisplayName = "bare IP ($gh_ip) → 403 from the proxy")]
    public void BareIpIsDenied() => AssertCode("bare_ip", "403", connect: true);

    [Fact(DisplayName = "--noproxy '*' fails (fail-closed)")]
    public void NoProxyFails() => Assert.True(Probes.HasPassed("noproxy_fails"), Probes.Describe());

    [Fact(DisplayName = "api.anthropic.com → 403 in a session without agents or tools (they bring their domains)")]
    public void AnthropicIsDeniedWithoutAgents() => AssertCode("api_anthropic_com", "403", connect: true);

    [Fact(DisplayName = "api.openai.com → 403 in a session without agents or tools (they bring their domains)")]
    public void OpenAiIsDeniedWithoutAgents() => AssertCode("api_openai_com", "403", connect: true);

    [Fact(DisplayName = "opencode.ai → 403 in a session without agents or tools (they bring their domains)")]
    public void OpenCodeIsDeniedWithoutAgents() => AssertCode("opencode_ai", "403", connect: true);

    [Fact(DisplayName = "registry.npmjs.org → 403 in a session without agents or tools (they bring their domains)")]
    public void NpmIsDeniedWithoutAgents() => AssertCode("registry_npmjs_org", "403", connect: true);

    [Fact(DisplayName = "port other than 443 refused for CONNECT")]
    public void OtherPortsAreRefused() => AssertCode("port_8443", "403", connect: true);

    [Fact(DisplayName = "a session leaves no anonymous volumes behind (squid's log and spool dirs are tmpfs)")]
    public void LeavesNoAnonymousVolumes() =>
        Assert.True(fixture.NewDanglingVolumes.Count == 0, string.Join('\n', fixture.NewDanglingVolumes));

    [Fact(DisplayName = "the proxy's name resolves inside the sandbox")]
    public void ProxyNameResolves() => Assert.True(Probes.HasPassed("proxy_resolves"), Probes.Describe());

    [Fact(DisplayName = "external names don't resolve inside the sandbox")]
    public void ExternalNamesDontResolve() =>
        Assert.True(Probes.HasPassed("external_doesnt_resolve"), Probes.Describe());

    [Fact(DisplayName = "proxy.log records the denial (TCP_DENIED example.com:443)")]
    public void ProxyLogRecordsTheDenial() =>
        Assert.Matches(new Regex("TCP_DENIED[A-Z_]*/403 CONNECT example.com:443"), fixture.ProxyLog);

    [Fact(DisplayName = "proxy.log records the allowed request")]
    public void ProxyLogRecordsTheAllowedRequest() =>
        Assert.Matches(new Regex("CONNECT github.com:443"), fixture.ProxyLog);

    [Fact(DisplayName = "proxy.log has no healthcheck probes")]
    public void ProxyLogHasNoHealthchecks() => Assert.DoesNotMatch(new Regex(" 127.0.0.1 "), fixture.ProxyLog);

    [Fact(DisplayName = "first request after start succeeds 10/10 (proxy healthcheck)")]
    public void FirstRequestAfterStartSucceeds() =>
        Assert.True(fixture.FirstRequestFailures.Count == 0, string.Join("\n\n", fixture.FirstRequestFailures));

    /// <summary>Checks a request's curl -w '%{http_code} %{http_connect}': the HTTP code, or the CONNECT
    /// code for a tunnel the proxy refused.</summary>
    private void AssertCode(string name, string expected, bool connect)
    {
        var prefix = $"{name}=";
        var line = Probes.Session.StdoutLines.FirstOrDefault(each => each.StartsWith(prefix, StringComparison.Ordinal));
        var codes = line?[prefix.Length..].Split(' ');
        var actual = codes?.ElementAtOrDefault(connect ? 1 : 0);

        Assert.True(actual == expected, $"{name}: expected {expected}, got \"{line}\"; {Probes.Describe()}");
    }

    /// <summary>Runs the requests and name lookups in one session, then ten sessions that each make one request
    /// right after start.</summary>
    /// <remarks>
    /// The proxy decides each request on its own, so one
    /// session makes them all. The first-request check still needs its ten sessions.
    /// </remarks>
    public sealed class Fixture : SmokeFixture
    {
        /// <summary>Gets the session's "name=code connect-code" lines and its probes.</summary>
        internal ProbeResult Probes { get; private set; } = null!;

        /// <summary>Gets the dangling volumes there after the session that weren't there before it.</summary>
        internal IReadOnlyList<string> NewDanglingVolumes { get; private set; } = [];

        /// <summary>Gets the session's proxy.log.</summary>
        internal string ProxyLog { get; private set; } = null!;

        /// <summary>Gets the results of the first-request sessions that didn't get a 200.</summary>
        internal IReadOnlyList<string> FirstRequestFailures { get; private set; } = [];

        protected override void Prepare()
        {
            (string Name, string Url)[] requests =
            [
                ("github", "https://github.com"),
                ("example", "https://example.com"),
                ("plain", "http://example.com"),
                ("bare_ip", $"https://{FindGitHubAddress()}"),
                ("api_anthropic_com", "https://api.anthropic.com"),
                ("api_openai_com", "https://api.openai.com"),
                ("opencode_ai", "https://opencode.ai"),
                ("registry_npmjs_org", "https://registry.npmjs.org"),
                ("port_8443", "https://github.com:8443"),
            ];
            var setup = new StringBuilder();
            foreach (var (name, url) in requests)
            {
                setup.Append($$"""
                    echo "{{name}}=$(curl -s -o /dev/null -w '%{http_code} %{http_connect}' -I {{url}})"

                    """);
            }
            var probe = new SandboxProbe { Setup = setup.ToString(), Runner = SandboxProbe.EvalInSubshell }
                .Add("noproxy_fails", "! curl -s -m 10 -o /dev/null --noproxy '*' https://github.com")
                // Docker Engine 23.0.11, 25.0.5 and 26.0+ don't forward DNS from an internal network
                // (CVE-2024-29018); docs/usage.md#network relies on that.
                .Add("proxy_resolves", "getent hosts proxy >/dev/null")
                .Add("external_doesnt_resolve", "! getent hosts example.com >/dev/null");

            var danglingBefore = ListDanglingVolumes();
            Probes = probe.Run(Cli);
            NewDanglingVolumes = [.. ListDanglingVolumes().Except(danglingBefore)];
            ProxyLog = File.ReadAllText(Path.Combine(SmokeProject.FindLastSession(RuntimeDir), "proxy.log"));

            List<string> failures = [];
            for (var i = 0; i < 10; i++)
            {
                var request = Cli.Run("run", "curl", "-s", "-o", "/dev/null", "-w", "%{http_code}", "-I",
                    "https://github.com");
                if (request.CapturedStdout != "200")
                    failures.Add(request.Describe());
            }
            FirstRequestFailures = failures;
        }

        private static IReadOnlyList<string> ListDanglingVolumes() =>
            Docker.ReadLines("volume", "ls", "-q", "--filter", "dangling=true");

        private static string FindGitHubAddress() =>
            Dns.GetHostAddresses("github.com").First(address => address.AddressFamily == AddressFamily.InterNetwork)
                .ToString();
    }
}
