using Microsoft.Win32.SafeHandles;
using Pen.Platform;
using Pen.Sessions;

namespace Pen.GitProtection;

/// <summary>
/// Puts a session's protected files back at its end from the copies <see cref="ProtectedFiles.Save"/> took, keeping
/// what it renamed, put back and opened as it goes.
/// </summary>
/// <remarks>
/// <para>
/// A changed regular file is written back in place, since another session's single-file mount pins the inode and a
/// rename would leave that mount protecting a stale file; anything else there is renamed aside and the file
/// recreated. Identical files aren't touched.
/// </para>
/// <para>
/// Adopted files aren't looked at: nothing vouches for them, so the end check quarantines whatever is there
/// (<see cref="ProtectedManifest.Vouches"/>).
/// </para>
/// <para>
/// Like the rest of the check, it reads files directly and never runs git. Checks made just before a write can still
/// race with another session's agent; the last component is never followed regardless.
/// </para>
/// </remarks>
/// <param name="session">The session that's ending.</param>
/// <param name="dir">The project dir.</param>
/// <param name="commonGitDir">The git dir the session started with, or null for dir/.git.</param>
/// <param name="changes">
/// Where it records, as it goes: in Moved what it renamed, or couldn't rename or restore; in Restored the files
/// that changed with nothing to set aside (missing, directory gone, or only mode); in Opened the directories it
/// added u+rwx to so it could write in them, and left so; in Blocked the directories whose modes kept a file from
/// being put back, for advice on what to fix.
/// </param>
internal sealed class ProtectedFileRestore(SessionDir session, string dir, string? commonGitDir, CheckChanges changes)
{
    private readonly SessionDir _session = session;
    private readonly string _dir = dir;
    private readonly string? _commonGitDir = commonGitDir;
    private readonly CheckChanges _changes = changes;

    /// <summary>Puts each file in the session's manifest back. Call it once per instance.</summary>
    /// <returns>Whether that's all done, or there was no manifest.</returns>
    /// <remarks>
    /// <para>
    /// Only once all are done are the copies removed, so a retried end can't write them over later edits. The
    /// manifest stays, as <see cref="SessionDir.RestoredManifest"/>: a retried end and a session starting now both
    /// need to know what this one vouched for.
    /// </para>
    /// <para>
    /// If not, an entry it added to <see cref="CheckChanges.Moved"/> with no destination fails the check, and the
    /// caller must keep the session's files: they're the only good copies, and a new session would otherwise take
    /// what's on disk as its baseline. Whatever goes wrong, the original goes back before anything that could stop
    /// it, such as keeping the changed version.
    /// </para>
    /// </remarks>
    public bool Run()
    {
        var firstMoved = _changes.Moved.Count;
        ProtectedManifest? manifest;
        try
        {
            manifest = ProtectedFiles.Read(_session);
        }
        catch (Exception exception) when (ProtectedFiles.IsDamage(exception))
        {
            _changes.Moved.Add(new(_session.ProtectedManifest, "", "pen's list of protected files couldn't be " +
                "read, so they weren't compared. COULD NOT RESTORE THEM: " +
                "don't run git here until you've checked them"));
            return false;
        }
        if (manifest is null)
            return true;
        foreach (var file in manifest.Files.Where(file => !file.Adopted))
            RestoreOrRecord(file);
        if (_changes.Moved.Skip(firstMoved).Any(quarantined => quarantined.Failed))
            return false;
        File.Move(_session.ProtectedManifest, _session.RestoredManifest, overwrite: true);
        Directory.Delete(_session.ProtectedDir, recursive: true);
        return true;
    }

    /// <summary>Puts one file back, or records that it couldn't.</summary>
    private void RestoreOrRecord(ProtectedFile file)
    {
        try
        {
            RestoreOne(file);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or PenException)
        {
            _changes.Moved.Add(new(file.Path, "", "a protected file that may have changed. COULD NOT " +
                $"RESTORE IT ({Out.Describe(exception)}): don't run git here until you've dealt with it"));
        }
    }

