using System.Text;

namespace Pen.GitProtection;

/// <summary>The result of a git call: exit code, raw stdout, and stderr text.</summary>
/// <param name="ExitCode">git's exit code.</param>
/// <param name="Stdout">What git wrote to stdout, as bytes.</param>
/// <param name="Stderr">What git wrote to stderr.</param>
internal sealed record GitResult(int ExitCode, byte[] Stdout, string Stderr)
{
    /// <summary>Whether git exited with 0.</summary>
    public bool Ok => ExitCode == 0;

    /// <summary>Stdout decoded as UTF-8.</summary>
    public string Text => Encoding.UTF8.GetString(Stdout);

    /// <summary>The non-empty lines of stdout.</summary>
    public IEnumerable<string> Lines => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
