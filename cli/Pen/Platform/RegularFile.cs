using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;

namespace Pen.Platform;

/// <summary>A regular file's exact bytes and mode, read through one descriptor.</summary>
internal sealed record RegularFile(byte[] Bytes, UnixFileMode Mode);
