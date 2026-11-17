namespace Pen.Platform;

/// <summary>Path operations whose preconditions the callers establish.</summary>
internal static class Paths
{
    /// <summary>Gets the directory holding <paramref name="path"/>, which must not be a root.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is a root or empty: a bug in the caller.</exception>
    internal static string GetParent(string path) =>
        Path.GetDirectoryName(path) ?? throw new ArgumentException($"'{path}' has no parent directory", nameof(path));

    /// <summary>Lists the directories holding <paramref name="path"/>, nearest first and the root last.</summary>
    /// <remarks>
    /// Only the text is looked at (<see cref="Path.GetDirectoryName(string?)"/>), one step at a time as it's asked
    /// for: nothing on disk, so a symlink on the way isn't followed.
    /// </remarks>
    internal static IEnumerable<string> EnumerateAncestors(string path)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
            yield return parent;
    }
}
