using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pen.Platform;

/// <summary>Child processes. None of them inherit anything but stdin/stdout/stderr (so never a lock fd).</summary>
internal static class Proc
{
    public static ProcessStartInfo Info(
        string file,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            InheritedHandles = [],
        };
        if (env != null)
        {
            foreach (var (k, v) in env)
                psi.Environment[k] = v;
        }

        return psi;
    }

    /// <summary>Runs with the terminal's stdio and returns the exit code.</summary>
    public static int Run(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null) =>
        Code(Guard(file, () => Process.Run(Info(file, args, env))));

    /// <summary>Runs with output captured.</summary>
    public static ProcessTextOutput Capture(
        string file,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = Info(file, args, env);
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        return Guard(file, () => Process.RunAndCaptureText(psi));
    }

    /// <summary>Starts a child that shares the terminal; the caller waits on it.</summary>
    public static Process Start(ProcessStartInfo psi) => Guard(psi.FileName, () => Process.Start(psi)!);

    public static int Code(ProcessExitStatus s) => s.Signal is { } sig ? 128 + SignalNumber(sig) : s.ExitCode;

    private static int SignalNumber(PosixSignal s) => s switch
    {
        PosixSignal.SIGHUP => 1,
        PosixSignal.SIGINT => 2,
        PosixSignal.SIGQUIT => 3,
        PosixSignal.SIGTERM => 15,
        _ => (int)s > 0 ? (int)s : 1,
    };

    private static T Guard<T>(string file, Func<T> f)
    {
        try
        {
            return f();
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 2) // ENOENT
        {
            throw new PenException($"{file} not found: is it installed and on PATH?");
        }
        catch (Win32Exception e)
        {
            throw new PenException($"can't run {file}: {e.Message}");
        }
    }
}
