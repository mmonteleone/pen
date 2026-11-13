using System.Globalization;
using System.Text;
using System.Text.Json;
using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;

namespace Pen.GitProtection;

/// <summary>
/// Copies of the protected files, taken at a session's start and put back at
/// its end, since a single-file mount can be got past (case variants of the
/// name, on a case-insensitive filesystem). They live in the session dir, which
/// nothing mounts into the agent or dind. Files over the 1 MB read cap are
/// refused rather than saved in part: a config that size was refused already,
/// and no real commondir or .git file comes near it.
/// <para>
/// Like the rest of the check, everything here reads files directly and never
/// runs git. Checks made just before a write can still race with another
/// session's agent; the last component is never followed regardless.
/// </para>
/// </summary>
internal static class ProtectedFiles
{
    private const UnixFileMode Dir700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode File600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const int ManifestVersion = 1;

    /// <summary>
    /// Saves the files in <paramref name="mounts"/> marked Verify, refusing the
    /// session if one can't be saved whole. A file <paramref name="running"/>
    /// sessions already saved keeps their copy, the oldest that still has one: its
    /// agent may have changed the file since, and this session's end would otherwise
    /// put that change back after that session's end had removed it. The file on disk
    /// is only taken when no session has a copy, which means none could have changed
    /// it since the last one put it back. The same file can have
    /// another name by now (an agent's rename on a case-insensitive filesystem), so
    /// they're matched by what each name resolves to today. A session that adopted
    /// a file hands that on, since its copy is whatever was there. The manifest goes
    /// last, through a rename, so its presence means the copies are complete.
    /// </summary>
    public static void Save(SessionDir session, IReadOnlyList<Mount> mounts, string dir, IReadOnlyList<SessionDir> running)
    {
        // Project.CheckAllowed covers the project dir; this covers every mount, the common git dir among them.
        var realSession = Native.RealPath(session.Path) ?? session.Path;
        if (RepoGuard.IsInside(dir, realSession) || mounts.Any(m => RepoGuard.IsInside(m.Path, realSession)))
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen's session dir {Out.Field(realSession)} would be inside the sandbox");

        var older = Older(running);
        Directory.CreateDirectory(session.ProtectedDir, Dir700);
        try
        {
            var files = new List<ProtectedFile>();
            foreach (var m in mounts.Where(m => m.Verify))
            {
                var real = RequireRealPath(dir, m.Path);
                var baseline = Baseline(dir, real, older.Files);
                var copy = files.Count.ToString(CultureInfo.InvariantCulture);
                WriteNew(Path.Combine(session.ProtectedDir, copy), baseline.Bytes, File600);
                files.Add(new(real, copy, baseline.Mode, IsAdopted(real, older)));
            }
            var dirs = mounts.Where(m => m.ReadOnly && !m.Verify && Directory.Exists(m.Path)).Select(m => m.Path).ToList();
            var tmp = session.ProtectedManifest + ".tmp";
            File.WriteAllText(tmp, Serialize(new(files, dirs)));
            File.Move(tmp, session.ProtectedManifest);
        }
        catch
        {
            // They can hold secrets (a token in a remote's URL), and this session won't end to remove them.
            Directory.Delete(session.ProtectedDir, recursive: true);
            throw;
        }
    }

    private static bool IsDamage(Exception e) =>
        e is IOException or UnauthorizedAccessException or JsonException or PenException or KeyNotFoundException or InvalidOperationException or FormatException;

    /// <summary>The manifest, or null for a session that has none. Throws if it can't be read.</summary>
    public static ProtectedManifest? Read(SessionDir session) =>
        File.Exists(session.ProtectedManifest) ? Parse(File.ReadAllText(session.ProtectedManifest)) : null;

