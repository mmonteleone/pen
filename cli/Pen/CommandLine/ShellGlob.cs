using Pen.Proxy;

namespace Pen.CommandLine;

/// <summary>
/// Tells when a domain argument of pen allow or pen deny may be a file name the shell put there for an unquoted *.
/// </summary>
internal static class ShellGlob
{
    /// <summary>
    /// Tells whether <paramref name="name"/> names an entry of <paramref name="dir"/>, ignoring case.
    /// </summary>
    /// <returns>False when the directory can't be listed.</returns>
    /// <remarks>Case is ignored since the parser lowercases the arguments.</remarks>
    public static bool NamesEntry(string dir, string name)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir)
                .Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Formats the advice for an argument of pen <paramref name="command"/> named like a file.</summary>
    /// <remarks>
    /// pen deny never takes <see cref="Allowlist.AnyDomain"/>, so its advice doesn't suggest quoting it.
    /// </remarks>
    public static string FormatAdvice(string command) =>
        "did your shell expand *? " + (command == "deny"
            ? "pen deny takes domains, not '*'"
            : $"quote it: pen {command} '*'");
}
