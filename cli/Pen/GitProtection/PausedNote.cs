namespace Pen.GitProtection;

/// <summary>A detail of a paused operation.</summary>
/// <param name="Text">The heading.</param>
/// <param name="Lines">The lines under it, already safe to print.</param>
/// <param name="More">How many more lines there were than are listed.</param>
internal sealed record PausedNote(string Text, List<string> Lines, int More = 0);