    /// <summary>Puts one file back if it's missing, changed or has another mode.</summary>
    private void RestoreOne(ProtectedFile file)
    {
        var original = Native.ReadProtected(Path.Combine(_session.ProtectedDir, file.Copy))?.Bytes
            ?? throw new PenException("pen's copy of it is damaged");
        var root = RepoGuard.IsInside(_dir, file.Path) ? _dir : _commonGitDir;
        if (!CheckParents(file, root))
            return;

        RegularFile? current;
        try
        {
            current = Native.ReadProtected(file.Path);
        }
        catch (FileNotFoundException)
        {
            Recreate(file, original);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            current = null;
        }

        if (current is not null && current.Bytes.AsSpan().SequenceEqual(original))
        {
            if (current.Mode != file.Mode)
                RestoreMode(file, current.Mode);
            return;
        }
        if (!TryWriteInPlace(file, original, current))
        {
            ReplaceAside(file, original);
            return;
        }
        KeepChangedVersion(file, current!.Bytes);
    }

    /// <summary>Recreates a file that was deleted during the session.</summary>
    private void Recreate(ProtectedFile file, byte[] original)
    {
        OpenParent(file.Path);
        ProtectedFiles.WriteNew(file.Path, original, file.Mode);
        _changes.Restored.Add(new(file.Path, "it was deleted during the session, and has been recreated"));
    }

