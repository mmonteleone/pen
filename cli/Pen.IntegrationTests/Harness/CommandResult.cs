namespace Pen.IntegrationTests.Harness;

/// <summary>What a finished process wrote and how it exited.</summary>
/// <param name="ExitCode">The exit code, or 128 plus the signal number if a signal ended it.</param>
/// <param name="Stdout">Everything it wrote to stdout.</param>
/// <param name="Stderr">Everything it wrote to stderr.</param>
/// <param name="Output">Stdout and stderr together, in the order they arrived, as 2&gt;&amp;1 gives.</param>
internal sealed record CommandResult(int ExitCode, string Stdout, string Stderr, string Output)
{
    /// <summary>Gets whether the output is all there: false if something still held the pipes open when the
    /// process ended, and they were read for only a few seconds more.</summary>
    /// <remarks>A check that relies on the full output, such as a line that must not appear, asserts it.</remarks>
    public bool OutputComplete { get; init; } = true;

    /// <summary>Gets stdout as $(…) captures it: without its trailing newlines.</summary>
    public string CapturedStdout => Stdout.TrimEnd('\n');

    /// <summary>Gets the combined output's lines.</summary>
    public IReadOnlyList<string> OutputLines => Output.TrimEnd('\n').Split('\n');

    /// <summary>Gets stdout's lines, as $(…) captures them.</summary>
    public IReadOnlyList<string> StdoutLines => CapturedStdout.Split('\n');

    /// <summary>Gets stderr's lines, as 2&gt;&amp;1 &gt;/dev/null captures them.</summary>
    public IReadOnlyList<string> StderrLines => Stderr.TrimEnd('\n').Split('\n');

    /// <summary>Checks whether stdout has exactly this line, as grep -qxF on $(…) does.</summary>
    /// <param name="line">The whole line.</param>
    /// <returns>True if it's there.</returns>
    public bool HasStdoutLine(string line) => StdoutLines.Contains(line, StringComparer.Ordinal);

    /// <summary>Checks whether a line of the combined output starts with <paramref name="prefix"/>.</summary>
    /// <param name="prefix">The start of the line.</param>
    /// <returns>True if some line starts with it.</returns>
    public bool HasLineStartingWith(string prefix) =>
        OutputLines.Any(line => line.StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Checks whether this is a refusal: a non-zero exit with the message in the output.</summary>
    /// <param name="message">A part of the expected message.</param>
    /// <returns>True if it exited non-zero and said so.</returns>
    public bool IsRefusal(string message) => ExitCode != 0 && Output.Contains(message, StringComparison.Ordinal);

    /// <summary>Checks whether this is a usage error: exit 2 with the message in the output.</summary>
    /// <param name="message">A part of the expected message.</param>
    /// <returns>True if it exited 2 and said so.</returns>
    public bool IsUsageError(string message) => ExitCode == 2 && Output.Contains(message, StringComparison.Ordinal);

    /// <summary>Requires a zero exit, for a command whose status no check reads.</summary>
    /// <remarks>A fixture calls it, so a failure there fails the whole class.</remarks>
    /// <returns>This result.</returns>
    /// <exception cref="InvalidOperationException">The command exited non-zero.</exception>
    public CommandResult RequireSuccess() =>
        ExitCode == 0 ? this : throw new InvalidOperationException($"a command that must succeed failed: {Describe()}");

    /// <summary>Describes the result for an assertion message: the exit code, then the combined output.</summary>
    /// <returns>The description.</returns>
    public string Describe() =>
        OutputComplete
            ? $"exit {ExitCode}, output:\n{Output}"
            : $"exit {ExitCode}, output (cut short: the pipes were still open after it ended):\n{Output}";
}
