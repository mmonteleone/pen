using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>Forwarded ports: the ports service that publishes them on the host's loopback, what it can't be used
/// for, a port that's taken, and ports from .pen/config.toml.</summary>
/// <remarks>
/// These sessions run with the real HOME, as smoke.sh's did, so that pen ps shows the project, which is under
/// it, as ~/…. The real ~/.config/pen's tools go into them, which the TOOLS cell allows for.
/// </remarks>
public sealed class PortForwardingTests(PortForwardingTests.Fixture fixture)
    : IClassFixture<PortForwardingTests.Fixture>
{
    private const int Port = Fixture.Port;

    [Fact(DisplayName = "pen --port 8765: the host reaches a server in the sandbox at 127.0.0.1:8765")]
    public void HostReachesTheServer() => AssertServed(fixture.ByAddress, "served-from-sandbox");

    [Fact(DisplayName = "...and at localhost:8765")]
    public void HostReachesTheServerAtLocalhost() => AssertServed(fixture.ByName, "served-from-sandbox");

    [Fact(DisplayName = "...published on the host's 127.0.0.1 only")]
    public void PublishedOnLoopbackOnly() => Assert.Equal($"{Port}/tcp=127.0.0.1:{Port} ", fixture.Bindings);

    [Fact(DisplayName = "the forwarder runs as nobody, with no capabilities, on a read-only root")]
    public void ForwarderIsLockedDown() =>
        Assert.Equal("nobody [ALL] true [no-new-privileges:true]", fixture.ForwarderSettings);

    [Fact(DisplayName = "the agent is still on the sandbox network alone")]
    public void AgentIsOnTheSandboxNetworkAlone() =>
        Assert.Matches(new Regex("^[^ ]+_sandbox $", RegexOptions.Multiline), fixture.AgentNetworks);

    [Fact(DisplayName = "the agent still can't reach the internet directly")]
    public void AgentCantReachTheInternet() => AssertLine(fixture.Probes, "fail: direct");

    [Fact(DisplayName = "...nor through the forwarder: ports:8765 leads back to the agent itself")]
    public void PortsLeadsBackToTheAgent() => AssertLine(fixture.Probes, "ok: loopback");

    [Fact(DisplayName = "...so using it as a proxy fails")]
    public void ForwarderIsNoProxy() => AssertLine(fixture.Probes, "fail: viaports");

    [Fact(DisplayName = "...and the forwarder listens on no other port")]
    public void ForwarderListensOnNoOtherPort() => AssertLine(fixture.Probes, "fail: otherport");

    [Fact(DisplayName = "the session's ports file lists 8765")]
    public void PortsFileListsThePort() => Assert.Equal($"{Port}", fixture.PortsFile);

    [Fact(DisplayName = "pen ps shows the forwarded port")]
    public void PsShowsThePort() =>
        Assert.Matches(
            new Regex(
                $"^{Regex.Escape(fixture.ProjectDisplay)} +{fixture.FirstId} +{Port} +{fixture.FirstToolsCell}"
                    + "[0-9]+[smhd]",
                RegexOptions.Multiline),
            fixture.Ps.Stdout);

    [Fact(DisplayName = "the same --port in a second session is an error")]
    public void SamePortIsAnError() =>
        Assert.True(
            fixture.SamePort.ExitCode == 1
                && fixture.SamePort.Output.Contains(
                    $"pen: --port {Port}: port {Port} is already in use on this machine", StringComparison.Ordinal),
            fixture.SamePort.Describe());

    [Fact(DisplayName = "...and leaves no session behind")]
    public void SamePortLeavesNoSession() => Assert.Equal(1, fixture.AgentsAfterSamePort);

    [Fact(DisplayName = "the same port from .pen/config.toml: a second session starts")]
    public void ConfigPortSessionStarts() =>
        Assert.True(fixture.Second.Split('\n').Contains("started"), fixture.Second);

    [Fact(DisplayName = "...with a warning")]
    public void ConfigPortSessionWarns() =>
        Assert.Contains(
            $"pen: not forwarding port {Port} (from .pen/config.toml): it's already in use", fixture.Second,
            StringComparison.Ordinal);

    [Fact(DisplayName = "...forwarding nothing: an empty ports file, no ports service or forwarder")]
    public void ConfigPortSessionForwardsNothing()
    {
        var second = fixture.SecondForwarding;

        Assert.True(
            second.PortsFile == "" && !second.OverrideHasPorts && !second.HasForwarder,
            $"ports file: {second.PortsFile ?? "missing"}, \"ports\": in session.yaml: {second.OverrideHasPorts}, "
                + $"forwarder: {second.HasForwarder}");
    }

    [Fact(DisplayName = "...and pen ps shows no ports for it")]
    public void PsShowsNoPorts() =>
        Assert.Matches(
            new Regex(
                $"^{Regex.Escape(fixture.ProjectDisplay)} +{fixture.SecondId} +{fixture.SecondToolsCell}[0-9]+[smhd] ",
                RegexOptions.Multiline),
            fixture.SecondPs.Stdout);

    [Fact(DisplayName = "the first session still forwards 8765")]
    public void FirstStillForwards() => AssertServed(fixture.StillServed, "served-from-sandbox");

    [Fact(DisplayName = "the forwarder goes with its session")]
    public void ForwarderGoesWithItsSession() => Assert.False(fixture.ForwarderAfter);

    [Fact(DisplayName = "...as does its network")]
    public void NetworkGoesWithItsSession() => Assert.Empty(fixture.NetworksAfter);

    [Fact(DisplayName = "...and the port is free again")]
    public void PortIsFreeAgain() => Assert.False(fixture.PortOpenAfter);

    [Fact(DisplayName = "ports = [8765] in .pen/config.toml alone forwards it")]
    public void ConfigPortAloneForwards() => Assert.True(fixture.ConfigPortServed);

    [Fact(DisplayName = "nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    private static void AssertServed(CommandResult curl, string text) =>
        Assert.True(curl.Stdout.Contains(text, StringComparison.Ordinal), curl.Describe());

    private static void AssertLine(string output, string line) =>
        Assert.True(output.Split('\n').Contains(line), $"no line \"{line}\" in:\n{output}");

    /// <summary>What the second session, whose port was taken, set up for forwarding.</summary>
    /// <param name="PortsFile">Its ports file's text, or null if there's none.</param>
    /// <param name="OverrideHasPorts">Whether its session.yaml has a "ports": key.</param>
    /// <param name="HasForwarder">Whether a ports container exists for it.</param>
    internal sealed record Forwarding(string? PortsFile, bool OverrideHasPorts, bool HasForwarder);

    /// <summary>Runs a session that serves on the forwarded port and probes the network from inside, then a second
    /// with the same --port and a third with it in .pen/config.toml, then a last one with only the config's
    /// port.</summary>
    public sealed class Fixture : SmokeFixture
    {
        /// <summary>The forwarded port.</summary>
        internal const int Port = 8765;

        /// <summary>A sandbox script that answers any HTTP request with /tmp/srv/hello.txt. It reads the request's
        /// headers first, so closing doesn't reset the connection before curl reads the reply.</summary>
        private const string Respond = """
            #!/bin/sh
            while IFS= read -r l && [ -n "${l%?}" ]; do :; done
            printf "HTTP/1.0 200 OK\r\n\r\n"
            cat /tmp/srv/hello.txt
            """;

        private PenCli _pen = null!;

        internal string ProjectDisplay { get; private set; } = null!;

        /// <summary>Gets what the first session printed by the time it was ready: its probes' lines.</summary>
        internal string Probes { get; private set; } = null!;

        internal CommandResult ByAddress { get; private set; } = null!;

        internal CommandResult ByName { get; private set; } = null!;

        internal string Bindings { get; private set; } = null!;

        internal string ForwarderSettings { get; private set; } = null!;

        internal string AgentNetworks { get; private set; } = null!;

        internal string? PortsFile { get; private set; }

        internal string FirstId { get; private set; } = null!;

        internal string FirstToolsCell { get; private set; } = null!;

        internal CommandResult Ps { get; private set; } = null!;

        internal CommandResult SamePort { get; private set; } = null!;

        internal int AgentsAfterSamePort { get; private set; }

        /// <summary>Gets what the second session printed once it started, or ended.</summary>
        internal string Second { get; private set; } = null!;

        internal string SecondId { get; private set; } = null!;

        internal string SecondToolsCell { get; private set; } = null!;

        internal Forwarding SecondForwarding { get; private set; } = null!;

        internal CommandResult SecondPs { get; private set; } = null!;

        internal CommandResult StillServed { get; private set; } = null!;

        internal bool ForwarderAfter { get; private set; }

        internal IReadOnlyList<string> NetworksAfter { get; private set; } = [];

        internal bool PortOpenAfter { get; private set; }

        internal bool ConfigPortServed { get; private set; }

        internal int ContainersAtEnd { get; private set; }

        private string RealRuntimeDir => Path.Combine(SmokeEnvironment.RealHome, ".local", "state", "pen", Key);

        private string PenDir => Path.Combine(Project.Dir, ".pen");

        private Regex AgentName => new($"^{Key}-.*-agent$");

        protected override void Prepare()
        {
            if (IsPortOpen(Port))
            {
                throw new InvalidOperationException(
                    $"port {Port} is already in use on this machine: free it to run this section");
            }
            _pen = PenCli.CreateForRealHome(Project.Dir);
            ProjectDisplay = PsTable.FormatProjectDir(Project.Dir);
            Files.DeleteTree(PenDir);

            var first = Track(_pen.Start("--port", $"{Port}", "run", "bash", "-c", BuildFirstScript()));
            WaitForLine(first, "ready");
            if (!first.OutputSoFar.Split('\n').Contains("ready"))
            {
                throw new InvalidOperationException(
                    $"the port-forwarding session didn't get ready: {first.OutputSoFar}");
            }
            Probes = first.OutputSoFar;
            var agent = Docker.ListRunningNames().First(AgentName.IsMatch);
            InspectFirst(agent);

            SamePort = _pen.Run("--port", $"{Port}", "run", "true");
            AgentsAfterSamePort = Docker.ListRunningNames().Count(AgentName.IsMatch);
            var second = RunSecond(agent);

            Docker.Run("rm", "-f", agent, second.Agent).RequireSuccess();
            first.WaitForExit();
            second.Command.WaitForExit();
            ForwarderAfter = Docker.Run("inspect", ForwarderOf(agent)).ExitCode == 0;
            NetworksAfter = Docker.ReadLines("network", "ls", "-q", "--filter", $"name=^{Key}-");
            PortOpenAfter = IsPortOpen(Port);

            RunConfigPortAlone();
            Files.DeleteTree(PenDir);
            ContainersAtEnd = Docker.CountContainers(Key);
        }

        private void InspectFirst(string agent)
        {
            var forwarder = ForwarderOf(agent);
            FirstId = IdOf(agent);
            var session = Path.Combine(RealRuntimeDir, "sessions", FirstId);
            ByAddress = Curl($"http://127.0.0.1:{Port}/hello.txt");
            ByName = Curl($"http://localhost:{Port}/hello.txt");
            Bindings = Docker.Run(
                "inspect", "--format",
                "{{range $k, $v := .HostConfig.PortBindings}}{{$k}}={{range $v}}{{.HostIp}}:{{.HostPort}}{{end}} {{end}}",
                forwarder).CapturedStdout;
            ForwarderSettings = Docker.Run(
                "inspect", "--format",
                "{{.Config.User}} {{.HostConfig.CapDrop}} {{.HostConfig.ReadonlyRootfs}} {{.HostConfig.SecurityOpt}}",
                forwarder).CapturedStdout;
            AgentNetworks = Docker.Run(
                "inspect", "--format", "{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}", agent).Stdout;
            var portsFile = Path.Combine(session, "ports");
            PortsFile = File.Exists(portsFile) ? File.ReadAllText(portsFile).TrimEnd('\n') : null;
            FirstToolsCell = PsTable.BuildToolsCell(session);
            Ps = _pen.Run("ps").RequireSuccess();
        }

        /// <summary>The second session's agent container and the command running it.</summary>
        /// <param name="Agent">The agent container.</param>
        /// <param name="Command">The command.</param>
        private sealed record SecondSession(string Agent, RunningCommand Command);

        /// <summary>Starts a second session with the port in .pen/config.toml while the first holds it.</summary>
        /// <returns>The second session's agent container and the command running it.</returns>
        private SecondSession RunSecond(string firstAgent)
        {
            Directory.CreateDirectory(PenDir);
            File.WriteAllText(Path.Combine(PenDir, "config.toml"), $"ports = [{Port}]\n");
            var session = Track(_pen.Start("run", "bash", "-c", "echo started; sleep 120"));
            WaitForLine(session, "started");
            Second = session.OutputSoFar;
            var second = Docker.ListRunningNames().First(name => AgentName.IsMatch(name) && name != firstAgent);
            SecondId = IdOf(second);
            var dir = Path.Combine(RealRuntimeDir, "sessions", SecondId);
            var portsFile = Path.Combine(dir, "ports");
            SecondForwarding = new Forwarding(
                File.Exists(portsFile) ? File.ReadAllText(portsFile) : null,
                File.ReadAllText(Path.Combine(dir, "session.yaml")).Contains("\"ports\":", StringComparison.Ordinal),
                Docker.Run("inspect", ForwarderOf(second)).ExitCode == 0);
            SecondToolsCell = PsTable.BuildToolsCell(dir);
            SecondPs = _pen.Run("ps");
            StillServed = Curl($"http://127.0.0.1:{Port}/hello.txt");
            return new SecondSession(second, session);
        }

        private void RunConfigPortAlone()
        {
            var session = Track(_pen.Start("run", "bash", "-c", $"""
                mkdir -p /tmp/srv && echo config-port >/tmp/srv/hello.txt && {ServeHello()} >/dev/null 2>&1 & sleep 120
                """));
            ConfigPortServed = Polling.WaitUntil(
                () => new HostCommand("curl", ["-s", "-m", "2", $"http://127.0.0.1:{Port}/hello.txt"]).Run().Stdout
                    .Contains("config-port", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30));
            Docker.RemoveContainers(Docker.ListRunningNames().Where(name => AgentName.IsMatch(name)));
            session.WaitForExit();
        }

        private static string BuildFirstScript() => $$"""
            mkdir -p /tmp/srv && echo served-from-sandbox >/tmp/srv/hello.txt
            {{ServeHello()}} >/dev/null 2>&1 &
            for i in $(seq 100); do curl -s --noproxy '*' -o /dev/null localhost:{{Port}} && break; sleep 0.1; done
            t() { if eval "$1" >/dev/null 2>&1; then echo "ok: $2"; else echo "fail: $2"; fi; }
            t "curl -s -m 5 --noproxy '*' https://github.com" direct
            t "curl -s -m 5 --noproxy '*' http://ports:{{Port}}/hello.txt | grep -q served-from-sandbox" loopback
            t "curl -s -m 5 -x http://ports:{{Port}} https://github.com" viaports
            t "curl -s -m 5 --noproxy '*' http://ports:80/" otherport
            echo ready
            sleep 120
            """;

        /// <summary>Builds a sandbox command serving /tmp/srv/hello.txt over HTTP on the port, for any path, with
        /// socat (the base has no Python).</summary>
        private static string ServeHello() =>
            $"printf %s {ShellQuote.Quote(Respond)} >/tmp/srv/respond && chmod +x /tmp/srv/respond && "
            + $"socat TCP-LISTEN:{Port},fork,reuseaddr EXEC:/tmp/srv/respond";

        /// <summary>Waits up to 60 s for a session to print a line, or to end.</summary>
        private static void WaitForLine(RunningCommand session, string line) =>
            Polling.WaitUntil(
                () => session.HasExited || session.OutputSoFar.Split('\n').Contains(line), TimeSpan.FromSeconds(60));

        private string IdOf(string agent) => agent[(Key.Length + 1)..^"-agent".Length];

        private static string ForwarderOf(string agent) => agent[..^"-agent".Length] + "-ports";

        private static CommandResult Curl(string url) => new HostCommand("curl", ["-s", "-m", "5", url]).Run();

        /// <summary>Checks whether something on the host accepts connections on 127.0.0.1:port within 2 s.</summary>
        private static bool IsPortOpen(int port)
        {
            using var client = new TcpClient();
            try
            {
                return client.ConnectAsync("127.0.0.1", port).Wait(TimeSpan.FromSeconds(2)) && client.Connected;
            }
            catch (AggregateException)
            {
                return false;
            }
        }
    }
}
