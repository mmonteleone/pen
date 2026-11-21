using System.Globalization;

namespace Pen.IntegrationTests.Harness;

/// <summary>Runs pen on a fresh pseudo-terminal through tests/pty-run.py, typing and hanging up on cue.</summary>
internal sealed class Pty
{
    private readonly List<(string Pattern, string Bytes)> _sends = [];

    /// <summary>Gets the regex after which the terminal is closed (SIGHUP to its foreground group), if any.</summary>
    public string? HangupOn { get; init; }

    /// <summary>Gets how long pty-run.py waits before it kills the command, if not its default of 120 s.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Writes bytes to the terminal once the output matches, in the order added.</summary>
    /// <param name="pattern">A Python regex for the output so far.</param>
    /// <param name="bytes">The bytes, with Python escapes such as \x03 for Ctrl-C.</param>
    /// <returns>This pty.</returns>
    public Pty SendOn(string pattern, string bytes)
    {
        _sends.Add((pattern, bytes));
        return this;
    }

    /// <summary>Runs pen with these arguments on the terminal, with the pen's HOME and directory.</summary>
    /// <param name="pen">The pen.</param>
    /// <param name="args">Pen's arguments.</param>
    /// <returns>What the terminal showed, and how pen ended.</returns>
    public PtyResult Run(PenCli pen, params string[] args)
    {
        List<string> options = [Path.Combine(IntegrationEnvironment.RepoRoot, "tests", "pty-run.py")];
        foreach (var (pattern, bytes) in _sends)
            options.AddRange(["--send-on", pattern, bytes]);
        if (HangupOn != null)
            options.AddRange(["--hangup-on", HangupOn]);
        if (TimeoutSeconds is { } seconds)
            options.AddRange(["--timeout", seconds.ToString(CultureInfo.InvariantCulture)]);
        var limit = TimeSpan.FromSeconds((TimeoutSeconds ?? 120) * 2 + 30);
        var command = pen.Wrap("python3", [.. options, "--", IntegrationEnvironment.PenPath, .. args]) with
        {
            Timeout = limit,
        };
        return PtyResult.Parse(command.Run());
    }
}
