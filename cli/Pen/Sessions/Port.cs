using System.Net;
using System.Net.Sockets;

namespace Pen.Sessions;

/// <summary>
/// A port to forward from the host's 127.0.0.1 to the same port in the
/// sandbox. <see cref="FromFlag"/>: asked for with pen --port, rather than
/// only by ports in .pen/config.toml; a clash on the host is then an error
/// instead of a warning.
/// </summary>
internal readonly record struct Port(int Number, bool FromFlag)
{
    public static bool IsValid(long n) => n is >= 1 and <= 65535;

    /// <summary>
    /// The config's ports, then the flag's, without duplicates (lists append).
    /// A port in both counts as from the flag.
    /// </summary>
    public static List<Port> Merge(IEnumerable<int> config, IEnumerable<int> flag)
    {
        var fromFlag = flag.ToHashSet();
        var merged = new List<Port>();
        foreach (var n in config.Concat(flag))
        {
            if (!merged.Exists(p => p.Number == n))
                merged.Add(new(n, fromFlag.Contains(n)));
        }
        return merged;
    }

    /// <summary>
    /// The ports to forward: those free on the host. A taken --port is an
    /// error; a taken config port is skipped with a warning (another session
    /// of the project usually forwards it already). Ports are never remapped.
    /// </summary>
    public static List<int> Choose(IReadOnlyList<Port> ports, Func<int, bool> isTaken, Action<string> warn)
    {
        var taken = ports.Where(p => isTaken(p.Number)).ToList();
        var flagTaken = taken.Where(p => p.FromFlag).Select(p => p.Number).ToList();
        if (flagTaken.Count > 0)
        {
            throw new PenException(flagTaken.Count == 1
                ? $"--port {flagTaken[0]}: port {flagTaken[0]} is already in use on this machine (127.0.0.1): " +
                    "stop what's using it (pen ps lists sessions forwarding ports), or pick another port"
                : $"--port: ports {string.Join(", ", flagTaken)} are already in use on this machine (127.0.0.1): " +
                    "stop what's using them (pen ps lists sessions forwarding ports), or pick other ports");
        }

        foreach (var p in taken)
        {
            warn($"not forwarding port {p.Number} (from .pen/config.toml): it's " +
                "already in use on this machine, maybe by another pen session (pen ps)");
        }

        return ports.Where(p => !taken.Contains(p)).Select(p => p.Number).ToList();
    }

    /// <summary>
    /// Whether something on the host already has <paramref name="port"/> on
    /// 127.0.0.1: binding it fails, or (macOS lets that bind succeed next to a
    /// listener on 0.0.0.0) connecting to it succeeds. A port only root may
    /// bind isn't taken: Docker publishes it.
    /// </summary>
    public static bool IsTakenOnHost(int port)
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            // So that connections in TIME_WAIT from an ended session's server don't count.
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(endpoint);
        }
        catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
        catch (SocketException) { }

        try
        {
            using var c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            return c.ConnectAsync(endpoint).Wait(TimeSpan.FromMilliseconds(500)) && c.Connected;
        }
        catch (AggregateException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
