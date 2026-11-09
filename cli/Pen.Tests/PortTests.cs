using System.Net;
using System.Net.Sockets;

namespace Pen.Tests;

/// <summary>
/// Runs alone: a child that another test forks in parallel holds every open fd
/// until it execs, so a closed listener can stay bound for that moment. It also
/// keeps other tests from being handed a port these have just released.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public class PortTests
{
    private static (List<int> Forwarded, List<string> Warnings) Choose(IReadOnlyList<Port> ports, params int[] taken)
    {
        var warnings = new List<string>();
        var forwarded = Port.Choose(ports, taken.Contains, warnings.Add);
        return (forwarded, warnings);
    }

    [Fact]
    public void FreePortsAreAllForwarded_InOrder()
    {
        var (forwarded, warnings) = Choose(Port.Merge([5173, 3000], [8080]));
        Assert.Equal([5173, 3000, 8080], forwarded);
        Assert.Empty(warnings);
        Assert.Empty(Choose([]).Forwarded);
    }

    [Fact]
    public void ATakenConfigPortIsSkippedWithAWarning_NeverRemapped()
    {
        var (forwarded, warnings) = Choose(Port.Merge([5173, 3000], [8080]), 3000);
        Assert.Equal([5173, 8080], forwarded);
        Assert.Equal(["not forwarding port 3000 (from .pen/config.toml): it's already in use on this machine, maybe by another pen session (pen ps)"], warnings);
    }

    [Fact]
    public void ATakenFlagPortIsAnError_BeforeAnyWarning()
    {
        var warnings = new List<string>();
        var e = Assert.Throws<PenException>(() => Port.Choose(Port.Merge([3000], [8765]), n => n is 3000 or 8765, warnings.Add));
        Assert.StartsWith("--port 8765: port 8765 is already in use on this machine", e.Message);
        Assert.Equal(1, e.ExitCode);
        Assert.Empty(warnings);

        // A port in both counts as the flag's.
        e = Assert.Throws<PenException>(() => Port.Choose(Port.Merge([3000], [3000, 4000]), n => n is 3000 or 4000, _ => { }));
        Assert.StartsWith("--port: ports 3000, 4000 are already in use", e.Message);
    }

    private static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public void APortListenedOnAt127001IsTaken()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Assert.True(Port.IsTakenOnHost(((IPEndPoint)l.LocalEndpoint).Port));
    }

    [Fact]
    public void APortListenedOnAt0000IsTaken()
    {
        // macOS lets 127.0.0.1 be bound next to it; the connect still finds it.
        using var l = new TcpListener(IPAddress.Any, 0);
        l.Start();
        Assert.True(Port.IsTakenOnHost(((IPEndPoint)l.LocalEndpoint).Port));
    }

    [Fact]
    public void AFreePortIsntTaken_EvenRightAfterAConnectionClosed()
    {
        int port;
        using (var l = new TcpListener(IPAddress.Loopback, 0))
        {
            l.Start();
            port = ((IPEndPoint)l.LocalEndpoint).Port;
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, port);
            using var server = l.AcceptTcpClient();
            server.Close(); // the server closes first: its side waits in TIME_WAIT
        }
        Assert.False(Port.IsTakenOnHost(port));
        Assert.False(Port.IsTakenOnHost(FreePort()));
    }
}
