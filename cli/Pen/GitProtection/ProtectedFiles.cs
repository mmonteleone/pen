using System.Globalization;
using System.Text;
using System.Text.Json;
using Pen.Platform;
using Pen.Sessions;

namespace Pen.GitProtection;

/// <summary>
/// Saves copies of the protected files at a session's start and reads the manifest that lists them; a
/// <see cref="ProtectedFileRestore"/> puts them back at its end.
/// </summary>
/// <remarks>
/// <para>
/// The copies exist since a single-file mount can be got past (case variants of the name, on a case-insensitive
/// filesystem). They live in the session dir, which nothing mounts into the agent or dind. Files over the 1 MB read
/// cap are refused rather than saved in part: a config that size was refused already, and no real commondir or .git
/// file comes near it.
/// </para>
/// <para>
/// Like the rest of the check, everything here reads files directly and never runs git. Checks made just before a
/// write can still race with another session's agent; the last component is never followed regardless.
/// </para>
/// </remarks>
internal static class ProtectedFiles
{
    /// <summary>The mode of a copy, and of a changed version kept beside a protected file.</summary>
    internal const UnixFileMode File600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode Dir700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const int ManifestVersion = 1;

    /// <summary>
    /// Saves the files in <paramref name="mounts"/> marked Verify, refusing the session if one can't be saved whole.
    /// </summary>
    /// <param name="session">The session that's starting.</param>
    /// <param name="mounts">The session's mounts.</param>
    /// <param name="dir">The project dir; messages are relative to it.</param>
    /// <param name="open">The project's other open sessions (<see cref="SessionRegistry.FindOpen"/>).</param>
    /// <param name="unlistedModules">
    /// The module repos there now that an open session didn't list at its start (<see cref="ModuleBaseline"/>).
    /// </param>
    /// <remarks>
    /// A file <paramref name="open"/> sessions already saved keeps their copy, the oldest that still has one: its
    /// agent may have changed the file since, and this session's end would otherwise put that change back after that
    /// session's end had removed it. The file on disk is only taken when no session has a copy, which means none
    /// could have changed it since the last one put it back. The same file can have another name by now (an agent's
    /// rename on a case-insensitive filesystem), so they're matched by what each name resolves to today. A session
    /// that adopted a file hands that on, since its copy is whatever was there. The manifest goes last, through a
    /// rename, so its presence means the copies are complete.
    /// </remarks>
    public static void Save(
        SessionDir session,
        IReadOnlyList<Mount> mounts,
        string dir,
        IReadOnlyList<SessionDir> open,
        IReadOnlyList<string> unlistedModules)
    {
        // Project.CheckAllowed covers the project dir; this covers every mount, the common git dir among them.
        var realSession = Native.RealPath(session.Path) ?? session.Path;
        if (RepoGuard.IsInside(dir, realSession) || mounts.Any(m => RepoGuard.IsInside(m.Path, realSession)))
        {
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen's " +
                $"session dir {Out.Field(realSession)} would be inside the sandbox");
        }

