namespace Pen.Platform;

/// <summary>A regular file read below a directory, and what was on the way to it.</summary>
/// <param name="Contents">The file's exact bytes and mode.</param>
/// <param name="Steps">
/// Each directory below the starting one, outermost first, then the file, as stat'ed through the descriptors the
/// read went through.
/// </param>
/// <remarks>
/// Device and inode in <paramref name="Steps"/> name what was read, however it's spelled and even if it has since
/// been moved, so they can be compared with other files without a race.
/// </remarks>
internal sealed record ProjectFile(RegularFile Contents, IReadOnlyList<FileStatus> Steps)
{
    public byte[] Bytes => Contents.Bytes;

    public UnixFileMode Mode => Contents.Mode;
}
