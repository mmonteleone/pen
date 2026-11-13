namespace Pen.Trust;

/// <summary>The hash of .pen/, or why there can't be one (then nothing can match an approval).</summary>
internal readonly record struct PenDirHashResult(string? Hash, string? Problem);
