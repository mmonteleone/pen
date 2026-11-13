using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Pen.Configuration;
using Pen.GitProtection;
using Pen.Platform;

namespace Pen.Trust;

/// <summary>
/// A hash of what docker build reads from .pen/ (all but config.toml, which Pen
/// allow edits and which holds only settings approved one by one). Every entry
/// counts by type, permission bits (COPY keeps them all), full relative path and
/// content or link target, each with its length first, so moving bytes between a
/// name and a content can't produce the same input. Entries come in a fixed order.
/// <para>
/// The agent may have written everything here. Whatever the hash wouldn't pin
/// down, or that could hang or exhaust Pen, means no hash: special files, names
/// that aren't valid UTF-8, trees over the limits, anything unreadable, and
/// symlinks other than a relative one through real directories to a real file
/// or directory in .pen/. Others could read content from outside, or be repointed
/// from outside, without changing the hash. Never runs docker or git.
/// </para>
/// <para>
/// The hash says what a directory held at one moment, and the agent may be able
/// to change .pen/ right after, so only a copy nobody else can write is safe to
/// build from: see <see cref="Snapshot"/>.
/// </para>
/// </summary>
internal sealed class PenDirHash : IDisposable
{
    private const long MaxBytes = 256L * 1024 * 1024;
    private const int MaxEntries = 10_000;
    private const int MaxDepth = 32;
    private const byte FileEntry = 0, DirectoryEntry = 1, LinkEntry = 2;

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

    private PenDirHash(string root, string? copy = null)
    {
        _root = root;
        _copy = copy;
    }

    public void Dispose() => _hash.Dispose();

    public static PenDirHashResult Compute(string penDir) => Walk(penDir, null);

    /// <summary>
    /// Copies <paramref name="penDir"/> to <paramref name="copy"/> (which must not
    /// exist, in a directory only Pen can reach) under the same refusals, keeping
    /// types, modes and modification times. config.toml stays out: it isn't hashed,
    /// so a build must not read it. Never follows a symlink, and reads each file
    /// through one descriptor. Returns the hash of the copy, which can't change afterwards,
    /// taken there so that symlinks are judged by the tree that gets built.
    /// </summary>
    public static PenDirHashResult Snapshot(string penDir, string copy)
    {
        var copied = Walk(penDir, copy);
        return copied.Hash == null ? copied : Compute(copy);
    }

