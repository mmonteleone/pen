namespace Pen.GitProtection;

/// <summary>A directory the check opened and left that way.</summary>
/// <param name="Path">The directory.</param>
/// <param name="Was">Its mode before.</param>
/// <param name="Now">Its mode now.</param>
internal sealed record ModeChange(string Path, UnixFileMode Was, UnixFileMode Now);
