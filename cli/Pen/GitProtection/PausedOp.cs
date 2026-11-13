namespace Pen.GitProtection;

/// <summary>State for a paused operation in a git dir, as found.</summary>
/// <param name="Path">The state directory.</param>
/// <param name="Kind">What kind of operation it is, as words.</param>
/// <param name="Notes">What in it is worth a look.</param>
internal sealed record PausedOp(string Path, string Kind, List<PausedNote> Notes);
