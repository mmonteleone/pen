using System.Diagnostics;
using System.Text;

namespace Pen.GitProtection;

/// <summary>The result of a git call: exit code, raw stdout, and stderr text.</summary>
internal sealed record GitResult(int ExitCode, byte[] Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    public string Text => Encoding.UTF8.GetString(Stdout);
    public IEnumerable<string> Lines => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
