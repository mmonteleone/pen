using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pen.IntegrationTests.Harness;

/// <summary>A started host command, whose stdout and stderr are collected while it runs.</summary>
internal sealed class RunningCommand : IDisposable
{
    /// <summary>How long output may still arrive once the process has ended.</summary>
    private static readonly TimeSpan DrainTime = TimeSpan.FromSeconds(5);

    private readonly HostCommand _command;
    private readonly Process _process;
    private readonly StringBuilder _stdout = new();
    private readonly StringBuilder _stderr = new();
    private readonly StringBuilder _output = new();
    private readonly Lock _outputLock = new();
    private readonly Task _readers;

    /// <summary>Starts collecting the output of <paramref name="process"/>.</summary>
    /// <param name="command">The command it runs, for messages.</param>
    /// <param name="process">The started process, with stdout and stderr redirected.</param>
    public RunningCommand(HostCommand command, Process process)
    {
        _command = command;
        _process = process;
        _readers = Task.WhenAll(
            CollectAsync(process.StandardOutput.BaseStream, _stdout),
            CollectAsync(process.StandardError.BaseStream, _stderr));
    }

    /// <summary>Gets the process ID.</summary>
    public int Pid => _process.Id;

    /// <summary>Gets whether the process has ended.</summary>
    public bool HasExited => _process.HasExited;

    /// <summary>Gets the combined output so far.</summary>
    public string OutputSoFar
    {
        get
        {
            lock (_outputLock)
                return _output.ToString();
        }
    }

    /// <summary>Sends a signal to the process, as kill -s does.</summary>
    /// <param name="signal">The signal's name without SIG, e.g. TERM, KILL or HUP.</param>
    public void Signal(string signal)
    {
        var kill = new HostCommand("kill", ["-s", signal, Pid.ToString(CultureInfo.InvariantCulture)]).Run();
        if (kill.ExitCode != 0 && !HasExited)
            throw new InvalidOperationException($"kill -s {signal} {Pid} failed: {kill.Output}");
    }

    /// <summary>Writes text to the process's stdin, if it was started with a stdin pipe.</summary>
    /// <param name="text">The text, e.g. y and a newline.</param>
    /// <remarks>A process that has closed its stdin, or ended, doesn't get it, as with a shell pipe.</remarks>
    public void WriteStdin(string text)
    {
        try
        {
            _process.StandardInput.Write(text);
            _process.StandardInput.Flush();
        }
        catch (IOException)
        {
            // The reading end is closed (EPIPE): what's left of the text goes nowhere, as echo's would.
        }
    }

    /// <summary>Closes the process's stdin, if it was started with a stdin pipe.</summary>
    public void CloseStdin()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Closing flushes what's buffered, which fails if the process has closed its end.
        }
    }

    /// <summary>Waits for the process to end and for its output, killing it if it runs past its time limit.</summary>
    /// <returns>Its output and exit code.</returns>
    /// <exception cref="TimeoutException">It ran past the command's time limit.</exception>
    public CommandResult WaitForExit()
    {
        if (!_process.WaitForExit(_command.Timeout))
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
            throw new TimeoutException(
                $"{_command.FileName} {string.Join(' ', _command.Arguments)} ran past {_command.Timeout}; "
                + $"output so far:\n{OutputSoFar}");
        }
        // As wait does for a background job writing to a file: a leftover child (docker compose run, after a
        // SIGKILL'd pen) may hold the pipes open, so take what's there once the process has ended.
        var drained = _readers.Wait(DrainTime);
        lock (_outputLock)
        {
            return new CommandResult(_process.ExitCode, _stdout.ToString(), _stderr.ToString(), _output.ToString())
            {
                OutputComplete = drained,
            };
        }
    }

    /// <summary>Kills the process if it's still running, and releases it.</summary>
    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
        _process.Dispose();
    }

    private async Task CollectAsync(Stream stream, StringBuilder own)
    {
        var decoder = Encoding.UTF8.GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(bytes)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, chars, 0);
                lock (_outputLock)
                {
                    own.Append(chars, 0, count);
                    _output.Append(chars, 0, count);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Disposed while a leftover child still held the pipe: the output taken so far is all there is.
        }
    }
}