        var older = ReadOlder(open);
        Directory.CreateDirectory(session.ProtectedDir, Dir700);
        try
        {
            var files = SaveCopies(session, mounts, dir, older, unlistedModules);
            var dirs = mounts.Where(m => m.ReadOnly && !m.Verify && Directory.Exists(m.Path))
                .Select(m => m.Path)
                .ToList();
            var temporary = session.ProtectedManifest + ".tmp";
            File.WriteAllText(temporary, Serialize(new(files, dirs)));
            File.Move(temporary, session.ProtectedManifest);
        }
        catch
        {
            // They can hold secrets (a token in a remote's URL), and this session won't end to remove them.
            Directory.Delete(session.ProtectedDir, recursive: true);
            throw;
        }
    }

    /// <summary>Writes a copy of each file in <paramref name="mounts"/> marked Verify into the protected dir.</summary>
    /// <returns>The saved files, in mount order; each copy is named by its index.</returns>
    private static List<ProtectedFile> SaveCopies(
        SessionDir session,
        IReadOnlyList<Mount> mounts,
        string dir,
        OlderSessions older,
        IReadOnlyList<string> unlistedModules)
    {
        var files = new List<ProtectedFile>();
        foreach (var mount in mounts.Where(m => m.Verify))
        {
            var real = RequireRealPath(dir, mount.Path);
            var baseline = ReadBaseline(dir, real, older.Files);
            var copy = files.Count.ToString(CultureInfo.InvariantCulture);
            WriteNew(Path.Combine(session.ProtectedDir, copy), baseline.Bytes, File600);
            files.Add(new(real, copy, baseline.Mode, IsAdopted(real, older, unlistedModules)));
        }
        return files;
    }

    /// <summary>Tells whether an exception reading a manifest means it's missing or damaged.</summary>
    internal static bool IsDamage(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or PenException
            or KeyNotFoundException or InvalidOperationException or FormatException;

    /// <summary>Reads the session's manifest, before its files are put back.</summary>
    /// <returns>The manifest, or null for a session that has none.</returns>
    /// <remarks>Throws if it can't be read; <see cref="IsDamage"/> holds for the usual causes.</remarks>
    public static ProtectedManifest? Read(SessionDir session) =>
        File.Exists(session.ProtectedManifest) ? Parse(File.ReadAllText(session.ProtectedManifest)) : null;

    /// <summary>Reads what the session recorded at its start, whether or not its files have been put back.</summary>
    /// <returns>The record, or null if it has no such record, or none that can be read.</returns>
    /// <remarks>
    /// The manifest is tried before the restored manifest, since a session's manifest is moved from the first to the
    /// second once its files are put back.
    /// </remarks>
    private static ProtectedManifest? ReadRecord(SessionDir session)
    {
        try
        {
            try
            {
                if (Read(session) is { } manifest)
                    return manifest;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // moved just now
            }
            return File.Exists(session.RestoredManifest) ? Parse(File.ReadAllText(session.RestoredManifest)) : null;
        }
        catch (Exception exception) when (IsDamage(exception))
        {
            return null;
        }
    }

    /// <summary>Reads what the session-end check goes by: what the session recorded at its start.</summary>
    /// <returns>The record, or an empty one if there's none that can be read.</returns>
    /// <remarks>
    /// A session with no readable record vouches for no config.worktree, as one from a Pen that didn't record its
    /// module repos keeps none of them.
    /// </remarks>
    public static ProtectedManifest AtStart(SessionDir session) => ReadRecord(session) ?? new([], []);

    /// <summary>Writes a manifest as indented JSON with a trailing newline.</summary>
    internal static string Serialize(ProtectedManifest manifest)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", ManifestVersion);
            writer.WriteStartArray("files");
            foreach (var file in manifest.Files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", RequireEncodable(file.Path));
                writer.WriteString("copy", file.Copy);
                writer.WriteNumber("mode", (int)file.Mode);
                writer.WriteBoolean("adopted", file.Adopted);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("readOnlyDirs");
            foreach (var dir in manifest.ReadOnlyDirs)
                writer.WriteStringValue(RequireEncodable(dir));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>Reads a manifest written by <see cref="Serialize"/>.</summary>
    /// <exception cref="PenException">It's from another version of Pen, or a copy's name is damaged.</exception>
    internal static ProtectedManifest Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("version").GetInt32() != ManifestVersion)
            throw new PenException("the saved list of protected files is from another version of pen");
        var files = new List<ProtectedFile>();
        foreach (var entry in root.GetProperty("files").EnumerateArray())
        {
            var copy = entry.GetProperty("copy").GetString()!;
            if (copy.Length == 0 || !copy.All(char.IsAsciiDigit))
                throw new PenException("the saved list of protected files is damaged");
            var path = entry.GetProperty("path").GetString()!;
            var mode = (UnixFileMode)(entry.GetProperty("mode").GetInt32() & 0xFFF);
            files.Add(new(path, copy, mode, entry.GetProperty("adopted").GetBoolean()));
        }
        return new(files, root.GetProperty("readOnlyDirs").EnumerateArray().Select(d => d.GetString()!).ToList());
    }

    /// <summary>
    /// What open sessions have saved: their files by what each path resolves to now, oldest session first; the files
    /// any of them adopted; each one's read-only directories; and whether one has no record to go by.
    /// </summary>
    /// <remarks>
    /// A session that has put its files back is still here, as its agent may have planted what its check hasn't
    /// reached yet, but its copies are gone, so the next session's are what count.
    /// </remarks>
    private sealed record OlderSessions(
        Dictionary<string, List<SavedCopy>> Files,
        HashSet<string> Adopted,
        List<List<string>> ReadOnlyDirs,
        bool Unrecorded);

    /// <summary>An open session's saved file.</summary>
    private sealed record SavedCopy(SessionDir Session, ProtectedFile File);

    /// <summary>Reads what <paramref name="open"/> sessions have saved.</summary>
    private static OlderSessions ReadOlder(IReadOnlyList<SessionDir> open)
    {
        var files = new Dictionary<string, List<SavedCopy>>(StringComparer.Ordinal);
        var adopted = new HashSet<string>(StringComparer.Ordinal);
        var readOnly = new List<List<string>>();
        var unrecorded = false;
        foreach (var session in open.OrderBy(session => session.ReadStarted() ?? DateTime.MaxValue))
        {
            var manifest = ReadRecord(session);
            if (manifest == null)
            {
                // Its record goes after its compose.yaml, so one that's still open with
                // no record is from an older Pen or damaged: its agent could write.
                unrecorded |= session.IsOpen;
                continue;
            }
            readOnly.Add(manifest.ReadOnlyDirs);
            foreach (var file in manifest.Files)
            {
                var real = Native.RealPath(file.Path) ?? file.Path;
                if (!files.TryGetValue(real, out var saved))
                    files[real] = saved = [];
                saved.Add(new(session, file));
                if (file.Adopted)
                    adopted.Add(real);
            }
        }
        return new(files, adopted, readOnly, unrecorded);
    }

    /// <summary>
    /// Tells whether <paramref name="path"/> is a file an open session's agent could have made: one in a module repo
    /// an open session didn't list, one a session adopted, or a config.worktree none saved where one could write.
    /// </summary>
    /// <remarks>
    /// The session-end check quarantines a module repo the session didn't list, so nothing in it is put back. A
    /// session adopted a file only in a repo it didn't list, which this one doesn't list either, or a config.worktree.
    /// Being read-only in one session says nothing about the others.
    /// </remarks>
    private static bool IsAdopted(string path, OlderSessions older, IReadOnlyList<string> unlistedModules)
    {
        if (unlistedModules.Any(repo => RepoGuard.IsInside(repo, path)))
            return true;
        if (older.Adopted.Contains(path))
            return true;
        if (!string.Equals(Path.GetFileName(path), "config.worktree", StringComparison.OrdinalIgnoreCase))
            return false;
        if (older.Files.ContainsKey(path))
            return false;
        return older.Unrecorded || older.ReadOnlyDirs.Any(dirs => !dirs.Any(d => RepoGuard.IsInside(d, path)));
    }

    /// <summary>
    /// Reads what to save for <paramref name="path"/>: the oldest open session's copy that's still there, or else
    /// the file on disk.
    /// </summary>
    /// <exception cref="PenException">The file on disk can't be read or saved whole.</exception>
    private static RegularFile ReadBaseline(string dir, string path, Dictionary<string, List<SavedCopy>> saved)
    {
        foreach (var older in saved.GetValueOrDefault(path) ?? [])
        {
            try
            {
                var copy = Native.ReadProtected(Path.Combine(older.Session.ProtectedDir, older.File.Copy));
                if (copy != null)
                    return new(copy.Bytes, older.File.Mode);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // that session has put its files back
            }
        }
        try
        {
            return Native.ReadProtected(path)
                ?? throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't save a copy " +
                    $"of {RepoGuard.FormatPath(dir, path)}: it isn't a regular file of at most 1 MB, so it " +
                    "couldn't be restored at the end of the session. Inspect it and remove it, then rerun");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't read " +
                $"{RepoGuard.FormatPath(dir, path)} to save a copy of it: fix its permissions, then rerun");
        }
    }

    /// <summary>Resolves <paramref name="path"/> as it is on disk, which may differ from it only in case.</summary>
    /// <returns>The real path.</returns>
    /// <exception cref="PenException">
    /// It resolves anywhere else, which means a symlink was swapped in since it was checked, or it's gone.
    /// </exception>
    private static string RequireRealPath(string dir, string path)
    {
        var real = Native.RealPath(path);
        if (real == null || !string.Equals(real, path, StringComparison.OrdinalIgnoreCase))
        {
            throw new PenException($"refusing to run in {Out.Field(dir)}: {RepoGuard.FormatPath(dir, path)} " +
                "isn't where pen expected it (a symlink, or it's gone): inspect it, then rerun");
        }

        return real;
    }

    /// <summary>Checks that a path can be written to the manifest and read back the same.</summary>
    /// <returns>The path.</returns>
    /// <remarks>Paths reach here from the file system, as valid UTF-16; one that isn't is refused.</remarks>
    private static string RequireEncodable(string path)
    {
        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetByteCount(path);
        }
        catch (ArgumentException)
        {
            throw new PenException($"pen can't record the path {Out.Field(path)}: it isn't valid text");
        }
        return path;
    }

    /// <summary>Creates a file with exactly this content and mode.</summary>
    /// <remarks>Fails if anything is there, a dangling symlink included.</remarks>
    internal static void WriteNew(string path, byte[] bytes, UnixFileMode mode)
    {
        using var stream = new FileStream(path,
            new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = File600 });
        stream.Write(bytes);
        stream.Flush();
        File.SetUnixFileMode(stream.SafeFileHandle, mode);
    }
}
