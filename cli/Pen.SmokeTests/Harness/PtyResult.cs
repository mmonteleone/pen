using System.Globalization;

namespace Pen.SmokeTests.Harness;

/// <summary>What pty-run.py printed: the terminal's output, then EXIT, TIMEOUT and AFTER-HANGUP lines.</summary>
/// <param name="Text">Everything it printed to stdout, as smoke.sh's $out held it.</param>
/// <param name="Stderr">What pty-run.py itself wrote to stderr, such as a Python error if it crashed.</param>
/// <param name="ExitCode">The command's exit code from its EXIT line, or null if there's none.</param>
/// <param name="AfterHangupSeconds">How long the command took to exit after the hangup, if there was one.</param>
/// <param name="TimedOut">Whether pty-run.py killed the command at its timeout: its last line is TIMEOUT.</param>
/// <remarks>With neither an EXIT line nor a timeout, pty-run.py itself failed; <see cref="Stderr"/> says
/// why.</remarks>
internal sealed record PtyResult(string Text, string Stderr, int? ExitCode, double? AfterHangupSeconds, bool TimedOut)
{
    /// <summary>Reads pty-run.py's output.</summary>
    /// <param name="result">Its result.</param>
    /// <returns>The parsed output.</returns>
    public static PtyResult Parse(CommandResult result)
    {
        int? exitCode = null;
        double? afterHangup = null;
        var lines = result.Stdout.Split('\n');
        foreach (var line in lines)
        {
            if (line.StartsWith("EXIT ", StringComparison.Ordinal))
                exitCode = int.Parse(line["EXIT ".Length..], CultureInfo.InvariantCulture);
            else if (line.StartsWith("AFTER-HANGUP ", StringComparison.Ordinal))
                afterHangup = double.Parse(line["AFTER-HANGUP ".Length..], CultureInfo.InvariantCulture);
        }
        var timedOut = lines.LastOrDefault(line => line.Length > 0) == "TIMEOUT";
        return new PtyResult(result.Stdout, result.Stderr, exitCode, afterHangup, timedOut);
    }

    /// <summary>Describes the result for an assertion message: how it ended, the terminal's output and any
    /// error from pty-run.py.</summary>
    /// <returns>The description.</returns>
    public string Describe()
    {
        var ending = (ExitCode, TimedOut) switch
        {
            ({ } code, _) => $"exit {code}",
            (null, true) => "timed out",
            (null, false) => "pty-run.py failed",
        };
        return Stderr.Length == 0
            ? $"{ending}, output:\n{Text}"
            : $"{ending}, output:\n{Text}\npty-run.py's stderr:\n{Stderr}";
    }
}
