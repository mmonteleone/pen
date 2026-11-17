using System.Diagnostics.CodeAnalysis;
using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// The mode changes the .git check makes to directories it must look or write in, and how it shows modes.
/// </summary>
internal static class FileModes
{
    /// <summary>Adds u+rx to a directory so it can be listed, and u+w too if <paramref name="write"/>.</summary>
    /// <param name="path">The directory.</param>
    /// <param name="write">Whether to add u+w too.</param>
    /// <param name="change">The mode change made, when it returns true.</param>
    /// <returns>
    /// Whether it changed the mode: false for a symlink, a directory that already has the bits, or one it can't
    /// change.
    /// </returns>
    /// <remarks>
    /// Never through a symlink, which an overlapping session could have swapped in since the listing: chmod would
    /// change whatever it points to. Checking just before is the portable way, since glibc's fchmodat can't do
    /// AT_SYMLINK_NOFOLLOW.
    /// </remarks>
    internal static bool TryMakeListable(string path, bool write, [NotNullWhen(true)] out ModeChange? change)
    {
        change = null;
        try
        {
            if (PathChecks.IsLink(path))
                return false;
            var was = File.GetUnixFileMode(path);
            var added = UnixFileMode.UserRead | UnixFileMode.UserExecute;
            if (write)
                added |= UnixFileMode.UserWrite;
            var now = was | added;
            if (now == was)
                return false; // the mode isn't why it can't be listed
            File.SetUnixFileMode(path, now);
            change = new(path, was, now);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Formats a file mode in octal, as chmod takes it.</summary>
    internal static string Octal(UnixFileMode mode) => Convert.ToString((int)mode, 8);
}
