using System.Diagnostics;

namespace Pen.Tests;

[Collection(nameof(LockAndProcessTests))] // FakeDocker changes PATH; the console is replaced
public class AgentRunnerTests
{
    /// <summary>
    /// A fake docker whose rm -f notes the removal. Its compose run exits 0 at once, or, with a wait file, notes
    /// that it's running and waits for the removal (as compose waits on its container), then exits 3.
    /// </summary>
    private const string Docker = """
        case "$*" in
          "rm -f "*) : > removed; exit 0 ;;
        esac
        case " $* " in
          *" run --rm "*)
            [ -e wait ] || exit 0
            : > running; i=0; while [ ! -e removed ] && [ $i -lt 400 ]; do sleep 0.05; i=$((i+1)); done; exit 3 ;;
        esac
        exit 0

        """;

    /// <summary>Long enough for a stop's asynchronous container removal to have reached the fake docker.</summary>
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);

    private static SessionDir NewSession(TempDir t) => new("pen-repo-0123abcd", t.Sub("session"));

    private static string Calls(TempDir t) => Path.Combine(t.Path, "calls");

    private static (int Exit, string Stderr) RunCapturingStderr(Func<int> run)
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var exit = run();
            return (exit, captured.ToString());
        }
        finally
        {
            Console.SetError(stderr);
        }
    }

    [Fact]
    public void SecretsReachTheCommandLineByNameOnly()
    {
        using var t = new TempDir();
        using var fake = new FakeDocker(t, Docker);
        using var signals = new SessionSignals();
        var session = NewSession(t);

        var exit = new AgentRunner(session, new Dictionary<string, string>(), signals)
            .Run(["true"], "tok_secret", [new("FOO", "foo_secret")]);

        Assert.Equal(0, exit);
        // -T only when stdin or stdout isn't a terminal, as under a test runner.
        var notTty = Native.IsATty(0) && Native.IsATty(1) ? "" : " -T";
        var call = Assert.Single(File.ReadAllLines(Calls(t)));
        Assert.EndsWith($" run --rm --name {session.Container}{notTty} -e GH_TOKEN -e FOO agent true", call,
            StringComparison.Ordinal);
        Assert.DoesNotContain("_secret", call, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentThatCantStartIsReportedAndReturnsOne()
    {
        using var t = new TempDir();
        using var signals = new SessionSignals();
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", t.Sub("empty")); // no docker
        try
        {
            var (exit, stderr) = RunCapturingStderr(() =>
                new AgentRunner(NewSession(t), new Dictionary<string, string>(), signals).Run(["true"], null, []));

            Assert.Equal(1, exit);
            Assert.Equal("pen: docker not found: is it installed and on PATH?\n", stderr);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }

    [Fact]
    public void AStopBeforeTheStartNeverStartsTheAgent()
    {
        using var t = new TempDir();
        using var fake = new FakeDocker(t, Docker);
        using var signals = new SessionSignals();
        signals.RequestStop();

        var (exit, stderr) = RunCapturingStderr(() =>
            new AgentRunner(NewSession(t), new Dictionary<string, string>(), signals).Run(["true"], null, []));
        Thread.Sleep(Settle);

        Assert.Equal(1, exit);
        Assert.Equal("pen: stopped before the session started\n", stderr);
        Assert.False(File.Exists(Calls(t)), "docker was run");
    }

    [Fact]
    public void AStopWhileTheAgentRunsRemovesItsContainer()
    {
        using var t = new TempDir();
        using var fake = new FakeDocker(t, Docker);
        File.WriteAllText(Path.Combine(t.Path, "wait"), "");
        using var signals = new SessionSignals { Grace = TimeSpan.FromSeconds(5) };
        var session = NewSession(t);
        var running = Path.Combine(t.Path, "running");
        var stopper = new Thread(() =>
        {
            var waited = Stopwatch.StartNew();
            while (!File.Exists(running) && waited.Elapsed < TimeSpan.FromSeconds(20))
                Thread.Sleep(10);
            signals.RequestStop();
        });
        stopper.Start();

        var exit = new AgentRunner(session, new Dictionary<string, string>(), signals).Run(["true"], null, []);
        stopper.Join();

        Assert.True(File.Exists(running), "the agent never started");
        Assert.Equal(3, exit); // compose ended on its own once the container went, before any signal
        Assert.EndsWith($"rm -f {session.Container}\n", File.ReadAllText(Calls(t)), StringComparison.Ordinal);
    }

    [Fact]
    public void AStopAfterTheAgentEndedTouchesNothing()
    {
        using var t = new TempDir();
        using var fake = new FakeDocker(t, Docker);
        using var signals = new SessionSignals { Grace = TimeSpan.FromMilliseconds(100) };

        var exit = new AgentRunner(NewSession(t), new Dictionary<string, string>(), signals).Run(["true"], null, []);
        signals.RequestStop(); // a hangup during teardown, say
        Thread.Sleep(Settle);

        Assert.Equal(0, exit);
        var call = Assert.Single(File.ReadAllLines(Calls(t)));
        Assert.Contains(" run --rm ", call, StringComparison.Ordinal);
    }
}
