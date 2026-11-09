using System.Diagnostics;

namespace Pen.Tests;

public class SessionSignalsTests
{
    private static Process Sleeper() => Proc.Start(Proc.Info("sleep", ["30"]));

    /// <summary>SIGHUP after the child started but before it was handed over still stops it.</summary>
    [Fact]
    public void AStopBetweenStartingTheChildAndSettingItStillStopsIt()
    {
        using var signals = new SessionSignals { Grace = TimeSpan.FromMilliseconds(100) };
        using var child = Sleeper();
        signals.RequestStop(); // the window: started, not yet set
        Assert.True(signals.Stopping);
        Assert.False(child.HasExited);
        signals.Child = child;
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
                signals.Child = child;
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
        signals.Child = child;
        Assert.False(child.HasExited);
        signals.RequestStop();
        Assert.True(child.WaitForExit(TimeSpan.FromSeconds(10)));
    }
}
