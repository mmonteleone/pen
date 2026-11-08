using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pen;

/// <summary>
/// While a session runs: SIGINT and SIGQUIT are ignored (the terminal delivers
/// them to the agent). SIGHUP and SIGTERM don't stop pen either: the child is
/// stopped, and pen then runs the normal end path.
/// </summary>
internal sealed class SessionSignals : IDisposable
{
    private readonly PosixSignalRegistration[] _registrations;
    private volatile Process? _child;
    private volatile string? _container;
    private int _stopping;

    public Process? Child { set => _child = value; }
    public string? Container { set => _container = value; }
    public bool Stopping => _stopping == 1;

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
        if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
        var child = _child;
        if (child == null) return; // not started yet or already done: the end path runs anyway
        var container = _container;
        _ = Task.Run(() =>
        {
            try
            {
                // With a TTY, compose forwards neither signal and keeps waiting
                // on the container, so the container goes first (as in the end
                // path), and compose then exits on its own.
                if (container != null) Proc.Capture("docker", ["rm", "-f", container]);
                if (!child.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    child.Signal(PosixSignal.SIGTERM);
                    if (!child.WaitForExit(TimeSpan.FromSeconds(5))) child.Kill();
                }
            }
            catch (InvalidOperationException) { } // already exited
        });
    }

    public void Dispose()
    {
        foreach (var r in _registrations) r.Dispose();
    }
}
