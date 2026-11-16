using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;

namespace Pen.Platform;

/// <summary>
/// The few libc calls Pen needs, same signatures on macOS and Linux, and CoreFoundation's normalization on macOS.
/// </summary>
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
    private const int ENXIO = 6, ENODEV = 19;
    private static int EOPNOTSUPP => OperatingSystem.IsMacOS() ? 102 : 95;
    private static int ELOOP => OperatingSystem.IsMacOS() ? 62 : 40;
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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

    // stat(2) and friends. struct stat differs by platform and architecture, so: on macOS fstatat(2),
    // whose 64-bit-inode struct has one layout on arm64 and x64 (where the symbol is fstatat$INODE64);
    // on Linux statx(2), whose struct has one layout on every architecture.
    [LibraryImport(Libc, EntryPoint = "fstatat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int FStatAtMacArm64(int dirfd, string path, byte* buffer, int flags);

    [LibraryImport(Libc, EntryPoint = "fstatat$INODE64", StringMarshalling = StringMarshalling.Utf8,
        SetLastError = true)]
    private static unsafe partial int FStatAtMacX64(int dirfd, string path, byte* buffer, int flags);

    [LibraryImport(Libc, EntryPoint = "fstat", SetLastError = true)]
    private static unsafe partial int FStatMacArm64(int fd, byte* buffer);

    [LibraryImport(Libc, EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static unsafe partial int FStatMacX64(int fd, byte* buffer);

    [LibraryImport(Libc, EntryPoint = "statx", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial int StatxNative(int dirfd, string path, int flags, uint mask, byte* buffer);

    [LibraryImport(Libc, EntryPoint = "readlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static unsafe partial nint ReadLinkAtNative(int dirfd, string path, byte* buffer, nuint size);

    // openat(2) is variadic too: as with open, O_CREAT is never passed, so the mode is never read.
    [LibraryImport(Libc, EntryPoint = "openat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenAtNative(int dirfd, string path, int flags);

    [LibraryImport(Libc, EntryPoint = "unlinkat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int UnlinkAtNative(int dirfd, string path, int flags);

    private static int AT_FDCWD => OperatingSystem.IsMacOS() ? -2 : -100;
    private static int AT_SYMLINK_NOFOLLOW => OperatingSystem.IsMacOS() ? 0x20 : 0x100;
    private static int O_DIRECTORY
    {
        get
        {
            if (OperatingSystem.IsMacOS())
                return 0x100000;
            return RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x4000 : 0x10000;
        }
    }

    // S_IFMT, S_IFDIR, S_IFLNK and S_IFREG, the same on both platforms.
    private const int ModeTypeMask = 0xF000, ModeDirectory = 0x4000, ModeSymlink = 0xA000, ModeRegular = 0x8000;

    /// <summary>The size of the buffer given to fstatat or statx: more than either struct needs.</summary>
    private const int StatBufferSize = 512;

    /// <summary>Whether stat calls work here, found on first use.</summary>
    private static readonly Lazy<bool> StatWorks = new(() =>
    {
        try
        {
            return StatAt(AT_FDCWD, "/", follow: true) != null;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    });

    /// <summary>Gets whether <see cref="Stat"/> works on this platform.</summary>
    /// <remarks>
    /// macOS on arm64 and x64, and Linux with a libc that has statx (glibc 2.28 and later, musl 1.2.5 and later).
    /// </remarks>
    public static bool CanStat => StatWorks.Value;

    /// <summary>Gets what's at <paramref name="path"/> (through a symlink if <paramref name="follow"/>).</summary>
    /// <returns>What's there, or null if that can't be found out (nothing there, or no stat here).</returns>
    public static FileStatus? Stat(string path, bool follow) => CanStat ? StatAt(AT_FDCWD, path, follow) : null;

    /// <summary>Gets what's at <paramref name="path"/> relative to the directory <paramref name="dirfd"/>.</summary>
    /// <returns>What's there, or null if the call failed or the platform's layout isn't known.</returns>
    private static FileStatus? StatAt(int dirfd, string path, bool follow) =>
        ReadStatus(dirfd, path, follow ? 0 : AT_SYMLINK_NOFOLLOW);

    /// <summary>Gets what the open descriptor <paramref name="fd"/> is, with fstat(2) or statx(2).</summary>
    /// <returns>What it is, or null if the call failed or the platform's layout isn't known.</returns>
    private static FileStatus? StatOpen(int fd) => ReadStatus(fd, null, 0);

    /// <summary>
    /// Stats <paramref name="path"/> relative to the directory <paramref name="fd"/> with fstatat or statx, or, when
    /// <paramref name="path"/> is null, the descriptor itself with fstat or statx's AT_EMPTY_PATH.
    /// </summary>
    /// <returns>What's there, or null if the call failed or the platform's layout isn't known.</returns>
    private static unsafe FileStatus? ReadStatus(int fd, string? path, int flags)
    {
        var buffer = stackalloc byte[StatBufferSize];
        var bytes = new ReadOnlySpan<byte>(buffer, StatBufferSize);
        ulong device;
        ulong inode;
        ulong links;
        int mode;
        if (OperatingSystem.IsMacOS())
        {
            var result = (RuntimeInformation.ProcessArchitecture, path) switch
            {
                (Architecture.Arm64, null) => FStatMacArm64(fd, buffer),
                (Architecture.X64, null) => FStatMacX64(fd, buffer),
                (Architecture.Arm64, _) => FStatAtMacArm64(fd, path, buffer, flags),
                (Architecture.X64, _) => FStatAtMacX64(fd, path, buffer, flags),
                _ => -1,
            };
            if (result != 0)
                return null;
            // st_dev (int32) at 0, st_mode (uint16) at 4, st_nlink (uint16) at 6, st_ino (uint64) at 8.
            device = MemoryMarshal.Read<uint>(bytes);
            mode = MemoryMarshal.Read<ushort>(bytes[4..]);
            links = MemoryMarshal.Read<ushort>(bytes[6..]);
            inode = MemoryMarshal.Read<ulong>(bytes[8..]);
        }
        else if (OperatingSystem.IsLinux())
        {
            const uint STATX_TYPE = 0x1, STATX_NLINK = 0x4, STATX_INO = 0x100;
            const int AT_EMPTY_PATH = 0x1000;
            if (path == null)
                flags |= AT_EMPTY_PATH;
            if (StatxNative(fd, path ?? "", flags, STATX_TYPE | STATX_NLINK | STATX_INO, buffer) != 0)
                return null;
            // stx_nlink (uint32) at 16, stx_mode (uint16) at 28, stx_ino (uint64) at 32, stx_dev_major and
            // stx_dev_minor (uint32) at 136.
            links = MemoryMarshal.Read<uint>(bytes[16..]);
            mode = MemoryMarshal.Read<ushort>(bytes[28..]);
            inode = MemoryMarshal.Read<ulong>(bytes[32..]);
            device = ((ulong)MemoryMarshal.Read<uint>(bytes[136..]) << 32) | MemoryMarshal.Read<uint>(bytes[140..]);
        }
        else
        {
            return null;
        }
        var type = mode & ModeTypeMask;
        return new(device, inode, type == ModeDirectory, type == ModeSymlink, links, type == ModeRegular);
    }

    /// <summary>The largest symlink target <see cref="ReadLink"/> reads: PATH_MAX on Linux, above macOS's.</summary>
    private const int MaxLinkTarget = 4096;

    /// <summary>Reads what the symlink at <paramref name="path"/> holds, as its exact bytes.</summary>
    /// <returns>The bytes, or null if it isn't a symlink or can't be read.</returns>
    public static byte[]? ReadLink(string path) => CanStat ? ReadLinkAt(AT_FDCWD, path) : null;

    private static unsafe byte[]? ReadLinkAt(int dirfd, string path)
    {
        // One spare byte: filling it means the target may be longer than what was read.
        var buffer = new byte[MaxLinkTarget + 1];
        nint read;
        fixed (byte* pointer = buffer)
            read = ReadLinkAtNative(dirfd, path, pointer, (nuint)buffer.Length);
        return read < 0 || read > MaxLinkTarget ? null : buffer[..(int)read];
    }

    /// <summary>
    /// Removes the symlink at <paramref name="path"/>, below <paramref name="root"/>, opening each directory on the
    /// way down from <paramref name="root"/> without following a symlink.
    /// </summary>
    /// <param name="root">A directory to start from, opened as named.</param>
    /// <param name="path">The symlink, below <paramref name="root"/>.</param>
    /// <returns>What the symlink held, as its exact bytes.</returns>
    /// <exception cref="UnauthorizedAccessException">Permission is denied, naming the directory it was in.</exception>
    /// <exception cref="IOException">
    /// Anything else stopped it: what's there isn't a symlink any more, a directory on the way is a symlink now, or
    /// the platform's flags aren't known.
    /// </exception>
    /// <remarks>
    /// A path, rechecked and then removed, could have a directory on the way swapped for a symlink in between, so
    /// the removal would happen wherever that leads. Through descriptors, what's checked (fstatat with
    /// AT_SYMLINK_NOFOLLOW) and what's removed (unlinkat) are in the one directory reached without a symlink, and
    /// unlinkat without AT_REMOVEDIR never removes a directory.
    /// </remarks>
    public static byte[] RemoveSymlink(string root, string path)
    {
        // O_NOFOLLOW and O_DIRECTORY differ by architecture on Linux, so this fails closed where they aren't known.
        if (!CanStat || (!OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)))
        {
            throw new IOException("pen can't remove a symlink safely on this platform");
        }
        var relative = Path.GetRelativePath(root, path);
        if (relative == "." || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
        {
            throw new IOException($"it isn't below {root}");
        }
        var parts = relative.Split('/');
        int fd = OpenNative(root, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        if (fd < 0)
            throw ErrnoException(root);
        try
        {
            var at = root;
            foreach (var part in parts[..^1])
            {
                at = Path.Combine(at, part);
                int next = OpenAtNative(fd, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
                if (next < 0)
                    throw ErrnoException(at);
                CloseNative(fd);
                fd = next;
            }
            var name = parts[^1];
            if (StatAt(fd, name, follow: false) is not { IsSymlink: true })
                throw new IOException("it isn't a symlink any more");
            var target = ReadLinkAt(fd, name) ?? throw new IOException("what it holds can't be read");
            if (UnlinkAtNative(fd, name, 0) != 0)
                throw ErrnoException(at);
            return target;
        }
        finally
        {
            CloseNative(fd);
        }
    }

    /// <summary>
    /// Reads the regular file at <paramref name="relative"/> below <paramref name="root"/>, opening each directory on
    /// the way down from <paramref name="root"/>, and the file itself, without following a symlink.
    /// </summary>
    /// <param name="root">A directory to start from, opened as named.</param>
    /// <param name="relative">
    /// The file, relative to <paramref name="root"/>, with no empty, "." or ".." component.
    /// </param>
    /// <returns>
    /// The file's bytes and mode, both read through the one descriptor, and what each directory on the way and the
    /// file are, from the descriptors opened.
    /// </returns>
    /// <exception cref="UnauthorizedAccessException">Permission is denied, naming the path.</exception>
    /// <exception cref="FileNotFoundException">Nothing is at the path, or a directory on the way is gone.</exception>
    /// <exception cref="IOException">
    /// Anything else stopped it: the path isn't below <paramref name="root"/>, a directory on the way isn't one (a
    /// symlink, say), the file is a symlink, isn't a regular file or is over 1 MB, a descriptor can't be stat'ed, or
    /// the platform's flags aren't known.
    /// </exception>
    /// <remarks>
    /// The project can be written by an agent while this runs, so a path checked and then read could have a directory
    /// on the way swapped for a symlink in between, and the read would copy whatever that leads to. Through
    /// descriptors, each step is taken in the directory reached without a symlink. The final open doesn't block, so a
    /// FIFO opens at once and is refused by its type, and type, size, bytes and mode all come from that descriptor.
    /// </remarks>
    public static ProjectFile ReadInsideProject(string root, string relative)
    {
        // O_NOFOLLOW and O_DIRECTORY differ by architecture on Linux, so this fails closed where they aren't known.
        if (!CanStat || (!OperatingSystem.IsMacOS()
            && RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)))
        {
            throw new IOException("pen can't read a file in the project safely on this platform");
        }
        var parts = relative.Split('/');
        if (Path.IsPathRooted(relative) || relative.Contains('\0') || parts.Any(part => part is "" or "." or ".."))
            throw new IOException($"{relative} isn't a path below {root}");
        int fd = OpenNative(root, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
        if (fd < 0)
            throw ReadInsideException(root, "isn't a directory");
        try
        {
            var at = root;
            var steps = new List<FileStatus>();
            foreach (var part in parts[..^1])
            {
                at = Path.Combine(at, part);
                int next = OpenAtNative(fd, part, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
                if (next < 0)
                    throw ReadInsideException(at, "isn't a directory (a symlink, say)");
                CloseNative(fd);
                fd = next;
                steps.Add(StatOpen(fd) ?? throw new IOException($"{at} can't be stat'ed"));
            }
            var path = Path.Combine(root, relative);
            int file = OpenAtNative(fd, parts[^1], O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC);
            if (file < 0)
                throw ReadInsideException(path, "is a symlink");
            using var handle = new SafeFileHandle(file, ownsHandle: true);
            if (handle.Type != System.IO.FileHandleType.RegularFile)
                throw new IOException($"{path} isn't a regular file");
            var bytes = ReadBounded(handle)
                ?? throw new IOException($"{path} is over 1 MB or changed while it was read");
            steps.Add(StatOpen(file) ?? throw new IOException($"{path} can't be stat'ed"));
            return new(new(bytes, File.GetUnixFileMode(handle)), steps);
        }
        finally
        {
            CloseNative(fd);
        }
    }

    /// <summary>Turns the last call's errno, in <see cref="ReadInsideProject"/>, into an exception.</summary>
    /// <param name="path">The path the step opened.</param>
    /// <param name="refused">What's wrong with <paramref name="path"/> if O_NOFOLLOW or O_DIRECTORY refused it.</param>
    private static Exception ReadInsideException(string path, string refused)
    {
        int errno = Marshal.GetLastPInvokeError();
        if (errno is EACCES or EPERM)
            return new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        if (errno == ENOENT)
            return new FileNotFoundException($"{path} doesn't exist", path);
        if (errno == ELOOP || errno == ENOTDIR)
            return new IOException($"{path} {refused}");
        // A socket or device node can't be opened for reading: ENXIO, EOPNOTSUPP or ENODEV.
        if (errno is ENXIO or ENODEV || errno == EOPNOTSUPP)
            return new IOException($"{path} isn't a regular file");
        return new IOException($"{path}: {Marshal.GetPInvokeErrorMessage(errno)}");
    }

    /// <summary>Turns the last call's errno into the exception for <paramref name="path"/>.</summary>
    private static Exception ErrnoException(string path)
    {
        int errno = Marshal.GetLastPInvokeError();
        if (errno is EACCES or EPERM)
            return new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        // ELOOP or ENOTDIR: O_NOFOLLOW met a symlink, or O_DIRECTORY something else.
        if (errno == ELOOP || errno == ENOTDIR)
            return new IOException($"{path} isn't a directory any more (a symlink, say)");
        return new IOException($"{path}: {Marshal.GetPInvokeErrorMessage(errno)}");
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

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    // CFStringNormalizationForm is a CFIndex; kCFStringNormalizationFormC is 2.
    private const nint CFStringNormalizationFormC = 2;

    /// <summary>A CFRange: where a range of a CFString starts and how long it is, both CFIndex.</summary>
    private readonly record struct CFRange(nint Location, nint Length);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringCreateMutable")]
    private static partial nint CFStringCreateMutable(nint allocator, nint maxLength);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringAppendCharacters")]
    private static unsafe partial void CFStringAppendCharacters(nint text, char* characters, nint count);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringNormalize")]
    private static partial void CFStringNormalize(nint text, nint form);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringGetLength")]
    private static partial nint CFStringGetLength(nint text);

    [LibraryImport(CoreFoundation, EntryPoint = "CFStringGetCharacters")]
    private static unsafe partial void CFStringGetCharacters(nint text, CFRange range, char* buffer);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static partial void CFRelease(nint value);

    /// <summary>Gets a name in Unicode normalization form C on macOS, and as it is elsewhere.</summary>
    /// <returns>The name in form C; <paramref name="text"/> itself when it's all ASCII or not on macOS.</returns>
    /// <remarks>
    /// <para>
    /// Pen runs with invariant globalization, where string.Normalize returns its input unchanged, so this asks
    /// CoreFoundation, which every macOS has. macOS file systems (APFS, HFS+) treat NFC and NFD spellings as one
    /// name, so names compared in form C match as they do there.
    /// </para>
    /// <para>
    /// Linux file systems treat NFC and NFD spellings as different names, so there names are compared by their
    /// exact code points, as the file system does.
    /// </para>
    /// </remarks>
    /// <exception cref="OutOfMemoryException">CoreFoundation couldn't make the string.</exception>
    public static unsafe string NormalizeFormC(string text)
    {
        if (!OperatingSystem.IsMacOS() || Ascii.IsValid(text))
            return text;
        var mutable = CFStringCreateMutable(0, 0);
        if (mutable == 0)
            throw new OutOfMemoryException("CoreFoundation couldn't make a string to normalize");
        try
        {
            fixed (char* characters = text)
                CFStringAppendCharacters(mutable, characters, text.Length);
            CFStringNormalize(mutable, CFStringNormalizationFormC);
            var length = (int)CFStringGetLength(mutable);
            var buffer = new char[length];
            fixed (char* destination = buffer)
                CFStringGetCharacters(mutable, new CFRange(0, length), destination);
            return new string(buffer);
        }
        finally
        {
            CFRelease(mutable);
        }
    }

    /// <summary>Where struct dirent keeps d_type and d_name.</summary>
    /// <param name="Type">The offset of d_type.</param>
    /// <param name="Name">The offset of d_name.</param>
    private readonly record struct DirentOffsets(int Type, int Name);
}
