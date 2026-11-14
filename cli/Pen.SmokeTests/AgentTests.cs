using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The agents as tools: installed where the agent can't change them, with their domains, implied by a
/// command named after one, and, with credentials, answering a prompt.</summary>
public sealed class AgentTests(AgentTests.Fixture fixture) : IClassFixture<AgentTests.Fixture>
{
    private const string Prompt = "What is 2+2? Reply with just the digit.";

    private ProbeResult Agents => fixture.Agents;

    [Fact(DisplayName = "claude --version works as agent")]
    public void ClaudeVersionWorks() => AssertVersion("claude");

    [Fact(DisplayName = "codex --version works as agent")]
    public void CodexVersionWorks() => AssertVersion("codex");

    [Fact(DisplayName = "opencode --version works as agent")]
    public void OpenCodeVersionWorks() => AssertVersion("opencode");

    [Fact(DisplayName = "pi --version works as agent")]
    public void PiVersionWorks() => AssertVersion("pi");

    [Fact(DisplayName = "agents live in system paths, not $HOME")]
    public void AgentsLiveInSystemPaths() => Assert.True(Agents.HasPassed("system_paths"), Agents.Describe());

    [Fact(DisplayName = "agent user can't write the agents' install dirs")]
    public void InstallDirsAreReadOnly() => Assert.True(Agents.HasPassed("install_dirs_read_only"), Agents.Describe());

    [Fact(DisplayName =
        "the agents' recipes bring their domains (api.anthropic.com, api.openai.com, opencode.ai, models.dev, registry.npmjs.org)")]
    public void RecipesBringTheirDomains() => Assert.True(Agents.HasPassed("domains"), Agents.Describe());

    [Fact(DisplayName = "auto-updaters off (env)")]
    public void AutoUpdatersAreOff() => Assert.True(Agents.HasPassed("auto_updaters_off"), Agents.Describe());

    [Fact(DisplayName = "with an empty config, pen claude --version runs Claude Code (implied)")]
    public void ClaudeIsImplied() =>
        AssertImplied(fixture.ImpliedClaude, @"^[0-9]+\.[0-9]+\.[0-9]+ \(Claude Code\)");

    [Fact(DisplayName = "...and so does pen run claude --version")]
    public void ClaudeIsImpliedWithRun() =>
        AssertImplied(fixture.ImpliedClaudeWithRun, @"^[0-9]+\.[0-9]+\.[0-9]+ \(Claude Code\)");

    [Fact(DisplayName = "...but a shell has no agents (pen run bash -c 'command -v claude codex opencode pi')")]
    public void ShellHasNoAgents() =>
        Assert.True(fixture.ShellAgents.CapturedStdout == "none", fixture.ShellAgents.Describe());

    [Fact(DisplayName = "with an empty config, pen codex --version runs Codex (implied)")]
    public void CodexIsImplied() => AssertImplied(fixture.ImpliedCodex, @"^codex-cli [0-9]+\.[0-9]+\.[0-9]+$");

    [Fact(DisplayName = "...and pen pi --version runs Pi (implied)")]
    public void PiIsImplied() => AssertImplied(fixture.ImpliedPi, @"^[0-9]+\.[0-9]+\.[0-9]+$");

    [Fact(DisplayName = "...and pen opencode --version runs OpenCode (implied)")]
    public void OpenCodeIsImplied() => AssertImplied(fixture.ImpliedOpenCode, @"^[0-9]+\.[0-9]+\.[0-9]+$");

    [Fact(DisplayName = "pen ps shows claude:latest for an implied agent")]
    public void PsShowsTheImpliedAgent()
    {
        var project = fixture.Project.Dir;
        var home = SmokeEnvironment.RealHome;
        var tilde = project.StartsWith(home, StringComparison.Ordinal) ? "~" + project[home.Length..] : project;
        var line = new Regex(
            $"^({Regex.Escape(tilde)}|{Regex.Escape(project)}) +[0-9a-f]{{8}} +claude:latest +[0-9]+[smh][0-9m]* "
            + "+claude mcp serve$",
            RegexOptions.Multiline);

        Assert.Matches(line, fixture.PsWithImpliedAgent.CapturedStdout);
    }

    [Fact(DisplayName = "claude answers a trivial prompt through the proxy")]
    public void ClaudeAnswersAPrompt() => AssertAnswered(fixture.ClaudePrompt, "CLAUDE_CODE_OAUTH_TOKEN");

    [Fact(DisplayName = "codex answers a trivial prompt through the proxy")]
    public void CodexAnswersAPrompt() => AssertAnswered(fixture.CodexPrompt, "OPENAI_API_KEY");

    [SlowFact(DisplayName = "pen upgrade gives a new base image ID (PEN_REFRESH)")]
    public void UpgradeGivesANewBaseId()
    {
        var upgrade = fixture.Upgrade;

        Assert.True(upgrade.BaseBefore != upgrade.BaseAfter, $"base {upgrade.BaseBefore} → {upgrade.BaseAfter}");
    }

