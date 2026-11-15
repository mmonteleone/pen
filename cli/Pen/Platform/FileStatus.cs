namespace Pen.Platform;

/// <summary>What stat(2) says about a file: which file it is, and whether it's a directory or a symlink.</summary>
/// <param name="Device">The device it's on.</param>
/// <param name="Inode">Its inode number on that device.</param>
/// <param name="IsDirectory">Whether it's a directory.</param>
/// <param name="IsSymlink">Whether it's a symlink (only when the symlink itself was asked about).</param>
/// <remarks>
/// Device and inode name one file however the path to it was spelled: through a symlink, "..", or a macOS firmlink
/// (/System/Volumes/Data/Users is /Users), which realpath(3) leaves as written.
/// </remarks>
internal readonly record struct FileStatus(ulong Device, ulong Inode, bool IsDirectory, bool IsSymlink);
