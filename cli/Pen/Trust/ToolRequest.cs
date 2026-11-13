using Pen.Tools;

namespace Pen.Trust;

/// <summary>
/// A tool in .pen/ that needs an approval: <paramref name="IsNew"/> when the tool at this
/// version does, and otherwise only because of <paramref name="Domains"/>, those its recipe
/// brings that weren't approved for it.
/// </summary>
internal sealed record ToolRequest(ToolSpec Tool, bool IsNew, IReadOnlyList<string> Domains);
