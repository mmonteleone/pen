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

    [LibraryImport(Libc, EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int AccessNative(string path, int mode);

    private const int X_OK = 1, R_OK = 4;

    /// <summary>
    /// Whether the directory is shut to this user or gone, so host git can't get
    /// in either. Only those: any other failure (ENAMETOOLONG, say) says nothing
    /// about whether it can be entered.
    /// </summary>
    public static bool IsShutOrGone(string path) =>
        AccessNative(path, X_OK) != 0 && Marshal.GetLastPInvokeError() is EACCES or ENOENT or ENOTDIR;

    /// <summary>Whether this user can enter the directory, as host git would have to. False if it's gone.</summary>
    public static bool CanEnter(string path) => AccessNative(path, X_OK) == 0;

    /// <summary>Whether this user can both enter the directory and list it. False if it's gone.</summary>
    public static bool CanList(string path) => AccessNative(path, R_OK | X_OK) == 0;

    public const int LOCK_SH = 1, LOCK_EX = 2, LOCK_NB = 4, LOCK_UN = 8;

    // open(2) flags differ between the platforms; O_RDWR is 2 and ENOENT 2 on both.
    private const int O_RDONLY = 0, O_RDWR = 2, ENOENT = 2, EPERM = 1, EACCES = 13, ENOTDIR = 20;
    private static int O_CLOEXEC => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int O_NONBLOCK => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    /// <summary>The largest file <see cref="ReadRegularFile"/> reads: far above any real path file or config.</summary>
    private const int MaxTextFile = 1024 * 1024;

    /// <summary>
    /// A regular file's text, or null if it's anything else or over
    /// <see cref="MaxTextFile"/>: opening a FIFO for reading blocks until a writer
    /// comes, and a sparse file costs nothing to make and runs pen out of memory.
    /// The non-blocking open returns at once, and the size check and read use that
    /// same descriptor.
    /// </summary>
    public static string? ReadRegularFile(string path)
    {
        int fd = OpenNative(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno is EACCES or EPERM) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            if (errno is ENOENT or ENOTDIR) throw new FileNotFoundException(path);
            return null; // a socket, among others, can't be opened at all
        }
        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (handle.Type != System.IO.FileHandleType.RegularFile) return null;
        var length = RandomAccess.GetLength(handle);
        if (length > MaxTextFile) return null;
        using var reader = new StreamReader(new FileStream(handle, FileAccess.Read));
        // One spare char: filling it means the file grew past what was checked.
        var chars = new char[length + 1];
        int read = reader.ReadBlock(chars, 0, chars.Length);
        return read > length ? null : new string(chars, 0, read);
    }

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
