namespace Pen.Platform;

/// <summary>Path operations whose preconditions the callers establish.</summary>
internal static class Paths
{
    /// <summary>Gets the directory holding <paramref name="path"/>, which must not be a root.</summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> is a root or empty: a bug in the caller.</exception>
    internal static string GetParent(string path) =>
        Path.GetDirectoryName(path) ?? throw new ArgumentException($"'{path}' has no parent directory", nameof(path));
}
