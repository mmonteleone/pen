using System.Diagnostics;

namespace Pen.TestSupport;

/// <summary>Starts processes in a new session, without the test run's controlling terminal.</summary>
internal static class NoTerminal
{
    /// <summary>
    /// A start info that runs <paramref name="file"/> in a new session. An interactive shell opens /dev/tty even when
    /// its stdin is redirected, so started from a terminal it takes the foreground process group, reads keystrokes,
    /// or stops on SIGTTIN until a test's timeout. macOS has no setsid command, but has perl.
    /// </summary>
    /// <param name="file">The program to run.</param>
    /// <param name="args">Its arguments.</param>
    public static ProcessStartInfo StartInfo(string file, params string[] args) =>
        new("perl", ["-MPOSIX", "-e", Script, file, .. args]);

    private const string Script = "POSIX::setsid(); exec { $ARGV[0] } @ARGV or die \"$ARGV[0]: $!\"";
}
