namespace Pen.GitProtection;

/// <summary>A file or state directory a shell prompt would run.</summary>
/// <param name="Path">The file or directory.</param>
/// <param name="StateDir">
/// The state directory holding it, if it's a file: the directory can go aside in its place.
/// </param>
/// <param name="Why">Why it's quarantined.</param>
internal sealed record PromptHazard(string Path, string? StateDir, string Why);
