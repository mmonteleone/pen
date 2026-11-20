using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Pen.Platform;

namespace Pen.Sessions;

/// <summary>Handles the signals Pen gets while a session runs, from creation until disposed.</summary>
/// <remarks>
/// SIGINT and SIGQUIT are ignored (the terminal delivers them to the agent). SIGHUP and SIGTERM don't stop Pen
/// either: the child is stopped, and Pen then runs the normal end path.
/// </remarks>
internal sealed class SessionSignals : IDisposable
{
    private readonly PosixSignalRegistration[] _registrations;
    private Process? _child;
    private volatile string? _container;
    private int _stopping;
    private int _stopIssued;

    /// <summary>
    /// Records the agent's container (null when the child has none), which a stop removes first, and then its running
    /// child.
    /// </summary>
    /// <remarks>
    /// The container is recorded before the child, so a stop that sees the child sees the container too. A stop
    /// requested before the child was recorded (a signal between starting the child and recording it) stops it now.
    /// The exchanges on both sides are full fences, so either the signal sees the child or this method sees
    /// <see cref="Stopping"/>.
    /// </remarks>
    public void Track(string? container, Process child)
    {
        _container = container;
        Interlocked.Exchange(ref _child, child);
        if (Stopping)
            StopChild();
    }

    /// <summary>Forgets the child, so a later stop leaves it alone: it has ended, or wasn't waited on.</summary>
    public void Untrack() => Interlocked.Exchange(ref _child, null);

    /// <summary>Gets whether SIGHUP or SIGTERM has asked the session to stop.</summary>
    public bool Stopping => Volatile.Read(ref _stopping) == 1;

    /// <summary>Gets how long the child gets after each step before the next, harder one.</summary>
    internal TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Starts handling the signals.</summary>
    [SuppressMessage("Reliability", "CA2000", Justification = "Kept in a field that Dispose disposes")]
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

    /// <summary>Handles SIGHUP and SIGTERM: keeps Pen running and stops the session.</summary>
    private void Stop(PosixSignalContext context)
    {
        context.Cancel = true;
        RequestStop();
    }

    /// <summary>Stops the session, as SIGHUP and SIGTERM do.</summary>
    internal void RequestStop()
    {
        Interlocked.Exchange(ref _stopping, 1);
        StopChild(); // no child yet or any more: the end path runs anyway
    }

    /// <summary>Stops the child, once, if there is one: the container first, then compose, harder each grace.</summary>
    private void StopChild()
    {
        var child = Volatile.Read(ref _child);
        if (child is null || Interlocked.Exchange(ref _stopIssued, 1) == 1)
            return;
        var container = _container;
        var grace = Grace;
        _ = Task.Run(() =>
        {
            try
            {
                // With a TTY, compose forwards neither signal and keeps waiting on the container, so the container
                // goes first (as in the end path), and compose then exits on its own.
                if (container is not null)
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

    /// <summary>Stops handling the signals.</summary>
    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration.Dispose();
    }
}
