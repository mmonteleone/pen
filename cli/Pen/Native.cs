using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pen;

/// <summary>The few libc calls pen needs. Same signatures on macOS and Linux.</summary>
internal static partial class Native
{
    private const string Libc = "libc";

    [LibraryImport(Libc, EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint RealPathNative(string path, nint resolved);

    [LibraryImport(Libc, EntryPoint = "free")]
    private static partial void Free(nint ptr);

    [LibraryImport(Libc, EntryPoint = "getuid")]
    public static partial uint GetUid();

    [LibraryImport(Libc, EntryPoint = "getgid")]
    public static partial uint GetGid();

    [LibraryImport(Libc, EntryPoint = "isatty")]
    private static partial int IsATtyNative(int fd);

    // open(2) is variadic, and on Apple arm64 variadic arguments go on the
    // stack, so a mode passed as a fixed argument arrives as garbage. So this
    // binding never passes O_CREAT (the mode is never read), and creat(2),
    // which isn't variadic, makes missing files.
    [LibraryImport(Libc, EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenNative(string path, int flags);

    [LibraryImport(Libc, EntryPoint = "creat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int CreatNative(string path, uint mode);

    [LibraryImport(Libc, EntryPoint = "close")]
    private static partial int CloseNative(int fd);

    [LibraryImport(Libc, EntryPoint = "flock", SetLastError = true)]
    private static partial int FlockNative(int fd, int operation);

    [LibraryImport(Libc, EntryPoint = "rename", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int RenameNative(string from, string to);

    /// <summary>
    /// rename(2): moves whatever is at <paramref name="from"/> (a file, a
    /// directory or a symlink itself), unlike File.Move/Directory.Move, which
    /// follow symlinks to decide what they're moving.
    /// </summary>
    public static void Rename(string from, string to)
    {
        if (RenameNative(from, to) != 0)
            throw new IOException(Marshal.GetLastPInvokeErrorMessage());
    }

    public const int LOCK_SH = 1, LOCK_EX = 2, LOCK_NB = 4, LOCK_UN = 8;

    // open(2) flags differ between the platforms; O_RDWR is 2 and ENOENT 2 on both.
    private const int O_RDWR = 2, ENOENT = 2;
    private static int O_CLOEXEC => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    /// <summary>realpath(3): resolves symlinks and, on macOS, the on-disk case. Null if the path doesn't exist.</summary>
    public static string? RealPath(string path)
    {
        var p = RealPathNative(path, 0);
        if (p == 0) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { Free(p); }
    }

    public static bool IsATty(int fd) => IsATtyNative(fd) == 1;

    /// <summary>
    /// Opens (creating if needed, mode 600) a lock file with O_CLOEXEC, without
    /// .NET's own advisory locking, which would interfere with flock below.
    /// </summary>
    public static SafeFileHandle OpenLockFile(string path)
    {
        int fd = OpenNative(path, O_RDWR | O_CLOEXEC);
        if (fd < 0 && Marshal.GetLastPInvokeError() == ENOENT)
        {
            // Truncating a lock file is harmless, and racing creators both succeed.
            int c = CreatNative(path, 0b110_000_000);
            if (c >= 0) CloseNative(c);
            fd = OpenNative(path, O_RDWR | O_CLOEXEC);
        }
        if (fd < 0)
            throw new IOException($"can't open {path}: {Marshal.GetLastPInvokeErrorMessage()}");
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    /// <summary>flock(2). Returns false only when the lock is held elsewhere (LOCK_NB).</summary>
    public static bool Flock(SafeFileHandle handle, int operation)
    {
        bool added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            if (FlockNative((int)handle.DangerousGetHandle(), operation) == 0) return true;
            int errno = Marshal.GetLastPInvokeError();
            // EWOULDBLOCK is 35 on macOS and 11 on Linux.
            if (errno == (OperatingSystem.IsMacOS() ? 35 : 11)) return false;
            throw new IOException($"flock failed: {Marshal.GetPInvokeErrorMessage(errno)}");
        }
        finally
        {
            if (added) handle.DangerousRelease();
        }
    }
}
