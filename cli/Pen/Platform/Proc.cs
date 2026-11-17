using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Pen.Platform;

/// <summary>Runs child processes.</summary>
/// <remarks>None of them inherit anything but stdin/stdout/stderr (so never a lock fd).</remarks>
internal static class Proc
{
    /// <summary>Creates the start info for running <paramref name="file"/>, inheriting no handles.</summary>
    /// <param name="file">The program.</param>
    /// <param name="args">Its arguments.</param>
    /// <param name="env">Environment variables to set on top of Pen's own, if any.</param>
    public static ProcessStartInfo CreateStartInfo(
        string file,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var startInfo = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            InheritedHandles = [],
        };
        if (env is not null)
        {
            foreach (var (name, value) in env)
                startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    /// <summary>Runs <paramref name="file"/> with the terminal's stdio.</summary>
    /// <returns>Its exit code, as <see cref="ToExitCode"/> gives it.</returns>
    public static int Run(string file, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null) =>
        ToExitCode(Guard(file, () => Process.Run(CreateStartInfo(file, args, env))));

    /// <summary>Runs <paramref name="file"/> with its stdout and stderr captured.</summary>
    public static ProcessTextOutput Capture(
        string file,
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var startInfo = CreateStartInfo(file, args, env);
        startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;
        return Guard(file, () => Process.RunAndCaptureText(startInfo));
    }

    /// <summary>Starts a child that shares the terminal; the caller waits on it.</summary>
    /// <remarks>
    /// Process.Start returns null only when shell execution reuses a running process, and Pen never turns
    /// UseShellExecute on (its start infos leave it false).
    /// </remarks>
    public static Process Start(ProcessStartInfo startInfo) =>
        Guard(startInfo.FileName, () => Process.Start(startInfo)!);

    /// <summary>Converts a child's exit status to an exit code.</summary>
    /// <returns>Its exit code, or 128 plus the signal's number when a signal ended it, as a shell reports it.</returns>
    public static int ToExitCode(ProcessExitStatus status) =>
        status.Signal is { } signal ? 128 + GetSignalNumber(signal) : status.ExitCode;

    private static int GetSignalNumber(PosixSignal signal) => signal switch
    {
        PosixSignal.SIGHUP => 1,
        PosixSignal.SIGINT => 2,
        PosixSignal.SIGQUIT => 3,
        PosixSignal.SIGTERM => 15,
        _ => (int)signal > 0 ? (int)signal : 1,
    };

    /// <summary>
    /// Runs <paramref name="start"/>, turning a failure to start <paramref name="file"/> into a message.
    /// </summary>
    /// <exception cref="PenException"><paramref name="file"/> isn't found or can't be run.</exception>
    private static T Guard<T>(string file, Func<T> start)
    {
        try
        {
            return start();
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 2) // ENOENT
        {
            throw new PenException($"{file} not found: is it installed and on PATH?");
        }
        catch (Win32Exception exception)
        {
            throw new PenException($"can't run {file}: {exception.Message}");
        }
    }
}