    /// <summary>Writes the original back into the regular file that's there now, with its mode.</summary>
    /// <param name="file">The saved file.</param>
    /// <param name="original">Its contents at the start.</param>
    /// <param name="current">What's there now, or null if it couldn't be read.</param>
    /// <returns>Whether it was written back; false if it isn't a regular file it can open for writing.</returns>
    private static bool TryWriteInPlace(ProtectedFile file, byte[] original, RegularFile? current)
    {
        if (current is null)
            return false;
        SafeFileHandle? writable;
        // A file made read-only or unwritable can't be restored in place.
        try
        {
            writable = Native.OpenProtected(file.Path, write: true);
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        if (writable is null)
            return false;
        using (writable)
        {
            RandomAccess.Write(writable, original, 0);
            RandomAccess.SetLength(writable, original.Length);
            File.SetUnixFileMode(writable, file.Mode);
        }
        return true;
    }

    /// <summary>Renames what's there aside and recreates the file, if it can be moved.</summary>
    private void ReplaceAside(ProtectedFile file, byte[] original)
    {
        OpenParent(file.Path);
        var quarantined = GitCheck.Move(file.Path, "it's no longer the regular file it was at the " +
            "start of the session (a symlink, a directory, or too big or unreadable " +
            "to compare), so it was moved aside and the original put back", breakLayout: false);
        _changes.Moved.Add(quarantined);
        if (!quarantined.Failed)
            ProtectedFiles.WriteNew(file.Path, original, file.Mode);
    }

    /// <summary>Keeps the changed version of a file whose original was written back, beside it.</summary>
    private void KeepChangedVersion(ProtectedFile file, byte[] changed)
    {
        OpenParent(file.Path);
        var to = GitCheck.FreeName(file.Path);
        try
        {
            ProtectedFiles.WriteNew(to, changed, ProtectedFiles.File600);
            _changes.Moved.Add(new(file.Path, to, "it changed during the session, so the original was written back " +
                "and the changed version kept here (git config on this machine during a session does this too)"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _changes.Moved.Add(new(file.Path, "", "it changed during the session. The original was " +
                $"written back, but the changed version couldn't be kept ({Out.Describe(exception)})"));
        }
    }

    /// <summary>
    /// Lets this user make and rename entries beside the file, if the agent took that away (chmod 555 .git).
    /// </summary>
    /// <remarks>The mode change is reported; a directory it still can't write in is named in Blocked.</remarks>
    private void OpenParent(string path)
    {
        var parent = Path.GetDirectoryName(path)!;
        if (Native.CanWriteIn(parent))
            return;
        _changes.TryOpenRecording(parent, write: true);
        if (!Native.CanWriteIn(parent))
            _changes.Blocked.Add(parent);
    }

    /// <summary>Checks that the file's directory is still the real one it was.</summary>
    /// <param name="file">The saved file.</param>
    /// <param name="root">The sandbox's mount the file is under, or null if it's under neither.</param>
    /// <returns>Whether to go on and restore it; false if a directory on the way was moved aside or is gone.</returns>
    /// <remarks>
    /// Directories below the sandbox's mount can be moved and replaced from inside it, so a symlink among them is
    /// renamed aside: writing through it would land wherever the agent pointed it. A directory that's gone or is now
    /// a file is left so (a worktree removed during the session, say) and reported: git reads nothing from there, and
    /// recreating it would invent a half-made entry. One that can't be entered, <paramref name="root"/> included, is
    /// neither: what's in it may have changed, so it's a failure (it throws), and Blocked names it.
    /// </remarks>
    private bool CheckParents(ProtectedFile file, string? root)
    {
        var parent = Path.GetDirectoryName(file.Path)!;
        foreach (var directory in ListDirectoriesFromRoot(parent, root))
        {
            if (root is not null && directory != root && RepoGuard.IsLink(directory))
            {
                _changes.Moved.Add(GitCheck.Move(directory, "a symlink in place of a directory holding the protected " +
                    $"file {Out.Field(file.Path)}: it points git at a file somewhere else", breakLayout: false));
                return false;
            }
            if (File.Exists(directory) || Native.IsMissing(directory))
            {
                RecordDirectoryGone(file, directory);
                return false;
            }
            if (!Native.CanEnter(directory))
            {
                _changes.Blocked.Add(FindTopmostShut(directory));
                throw new UnauthorizedAccessException($"Access to the path '{directory}' is denied.");
            }
        }
        if (!string.Equals(Native.RealPath(parent), parent, StringComparison.OrdinalIgnoreCase))
            throw new PenException("its directory now leads somewhere else");
        return true;
    }

    /// <summary>Lists the directories from <paramref name="root"/> down to <paramref name="parent"/>.</summary>
    /// <returns>Root first and parent last, or only the parent if it isn't inside root.</returns>
    private static Stack<string> ListDirectoriesFromRoot(string parent, string? root)
    {
        var directories = new Stack<string>();
        directories.Push(parent);
        if (root is null || !RepoGuard.IsInside(root, parent))
            return directories;
        var directory = parent;
        while (directory != root)
        {
            directory = Path.GetDirectoryName(directory)!;
            directories.Push(directory);
        }
        return directories;
    }

    /// <summary>Finds the first directory on the way to <paramref name="path"/> this user can't enter.</summary>
    /// <returns>That directory, or the path itself if every one can be entered.</returns>
    /// <remarks>Changing that one is what lets Pen get further.</remarks>
    private static string FindTopmostShut(string path)
    {
        var chain = new Stack<string>();
        for (var directory = path; directory is not null; directory = Path.GetDirectoryName(directory))
            chain.Push(directory);
        return chain.FirstOrDefault(directory => !Native.CanEnter(directory)) ?? path;
    }

    /// <summary>Records that a file wasn't put back because a directory holding it went.</summary>
    private void RecordDirectoryGone(ProtectedFile file, string directory)
    {
        var what = RepoGuard.Exists(directory) ? "was replaced by a file" : "was removed";
        _changes.Restored.Add(new(file.Path, $"not put back: {Out.Field(directory)} {what} during the session"));
    }

    /// <summary>Puts back the mode of a file whose contents didn't change.</summary>
    private void RestoreMode(ProtectedFile file, UnixFileMode was)
    {
        using var handle = Native.OpenProtected(file.Path, write: false)
            ?? throw new PenException("it can't be opened");
        File.SetUnixFileMode(handle, file.Mode);
        _changes.Restored.Add(new(file.Path, "its mode was changed during the session to " +
            $"{GitCheck.Octal(was)}, and is back to {GitCheck.Octal(file.Mode)}"));
    }
}
