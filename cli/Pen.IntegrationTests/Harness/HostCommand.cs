using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;

namespace Pen.IntegrationTests.Harness;

/// <summary>A program to run on the host, with its directory, environment changes and time limit.</summary>
/// <param name="FileName">The program: a path, or a name looked up on PATH.</param>
/// <param name="Arguments">Its arguments.</param>
internal sealed record HostCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Gets the time limit for a command that doesn't set its own.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromMinutes(15);

    /// <summary>Gets the directory it runs in, or null for the test process's.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the changes to the test process's environment: a null value removes the variable.</summary>
    public ImmutableDictionary<string, string?> Environment { get; init; } =
        ImmutableDictionary<string, string?>.Empty;

    /// <summary>Gets how long it may run before it's killed and the test fails.</summary>
    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>Gets text written to stdin when the command starts, as echo y | does, or null for none.</summary>
    /// <remarks>Stdin is then closed, unless <see cref="KeepStdinOpen"/> is set.</remarks>
    public string? Stdin { get; init; }

    /// <summary>Gets whether stdin is a pipe left open for <see cref="RunningCommand.CloseStdin"/>.</summary>
    /// <remarks>With neither this nor <see cref="Stdin"/>, stdin is /dev/null, as with &lt;/dev/null.</remarks>
    public bool KeepStdinOpen { get; init; }

    /// <summary>Runs the command to its end.</summary>
    /// <returns>Its output and exit code.</returns>
    public CommandResult Run()
    {
        using var running = Start();
        return running.WaitForExit();
    }

    /// <summary>Starts the command without waiting for it.</summary>
    /// <returns>The running command.</returns>
    public RunningCommand Start()
    {
        // sh redirects stdin from /dev/null, as </dev/null does, then becomes the command (same PID).
        ProcessStartInfo startInfo = KeepStdinOpen || Stdin != null
            ? new(FileName, Arguments)
            {
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            }
            : new("/bin/sh", ["-c", "exec \"$0\" \"$@\" </dev/null", FileName, .. Arguments]);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        if (WorkingDirectory != null)
            startInfo.WorkingDirectory = WorkingDirectory;
        foreach (var (name, value) in Environment)
        {
            if (value == null)
                startInfo.Environment.Remove(name);
            else
                startInfo.Environment[name] = value;
        }
        var running = new RunningCommand(this, Process.Start(startInfo)!);
        if (Stdin != null)
        {
            running.WriteStdin(Stdin);
            if (!KeepStdinOpen)
                running.CloseStdin();
        }
        return running;
    }
}