    [SlowFact(DisplayName = "...so a session's agent is reinstalled on next use (install time changes)")]
    public void UpgradeReinstallsTheAgent()
    {
        var upgrade = fixture.Upgrade;
        var installed = new Regex("^[0-9]+$");

        Assert.True(
            installed.IsMatch(upgrade.InstalledBefore) && installed.IsMatch(upgrade.InstalledAfter)
                && upgrade.InstalledBefore != upgrade.InstalledAfter,
            $"claude's install time: {upgrade.InstalledBefore} → {upgrade.InstalledAfter}");
    }

    [SlowFact(DisplayName = "pen upgrade pulls the proxy image")]
    public void UpgradePullsTheProxyImage() =>
        Assert.True(fixture.Upgrade.Log.Output.Contains("ubuntu/squid", StringComparison.Ordinal),
            fixture.Upgrade.Log.Describe());

    private void AssertVersion(string agent) =>
        Assert.True(
            Agents.Session.StdoutLines.Any(new Regex($"^{agent}=.*[0-9]+\\.[0-9]+").IsMatch),
            $"{agent} --version works as agent; {Agents.Describe()}");

    private static void AssertImplied(CommandResult result, string versionLine) =>
        Assert.True(
            result.ExitCode == 0 && new Regex(versionLine, RegexOptions.Multiline).IsMatch(result.Output),
            result.Describe());

    private static void AssertAnswered(PromptResult? prompt, string credential)
    {
        SmokeEnvironment.RequireCredential(credential);
        Assert.NotNull(prompt);
        if (prompt.DeniedHosts.Count > 0)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{prompt.Agent}: denied → {string.Join(' ', prompt.DeniedHosts)}");
        }

