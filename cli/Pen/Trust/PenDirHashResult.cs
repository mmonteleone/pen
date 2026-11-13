namespace Pen.Trust;

/// <summary>The hash of .pen/, or why there can't be one.</summary>
/// <param name="Hash">The hash, or null when there's a problem.</param>
/// <param name="Problem">Why there's no hash, or null; then nothing can match an approval.</param>
internal readonly record struct PenDirHashResult(string? Hash, string? Problem);
