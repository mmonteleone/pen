using System.Net;
using System.Net.Sockets;

namespace Pen.Sessions;

/// <summary>A port to forward from the host's 127.0.0.1 to the same port in the sandbox.</summary>
/// <param name="Number">The port.</param>
/// <param name="FromFlag">
/// Asked for with pen --port, rather than only by ports in .pen/config.toml; a clash on the host is then an error
/// instead of a warning.
/// </param>
internal readonly record struct Port(int Number, bool FromFlag)
{
    /// <summary>Tells whether <paramref name="number"/> is a TCP port, 1 to 65535.</summary>
    public static bool IsValid(long number) => number is >= 1 and <= 65535;

    /// <summary>Merges the config's ports and the flag's.</summary>
    /// <returns>The config's ports, then the flag's, without duplicates (lists append).</returns>
    /// <remarks>A port in both counts as from the flag.</remarks>
    public static List<Port> Merge(IEnumerable<int> config, IEnumerable<int> flag)
    {
        var fromFlag = flag.ToHashSet();
        var merged = new List<Port>();
        foreach (var number in config.Concat(flag))
        {
            if (!merged.Exists(port => port.Number == number))
                merged.Add(new(number, fromFlag.Contains(number)));
        }
        return merged;
    }

    /// <summary>Chooses the ports to forward: those free on the host.</summary>
    /// <param name="ports">The ports asked for.</param>
    /// <param name="isTaken">Tells whether a port is taken on the host.</param>
    /// <param name="warn">Writes a warning.</param>
    /// <returns>The free ones, in order; a taken --port throws.</returns>
    /// <remarks>
    /// A taken config port is skipped with a warning (another session of the project usually forwards it
    /// already). Ports are never remapped.
    /// </remarks>
    public static List<int> Choose(IReadOnlyList<Port> ports, Func<int, bool> isTaken, Action<string> warn)
    {
        var taken = ports.Where(port => isTaken(port.Number)).ToList();
        var flagTaken = taken.Where(port => port.FromFlag).Select(port => port.Number).ToList();
        if (flagTaken.Count > 0)
        {
            throw new PenException(flagTaken.Count == 1
                ? $"--port {flagTaken[0]}: port {flagTaken[0]} is already in use on this machine (127.0.0.1): " +
                    "stop what's using it (pen ps lists sessions forwarding ports), or pick another port"
                : $"--port: ports {string.Join(", ", flagTaken)} are already in use on this machine (127.0.0.1): " +
                    "stop what's using them (pen ps lists sessions forwarding ports), or pick other ports");
        }

        foreach (var port in taken)
        {
            warn($"not forwarding port {port.Number} (from .pen/config.toml): it's " +
                "already in use on this machine, maybe by another pen session (pen ps)");
        }

        return [.. ports.Where(port => !taken.Contains(port)).Select(port => port.Number)];
    }

    /// <summary>Tells whether something on the host already has <paramref name="port"/> on 127.0.0.1.</summary>
    /// <remarks>
    /// It has when binding it fails, or (macOS lets that bind succeed next to a listener on 0.0.0.0) connecting to
    /// it succeeds. A port only root may bind isn't taken: Docker publishes it.
    /// </remarks>
    public static bool IsTakenOnHost(int port)
    {
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        try
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            // So that connections in TIME_WAIT from an ended session's server don't count.
            listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            listener.Bind(endpoint);
        }
        catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return true;
        }
        catch (SocketException) { }

        try
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            return client.ConnectAsync(endpoint).Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
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