        Assert.True(prompt.Session.Output.Contains('4', StringComparison.Ordinal), prompt.Session.Describe());
    }

    /// <summary>A prompt session's result and the hosts its proxy denied (printed as a note).</summary>
    /// <param name="Agent">The agent, e.g. claude.</param>
    /// <param name="Session">The session.</param>
    /// <param name="DeniedHosts">The host:port of each TCP_DENIED line in its proxy.log, sorted, once each.</param>
    internal sealed record PromptResult(string Agent, CommandResult Session, IReadOnlyList<string> DeniedHosts);

    /// <summary>What pen upgrade changed: the base's ID and an agent's install time in a session's tools image,
    /// before and after, and the upgrade's own output.</summary>
    /// <param name="BaseBefore">pen:latest's ID before.</param>
    /// <param name="BaseAfter">pen:latest's ID after.</param>
    /// <param name="InstalledBefore">Claude Code's mtime (stat -c %Y) in a session before.</param>
    /// <param name="InstalledAfter">The same after.</param>
    /// <param name="Log">pen upgrade's result.</param>
    internal sealed record UpgradeResult(
        string BaseBefore, string BaseAfter, string InstalledBefore, string InstalledAfter, CommandResult Log);

    /// <summary>Runs the agents' probes in one session with all four agents as tools, the implied-agent sessions
    /// with an empty config, a pen ps while an implied agent runs, and, for each credential that's set, a prompt
    /// session. pen upgrade runs only when a slow check first asks for it.</summary>
    public sealed class Fixture : SmokeFixture
    {
        private static readonly string[] AgentTools =
            ["--tool", "claude", "--tool", "codex", "--tool", "opencode", "--tool", "pi"];

        private readonly Lazy<UpgradeResult> _upgrade;

        /// <summary>Initializes a new instance of the <see cref="Fixture"/> class.</summary>
        public Fixture() => _upgrade = new Lazy<UpgradeResult>(RunUpgrade);

        internal ProbeResult Agents { get; private set; } = null!;

        internal CommandResult ImpliedClaude { get; private set; } = null!;

        internal CommandResult ImpliedClaudeWithRun { get; private set; } = null!;

        internal CommandResult ShellAgents { get; private set; } = null!;

        internal CommandResult ImpliedCodex { get; private set; } = null!;

        internal CommandResult ImpliedPi { get; private set; } = null!;

        internal CommandResult ImpliedOpenCode { get; private set; } = null!;

        internal CommandResult PsWithImpliedAgent { get; private set; } = null!;

        internal PromptResult? ClaudePrompt { get; private set; }

        internal PromptResult? CodexPrompt { get; private set; }

        internal UpgradeResult Upgrade => _upgrade.Value;

        protected override void Prepare()
        {
            // The agents are tools, so they come with --tool.
            Agents = new SandboxProbe
            {
                Setup = """
                    for c in claude codex opencode pi; do printf "%s=" $c; timeout 30 $c --version </dev/null 2>&1 | tail -1; done
                    """,
                Runner = SandboxProbe.EvalInSubshell,
            }
                .Add("system_paths", """
                    for c in claude codex opencode pi; do case $(readlink -f "$(command -v $c)") in /usr/local/*|/opt/*) ;; *) exit 1 ;; esac; done
                    """)
                .Add("install_dirs_read_only", """
                    for d in claude codex opencode pi; do ! touch /opt/pen/$d/lib/node_modules/.x 2>/dev/null || exit 1; done; ! touch /usr/local/bin/.x 2>/dev/null && ! touch /etc/codex/.x 2>/dev/null
                    """)
                .Add("domains", """
                    for d in api.anthropic.com api.openai.com opencode.ai models.dev registry.npmjs.org; do [ "$(curl -s -o /dev/null -w "%{http_connect}" -I https://$d)" != 403 ] || { echo "$d blocked" >&2; exit 1; }; done
                    """)
                .Add("auto_updaters_off", """
                    [ "$DISABLE_AUTOUPDATER" = 1 ] && [ "$CLAUDE_CODE_DISABLE_AUTOUPDATE" = 1 ] && [ "$OPENCODE_DISABLE_AUTOUPDATE" = 1 ] && [ "$PI_SKIP_VERSION_CHECK" = 1 ] && grep -q "check_for_update_on_startup = false" /etc/codex/managed_config.toml
                    """)
                .Run(Cli, AgentTools);

            // A command named after an agent brings it, with no config at all.
            Home.MakeToolFree();
            ImpliedClaude = Cli.Run("claude", "--version");
            ImpliedClaudeWithRun = Cli.Run("run", "claude", "--version");
            ShellAgents = Cli.Run("run", "bash", "-c", "command -v claude codex opencode pi || echo none");
            ImpliedCodex = Cli.Run("codex", "--version");
            ImpliedPi = Cli.Run("pi", "--version");
            ImpliedOpenCode = Cli.Run("opencode", "--version");
            PsWithImpliedAgent = RunPsWithImpliedAgent();

            // Trivial prompts, only when credentials are in the environment. They reach the agent as [env]
            // entries in the throwaway config, the way users pass them. The project's home volume is fresh, so
            // this also shows [env] CLAUDE_CODE_OAUTH_TOKEN starts Claude logged in.
            ClaudePrompt = RunPrompt("claude", "CLAUDE_CODE_OAUTH_TOKEN", "claude", "-p", Prompt);
            CodexPrompt = RunPrompt("codex", "OPENAI_API_KEY", "codex", "exec", "--skip-git-repo-check", Prompt);
            Home.MakeToolFree();
        }

        /// <summary>Runs pen ps while pen claude mcp serve waits for requests on its stdin, held open, then
        /// removes its agent container so the session ends.</summary>
        private CommandResult RunPsWithImpliedAgent()
        {
            var implied = Track(Cli.StartWithOpenStdin("claude", "mcp", "serve"));
            var agentName = new Regex($"^{Key}-.*-agent$");
            if (!Polling.WaitForContainer(agentName.ToString()))
                throw new InvalidOperationException($"the session for pen ps didn't start: {implied.OutputSoFar}");
            var ps = Cli.Run("ps");
            var agent = Docker.ListRunningNames().FirstOrDefault(agentName.IsMatch);
            if (agent != null)
                Docker.Run("rm", "-f", agent);
            implied.WaitForExit();
            implied.CloseStdin();
            return ps;
        }

        private PromptResult? RunPrompt(string agent, string credential, params string[] command)
        {
            if (SmokeEnvironment.FindCredential(credential) is not { } value)
                return null;
            Home.WriteConfig("[env]", $"{credential} = \"{value}\"");
            var session = Cli.Run([.. AgentTools, "run", "timeout", "180", .. command]);
            var log = File.ReadAllLines(Path.Combine(SmokeProject.FindLastSession(RuntimeDir), "proxy.log"));
            var denied = log.Where(line => line.Contains("TCP_DENIED", StringComparison.Ordinal))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(4) ?? "")
                .Distinct().Order(StringComparer.Ordinal);
            return new PromptResult(agent, session, [.. denied]);
        }

        /// <summary>Runs pen upgrade between two sessions that read when the claude tool was installed.</summary>
        /// <remarks>pen upgrade gives the base a new ID (PEN_REFRESH), so the tools image rebuilds on next use,
        /// even with nothing else new.</remarks>
        private UpgradeResult RunUpgrade()
        {
            var baseBefore = ReadBaseId();
            var installedBefore = ReadClaudeInstallTime();
            var upgrade = Cli.Run("upgrade");
            if (upgrade.ExitCode != 0)
                throw new InvalidOperationException($"pen upgrade: {upgrade.Describe()}");
            return new UpgradeResult(baseBefore, ReadBaseId(), installedBefore, ReadClaudeInstallTime(), upgrade);
        }

        private static string ReadBaseId() =>
            Docker.ReadLines("image", "inspect", "-f", "{{.Id}}", "pen:latest").Single();

        private string ReadClaudeInstallTime() =>
            Cli.Run("--tool", "claude", "run", "stat", "-c", "%Y",
                "/opt/pen/claude/lib/node_modules/@anthropic-ai/claude-code/bin/claude.exe")
                .StdoutLines[^1];
    }
}