    private static PenDirHashResult Walk(string penDir, string? copy)
    {
        try
        {
            if (RepoGuard.IsLink(penDir))
                return new(null, ".pen is a symlink");
            using var walk = new PenDirHash(penDir, copy);
            walk._hash.AppendData("pen .pen/ hash 1\n"u8);
            var root = new DirectoryInfo(penDir);
            walk.Head(DirectoryEntry, root.UnixFileMode, "");
            walk.Directory("", root, depth: 0);
            return new(Convert.ToHexStringLower(walk._hash.GetHashAndReset()), null);
        }
        catch (PenException e)
        {
            return new(null, e.Message);
        }
        catch (Exception e)
            when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return new(null, "part of .pen/ can't be read");
        }
    }

    private static string Show(string rel) => ".pen/" + Out.Field(rel);

    private void Directory(string rel, FileSystemInfo self, int depth)
    {
        if (depth > MaxDepth)
            throw new PenException($"{Show(rel)} is nested too deeply");
        var full = Full(rel);
        // Only readdir sees the real bytes; .NET's listing can't name a bad entry.
        if (Native.ListUndecodable(full) is not { Count: 0 })
            throw new PenException($"{Show(rel)} can't be listed, or has a name that isn't valid UTF-8");
        // Writable until its entries are in; its real mode comes last.
        if (_copy != null)
        {
            System.IO.Directory.CreateDirectory(CopyOf(rel),
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

        foreach (var entry in entries)
        {
            var path = rel.Length == 0 ? entry.Name : rel + "/" + entry.Name;
            if (path == Config.FileName)
            {
                // Left out of the hash, so it must really be the file that's meant:
                // a directory or a link by this name would be an unhashed way in.
                ReadFile(path, hashed: false);
                continue;
            }
            if (entry.LinkTarget is { } target)
            {
                CheckLink(path, target);
                Head(LinkEntry, 0, path);
                Frame(Encoding.UTF8.GetBytes(target));
                if (_copy != null)
                    File.CreateSymbolicLink(CopyOf(path), target);
            }
            else if ((entry.Attributes & FileAttributes.Directory) != 0)
            {
                Head(DirectoryEntry, entry.UnixFileMode, path);
                Directory(path, entry, depth + 1);
            }
            else
            {
                ReadFile(path, hashed: true);
            }
        }

        if (_copy != null)
        {
            System.IO.Directory.SetLastWriteTimeUtc(CopyOf(rel), self.LastWriteTimeUtc);
            File.SetUnixFileMode(CopyOf(rel), self.UnixFileMode);
        }
    }

    private void ReadFile(string rel, bool hashed)
    {
        using var file = Native.OpenRegularNoFollow(Full(rel))
            ?? throw new PenException($"{Show(rel)} isn't a regular file pen can read");
        var length = RandomAccess.GetLength(file);
        if (length > MaxBytes - _bytes)
            throw new PenException($".pen/ is over {MaxBytes / (1024 * 1024)} MiB, at {Show(rel)}");
        _bytes += length;

        var mode = File.GetUnixFileMode(file);
        if (hashed)
        {
            Head(FileEntry, mode, rel);
            Length(length);
        }
        using var copy = _copy == null || !hashed ? null : new FileStream(CopyOf(rel), new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        var buffer = new byte[64 * 1024];
        long offset = 0;
        for (int n; (n = RandomAccess.Read(file, buffer, offset)) > 0; offset += n)
        {
            if (n > length - offset)
                throw new PenException($"{Show(rel)} changed while pen read it");
            if (hashed)
                _hash.AppendData(buffer, 0, n);
            copy?.Write(buffer, 0, n);
        }
        if (offset != length)
            throw new PenException($"{Show(rel)} changed while pen read it");
        if (copy != null)
        {
            copy.Flush();
            File.SetLastWriteTimeUtc(copy.SafeFileHandle, File.GetLastWriteTimeUtc(file));
            File.SetUnixFileMode(copy.SafeFileHandle, mode);
        }
    }

    private string CopyOf(string rel) => rel.Length == 0 ? _copy! : Path.Combine(_copy!, rel);

    /// <summary>
    /// Follows the target by hand: it has to stay in .pen/ at every step and pass
    /// only through real directories, which also rules out loops and chains.
    /// </summary>
    private void CheckLink(string rel, string target)
    {
        var refused = new PenException($"{Show(rel)} is a symlink that " +
            "doesn't lead straight to a file or directory in .pen/");
        // U+FFFD: a target that isn't valid UTF-8 can't be hashed for what it is.
        if (target.Contains('\uFFFD') || target.StartsWith('/'))
            throw refused;

        var at = rel.Split('/')[..^1].ToList();
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
            var path = Full(string.Join('/', at));
            if (RepoGuard.IsLink(path) || !Path.Exists(path))
                throw refused;
        }
        // config.toml isn't hashed, so a link to it (ignoring case, for macOS) could change unseen.
        if (at.Count == 1 && at[0].Equals(Config.FileName, StringComparison.OrdinalIgnoreCase))
            throw refused;
    }

    private string Full(string rel) => rel.Length == 0 ? _root : Path.Combine(_root, rel);

    private void Head(byte type, UnixFileMode mode, string rel)
    {
        Span<byte> head = stackalloc byte[5];
        head[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(head[1..], (int)mode);
        _hash.AppendData(head);
        Frame(Encoding.UTF8.GetBytes(rel));
    }

    private void Frame(byte[] bytes)
    {
        Length(bytes.Length);
        _hash.AppendData(bytes);
    }

    private void Length(long length)
    {
        Span<byte> bytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, length);
        _hash.AppendData(bytes);
    }
}
