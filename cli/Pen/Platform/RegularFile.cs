namespace Pen.Platform;

/// <summary>A regular file's exact bytes and mode, read through one descriptor.</summary>
/// <param name="Bytes">The file's contents.</param>
/// <param name="Mode">The file's permission bits.</param>
internal sealed record RegularFile(byte[] Bytes, UnixFileMode Mode);
