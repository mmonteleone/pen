using System.Diagnostics;

namespace Pen;

/// <summary>
/// Keeps a running session's proxy.log bounded: the agent decides how many
/// requests squid logs. While the session runs, a timer checks the log's size;
/// once it passes <see cref="Limit"/>, its last <see cref="Keep"/> bytes go to
/// proxy.log.1 (replacing the previous one) and the log is truncated in place.
/// Squid holds the log open with O_APPEND, so it's never renamed. The truncate
/// runs in the proxy container: on a VM-backed Docker, a truncate on the host
/// leaves the container appending at its old offset, after a run of NULs.
/// </summary>
internal sealed class ProxyLogTrimmer : IDisposable
{
    internal const long Limit = 16L << 20;
    internal const int Keep = 4 << 20;
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExecTimeout = TimeSpan.FromSeconds(30);
    private const string LogInProxy = "/var/log/squid/access.log";

    private readonly SessionDir _session;
    private readonly Func<byte[]?> _tailAndTruncate;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <param name="tailAndTruncate">
    /// Returns the log's last <see cref="Keep"/> bytes (or more, if it grew
    /// meanwhile) and truncates it right after reading them, or null if either
    /// failed, in which case the log was left alone or may be cut short.
    /// </param>
    internal ProxyLogTrimmer(SessionDir session, Func<byte[]?> tailAndTruncate, TimeSpan interval)
    {
        _session = session;
        _tailAndTruncate = tailAndTruncate;
        // A thread-pool timer: it never keeps pen running, and Dispose stops it.
        _timer = new Timer(_ => Tick(), null, interval, interval);
    }

    /// <summary>Trims the session's log through its proxy container, using compose's <paramref name="env"/>.</summary>
    public static ProxyLogTrimmer Start(SessionDir session, IReadOnlyDictionary<string, string> env) =>
        new(session, () => TailAndTruncateInProxy(session, env), Interval);

    private void Tick()
    {
        // One trim at a time; a tick during one is skipped.
        if (!_gate.TryEnter()) return;
        try
        {
            if (!_disposed) TrimIfOver();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PenException) { } // tried again next tick
        finally { _gate.Exit(); }
    }

    /// <summary>Trims the log if it's over <see cref="Limit"/>. Returns whether it did.</summary>
    internal bool TrimIfOver()
    {
        var log = new FileInfo(_session.ProxyLog);
        if (!log.Exists || log.Length <= Limit) return false;
        var tail = _tailAndTruncate();
        if (tail == null) return false;

        // The tail starts mid-file, so its first line is most likely cut: dropped.
        int start = 0;
        if (tail.Length >= Keep)
        {
            int nl = Array.IndexOf(tail, (byte)'\n');
            start = nl < 0 ? tail.Length : nl + 1;
        }
        // Written aside and renamed, so a reader or a pen that dies mid-write
        // never sees half a file. proxy.log.1 isn't mounted anywhere.
        var previous = _session.PreviousProxyLog;
        var tmp = previous + ".tmp";
        using (var f = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            f.Write(tail, start, tail.Length - start);
        File.Move(tmp, previous, overwrite: true);
        return true;
    }

    internal static byte[]? TailAndTruncateInProxy(SessionDir session, IReadOnlyDictionary<string, string> env)
    {
        // tail reads to the end and truncate follows at once, so only lines squid
        // writes between the two are lost. A tail that fails truncates nothing.
        var psi = Proc.Info("docker", session.ComposeArgs("exec", "-T", "proxy", "sh", "-c",
            $"tail -c {Keep} \"$1\" && truncate -s 0 \"$1\"", "sh", LogInProxy), env);
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        Session.ScrubComposeEnv(psi);
        using var p = Proc.Start(psi);
        _ = p.StandardError.ReadToEndAsync();
        var read = Task.Run(() => ReadCapped(p.StandardOutput.BaseStream, 4 * Keep));
        bool exited = p.WaitForExit(ExecTimeout);
        if (!exited || read.Wait(ExecTimeout) is false || read.Result == null)
        {
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            return null;
        }
        return p.ExitCode == 0 ? read.Result : null;
    }

    /// <summary>The whole stream, or null once it passes <paramref name="cap"/> bytes.</summary>
    private static byte[]? ReadCapped(Stream s, int cap)
    {
        var buf = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = s.Read(chunk)) > 0)
        {
            if (buf.Length + n > cap) return null;
            buf.Write(chunk, 0, n);
        }
        return buf.ToArray();
    }

    /// <summary>Stops the timer and waits for a trim in progress, so the end path reads finished files.</summary>
    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate) _disposed = true;
    }
}
