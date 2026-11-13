using System.Diagnostics;
using System.Runtime.InteropServices;
using Pen.Platform;

namespace Pen.Sessions;

/// <summary>
/// While a session runs: SIGINT and SIGQUIT are ignored (the terminal delivers
/// them to the agent). SIGHUP and SIGTERM don't stop Pen either: the child is
/// stopped, and Pen then runs the normal end path.
/// </summary>
internal sealed class SessionSignals : IDisposable
{
    private readonly PosixSignalRegistration[] _registrations;
    private Process? _child;
    private volatile string? _container;
    private int _stopping;
    private int _stopIssued;

    /// <summary>
    /// The running child. Set after <see cref="Container"/>. A stop requested
    /// before it was set (a signal between starting the child and setting it)
    /// stops it now. The exchanges on both sides are full fences, so either the
    /// signal sees the child or this setter sees <see cref="Stopping"/>.
    /// </summary>
    public Process? Child
    {
        set
        {
            Interlocked.Exchange(ref _child, value);
            if (value != null && Stopping)
                StopChild();
        }
    }
    public string? Container { set => _container = value; }
    public bool Stopping => Volatile.Read(ref _stopping) == 1;

    /// <summary>How long the child gets after each step before the next, harder one.</summary>
    internal TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(5);

    public SessionSignals()
    {
        _registrations =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, c => c.Cancel = true),
            PosixSignalRegistration.Create(PosixSignal.SIGQUIT, c => c.Cancel = true),
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop),
        ];
    }

    private void Stop(PosixSignalContext c)
    {
        c.Cancel = true;
        RequestStop();
    }

    /// <summary>What SIGHUP and SIGTERM do.</summary>
    internal void RequestStop()
    {
        Interlocked.Exchange(ref _stopping, 1);
        StopChild(); // no child yet or any more: the end path runs anyway
    }

    private void StopChild()
    {
        var child = Volatile.Read(ref _child);
        if (child == null || Interlocked.Exchange(ref _stopIssued, 1) == 1)
            return;
        var container = _container;
        var grace = Grace;
        _ = Task.Run(() =>
        {
            try
            {
                // With a TTY, compose forwards neither signal and keeps waiting
                // on the container, so the container goes first (as in the end
                // path), and compose then exits on its own.
                if (container != null)
                    Proc.Capture("docker", ["rm", "-f", container]);
                if (!child.WaitForExit(grace))
                {
                    child.Signal(PosixSignal.SIGTERM);
                    if (!child.WaitForExit(grace))
                        child.Kill();
                }
            }
            catch (InvalidOperationException) { } // already exited
        });
    }

    public void Dispose()
    {
        foreach (var r in _registrations)
            r.Dispose();
    }
}
