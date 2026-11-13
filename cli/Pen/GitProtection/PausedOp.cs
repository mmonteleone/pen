namespace Pen.GitProtection;

/// <summary>State for a paused operation in a git dir, as found: Path is the state directory.</summary>
internal sealed record PausedOp(string Path, string Kind, List<PausedNote> Notes);
