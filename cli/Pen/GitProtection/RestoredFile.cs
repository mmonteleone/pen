namespace Pen.GitProtection;

/// <summary>A protected file that changed with nothing to set aside.</summary>
/// <param name="Path">The file.</param>
/// <param name="What">What happened to it: it was missing, its directory went, or only its mode changed.</param>
internal sealed record RestoredFile(string Path, string What);
