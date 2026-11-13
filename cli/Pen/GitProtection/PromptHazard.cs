using System.Text;

namespace Pen.GitProtection;

/// <summary>
/// A file or state directory a shell prompt would run. StateDir is the directory
/// holding it, which can go aside in its place, if it's a file.
/// </summary>
internal sealed record PromptHazard(string Path, string? StateDir, string Why);
