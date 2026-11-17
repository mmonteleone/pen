using System.ComponentModel;
using System.Diagnostics;

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

    /// <summary>Runs a child from <paramref name="startInfo"/>, capturing what it redirects, as text.</summary>
    /// <param name="startInfo">The start info, from <see cref="CreateStartInfo"/> so it inherits no handles.</param>
    /// <exception cref="PenException">The program isn't found or can't be run.</exception>
    public static ProcessTextOutput CaptureText(ProcessStartInfo startInfo) =>
        Guard(startInfo.FileName, () => Process.RunAndCaptureText(startInfo));

    /// <summary>
    /// Runs a child with its stdout and stderr captured as bytes, both read at once so neither pipe fills up.
    /// </summary>
    /// <param name="startInfo">
    /// The start info, from <see cref="CreateStartInfo"/> so it inherits no handles; all three standard streams
    /// are redirected here.
    /// </param>
    /// <param name="stdin">
    /// Bytes written to the child's stdin while its output is read, before stdin is closed; null closes it
    /// straight away. A child that exits without reading them all isn't an error: its exit code says why.
    /// </param>
    /// <param name="limit">
    /// The most bytes kept from each stream, or null for no limit. A child that writes more to either is killed
    /// with its process tree, and the result is <see cref="CaptureEnd.OverLimit"/>.
    /// </param>
    /// <param name="timeout">
    /// How long the child has to close both streams and exit, or null for as long as it takes. One that takes
    /// longer is killed with its process tree, and the result is <see cref="CaptureEnd.TimedOut"/>.
    /// </param>
    /// <returns>How the child ended, its exit code and its output.</returns>
    /// <exception cref="PenException">The program isn't found or can't be run.</exception>
    /// <exception cref="IOException">
    /// A stream couldn't be read. The child is killed with its process tree first, as on any failure here, and the
    /// exception is rethrown as it was.
    /// </exception>
    /// <remarks>
    /// The streams are read and stdin written on thread-pool tasks, which this method waits on. Once the child is
    /// killed nothing more is waited for: a read or write still blocked then (a descendant that left the process
    /// tree can hold a pipe open) ends when the pipe closes, and its result and any error are discarded. Each task
    /// owns and disposes its stream, since disposing a pipe stream waits for a read blocked on it.
    /// </remarks>
    public static CapturedBytes CaptureBytes(
        ProcessStartInfo startInfo,
        byte[]? stdin = null,
        int? limit = null,
        TimeSpan? timeout = null)
    {
        startInfo.RedirectStandardInput = startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;
        var started = Stopwatch.GetTimestamp();
        using var process = Start(startInfo);
        // Taken here rather than in the tasks, which may start only after the process is disposed.
        var output = process.StandardOutput.BaseStream;
        var error = process.StandardError.BaseStream;
        var input = process.StandardInput.BaseStream;
        var stdout = Task.Run(() => Drain(output, limit));
        var stderr = Task.Run(() => Drain(error, limit));
        List<Task> pending = [stdout, stderr];
        try
        {
            if (stdin is null)
                Feed(input, null);
            else
                pending.Add(Task.Run(() => Feed(input, stdin)));

            while (pending.Count > 0)
            {
                var index = Task.WaitAny([.. pending], Remaining(started, timeout));
                if (index < 0)
                    return Kill(process, CaptureEnd.TimedOut);
                var done = pending[index];
                pending.RemoveAt(index);
                done.GetAwaiter().GetResult();
                if (done is Task<Drained> { Result.OverLimit: true })
                    return Kill(process, CaptureEnd.OverLimit);
            }

            ProcessExitStatus? status;
            if (timeout is null)
                status = process.WaitForExitStatus();
            else if (!process.TryWaitForExitStatus(Remaining(started, timeout), out status))
                return Kill(process, CaptureEnd.TimedOut);
            // ExitCode is already 128 plus the raw signal number when a signal ended the child, as Process.ExitCode
            // gives it, and is the same value ToExitCode returns.
            return new CapturedBytes(CaptureEnd.Exited, status.ExitCode, stdout.Result.Bytes, stderr.Result.Bytes);
        }
        catch
        {
            // Otherwise a failed read or write would leave the child running, bounded by neither limit nor timeout.
            KillTree(process);
            throw;
        }
        finally
        {
            foreach (var task in pending)
                Discard(task);
        }
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
    /// <remarks>
    /// ExitCode already holds 128 plus the raw signal number for a signalled child. Signal can't give that
    /// number: PosixSignal gives the signals it names negative values that aren't their numbers (SIGKILL is -11).
    /// </remarks>
    public static int ToExitCode(ProcessExitStatus status) => status.ExitCode;

    /// <summary>Reads <paramref name="stream"/> to its end, or until it passes <paramref name="limit"/>.</summary>
    /// <returns>The bytes, or none and OverLimit once there would be more than the limit.</returns>
    private static Drained Drain(Stream stream, int? limit)
    {
        using var owned = stream;
        using var bytes = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = stream.Read(chunk)) > 0)
        {
            if (limit is { } most && bytes.Length + count > most)
                return new Drained([], OverLimit: true);
            bytes.Write(chunk, 0, count);
        }

        return new Drained(bytes.ToArray(), OverLimit: false);
    }

    /// <summary>Writes <paramref name="bytes"/>, if any, to a child's <paramref name="stdin"/> and closes it.</summary>
    private static void Feed(Stream stdin, byte[]? bytes)
    {
        try
        {
            using (stdin)
            {
                if (bytes is not null)
                    stdin.Write(bytes);
            }
        }
        catch (IOException)
        {
            // the child exited without reading it all; its exit code says why
        }
    }

    /// <summary>Kills a captured child and its process tree.</summary>
    /// <returns>The result for a child ended by <paramref name="end"/>: no exit code (-1) and no output.</returns>
    private static CapturedBytes Kill(Process process, CaptureEnd end)
    {
        KillTree(process);
        return new CapturedBytes(end, -1, [], []);
    }

    /// <summary>Kills a captured child and its process tree, unless it has exited already.</summary>
    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // it had exited already
        }
        catch (Exception e) when (e is AggregateException or System.ComponentModel.Win32Exception)
        {
            // Best effort: the caller has decided the result already, and a failed signal (EPERM, say)
            // mustn't replace it.
        }
    }

    /// <summary>Observes any error from a read or write whose result is no longer wanted, whenever it ends.</summary>
    private static void Discard(Task task) =>
        task.ContinueWith(
            static ended => _ = ended.Exception,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

    /// <summary>The time left of <paramref name="timeout"/> since <paramref name="started"/>.</summary>
    /// <returns>What's left, never negative, or an infinite wait when there's no timeout.</returns>
    private static TimeSpan Remaining(long started, TimeSpan? timeout)
    {
        if (timeout is not { } total)
            return Timeout.InfiniteTimeSpan;
        var left = total - Stopwatch.GetElapsedTime(started);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>What one stream of a captured child gave.</summary>
    /// <param name="Bytes">Everything read, or nothing when it passed the limit.</param>
    /// <param name="OverLimit">Whether it passed the limit, so reading stopped.</param>
    private sealed record Drained(byte[] Bytes, bool OverLimit);

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

/// <summary>How a child run by <see cref="Proc.CaptureBytes"/> ended.</summary>
internal enum CaptureEnd
{
    /// <summary>It closed its output and exited, or a signal not sent by Pen ended it.</summary>
    Exited,

    /// <summary>It took longer than the timeout, so Pen killed it.</summary>
    TimedOut,

    /// <summary>It wrote more than the limit to stdout or stderr, so Pen killed it.</summary>
    OverLimit,
}

/// <summary>What <see cref="Proc.CaptureBytes"/> got from a child.</summary>
/// <param name="End">Whether the child ran to its end or Pen killed it, and why.</param>
/// <param name="ExitCode">
/// Its exit code, or 128 plus the signal's number when a signal ended it, as a shell reports it, when
/// <paramref name="End"/> is <see cref="CaptureEnd.Exited"/>; otherwise -1, since a killed child isn't waited for.
/// </param>
/// <param name="Stdout">What it wrote to stdout, or nothing when Pen killed it.</param>
/// <param name="Stderr">What it wrote to stderr, or nothing when Pen killed it.</param>
internal sealed record CapturedBytes(CaptureEnd End, int ExitCode, byte[] Stdout, byte[] Stderr);
