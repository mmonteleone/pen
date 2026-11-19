using System.Diagnostics;

namespace Pen.Tests;

public class SessionSignalsTests
{
    private static Process Sleeper() => Proc.Start(Proc.CreateStartInfo("sleep", ["30"]));

    /// <summary>SIGHUP after the child started but before it was handed over still stops it.</summary>
    [Fact]
    public void AStopBetweenStartingTheChildAndSettingItStillStopsIt()
    {
        using var signals = new SessionSignals { Grace = TimeSpan.FromMilliseconds(100) };
        using var child = Sleeper();
        signals.RequestStop(); // the window: started, not yet set
        Assert.True(signals.Stopping);
        Assert.False(child.HasExited);
        signals.Track(null, child);
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)), "the child kept running after a stop during startup");
    }

    [Fact]
    public void AStopRacingTheSetterAlwaysStopsTheChild()
    {
        var children = new List<Process>();
        try
        {
            var signalsList = new List<SessionSignals>();
            for (int i = 0; i < 20; i++)
            {
                var signals = new SessionSignals { Grace = TimeSpan.FromMilliseconds(100) };
                signalsList.Add(signals);
                var child = Sleeper();
                children.Add(child);
                using var go = new Barrier(2);
                var stopper = new Thread(() => { go.SignalAndWait(); signals.RequestStop(); });
                stopper.Start();
                go.SignalAndWait(TestContext.Current.CancellationToken);
                signals.Track(null, child);
                stopper.Join();
            }
            foreach (var (child, i) in children.Select((c, i) => (c, i)))
                Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)), $"child {i} kept running");
            foreach (var s in signalsList) s.Dispose();
        }
        finally
        {
            foreach (var c in children)
            {
                try { if (!c.HasExited) c.Kill(); } catch (InvalidOperationException) { }
                c.Dispose();
            }
        }
    }

    [Fact]
    public void AStopAfterTheChildIsSetStopsIt()
    {
        using var signals = new SessionSignals { Grace = TimeSpan.FromMilliseconds(100) };
        using var child = Sleeper();
        signals.Track(null, child);
        Assert.False(child.HasExited);
        signals.RequestStop();
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)));
    }
}

/// <summary>How a stop escalates: the container goes first, then compose gets SIGTERM after one grace and SIGKILL after another.</summary>
[Collection(nameof(LockAndProcessTests))] // FakeDocker changes PATH
public class SessionSignalsEscalationTests
{
    [Fact]
    public void AStopRemovesTheContainerFirst_ThenTerminatesAfterOneGrace_ThenKillsAfterAnother()
    {
        using var t = new TempDir();
        var term = Path.Combine(t.Path, "term");
        // The container's removal notes whether the child had already had SIGTERM.
        using var fake = new FakeDocker(t, $"if [ -e '{term}' ]; then echo after-term >> calls; fi\nexit 0\n");
        var grace = TimeSpan.FromSeconds(1);
        using var signals = new SessionSignals { Grace = grace };
        // Outlives SIGTERM, noting it, as compose waiting on a container can.
        using var child = Proc.Start(Proc.CreateStartInfo("sh",
            ["-c", $"trap 'touch \"{term}\"' TERM; while :; do sleep 0.05; done"]));
        signals.Track("pen-test-agent", child);

        var elapsed = Stopwatch.StartNew();
        signals.RequestStop();
        while (!File.Exists(term) && elapsed.Elapsed < TimeSpan.FromSeconds(20))
            Thread.Sleep(10);
        var termSeen = elapsed.Elapsed;
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(20)), "the child kept running");
        var exited = elapsed.Elapsed;

        Assert.Equal("rm -f pen-test-agent\n", File.ReadAllText(Path.Combine(t.Path, "calls")));
        Assert.True(File.Exists(term), "the child never had SIGTERM");
        Assert.True(termSeen >= grace, $"SIGTERM came {termSeen} after the stop, before one grace ({grace})");
        Assert.True(termSeen < grace * 2, $"SIGTERM came {termSeen} after the stop, not after one grace ({grace})");
        Assert.True(exited >= grace * 2, $"the child ended {exited} after the stop, before two graces");
        Assert.Equal(128 + 9, child.ExitCode); // SIGKILL: it ignores SIGTERM
    }
}
