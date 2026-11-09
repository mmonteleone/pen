using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pen;

/// <summary>
/// One saved file: its real path, its copy's name in the session's protected dir,
/// and its mode at the start. Adopted: a config.worktree first seen while another
/// session's agent could have planted it, so nothing vouches for the copy: it's
/// never written back, only moved aside if it changes.
/// </summary>
internal sealed record ProtectedFile(string Path, string Copy, UnixFileMode Mode, bool Adopted = false);

/// <summary>
/// What a session saved. Files: every single-file mount that must not change
/// (<see cref="Mount.Verify"/>). ReadOnlyDirs: the directories mounted read-only
/// as a whole, whose contents the sandbox can't change or add to.
/// </summary>
internal sealed record ProtectedManifest(List<ProtectedFile> Files, List<string> ReadOnlyDirs);

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
    /// sessions already saved keeps their copy, the oldest's: its agent may have
    /// changed the file since, and this session's end would otherwise put that
    /// change back after that session's end had removed it. The same file can have
    /// another name by now (an agent's rename on a case-insensitive filesystem), so
    /// they're matched by what each name resolves to today. The manifest goes
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
    /// Returns whether that's all done, and only then removes the copies, so a
    /// retried end can't write them over later edits. If not, an entry in
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
        foreach (var f in manifest.Files)
        {
            try { RestoreOne(session, f, dir, commonGitDir, moved, restored, opened, blocked); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PenException)
            {
                moved.Add(new(f.Path, "", $"a protected file that may have changed. COULD NOT RESTORE IT ({Out.Describe(e)}): don't run git here until you've dealt with it"));
            }
        }
        if (moved.Skip(before).Any(q => q.To.Length == 0)) return false;
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
            if (f.Adopted) return;
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
        if (f.Adopted)
        {
            OpenParent(f.Path, opened, blocked);
            GitCheck.Move(f.Path, "a config.worktree first seen while another session was running, changed since then", moved);
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

    /// <summary>The first directory on the way to <paramref name="path"/> this user can't enter: changing that one is what lets pen get further.</summary>
    private static string TopmostShut(string path)
    {
        var chain = new Stack<string>();
        for (var p = path; p != null; p = Path.GetDirectoryName(p)) chain.Push(p);
        return chain.FirstOrDefault(p => !Native.CanEnter(p)) ?? path;
    }

    private static bool DirectoryGone(ProtectedFile f, string directory, List<RestoredFile> restored)
    {
        if (!f.Adopted)
            restored.Add(new(f.Path, $"not put back: {Out.Field(directory)} {(RepoGuard.Exists(directory) ? "was replaced by a file" : "was removed")} during the session"));
        return false;
    }

    private static void RestoreMode(ProtectedFile f, UnixFileMode was, List<RestoredFile> restored)
    {
        using var handle = Native.OpenProtected(f.Path, write: false) ?? throw new PenException("it can't be opened");
        File.SetUnixFileMode(handle, f.Mode);
        restored.Add(new(f.Path, $"its mode was changed during the session to {Convert.ToString((int)was, 8)}, and is back to {Convert.ToString((int)f.Mode, 8)}"));
    }

    /// <summary>What running sessions have saved: their files by what each path resolves to now, the oldest session's where they overlap.</summary>
    private sealed record OlderSessions(Dictionary<string, (SessionDir Session, ProtectedFile File)> Files, List<string> ReadOnlyDirs, bool Any);

    private static OlderSessions Older(IReadOnlyList<SessionDir> running)
    {
        var files = new Dictionary<string, (SessionDir, ProtectedFile)>(StringComparer.Ordinal);
        var readOnly = new List<string>();
        bool any = false;
        foreach (var s in running.OrderByDescending(s => s.ReadStarted() ?? DateTime.MaxValue))
        {
            ProtectedManifest? manifest;
            // Ended while we looked, so it has put back what it saved; a session from an older pen has none.
            try { manifest = Read(s); }
            catch (Exception e) when (IsDamage(e)) { continue; }
            if (manifest == null) continue;
            any = true;
            readOnly.AddRange(manifest.ReadOnlyDirs);
            foreach (var f in manifest.Files) files[Native.RealPath(f.Path) ?? f.Path] = (s, f);
        }
        return new(files, readOnly, any);
    }

    /// <summary>Whether <paramref name="path"/> is a config.worktree a running session's agent could have made: that session didn't save it, and could write there.</summary>
    private static bool IsAdopted(string path, OlderSessions older) =>
        older.Any && !older.Files.ContainsKey(path)
        && string.Equals(Path.GetFileName(path), "config.worktree", StringComparison.OrdinalIgnoreCase)
        && !older.ReadOnlyDirs.Any(d => RepoGuard.IsInside(d, path));

    private static RegularFile Baseline(string dir, string path, Dictionary<string, (SessionDir Session, ProtectedFile File)> saved)
    {
        if (saved.TryGetValue(path, out var older))
        {
            try
            {
                var copy = Native.ReadProtected(Path.Combine(older.Session.ProtectedDir, older.File.Copy));
                if (copy != null) return new(copy.Bytes, older.File.Mode);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // that session ended: see Saved
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
