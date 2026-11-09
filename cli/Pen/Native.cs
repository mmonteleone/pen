using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;

namespace Pen;

/// <summary>
/// A directory entry whose name isn't valid UTF-8. CouldHoldRepo: a directory,
/// a symlink, or a type the file system doesn't report; not a plain file or a
/// special file, which git can't take for a repo.
/// </summary>
internal sealed record UndecodableEntry(byte[] Name, bool CouldHoldRepo)
{
    /// <summary>The name as .NET's listing shows it.</summary>
    public string Decoded => Encoding.UTF8.GetString(Name);

    /// <summary>Command substitution drops trailing newlines, so no printf command can name this one.</summary>
    public bool EndsInNewline => Name[^1] == (byte)'\n';

    /// <summary>
    /// The exact name as printf(1) text: octal escapes for all but the plainest
    /// bytes, and for a leading -, which printf would read as an option. ASCII
    /// only, so it's safe to print and to paste.
    /// </summary>
    public string Escaped
    {
        get
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Name.Length; i++)
            {
                var b = Name[i];
                if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'.' or (byte)'_' or (byte)' ' || (b == (byte)'-' && i > 0)) sb.Append((char)b);
                else sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            }
            return sb.ToString();
        }
    }
}

/// <summary>A regular file's exact bytes and mode, read through one descriptor.</summary>
internal sealed record RegularFile(byte[] Bytes, UnixFileMode Mode);

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

    private const int F_OK = 0, X_OK = 1, W_OK = 2, R_OK = 4;

    /// <summary>
    /// Whether the directory is shut to this user or gone, so host git can't get
    /// in either. Only those: any other failure (ENAMETOOLONG, say) says nothing
    /// about whether it can be entered.
    /// </summary>
    public static bool IsShutOrGone(string path) =>
        AccessNative(path, X_OK) != 0 && Marshal.GetLastPInvokeError() is EACCES or ENOENT or ENOTDIR;

    /// <summary>Whether nothing is at the path. False when something above it just can't be entered.</summary>
    public static bool IsMissing(string path) =>
        AccessNative(path, F_OK) != 0 && Marshal.GetLastPInvokeError() is ENOENT or ENOTDIR;

    /// <summary>Whether this user can enter the directory, as host git would have to. False if it's gone.</summary>
    public static bool CanEnter(string path) => AccessNative(path, X_OK) == 0;

    /// <summary>Whether this user can both enter the directory and list it. False if it's gone.</summary>
    public static bool CanList(string path) => AccessNative(path, R_OK | X_OK) == 0;

    /// <summary>Whether this user can make and remove entries in the directory. False if it's gone.</summary>
    public static bool CanWriteIn(string path) => AccessNative(path, W_OK | X_OK) == 0;

    [LibraryImport(Libc, EntryPoint = "opendir", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint OpenDirNative(string path);

    [LibraryImport(Libc, EntryPoint = "readdir", SetLastError = true)]
    private static partial nint ReadDirNative(nint dir);

    [LibraryImport(Libc, EntryPoint = "closedir")]
    private static partial int CloseDirNative(nint dir);

    private const byte DT_UNKNOWN = 0, DT_DIR = 4, DT_LNK = 10;

    /// <summary>Where struct dirent keeps d_type and d_name, on the 64-bit layouts pen knows.</summary>
    private static (int Type, int Name)? DirentLayout =>
        OperatingSystem.IsLinux() && nint.Size == 8 ? (18, 19)
        : OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? (20, 21)
        : null;

    /// <summary>
    /// The entries of <paramref name="dir"/> whose names aren't valid UTF-8, or
    /// null if that can't be found out (including on a platform whose dirent
    /// layout isn't known). Only readdir(3) sees the real bytes: .NET's listing
    /// has U+FFFD in place of the bad ones.
    /// </summary>
    public static List<UndecodableEntry>? ListUndecodable(string dir)
    {
        if (DirentLayout is not { } layout) return null;
        var handle = OpenDirNative(dir);
        if (handle == 0) return null;
        try
        {
            var found = new List<UndecodableEntry>();
            while (true)
            {
                var entry = ReadDirNative(handle);
                if (entry == 0) return Marshal.GetLastPInvokeError() == 0 ? found : null;
                var name = new List<byte>();
                for (byte b; (b = Marshal.ReadByte(entry, layout.Name + name.Count)) != 0;) name.Add(b);
                var bytes = name.ToArray();
                if (Utf8.IsValid(bytes)) continue;
                // Unknown counts as able to hold a repo: nothing here can look closer.
                var type = Marshal.ReadByte(entry, layout.Type);
                found.Add(new(bytes, type is DT_DIR or DT_LNK or DT_UNKNOWN));
            }
        }
        finally { CloseDirNative(handle); }
    }

    public const int LOCK_SH = 1, LOCK_EX = 2, LOCK_NB = 4, LOCK_UN = 8;

    // open(2) flags differ between the platforms; O_RDWR is 2 and ENOENT 2 on both.
    private const int O_RDONLY = 0, O_WRONLY = 1, O_RDWR = 2, ENOENT = 2, EPERM = 1, EACCES = 13, ENOTDIR = 20;
    private static int O_CLOEXEC => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;
    private static int O_NONBLOCK => OperatingSystem.IsMacOS() ? 0x4 : 0x800;
    private static int O_NOFOLLOW => OperatingSystem.IsMacOS() ? 0x100
        : RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000;

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
        using var handle = OpenRegular(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
        if (handle == null) return null;
        var length = RandomAccess.GetLength(handle);
        if (length > MaxTextFile) return null;
        using var reader = new StreamReader(new FileStream(handle, FileAccess.Read));
        // One spare char: filling it means the file grew past what was checked.
        var chars = new char[length + 1];
        int read = reader.ReadBlock(chars, 0, chars.Length);
        return read > length ? null : new string(chars, 0, read);
    }

    /// <summary>
    /// The regular file at <paramref name="path"/>, opened without blocking, or
    /// null if it's anything else, a symlink too when the flags say O_NOFOLLOW (a
    /// FIFO with no reader, a directory for writing and a socket can't be opened
    /// at all, and are null as well).
    /// </summary>
    private static SafeFileHandle? OpenRegular(string path, int flags)
    {
        int fd = OpenNative(path, flags);
        if (fd < 0)
        {
            int errno = Marshal.GetLastPInvokeError();
            if (errno is EACCES or EPERM) throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            if (errno is ENOENT or ENOTDIR) throw new FileNotFoundException(path);
            return null;
        }
        var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (handle.Type == System.IO.FileHandleType.RegularFile) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>
    /// A protected file's exact bytes and mode, or null if it's anything but a
    /// regular file (a symlink isn't followed) or over <see cref="MaxTextFile"/>.
    /// Never a partial read: a copy has to be complete to be restorable.
    /// </summary>
    public static RegularFile? ReadProtected(string path)
    {
        using var handle = OpenRegular(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);
        if (handle == null) return null;
        var length = RandomAccess.GetLength(handle);
        if (length > MaxTextFile) return null;
        // One spare byte: filling it means the file grew past what was checked.
        var bytes = new byte[length + 1];
        int total = 0;
        while (total < bytes.Length)
        {
            int n = RandomAccess.Read(handle, bytes.AsSpan(total), total);
            if (n == 0) break;
            total += n;
        }
        return total > length ? null : new(bytes[..total], File.GetUnixFileMode(handle));
    }

    /// <summary>
    /// The regular file at <paramref name="path"/>, never through a symlink at
    /// that name, for changing it through the descriptor. Null if it's anything else.
    /// </summary>
    public static SafeFileHandle? OpenProtected(string path, bool write) =>
        OpenRegular(path, (write ? O_WRONLY : O_RDONLY) | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW);

    /// <summary>
    /// The file at <paramref name="path"/> opened for reading if it's a regular
    /// file, or null for anything else or any failure: never follows a symlink
    /// there, and returns at once for a FIFO. Type and contents then come from one
    /// descriptor, so swapping the file changes nothing.
    /// </summary>
    public static SafeFileHandle? OpenRegularNoFollow(string path)
    {
        // O_NOFOLLOW differs by architecture on Linux, and fails closed where it isn't known.
        if (!OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) return null;
        try { return OpenRegular(path, O_RDONLY | O_NONBLOCK | O_CLOEXEC | O_NOFOLLOW); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
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
