using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Pen.Configuration;
using Pen.Platform;

namespace Pen.Trust;

/// <summary>Hashes what docker build reads from .pen/, and copies it to build from.</summary>
/// <remarks>
/// <para>
/// The hash covers all of .pen/ but config.toml, which pen allow and pen deny edit and which holds only settings
/// approved one by one. Every entry counts by type, permission bits (COPY keeps them all), full relative path and
/// content or link target, each with its length first, so moving bytes between a name and a content can't produce the
/// same input. Entries come in a fixed order.
/// </para>
/// <para>
/// The agent may have written everything here. Whatever the hash wouldn't pin down, or that could hang or exhaust
/// Pen, means no hash: special files, names that aren't valid UTF-8, trees over the limits, anything unreadable, and
/// symlinks other than a relative one through real directories to a real file or directory in .pen/. Other symlinks
/// could read content from outside, or be repointed from outside, without changing the hash. It never runs docker or
/// git.
/// </para>
/// <para>
/// The hash says what a directory held at one moment, and the agent may be able to change .pen/ right after, so only a
/// copy nobody else can write is safe to build from: see <see cref="Snapshot"/>.
/// </para>
/// </remarks>
internal sealed class PenDirHash : IDisposable
{
    private const long MaxBytes = 256L * 1024 * 1024;
    private const int MaxEntries = 10_000;
    private const int MaxDepth = 32;
    private const byte FileEntry = 0;
    private const byte DirectoryEntry = 1;
    private const byte LinkEntry = 2;
    private const string CantRead = "part of .pen/ can't be read";

    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0, // dot files are "hidden" on Unix
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    private readonly string _root;
    private readonly string? _copy;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _bytes;
    private int _entries;

    private PenDirHash(string root, string? copy)
    {
        _root = root;
        _copy = copy;
    }

    public void Dispose() => _hash.Dispose();

    /// <summary>Hashes <paramref name="penDir"/>.</summary>
    /// <returns>The hash, or the problem that means there's none.</returns>
    public static PenDirHashResult Compute(string penDir) => Walk(penDir, null);

    /// <summary>Copies <paramref name="penDir"/> to <paramref name="copy"/> and hashes the copy.</summary>
    /// <returns>The hash of the copy, or the problem that means there's none.</returns>
    /// <remarks>
    /// <paramref name="copy"/> must not exist yet, and must be in a directory only Pen can reach. The copy is made
    /// under the same refusals, keeping types, modes and modification times. config.toml stays out: it isn't hashed, so
    /// a build must not read it. It never follows a symlink, and reads each file through one descriptor. The hash is of
    /// the copy, which can't change afterwards, taken there so that symlinks are judged by the tree that gets built.
    /// </remarks>
    public static PenDirHashResult Snapshot(string penDir, string copy) =>
        Walk(penDir, copy) is PenDirHashResult.NoHash refused ? refused : Compute(copy);

    /// <summary>Hashes <paramref name="penDir"/>, copying it to <paramref name="copy"/> unless that's null.</summary>
    private static PenDirHashResult Walk(string penDir, string? copy)
    {
        try
        {
            if (PathChecks.IsLink(penDir))
                return new PenDirHashResult.NoHash(".pen is a symlink");
            using var walk = new PenDirHash(penDir, copy);
            walk._hash.AppendData("pen .pen/ hash 1\n"u8);
            var root = new DirectoryInfo(penDir);
            walk.AppendHead(DirectoryEntry, root.UnixFileMode, "");
            if (walk.HashDirectory("", root, depth: 0) is { } problem)
                return new PenDirHashResult.NoHash(problem);
            return new PenDirHashResult.Hashed(Convert.ToHexStringLower(walk._hash.GetHashAndReset()));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The agent can change .pen/ while it's read: what was listed may be gone, unreadable or of another
            // type by the time it's opened, so a check first would race. Whatever can't be read means no hash.
            // An entry gone when its status was read comes back from .NET as values (-1 for its mode), not as an
            // exception: WasFound checks for those before any is hashed into a result or given to the copy.
            return new PenDirHashResult.NoHash(CantRead);
        }
    }

    /// <summary>Describes <paramref name="relativePath"/> for a message, escaped.</summary>
    private static string Describe(string relativePath) => ".pen/" + Out.Field(relativePath);

    /// <summary>
    /// Hashes, and copies, the directory at <paramref name="relativePath"/> (empty for .pen/ itself) and all it holds.
    /// </summary>
    /// <returns>Why .pen/ can't have a hash, or null.</returns>
    /// <remarks><paramref name="self"/> gives the directory's mode and modification time.</remarks>
    private string? HashDirectory(string relativePath, FileSystemInfo self, int depth)
    {
        if (depth > MaxDepth)
            return $"{Describe(relativePath)} is nested too deeply";
        if (!TryListEntries(relativePath, out var entries, out var problem))
            return problem;
        // Checked once it's listed, so that a missing .pen/ is reported as one that can't be listed.
        if (!WasFound(self))
            return CantRead;
        foreach (var entry in entries)
        {
            problem = HashEntry(relativePath.Length == 0 ? entry.Name : relativePath + "/" + entry.Name, entry, depth);
            if (problem is not null)
                return problem;
        }

        if (_copy is { } copyRoot)
        {
            if (ReadTime(() => self.LastWriteTimeUtc) is not { } time)
                return CantRead;
            Directory.SetLastWriteTimeUtc(GetCopyPath(copyRoot, relativePath), time);
            File.SetUnixFileMode(GetCopyPath(copyRoot, relativePath), self.UnixFileMode);
        }
        return null;
    }

