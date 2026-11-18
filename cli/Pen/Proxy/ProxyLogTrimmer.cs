using Pen.Platform;
using Pen.Sessions;

namespace Pen.Proxy;

/// <summary>Keeps a running session's proxy.log bounded, from creation until disposed.</summary>
/// <remarks>
/// <para>The agent decides how many requests squid logs. While the session runs, a timer checks the log's size;
/// once it passes <see cref="Limit"/>, its last <see cref="Keep"/> bytes go to proxy.log.1 (replacing the previous
/// one) and the log is truncated in place.</para>
/// <para>Squid holds the log open with O_APPEND, so it's never renamed. The truncate runs in the proxy container:
/// on a VM-backed Docker, a truncate on the host leaves the container appending at its old offset, after a run of
/// NULs.</para>
/// </remarks>
internal sealed class ProxyLogTrimmer : IDisposable
{
    /// <summary>The size past which the log is trimmed.</summary>
    internal const long Limit = 16L << 20;

    /// <summary>How many bytes from the log's end a trim keeps, in proxy.log.1.</summary>
    internal const int Keep = 4 << 20;

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(30);
    private const string LogInProxy = "/var/log/squid/access.log";

    private readonly SessionDir _session;
    private readonly Func<byte[]?> _tailAndTruncate;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <summary>Starts the timer that trims <paramref name="session"/>'s log.</summary>
    /// <param name="session">The session whose proxy.log is kept bounded.</param>
    /// <param name="tailAndTruncate">
    /// Returns the log's last <see cref="Keep"/> bytes (or more, if it grew meanwhile) and truncates it right after
    /// reading them, or null if either failed, in which case the log was left alone or may be cut short.
    /// </param>
    /// <param name="interval">How often the timer checks the log's size.</param>
    internal ProxyLogTrimmer(SessionDir session, Func<byte[]?> tailAndTruncate, TimeSpan interval)
    {
        _session = session;
        _tailAndTruncate = tailAndTruncate;
        // A thread-pool timer: it never keeps Pen running, and Dispose stops it.
        _timer = new Timer(_ => Tick(), null, interval, interval);
    }

    /// <summary>
    /// Starts trimming the session's log through its proxy container, using compose's <paramref name="env"/>.
    /// </summary>
    public static ProxyLogTrimmer Start(SessionDir session, IReadOnlyDictionary<string, string> env) =>
        new(session, () => TailAndTruncateInProxy(session, env), Interval);

    /// <summary>Trims the log if it's over the limit, unless a trim is running or the trimmer is disposed.</summary>
    private void Tick()
    {
        // One trim at a time; a tick during one is skipped.
        if (!_gate.TryEnter())
            return;
        try
        {
            if (!_disposed)
                TrimIfOver();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PenException)
        {
            // tried again next tick
        }
        finally
        {
            _gate.Exit();
        }
    }

    /// <summary>Trims the log if it's over <see cref="Limit"/>.</summary>
    /// <returns>Whether it did.</returns>
    internal bool TrimIfOver()
    {
        var log = new FileInfo(_session.ProxyLog);
        if (!log.Exists || log.Length <= Limit)
            return false;
        var tail = _tailAndTruncate();
        if (tail is null)
            return false;

        // The tail starts mid-file, so its first line is most likely cut: dropped.
        var start = 0;
        if (tail.Length >= Keep)
        {
            var newline = Array.IndexOf(tail, (byte)'\n');
            start = newline < 0 ? tail.Length : newline + 1;
        }
        // Written aside and renamed, so a reader or a Pen that dies mid-write
        // never sees half a file. proxy.log.1 isn't mounted anywhere.
        var previous = _session.PreviousProxyLog;
        var tmp = previous + ".tmp";
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            file.Write(tail, start, tail.Length - start);
        File.Move(tmp, previous, overwrite: true);
        return true;
    }

    /// <summary>
    /// Reads the log's last <see cref="Keep"/> bytes and truncates it, in the session's proxy container.
    /// </summary>
    /// <returns>
    /// The bytes, or null if either failed, took longer than <see cref="ExecTimeout"/> or wrote more than four
    /// times <see cref="Keep"/> to stdout or stderr (the exec is then killed).
    /// </returns>
    internal static byte[]? TailAndTruncateInProxy(SessionDir session, IReadOnlyDictionary<string, string> env)
    {
        // tail reads to the end and truncate follows at once, so only lines squid
        // writes between the two are lost. A tail that fails truncates nothing.
        var output = new ComposeClient(session, env).ExecInProxyCapturingBytes(
            ["sh", "-c", $"tail -c {Keep} \"$1\" && truncate -s 0 \"$1\"", "sh", LogInProxy],
            limit: 4 * Keep, timeout: ExecTimeout);
        return output is { End: CaptureEnd.Exited, ExitCode: 0 } ? output.Stdout : null;
    }

    /// <summary>Stops the timer and waits for a trim in progress, so the end path reads finished files.</summary>
    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
            _disposed = true;
    }
}
