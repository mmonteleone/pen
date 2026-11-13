using System.Runtime.InteropServices;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;

namespace Pen.Platform;

/// <summary>The few libc calls Pen needs. Same signatures on macOS and Linux.</summary>
internal static partial class Native
{
    private const string Libc = "libc";

    [LibraryImport(Libc, EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint RealPathNative(string path, nint resolved);

    [LibraryImport(Libc, EntryPoint = "free")]
    private static partial void Free(nint ptr);

    /// <summary>Gets the process's real user ID, with getuid(2).</summary>
    [LibraryImport(Libc, EntryPoint = "getuid")]
    public static partial uint GetUid();

    /// <summary>Gets the process's real group ID, with getgid(2).</summary>
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
    /// Moves whatever is at <paramref name="from"/> (a file, a directory or a symlink itself) to
    /// <paramref name="to"/>, with rename(2).
    /// </summary>
    /// <exception cref="IOException">rename(2) failed.</exception>
    /// <remarks>Unlike File.Move/Directory.Move, which follow symlinks to decide what they're moving.</remarks>
    public static void Rename(string from, string to)
    {
        if (RenameNative(from, to) != 0)
            throw new IOException(Marshal.GetLastPInvokeErrorMessage());
    }

    [LibraryImport(Libc, EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int AccessNative(string path, int mode);

    private const int F_OK = 0, X_OK = 1, W_OK = 2, R_OK = 4;

    /// <summary>Checks whether the directory is shut to this user or gone, so host git can't get in either.</summary>
    /// <remarks>
    /// Only those: any other failure (ENAMETOOLONG, say) says nothing about whether it can be entered.
    /// </remarks>
    public static bool IsShutOrGone(string path) =>
        AccessNative(path, X_OK) != 0 && Marshal.GetLastPInvokeError() is EACCES or ENOENT or ENOTDIR;

    /// <summary>Checks whether nothing is at the path.</summary>
    /// <returns>True if nothing is there; false also when something above it just can't be entered.</returns>
    public static bool IsMissing(string path) =>
        AccessNative(path, F_OK) != 0 && Marshal.GetLastPInvokeError() is ENOENT or ENOTDIR;

    /// <summary>Checks whether this user can enter the directory, as host git would have to.</summary>
    /// <returns>True if it can; false also if the directory is gone.</returns>
    public static bool CanEnter(string path) => AccessNative(path, X_OK) == 0;

    /// <summary>Checks whether this user can both enter the directory and list it.</summary>
    /// <returns>True if it can; false also if the directory is gone.</returns>
    public static bool CanList(string path) => AccessNative(path, R_OK | X_OK) == 0;

    /// <summary>Checks whether this user can make and remove entries in the directory.</summary>
    /// <returns>True if it can; false also if the directory is gone.</returns>
    public static bool CanWriteIn(string path) => AccessNative(path, W_OK | X_OK) == 0;

    [LibraryImport(Libc, EntryPoint = "opendir", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint OpenDirNative(string path);

    [LibraryImport(Libc, EntryPoint = "readdir", SetLastError = true)]
    private static partial nint ReadDirNative(nint dir);

    [LibraryImport(Libc, EntryPoint = "closedir")]
    private static partial int CloseDirNative(nint dir);

    private const byte DT_UNKNOWN = 0, DT_DIR = 4, DT_LNK = 10;

    /// <summary>Gets where struct dirent keeps d_type and d_name, on the 64-bit layouts Pen knows.</summary>
    /// <returns>The offsets, or null on a platform whose layout isn't known.</returns>
    private static DirentOffsets? GetDirentLayout()
    {
        if (OperatingSystem.IsLinux() && nint.Size == 8)
            return new(Type: 18, Name: 19);
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return new(Type: 20, Name: 21);
        return null;
    }

    /// <summary>Lists the entries of <paramref name="dir"/> whose names aren't valid UTF-8.</summary>
    /// <returns>
    /// The entries, or null if that can't be found out (including on a platform whose dirent
    /// layout isn't known).
    /// </returns>
    /// <remarks>Only readdir(3) sees the real bytes: .NET's listing has U+FFFD in place of the bad ones.</remarks>
    public static List<UndecodableEntry>? ListUndecodable(string dir)
    {
        if (GetDirentLayout() is not { } layout)
            return null;
        var handle = OpenDirNative(dir);
        if (handle == 0)
            return null;
        try
        {
            var found = new List<UndecodableEntry>();
            while (true)
            {
                var entry = ReadDirNative(handle);
                if (entry == 0)
                    return Marshal.GetLastPInvokeError() == 0 ? found : null;
                var name = new List<byte>();
                for (byte b; (b = Marshal.ReadByte(entry, layout.Name + name.Count)) != 0;)
                    name.Add(b);
                var bytes = name.ToArray();
                if (Utf8.IsValid(bytes))
                    continue;
                // Unknown counts as able to hold a repo: nothing here can look closer.
                var type = Marshal.ReadByte(entry, layout.Type);
                found.Add(new(bytes, type is DT_DIR or DT_LNK or DT_UNKNOWN));
            }
        }
        finally
        {
            CloseDirNative(handle);
        }
    }

    public const int LOCK_SH = 1, LOCK_EX = 2, LOCK_NB = 4, LOCK_UN = 8;

    // open(2) flags differ between the platforms; O_RDWR is 2 and ENOENT 2 on both.
    private const int O_RDONLY = 0, O_WRONLY = 1, O_RDWR = 2, ENOENT = 2, EPERM = 1, EACCES = 13, ENOTDIR = 20;
    private static int O_CLOEXEC => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int O_NONBLOCK => OperatingSystem.IsMacOS() ? 0x4 : 0x800;
    private static int O_NOFOLLOW
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return 0x100;
            return RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000;
        }
    }

    /// <summary>The largest file <see cref="ReadRegularFile"/> reads: far above any real path file or config.</summary>
    private const int MaxTextFile = 1024 * 1024;

    /// <summary>Reads a regular file's text, through a symlink if it's one.</summary>
    /// <returns>The text, or null if it's anything but a regular file or over <see cref="MaxTextFile"/>.</returns>
    /// <remarks>
    /// Opening a FIFO for reading blocks until a writer comes, and a sparse file costs nothing to
    /// make and runs Pen out of memory. The non-blocking open returns at once, and the size check
    /// and read use that same descriptor.
    /// </remarks>
    public static string? ReadRegularFile(string path)
    {
        using var handle = OpenRegular(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
        if (handle == null)
            return null;
        var length = RandomAccess.GetLength(handle);
        if (length > MaxTextFile)
            return null;
        using var reader = new StreamReader(new FileStream(handle, FileAccess.Read));
        // One spare char: filling it means the file grew past what was checked.
        var chars = new char[length + 1];
        int read = reader.ReadBlock(chars, 0, chars.Length);
        return read > length ? null : new string(chars, 0, read);
    }

    /// <summary>Reads a regular file's exact bytes, through a symlink if it's one.</summary>
    /// <returns>The bytes, or null as for <see cref="ReadRegularFile"/>.</returns>
    public static byte[]? ReadRegularBytes(string path)
    {
        using var handle = OpenRegular(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
        return handle == null ? null : ReadBounded(handle);
    }

    /// <summary>Opens the regular file at <paramref name="path"/> without blocking.</summary>
    /// <returns>
    /// The handle, or null if it's anything else, a symlink too when the flags say O_NOFOLLOW (a
    /// FIFO with no reader, a directory for writing and a socket can't be opened at all, and are
    /// null as well).
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">Permission is denied.</exception>
    /// <exception cref="FileNotFoundException">Nothing is at the path.</exception>
    private static SafeFileHandle? OpenRegular(string path, int flags)
    {
        int fd = OpenNative(path, flags);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno is EACCES or EPERM)
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            if (errno is ENOENT or ENOTDIR)
                throw new FileNotFoundException(path);
            return null;
        }
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (handle.Type == System.IO.FileHandleType.RegularFile)
            return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>Reads a protected file's exact bytes and mode, never through a symlink.</summary>
    /// <returns>
    /// The file, or null if it's anything but a regular file (a symlink isn't followed) or over
    /// <see cref="MaxTextFile"/>.
    /// </returns>
    /// <remarks>Never a partial read: a copy has to be complete to be restorable.</remarks>
    public static RegularFile? ReadProtected(string path)
    {
        using var handle = OpenRegular(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);
        if (handle == null)
            return null;
        return ReadBounded(handle) is { } bytes ? new(bytes, File.GetUnixFileMode(handle)) : null;
    }

    /// <summary>Reads all of an open file.</summary>
    /// <returns>The bytes, or null if it's over <see cref="MaxTextFile"/> or grows past its size while read.</returns>
    private static byte[]? ReadBounded(SafeFileHandle handle)
    {
        var length = RandomAccess.GetLength(handle);
        if (length > MaxTextFile)
            return null;
        // One spare byte: filling it means the file grew past what was checked.
        var bytes = new byte[length + 1];
        int total = 0;
        while (total < bytes.Length)
        {
            int read = RandomAccess.Read(handle, bytes.AsSpan(total), total);
            if (read == 0)
                break;
            total += read;
        }
        return total > length ? null : bytes[..total];
    }

    /// <summary>
    /// Opens the regular file at <paramref name="path"/>, never through a symlink at that name, for
    /// changing it through the descriptor.
    /// </summary>
    /// <returns>The handle, or null if it's anything else.</returns>
    public static SafeFileHandle? OpenProtected(string path, bool write) =>
        OpenRegular(path, (write ? O_WRONLY : O_RDONLY) | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);

    /// <summary>
    /// Opens the file at <paramref name="path"/> for reading if it's a regular file, never following
    /// a symlink there, and returning at once for a FIFO.
    /// </summary>
    /// <returns>The handle, or null for anything else or any failure.</returns>
    /// <remarks>Type and contents then come from one descriptor, so swapping the file changes nothing.</remarks>
    public static SafeFileHandle? OpenRegularNoFollow(string path)
    {
        // O_NOFOLLOW differs by architecture on Linux, and fails closed where it isn't known.
        if (!OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return null;
        }

        try
        {
            return OpenRegular(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Resolves symlinks and, on macOS, the on-disk case, with realpath(3).</summary>
    /// <returns>The real path, or null if the path doesn't exist.</returns>
    public static string? RealPath(string path)
    {
        var resolved = RealPathNative(path, 0);
        if (resolved == 0)
            return null;
        try
        {
            return Marshal.PtrToStringUTF8(resolved);
        }
        finally
        {
            Free(resolved);
        }
    }

    /// <summary>Checks whether the file descriptor <paramref name="fd"/> is a terminal, with isatty(3).</summary>
    public static bool IsATty(int fd) => IsATtyNative(fd) == 1;

    /// <summary>Opens a lock file with O_CLOEXEC, creating it (mode 600) if needed.</summary>
    /// <exception cref="IOException">The file can't be opened.</exception>
    /// <remarks>Not through .NET's file APIs: their own advisory locking would interfere with flock below.</remarks>
    public static SafeFileHandle OpenLockFile(string path)
    {
        int fd = OpenNative(path, O_RDWR | O_CLOEXEC);
        if (fd < 0 && Marshal.GetLastPInvokeError() == ENOENT)
        {
            // Truncating a lock file is harmless, and racing creators both succeed.
            int created = CreatNative(path, 0b110_000_000);
            if (created >= 0)
                CloseNative(created);
            fd = OpenNative(path, O_RDWR | O_CLOEXEC);
        }
        if (fd < 0)
            throw new IOException($"can't open {path}: {Marshal.GetLastPInvokeErrorMessage()}");
        return new SafeFileHandle(fd, ownsHandle: true);
    }

    /// <summary>Applies or removes a lock on an open file, with flock(2).</summary>
    /// <returns>True on success; false only when the lock is held elsewhere (LOCK_NB).</returns>
    /// <exception cref="IOException">flock(2) failed for another reason.</exception>
    public static bool Flock(SafeFileHandle handle, int operation)
    {
        bool added = false;
        handle.DangerousAddRef(ref added);
        try
        {
            if (FlockNative((int)handle.DangerousGetHandle(), operation) == 0)
                return true;
            int errno = Marshal.GetLastPInvokeError();
            // EWOULDBLOCK is 35 on macOS and 11 on Linux.
            if (errno == (OperatingSystem.IsMacOS() ? 35 : 11))
                return false;
            throw new IOException($"flock failed: {Marshal.GetPInvokeErrorMessage(errno)}");
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
    }

    /// <summary>Where struct dirent keeps d_type and d_name.</summary>
    /// <param name="Type">The offset of d_type.</param>
    /// <param name="Name">The offset of d_name.</param>
    private readonly record struct DirentOffsets(int Type, int Name);
}