    /// <summary>Whether <paramref name="info"/> was there when .NET read its status, once, to keep.</summary>
    /// <remarks>
    /// For an entry gone at that moment, .NET gives -1 as its attributes and mode, which has every attribute bit,
    /// Directory included, set, and a time in 1601. The agent can make the path a directory again by the time
    /// it's listed, so a listing that works doesn't mean the mode or time that comes with it is real.
    /// </remarks>
    private static bool WasFound(FileSystemInfo info) =>
        info.Attributes != (FileAttributes)(-1) && info.UnixFileMode != (UnixFileMode)(-1);

    /// <summary>
    /// Lists the directory at <paramref name="relativePath"/> (empty for .pen/ itself), creating its copy.
    /// </summary>
    /// <remarks>
    /// The entries come in ordinal order of their names. <paramref name="problem"/> says why the directory can't be
    /// listed in full, or that .pen/ has too many entries.
    /// </remarks>
    private bool TryListEntries(
        string relativePath,
        [NotNullWhen(true)] out List<FileSystemInfo>? entries,
        [NotNullWhen(false)] out string? problem)
    {
        entries = null;
        var full = GetFullPath(relativePath);
        // Only readdir sees the real bytes; .NET's listing can't name a bad entry.
        if (Native.ListUndecodable(full) is not { Count: 0 })
        {
            problem = $"{Describe(relativePath)} can't be listed, or has a name that isn't valid UTF-8";
            return false;
        }
        // Writable until its entries are in; its real mode comes last.
        if (_copy is { } copyRoot)
        {
            Directory.CreateDirectory(GetCopyPath(copyRoot, relativePath),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var listed = new List<FileSystemInfo>();
        foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", Options))
        {
            if (++_entries > MaxEntries)
            {
                problem = $".pen/ has over {MaxEntries} files";
                return false;
            }
            listed.Add(entry);
        }
        listed.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        entries = listed;
        problem = null;
        return true;
    }

    /// <summary>Hashes, and copies, one <paramref name="entry"/> of a directory at <paramref name="depth"/>.</summary>
    /// <returns>Why .pen/ can't have a hash, or null.</returns>
    private string? HashEntry(string relativePath, FileSystemInfo entry, int depth)
    {
        if (relativePath == ConfigLoader.FileName)
        {
            // Left out of the hash, so it must really be the file that's meant: a directory or a link by this name
            // would be an unhashed way in.
            return CheckUnhashedFile(relativePath);
        }
        if (entry.LinkTarget is { } target)
            return HashLink(relativePath, target);
        if ((entry.Attributes & FileAttributes.Directory) != 0)
        {
            AppendHead(DirectoryEntry, entry.UnixFileMode, relativePath);
            return HashDirectory(relativePath, entry, depth + 1);
        }
        return HashFile(relativePath);
    }

    /// <summary>Hashes, and copies, the symlink at <paramref name="relativePath"/>.</summary>
    /// <returns>Why .pen/ can't have a hash, or null.</returns>
    private string? HashLink(string relativePath, string target)
    {
        if (CheckLink(relativePath, target) is { } refused)
            return refused;
        AppendHead(LinkEntry, 0, relativePath);
        AppendFramed(Encoding.UTF8.GetBytes(target));
        if (_copy is not { } copyRoot)
            return null;
        // macOS lets a symlink's target be empty, and File.CreateSymbolicLink refuses one: such a link can't be
        // copied, so the copy has no hash.
        if (target.Length == 0)
            return CantRead;
        File.CreateSymbolicLink(GetCopyPath(copyRoot, relativePath), target);
        return null;
    }

    /// <summary>Hashes, and copies, the regular file at <paramref name="relativePath"/>.</summary>
    /// <returns>
    /// Why .pen/ can't have a hash, or null: the file isn't a regular file, is over the limit or changes meanwhile.
    /// </returns>
    private string? HashFile(string relativePath) => ReadFile(relativePath, hashAndCopy: true);

    /// <summary>
    /// Reads the regular file at <paramref name="relativePath"/> through, neither hashing nor copying it.
    /// </summary>
    /// <returns>
    /// Why .pen/ can't have a hash, or null: the file isn't a regular file, is over the limit or changes meanwhile.
    /// </returns>
    private string? CheckUnhashedFile(string relativePath) => ReadFile(relativePath, hashAndCopy: false);

    /// <summary>
    /// Reads the regular file at <paramref name="relativePath"/>, hashing and copying it when
    /// <paramref name="hashAndCopy"/>.
    /// </summary>
    /// <returns>
    /// Why .pen/ can't have a hash, or null: the file isn't a regular file, is over the limit or changes meanwhile.
    /// </returns>
    private string? ReadFile(string relativePath, bool hashAndCopy)
    {
        using var file = Native.OpenRegularNoFollow(GetFullPath(relativePath));
        if (file is null)
            return $"{Describe(relativePath)} isn't a regular file pen can read";
        if (ReadLength(file) is not { } length)
            return CantRead;
        if (length > MaxBytes - _bytes)
            return $".pen/ is over {MaxBytes / (1024 * 1024)} MiB, at {Describe(relativePath)}";
        _bytes += length;

        var mode = File.GetUnixFileMode(file);
        if (hashAndCopy)
        {
            AppendHead(FileEntry, mode, relativePath);
            AppendLength(length);
        }
        using var copy = _copy is { } copyRoot && hashAndCopy ? CreateCopyFile(copyRoot, relativePath) : null;
        var buffer = new byte[64 * 1024];
        long offset = 0;
        for (int read; (read = RandomAccess.Read(file, buffer, offset)) > 0; offset += read)
        {
            if (read > length - offset)
                return $"{Describe(relativePath)} changed while pen read it";
            if (hashAndCopy)
                _hash.AppendData(buffer, 0, read);
            copy?.Write(buffer, 0, read);
        }
        if (offset != length)
            return $"{Describe(relativePath)} changed while pen read it";
        if (copy is not null)
        {
            copy.Flush();
            if (ReadTime(() => File.GetLastWriteTimeUtc(file)) is not { } time)
                return CantRead;
            File.SetLastWriteTimeUtc(copy.SafeFileHandle, time);
            File.SetUnixFileMode(copy.SafeFileHandle, mode);
        }
        return null;
    }

    /// <summary>Reads the length of the open <paramref name="file"/>.</summary>
    /// <returns>The length, or null when the file system says it can't seek, which RandomAccess refuses.</returns>
    private static long? ReadLength(SafeFileHandle file)
    {
        try
        {
            return RandomAccess.GetLength(file);
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads a modification time to give the copy.</summary>
    /// <returns>The time, or null when it's outside what <see cref="DateTime"/> holds.</returns>
    /// <remarks>
    /// Some file systems keep times far past year 9999, and the agent can set one; .NET's readers throw
    /// <see cref="ArgumentOutOfRangeException"/> for those and have no form that doesn't.
    /// </remarks>
    private static DateTime? ReadTime(Func<DateTime> read)
    {
        try
        {
            return read();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates the copy, under <paramref name="copyRoot"/>, of the file at <paramref name="relativePath"/>, which must
    /// not exist yet.
    /// </summary>
    private static FileStream CreateCopyFile(string copyRoot, string relativePath) =>
        new(GetCopyPath(copyRoot, relativePath), new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });

    private static string GetCopyPath(string copyRoot, string relativePath) =>
        relativePath.Length == 0 ? copyRoot : Path.Combine(copyRoot, relativePath);

    /// <summary>Checks that the symlink at <paramref name="relativePath"/> leads straight into .pen/.</summary>
    /// <returns>Why it doesn't, or null when it does.</returns>
    /// <remarks>
    /// It follows the target by hand: it has to stay in .pen/ at every step and pass only through real directories,
    /// which also rules out loops and chains.
    /// </remarks>
    private string? CheckLink(string relativePath, string target)
    {
        var refused = $"{Describe(relativePath)} is a symlink that " +
            "doesn't lead straight to a file or directory in .pen/";
        // U+FFFD: a target that isn't valid UTF-8 can't be hashed for what it is.
        if (target.Contains('\uFFFD') || target.StartsWith('/'))
            return refused;

        var at = relativePath.Split('/')[..^1].ToList();
        foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (at.Count == 0)
                    return refused;
                at.RemoveAt(at.Count - 1);
                continue;
            }
            at.Add(part);
            var path = GetFullPath(string.Join('/', at));
            if (PathChecks.IsLink(path) || !Path.Exists(path))
                return refused;
        }
        // config.toml isn't hashed, so a link to it (ignoring case, for macOS) could change unseen.
        if (at.Count == 1 && at[0].Equals(ConfigLoader.FileName, StringComparison.OrdinalIgnoreCase))
            return refused;
        return null;
    }

    private string GetFullPath(string relativePath) =>
        relativePath.Length == 0 ? _root : Path.Combine(_root, relativePath);

    /// <summary>Appends an entry's type, mode and framed path to the hash.</summary>
    private void AppendHead(byte type, UnixFileMode mode, string relativePath)
    {
        Span<byte> head = stackalloc byte[5];
        head[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(head[1..], (int)mode);
        _hash.AppendData(head);
        AppendFramed(Encoding.UTF8.GetBytes(relativePath));
    }

    /// <summary>Appends <paramref name="bytes"/> to the hash, after their length.</summary>
    private void AppendFramed(byte[] bytes)
    {
        AppendLength(bytes.Length);
        _hash.AppendData(bytes);
    }

    /// <summary>Appends <paramref name="length"/> to the hash, as 8 bytes, little-endian.</summary>
    private void AppendLength(long length)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, length);
        _hash.AppendData(bytes);
    }
}
