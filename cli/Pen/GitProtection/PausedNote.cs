using System.Text;

namespace Pen.GitProtection;

/// <summary>A detail of a paused operation: a heading, and lines already safe to print.</summary>
internal sealed record PausedNote(string Text, List<string> Lines, int More = 0);
