using Pen.Tools;

namespace Pen.Trust;

/// <summary>A tool in .pen/ that needs an approval.</summary>
/// <param name="Tool">The tool, at its version.</param>
/// <param name="IsNew">Whether the tool at this version needs approving; otherwise only its domains do.</param>
/// <param name="Domains">The domains its recipe brings that weren't approved for it.</param>
internal sealed record ToolRequest(ToolSpec Tool, bool IsNew, IReadOnlyList<string> Domains);
