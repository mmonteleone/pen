namespace Pen.Platform;

/// <summary>A regular file's exact bytes and permission bits, read through one descriptor.</summary>
internal sealed record RegularFile(byte[] Bytes, UnixFileMode Mode);
