using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Pen.Configuration;
using Pen.GitProtection;
using Pen.Platform;

namespace Pen.Trust;

/// <summary>Hashes what docker build reads from .pen/, and copies it to build from.</summary>
/// <remarks>
/// <para>
/// The hash covers all of .pen/ but config.toml, which pen allow edits and which holds only settings
/// approved one by one. Every entry counts by type, permission bits (COPY keeps them all), full relative
/// path and content or link target, each with its length first, so moving bytes between a name and a
/// content can't produce the same input. Entries come in a fixed order.
/// </para>
/// <para>
/// The agent may have written everything here. Whatever the hash wouldn't pin down, or that could hang
/// or exhaust Pen, means no hash: special files, names that aren't valid UTF-8, trees over the limits,
/// anything unreadable, and symlinks other than a relative one through real directories to a real file
/// or directory in .pen/. Others could read content from outside, or be repointed from outside, without
/// changing the hash. Never runs docker or git.
/// </para>
/// <para>
/// The hash says what a directory held at one moment, and the agent may be able to change .pen/ right
/// after, so only a copy nobody else can write is safe to build from: see <see cref="Snapshot"/>.
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

    /// <summary>Releases the hash.</summary>
    public void Dispose() => _hash.Dispose();

    /// <summary>Hashes <paramref name="penDir"/>.</summary>
    /// <returns>The hash, or the problem that means there's none.</returns>
    public static PenDirHashResult Compute(string penDir) => Walk(penDir, null);

    /// <summary>Copies <paramref name="penDir"/> to <paramref name="copy"/> and hashes the copy.</summary>
    /// <param name="penDir">The .pen/ to copy.</param>
    /// <param name="copy">Where to copy it: it must not exist, in a directory only Pen can reach.</param>
    /// <returns>The hash of the copy, or the problem that means there's none.</returns>
    /// <remarks>
    /// The copy is made under the same refusals, keeping types, modes and modification times. config.toml
    /// stays out: it isn't hashed, so a build must not read it. It never follows a symlink, and reads each
    /// file through one descriptor. The hash is of the copy, which can't change afterwards, taken there so
    /// that symlinks are judged by the tree that gets built.
    /// </remarks>
    public static PenDirHashResult Snapshot(string penDir, string copy)
    {
        var copied = Walk(penDir, copy);
        return copied.Hash == null ? copied : Compute(copy);
    }

    /// <summary>Hashes <paramref name="penDir"/>, copying it to <paramref name="copy"/> unless that's null.</summary>
    private static PenDirHashResult Walk(string penDir, string? copy)
    {
        try
        {
            if (RepoGuard.IsLink(penDir))
                return new(null, ".pen is a symlink");
            using var walk = new PenDirHash(penDir, copy);
            walk._hash.AppendData("pen .pen/ hash 1\n"u8);
            var root = new DirectoryInfo(penDir);
            walk.AppendHead(DirectoryEntry, root.UnixFileMode, "");
            walk.HashDirectory("", root, depth: 0);
            return new(Convert.ToHexStringLower(walk._hash.GetHashAndReset()), null);
        }
        catch (PenException exception)
        {
            return new(null, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            return new(null, "part of .pen/ can't be read");
        }
    }

    /// <summary>Describes <paramref name="relativePath"/> for a message, escaped.</summary>
    private static string Describe(string relativePath) => ".pen/" + Out.Field(relativePath);

    /// <summary>Hashes, and copies, the directory at <paramref name="relativePath"/> and all it holds.</summary>
    /// <param name="relativePath">Its path in .pen/, empty for .pen/ itself.</param>
    /// <param name="self">The directory, for its mode and modification time.</param>
    /// <param name="depth">How deep it is in .pen/.</param>
    private void HashDirectory(string relativePath, FileSystemInfo self, int depth)
    {
        if (depth > MaxDepth)
            throw new PenException($"{Describe(relativePath)} is nested too deeply");
        var entries = ListEntries(relativePath);
        foreach (var entry in entries)
            HashEntry(relativePath.Length == 0 ? entry.Name : relativePath + "/" + entry.Name, entry, depth);

        if (_copy != null)
        {
            Directory.SetLastWriteTimeUtc(GetCopyPath(relativePath), self.LastWriteTimeUtc);
            File.SetUnixFileMode(GetCopyPath(relativePath), self.UnixFileMode);
        }
    }

    /// <summary>Lists the directory at <paramref name="relativePath"/>, creating its copy.</summary>
    /// <returns>Its entries, in ordinal order of their names.</returns>
    /// <exception cref="PenException">When it can't be listed in full, or .pen/ has too many entries.</exception>
    private List<FileSystemInfo> ListEntries(string relativePath)
    {
        var full = GetFullPath(relativePath);
        // Only readdir sees the real bytes; .NET's listing can't name a bad entry.
        if (Native.ListUndecodable(full) is not { Count: 0 })
        {
            throw new PenException(
                $"{Describe(relativePath)} can't be listed, or has a name that isn't valid UTF-8");
        }
        // Writable until its entries are in; its real mode comes last.
        if (_copy != null)
        {
            Directory.CreateDirectory(GetCopyPath(relativePath),
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var entries = new List<FileSystemInfo>();
        foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", Options))
        {
            if (++_entries > MaxEntries)
                throw new PenException($".pen/ has over {MaxEntries} files");
            entries.Add(entry);
        }
        entries.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return entries;
    }

    /// <summary>Hashes, and copies, one <paramref name="entry"/> of a directory at <paramref name="depth"/>.</summary>
    private void HashEntry(string relativePath, FileSystemInfo entry, int depth)
    {
        if (relativePath == Config.FileName)
        {
            // Left out of the hash, so it must really be the file that's meant:
            // a directory or a link by this name would be an unhashed way in.
            CheckUnhashedFile(relativePath);
        }
        else if (entry.LinkTarget is { } target)
        {
            CheckLink(relativePath, target);
            AppendHead(LinkEntry, 0, relativePath);
            AppendFramed(Encoding.UTF8.GetBytes(target));
            if (_copy != null)
                File.CreateSymbolicLink(GetCopyPath(relativePath), target);
        }
        else if ((entry.Attributes & FileAttributes.Directory) != 0)
        {
            AppendHead(DirectoryEntry, entry.UnixFileMode, relativePath);
            HashDirectory(relativePath, entry, depth + 1);
        }
        else
        {
            HashFile(relativePath);
        }
    }

    /// <summary>Hashes, and copies, the regular file at <paramref name="relativePath"/>.</summary>
    /// <exception cref="PenException">When it isn't a regular file, is over the limit or changes meanwhile.</exception>
    private void HashFile(string relativePath) => ReadFile(relativePath, hashAndCopy: true);

    /// <summary>
    /// Reads the regular file at <paramref name="relativePath"/> through, neither hashing nor copying it.
    /// </summary>
    /// <exception cref="PenException">When it isn't a regular file, is over the limit or changes meanwhile.</exception>
    private void CheckUnhashedFile(string relativePath) => ReadFile(relativePath, hashAndCopy: false);

    /// <summary>
    /// Reads the regular file at <paramref name="relativePath"/>, hashing and copying it when
    /// <paramref name="hashAndCopy"/>.
    /// </summary>
    /// <exception cref="PenException">When it isn't a regular file, is over the limit or changes meanwhile.</exception>
    private void ReadFile(string relativePath, bool hashAndCopy)
    {
        using var file = Native.OpenRegularNoFollow(GetFullPath(relativePath))
            ?? throw new PenException($"{Describe(relativePath)} isn't a regular file pen can read");
        var length = RandomAccess.GetLength(file);
        if (length > MaxBytes - _bytes)
            throw new PenException($".pen/ is over {MaxBytes / (1024 * 1024)} MiB, at {Describe(relativePath)}");
        _bytes += length;

        var mode = File.GetUnixFileMode(file);
        if (hashAndCopy)
        {
            AppendHead(FileEntry, mode, relativePath);
            AppendLength(length);
        }
        using var copy = _copy == null || !hashAndCopy ? null : CreateCopyFile(relativePath);
        var buffer = new byte[64 * 1024];
        long offset = 0;
        for (int read; (read = RandomAccess.Read(file, buffer, offset)) > 0; offset += read)
        {
            if (read > length - offset)
                throw new PenException($"{Describe(relativePath)} changed while pen read it");
            if (hashAndCopy)
                _hash.AppendData(buffer, 0, read);
            copy?.Write(buffer, 0, read);
        }
        if (offset != length)
            throw new PenException($"{Describe(relativePath)} changed while pen read it");
        if (copy != null)
        {
            copy.Flush();
            File.SetLastWriteTimeUtc(copy.SafeFileHandle, File.GetLastWriteTimeUtc(file));
            File.SetUnixFileMode(copy.SafeFileHandle, mode);
        }
    }

    /// <summary>Creates the copy of the file at <paramref name="relativePath"/>, which must not exist yet.</summary>
    private FileStream CreateCopyFile(string relativePath) => new(GetCopyPath(relativePath), new FileStreamOptions
    {
        Mode = FileMode.CreateNew,
        Access = FileAccess.Write,
        UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
    });

    private string GetCopyPath(string relativePath) =>
        relativePath.Length == 0 ? _copy! : Path.Combine(_copy!, relativePath);

    /// <summary>Checks that the symlink at <paramref name="relativePath"/> leads straight into .pen/.</summary>
    /// <exception cref="PenException">When it doesn't.</exception>
    /// <remarks>
    /// It follows the target by hand: it has to stay in .pen/ at every step and pass only through real
    /// directories, which also rules out loops and chains.
    /// </remarks>
    private void CheckLink(string relativePath, string target)
    {
        var refused = new PenException($"{Describe(relativePath)} is a symlink that " +
            "doesn't lead straight to a file or directory in .pen/");
        // U+FFFD: a target that isn't valid UTF-8 can't be hashed for what it is.
        if (target.Contains('\uFFFD') || target.StartsWith('/'))
            throw refused;

        var at = relativePath.Split('/')[..^1].ToList();
        foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (at.Count == 0)
                    throw refused;
                at.RemoveAt(at.Count - 1);
                continue;
            }
            at.Add(part);
            var path = GetFullPath(string.Join('/', at));
            if (RepoGuard.IsLink(path) || !Path.Exists(path))
                throw refused;
        }
        // config.toml isn't hashed, so a link to it (ignoring case, for macOS) could change unseen.
        if (at.Count == 1 && at[0].Equals(Config.FileName, StringComparison.OrdinalIgnoreCase))
            throw refused;
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