    /// <summary>
    /// What the session recorded at its start, whether or not its files have been
    /// put back; null if it has no such record, or none that can be read. In that
    /// order, since a session's manifest is moved from the first to the second.
    /// </summary>
    private static ProtectedManifest? ReadRecord(SessionDir session)
    {
        try
        {
            try
            {
                if (Read(session) is { } manifest) return manifest;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { } // moved just now
            return File.Exists(session.RestoredManifest) ? Parse(File.ReadAllText(session.RestoredManifest)) : null;
        }
        catch (Exception e) when (IsDamage(e)) { return null; }
    }

    /// <summary>
    /// What the session-end check goes by: a session with no readable record
    /// vouches for no config.worktree, as one from a Pen that didn't record its
    /// module repos keeps none of them.
    /// </summary>
    public static ProtectedManifest AtStart(SessionDir session) => ReadRecord(session) ?? new([], []);

    internal static string Serialize(ProtectedManifest manifest)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", ManifestVersion);
            w.WriteStartArray("files");
            foreach (var f in manifest.Files)
            {
                w.WriteStartObject();
                w.WriteString("path", Encodable(f.Path));
                w.WriteString("copy", f.Copy);
                w.WriteNumber("mode", (int)f.Mode);
                w.WriteBoolean("adopted", f.Adopted);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartArray("readOnlyDirs");
            foreach (var d in manifest.ReadOnlyDirs) w.WriteStringValue(Encodable(d));
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    internal static ProtectedManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("version").GetInt32() != ManifestVersion)
            throw new PenException("the saved list of protected files is from another version of pen");
        var files = new List<ProtectedFile>();
        foreach (var f in root.GetProperty("files").EnumerateArray())
        {
            var copy = f.GetProperty("copy").GetString()!;
            if (copy.Length == 0 || !copy.All(char.IsAsciiDigit))
                throw new PenException("the saved list of protected files is damaged");
            files.Add(new(f.GetProperty("path").GetString()!, copy, (UnixFileMode)(f.GetProperty("mode").GetInt32() & 0xFFF), f.GetProperty("adopted").GetBoolean()));
        }
        return new(files, root.GetProperty("readOnlyDirs").EnumerateArray().Select(d => d.GetString()!).ToList());
    }

    /// <summary>
    /// Puts each file in the session's manifest back, and reports through
    /// <paramref name="moved"/> and <paramref name="restored"/>. A changed
    /// regular file is written back in place, since another session's single-file
    /// mount pins the inode and a rename would leave that mount protecting a
    /// stale file; anything else there is renamed aside and the file recreated.
    /// Identical files aren't touched.
    /// <para>
    /// Adopted files aren't looked at: nothing vouches for them, so the end check
    /// quarantines whatever is there (<see cref="ProtectedManifest.Vouches"/>).
    /// </para>
    /// <para>
    /// Returns whether that's all done, and only then removes the copies, so a
    /// retried end can't write them over later edits. The manifest stays, as
    /// <see cref="SessionDir.RestoredManifest"/>: a retried end and a session starting
    /// now both need to know what this one vouched for. If not, an entry in
    /// <paramref name="moved"/> with no destination fails the check, and the caller
    /// must keep the session's files: they're the only good copies, and a new
    /// session would otherwise take what's on disk as its baseline. Whatever goes
    /// wrong, the original goes back before anything that could stop it, such as
    /// keeping the changed version.
    /// </para>
    /// </summary>
    public static bool Restore(SessionDir session, string dir, string? commonGitDir, List<Quarantined> moved, List<RestoredFile> restored, List<ModeChange> opened, List<string> blocked)
    {
        ProtectedManifest? manifest;
        try { manifest = Read(session); }
        catch (Exception e) when (IsDamage(e))
        {
            moved.Add(new(session.ProtectedManifest, "", "pen's list of protected files couldn't be read, so they weren't compared. COULD NOT RESTORE THEM: don't run git here until you've checked them"));
            return false;
        }
        if (manifest == null) return true;
        int before = moved.Count;
        foreach (var f in manifest.Files.Where(f => !f.Adopted))
        {
            try { RestoreOne(session, f, dir, commonGitDir, moved, restored, opened, blocked); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PenException)
            {
                moved.Add(new(f.Path, "", $"a protected file that may have changed. COULD NOT RESTORE IT ({Out.Describe(e)}): don't run git here until you've dealt with it"));
            }
        }
        if (moved.Skip(before).Any(q => q.To.Length == 0)) return false;
        File.Move(session.ProtectedManifest, session.RestoredManifest, overwrite: true);
        Directory.Delete(session.ProtectedDir, recursive: true);
        return true;
    }

