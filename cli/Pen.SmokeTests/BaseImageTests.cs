using System.Runtime.InteropServices;

namespace Pen.SmokeTests;

/// <summary>What pen:latest holds, and doesn't, as built from the embedded Dockerfile.</summary>
public sealed class BaseImageTests(BaseImageTests.Fixture fixture) : IClassFixture<BaseImageTests.Fixture>
{
    private const string Image = "pen:latest";

    private ProbeResult Probes => fixture.Probes;

    [Fact(DisplayName = "uname -m matches the host's architecture")]
    public void ArchitectureMatchesTheHost()
    {
        var hostArch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "aarch64",
            Architecture.X64 => "x86_64",
            var other => other.ToString(),
        };

        Assert.True(Probes.Session.HasStdoutLine($"uname={hostArch}"), $"uname -m matches the host ({hostArch})");
    }

    [Fact(DisplayName = "Ubuntu 24.04")]
    public void IsUbuntu2404() => AssertPassed("ubuntu");

    [Fact(DisplayName = "no users or groups with ID 1000–60000 in the image")]
    public void HasNoUsersOrGroupsInTheUserRange() => AssertPassed("no_user_ids");

    [Fact(DisplayName = "nothing under /home/agent in the image")]
    public void HasNothingUnderHomeAgent() => AssertPassed("no_home_agent");

    [Fact(DisplayName = "system gitconfig: credential.https://github.com.helper=!gh auth git-credential")]
    public void SystemGitConfigUsesGhForCredentials() =>
        AssertSystemGitConfig("credential.https://github.com.helper=!gh auth git-credential");

    [Fact(DisplayName = "system gitconfig: url.https://github.com/.insteadof=git@github.com:")]
    public void SystemGitConfigRewritesScpStyleRemotes() =>
        AssertSystemGitConfig("url.https://github.com/.insteadof=git@github.com:");

    [Fact(DisplayName = "system gitconfig: url.https://github.com/.insteadof=ssh://git@github.com/")]
    public void SystemGitConfigRewritesSshRemotes() =>
        AssertSystemGitConfig("url.https://github.com/.insteadof=ssh://git@github.com/");

    [Fact(DisplayName = "system gitconfig: safe.directory=*")]
    public void SystemGitConfigTrustsEveryDirectory() => AssertSystemGitConfig("safe.directory=*");

    [Fact(DisplayName = "system gitconfig: core.excludesfile=/etc/pen/gitignore")]
    public void SystemGitConfigHasPensExcludesFile() => AssertSystemGitConfig("core.excludesfile=/etc/pen/gitignore");

    [Fact(DisplayName = "system gitignore lists .codegraph/")]
    public void SystemGitIgnoreListsCodeGraph() => AssertPassed("gitignore_codegraph");

    [Fact(DisplayName = "signing off via GIT_CONFIG_* (commit.gpgsign, tag.gpgsign = false)")]
    public void SigningIsOff() => AssertPassed("signing_off");

    [Fact(DisplayName = "SSH remote rewritten to https")]
    public void SshRemoteIsRewrittenToHttps() => AssertPassed("ssh_rewritten");

    [Fact(DisplayName =
        "tools present (git gh rg fd jq curl wget xz openssl less vim unzip bzip2 patch socat tini capsh docker 'docker compose')")]
    public void ToolsArePresent() => AssertPassed("tools_present");

    [Fact(DisplayName = "no Python, uv or build tools in the base (python3 python uv uvx make gcc cc)")]
    public void HasNoPythonOrBuildTools() => AssertPassed("no_python");

    [Fact(DisplayName = "no node, npm or npx on PATH in the base")]
    public void HasNoNodeOnPath() => AssertPassed("no_node");

    [Fact(DisplayName = "Pen's private Node runs (/opt/pen/node/bin/node)")]
    public void PrivateNodeRuns() => AssertPassed("private_node");

    [Fact(DisplayName = "the npm agent helper is in the base, root's and off PATH (/opt/pen/lib/npm-agent.sh)")]
    public void NpmAgentHelperIsRootsAndOffPath() => AssertPassed("npm_agent_helper");

    [Fact(DisplayName = "time zone data and /etc/services (tzdata, netbase)")]
    public void HasTimeZonesAndServices() => AssertPassed("tzdata_netbase");

    [Fact(DisplayName = "no .NET, PowerShell or CodeGraph in the base (they're tools)")]
    public void HasNoDotNet() => AssertPassed("no_dotnet");

    [Fact(DisplayName = "no .NET settings in the base's environment")]
    public void HasNoDotNetSettings() => AssertPassed("no_dotnet_env");

    [Fact(DisplayName = "no Claude Code in the base, or its settings (it's the claude tool)")]
    public void HasNoClaudeCode() => AssertPassed("no_claude");

    [Fact(DisplayName = "no Codex or Pi in the base, or their settings (they're the codex and pi tools)")]
    public void HasNoCodexOrPi() => AssertPassed("no_codex_pi");

    [Fact(DisplayName =
        "no agents at all in the base (claude codex opencode pi), or /opt/pen/agents, or their settings")]
    public void HasNoAgents() => AssertPassed("no_agents");

    [Fact(DisplayName = "the base's build left nothing in /tmp or root's home (compile caches, agents' first runs)")]
    public void BuildLeftNothingBehind() => AssertPassed("nothing_left");

    [Fact(DisplayName = "the base's last layer records PEN_REFRESH (/etc/pen/refresh)")]
    public void LastLayerRecordsRefresh() => AssertPassed("refresh_recorded");

    private void AssertPassed(string probe) => Assert.True(Probes.HasPassed(probe), Probes.Describe());

    private void AssertSystemGitConfig(string line) =>
        Assert.True(Probes.Session.HasStdoutLine($"sysgit: {line}"), Probes.Describe());

    /// <summary>Runs every probe in one container of pen:latest, with bash as its entrypoint and the image's own
    /// user.</summary>
    /// <remarks>
    /// One container runs what was a container per check, each command in a subshell of its own, since none of
    /// them writes anything another reads. The check for what the build left in /tmp and root's home goes first
    /// all the same, before anything else has run there.
    /// </remarks>
    public sealed class Fixture : SmokeFixture
    {
        internal ProbeResult Probes { get; private set; } = null!;

        protected override void Prepare()
        {
            var probe = new SandboxProbe
            {
                Setup = """
                    echo "uname=$(uname -m)"
                    git config --system --list | sed 's/^/sysgit: /'
                    """,
                Runner = SandboxProbe.EvalInSubshell,
            };
            probe.Add("nothing_left", """
                test -z "$(find /tmp /root/.cache /root/.config /root/.local /root/.npm -mindepth 1 2>/dev/null)"
                """);
            probe.Add("ubuntu", """
                . /etc/os-release && [ "$ID" = ubuntu ] && [ "$VERSION_ID" = 24.04 ]
                """);
            probe.Add("no_user_ids", """
                ! getent passwd | awk -F: "\$3>=1000 && \$3<=60000" | grep . && ! getent group | awk -F: "\$3>=1000 && \$3<=60000" | grep .
                """);
            probe.Add("no_home_agent", """
                ! test -e /home/agent
                """);
            probe.Add("gitignore_codegraph", """
                grep -qx '.codegraph/' /etc/pen/gitignore
                """);
            probe.Add("signing_off", """
                [ "$(git config commit.gpgsign)" = false ] && [ "$(git config tag.gpgsign)" = false ]
                """);
            probe.Add("ssh_rewritten", """
                [ "$(git ls-remote --get-url git@github.com:octo/repo.git)" = https://github.com/octo/repo.git ]
                """);
            probe.Add("tools_present", """
                for c in git gh rg fd jq curl wget xz openssl less vim unzip bzip2 patch socat tini capsh setpriv docker; do command -v $c >/dev/null || { echo "missing $c" >&2; exit 1; }; done; docker compose version >/dev/null
                """);
            probe.Add("no_python", """
                for c in python3 python uv uvx make gcc cc; do ! command -v $c >/dev/null || { echo "found $c" >&2; exit 1; }; done
                """);
            probe.Add("no_node", """
                ! command -v node && ! command -v npm && ! command -v npx && ! command -v corepack
                """);
            probe.Add("private_node", """
                /opt/pen/node/bin/node -e 'process.exit(process.version.startsWith("v") ? 0 : 1)'
                """);
            probe.Add("npm_agent_helper", """
                test -x /opt/pen/lib/npm-agent.sh && [ "$(stat -c %U:%a /opt/pen/lib /opt/pen/lib/npm-agent.sh | sort -u)" = root:755 ] && ! command -v npm-agent.sh
                """);
            probe.Add("tzdata_netbase", """
                test -f /usr/share/zoneinfo/Europe/Berlin && grep -q "^https" /etc/services
                """);
            probe.Add("no_dotnet", """
                ! command -v dotnet && ! command -v pwsh && ! command -v codegraph && ! test -e /usr/share/dotnet
                """);
            probe.Add("no_dotnet_env", """
                ! env | grep -E "^(DOTNET|ASPNETCORE|NUGET_XMLDOC|POWERSHELL)_"
                """);
            probe.Add("no_claude", """
                ! command -v claude && ! test -e /opt/pen/claude && ! env | grep -E "^(DISABLE_AUTOUPDATER|CLAUDE_CODE_)"
                """);
            probe.Add("no_codex_pi", """
                ! command -v codex && ! command -v pi && ! test -e /opt/pen/codex && ! test -e /opt/pen/pi && ! test -e /etc/codex && ! env | grep -E "^PI_"
                """);
            probe.Add("no_agents", """
                for c in claude codex opencode pi; do ! command -v $c >/dev/null && ! test -e /opt/pen/$c || exit 1; done; ! test -e /opt/pen/agents && ! env | grep -E "^(OPENCODE_|NPM_CONFIG_)"
                """);
            probe.Add("refresh_recorded", """
                test -s /etc/pen/refresh
                """);
            var container = Docker.Run("run", "--rm", "--entrypoint", "bash", Image, "-c", probe.BuildScript());
            Probes = new ProbeResult(container);
        }
    }
}
