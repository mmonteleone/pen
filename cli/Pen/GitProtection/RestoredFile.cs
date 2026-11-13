namespace Pen.GitProtection;

/// <summary>
/// A protected file that changed with nothing to set aside: it
/// was missing, its directory went, or only its mode changed.
/// </summary>
internal sealed record RestoredFile(string Path, string What);