    private static void RestoreOne(SessionDir session, ProtectedFile f, string dir, string? commonGitDir, List<Quarantined> moved, List<RestoredFile> restored, List<ModeChange> opened, List<string> blocked)
    {
        var original = Native.ReadProtected(Path.Combine(session.ProtectedDir, f.Copy))?.Bytes
            ?? throw new PenException("pen's copy of it is damaged");
        var root = RepoGuard.IsInside(dir, f.Path) ? dir : commonGitDir;
        if (!CheckParents(f, root, moved, restored, blocked)) return;

        RegularFile? current;
        try { current = Native.ReadProtected(f.Path); }
        catch (FileNotFoundException)
        {
            OpenParent(f.Path, opened, blocked);
            WriteNew(f.Path, original, f.Mode);
            restored.Add(new(f.Path, "it was deleted during the session, and has been recreated"));
            return;
        }
        catch (UnauthorizedAccessException) { current = null; }

        if (current != null && current.Bytes.AsSpan().SequenceEqual(original))
        {
            if (current.Mode != f.Mode) RestoreMode(f, current.Mode, restored);
            return;
        }
        Microsoft.Win32.SafeHandles.SafeFileHandle? writable = null;
        try
        {
            if (current != null)
            {
                // A file made read-only or unwritable can't be restored in place.
                try { writable = Native.OpenProtected(f.Path, write: true); }
                catch (UnauthorizedAccessException) { }
            }
            if (writable == null)
            {
                OpenParent(f.Path, opened, blocked);
                if (GitCheck.Move(f.Path, "it's no longer the regular file it was at the start of the session (a symlink, a directory, or too big or unreadable to compare), so it was moved aside and the original put back", moved))
                    WriteNew(f.Path, original, f.Mode);
                return;
            }
            RandomAccess.Write(writable, original, 0);
            RandomAccess.SetLength(writable, original.Length);
            File.SetUnixFileMode(writable, f.Mode);
        }
        finally { writable?.Dispose(); }

        OpenParent(f.Path, opened, blocked);
        var to = GitCheck.FreeName(f.Path);
        try
        {
            WriteNew(to, current!.Bytes, File600);
            moved.Add(new(f.Path, to, "it changed during the session, so the original was written back and the changed version kept here (git config on this machine during a session does this too)"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            moved.Add(new(f.Path, "", $"it changed during the session. The original was written back, but the changed version couldn't be kept ({Out.Describe(e)})"));
        }
    }

    /// <summary>Lets this user make and rename entries beside the file, if the agent took that away (chmod 555 .git). The mode change is reported.</summary>
    private static void OpenParent(string path, List<ModeChange> opened, List<string> blocked)
    {
        var parent = Path.GetDirectoryName(path)!;
        if (Native.CanWriteIn(parent)) return;
        GitCheck.TryOpen(parent, opened, write: true);
        if (!Native.CanWriteIn(parent)) blocked.Add(parent);
    }

    /// <summary>
    /// Whether the file's directory is still the real one it was. Directories
    /// below the sandbox's mount (<paramref name="root"/>) can be moved and
    /// replaced from inside it, so a symlink among them is renamed aside: writing
    /// through it would land wherever the agent pointed it. A directory that's
    /// gone or is now a file is left so (a worktree removed during the session,
    /// say) and reported: git reads nothing from there, and recreating it would
    /// invent a half-made entry. One that can't be entered, <paramref name="root"/>
    /// included, is neither: what's in it may have changed, so it's a failure,
    /// and <paramref name="blocked"/> names it.
    /// </summary>
    private static bool CheckParents(ProtectedFile f, string? root, List<Quarantined> moved, List<RestoredFile> restored, List<string> blocked)
    {
        var parent = Path.GetDirectoryName(f.Path)!;
        var parents = new Stack<string>();
        parents.Push(parent);
        if (root != null && RepoGuard.IsInside(root, parent))
            for (var p = parent; p != root; parents.Push(p)) p = Path.GetDirectoryName(p)!;
        foreach (var p in parents)
        {
            if (root != null && p != root && RepoGuard.IsLink(p))
            {
                GitCheck.Move(p, $"a symlink in place of a directory holding the protected file {Out.Field(f.Path)}: it points git at a file somewhere else", moved);
                return false;
            }
            if (File.Exists(p) || Native.IsMissing(p)) return DirectoryGone(f, p, restored);
            if (!Native.CanEnter(p))
            {
                blocked.Add(TopmostShut(p));
                throw new UnauthorizedAccessException($"Access to the path '{p}' is denied.");
            }
        }
        if (!string.Equals(Native.RealPath(parent), parent, StringComparison.OrdinalIgnoreCase))
            throw new PenException("its directory now leads somewhere else");
        return true;
    }

    /// <summary>The first directory on the way to <paramref name="path"/> this user can't enter: changing that one is what lets Pen get further.</summary>
    private static string TopmostShut(string path)
    {
        var chain = new Stack<string>();
        for (var p = path; p != null; p = Path.GetDirectoryName(p)) chain.Push(p);
        return chain.FirstOrDefault(p => !Native.CanEnter(p)) ?? path;
    }

    private static bool DirectoryGone(ProtectedFile f, string directory, List<RestoredFile> restored)
    {
        restored.Add(new(f.Path, $"not put back: {Out.Field(directory)} {(RepoGuard.Exists(directory) ? "was replaced by a file" : "was removed")} during the session"));
        return false;
    }

    private static void RestoreMode(ProtectedFile f, UnixFileMode was, List<RestoredFile> restored)
    {
        using var handle = Native.OpenProtected(f.Path, write: false) ?? throw new PenException("it can't be opened");
        File.SetUnixFileMode(handle, f.Mode);
        restored.Add(new(f.Path, $"its mode was changed during the session to {Convert.ToString((int)was, 8)}, and is back to {Convert.ToString((int)f.Mode, 8)}"));
    }

    /// <summary>
    /// What running sessions have saved: their files by what each path resolves to
    /// now, oldest session first; the files any of them adopted; each one's read-only
    /// directories; and whether one has no record to go by. A session that has put its
    /// files back is still here, as its agent may have planted what its check hasn't
    /// reached yet, but its copies are gone, so the next session's are what count.
    /// </summary>
    private sealed record OlderSessions(Dictionary<string, List<(SessionDir Session, ProtectedFile File)>> Files, HashSet<string> Adopted, List<List<string>> ReadOnlyDirs, bool Unrecorded);

    private static OlderSessions Older(IReadOnlyList<SessionDir> running)
    {
        var files = new Dictionary<string, List<(SessionDir, ProtectedFile)>>(StringComparer.Ordinal);
        var adopted = new HashSet<string>(StringComparer.Ordinal);
        var readOnly = new List<List<string>>();
        bool unrecorded = false;
        foreach (var s in running.OrderBy(s => s.ReadStarted() ?? DateTime.MaxValue))
        {
            var manifest = ReadRecord(s);
            if (manifest == null)
            {
                // Its record goes after its compose.yaml, so one that's still open with
                // no record is from an older Pen or damaged: its agent could write.
                unrecorded |= s.IsOpen;
                continue;
            }
            readOnly.Add(manifest.ReadOnlyDirs);
            foreach (var f in manifest.Files)
            {
                var real = Native.RealPath(f.Path) ?? f.Path;
                if (!files.TryGetValue(real, out var saved)) files[real] = saved = [];
                saved.Add((s, f));
                if (f.Adopted) adopted.Add(real);
            }
        }
        return new(files, adopted, readOnly, unrecorded);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is a config.worktree a running session's agent
    /// could have made: a session adopted it, or none saved it and one of them could
    /// write there. Being read-only in one says nothing about the others.
    /// </summary>
    private static bool IsAdopted(string path, OlderSessions older)
    {
        if (!string.Equals(Path.GetFileName(path), "config.worktree", StringComparison.OrdinalIgnoreCase)) return false;
        if (older.Adopted.Contains(path)) return true;
        if (older.Files.ContainsKey(path)) return false;
        return older.Unrecorded || older.ReadOnlyDirs.Any(dirs => !dirs.Any(d => RepoGuard.IsInside(d, path)));
    }

    private static RegularFile Baseline(string dir, string path, Dictionary<string, List<(SessionDir Session, ProtectedFile File)>> saved)
    {
        foreach (var older in saved.GetValueOrDefault(path) ?? [])
        {
            try
            {
                var copy = Native.ReadProtected(Path.Combine(older.Session.ProtectedDir, older.File.Copy));
                if (copy != null) return new(copy.Bytes, older.File.Mode);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // that session has put its files back
        }
        try
        {
            return Native.ReadProtected(path)
                ?? throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't save a copy of {RepoGuard.Rel(dir, path)}: it isn't a regular file of at most 1 MB, so it couldn't be restored at the end of the session. Inspect it and remove it, then rerun");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't read {RepoGuard.Rel(dir, path)} to save a copy of it: fix its permissions, then rerun");
        }
    }

    /// <summary>The path as it is on disk, which may differ only in case: anything else means a symlink was swapped in since it was checked.</summary>
    private static string RequireRealPath(string dir, string path)
    {
        var real = Native.RealPath(path);
        if (real == null || !string.Equals(real, path, StringComparison.OrdinalIgnoreCase))
            throw new PenException($"refusing to run in {Out.Field(dir)}: {RepoGuard.Rel(dir, path)} isn't where pen expected it (a symlink, or it's gone): inspect it, then rerun");
        return real;
    }

    /// <summary>Paths reach here from the file system, as valid UTF-16. One that isn't couldn't be written and read back the same.</summary>
    private static string Encodable(string path)
    {
        try { _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetByteCount(path); }
        catch (ArgumentException) { throw new PenException($"pen can't record the path {Out.Field(path)}: it isn't valid text"); }
        return path;
    }

    /// <summary>A new file with exactly this content and mode. Fails if anything is there, a dangling symlink included.</summary>
    private static void WriteNew(string path, byte[] bytes, UnixFileMode mode)
    {
        using var fs = new FileStream(path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = File600 });
        fs.Write(bytes);
        fs.Flush();
        File.SetUnixFileMode(fs.SafeFileHandle, mode);
    }
}
