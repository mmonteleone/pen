using System.IO.Enumeration;

namespace Pen.Platform;

/// <summary>Lists one directory, every entry in it, as the listing reports them.</summary>
internal static class DirectoryListing
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0, // dot files are "hidden" on Unix, and .git and .env are among them
        IgnoreInaccessible = false, // a directory that can't be listed has to be noticed
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Lists <paramref name="dir"/>'s entries without following a symlink among them.</summary>
    /// <remarks>
    /// Only the entries are left unfollowed: <paramref name="dir"/> itself is opened as named, so a symlink there is
    /// followed. A caller that needs it not to be a link checks that itself, as <c>MaskPlanner.List</c> does.
    /// </remarks>
    /// <returns>The entries, or null if the directory can't be listed.</returns>
    public static List<ListedEntry>? ReadOrNull(string dir)
    {
        try
        {
            return new FileSystemEnumerable<ListedEntry>(dir,
                (ref FileSystemEntry e) => new ListedEntry(
                    e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                Options).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>One name in a directory listing, as the listing reports it (no symlink followed).</summary>
/// <param name="Name">The name, with U+FFFD in place of bytes that aren't valid UTF-8.</param>
/// <param name="IsDirectory">Whether it's a directory, or a symlink to one.</param>
/// <param name="IsLink">Whether it's a symlink.</param>
internal readonly record struct ListedEntry(string Name, bool IsDirectory, bool IsLink);
