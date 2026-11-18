using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32.SafeHandles;
using Pen.Platform;
using Pen.Sessions;

namespace Pen.GitProtection;

/// <summary>
/// Puts a session's protected files back at its end from the copies <see cref="ProtectedFiles.Save"/> took, keeping
/// what it renamed, put back and made writable as it goes.
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
/// <para>
/// The common git dir is the one the session started with, or null for dir/.git. It records into changes as it
/// goes: in Moved what it renamed, or couldn't rename or restore; in Restored the files that changed with nothing to
/// set aside (missing, directory gone, or only mode); in ModeChanges the directories it added u+rwx to so it could
/// write in them, and left so; in Blocked the directories whose modes kept a file from being put back, for advice on
/// what to fix.
/// </para>
/// </remarks>
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
    /// If not, a <see cref="Quarantined.Failed"/> it added to <see cref="CheckChanges.Moved"/> fails the check, and the
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
            _changes.Add(new Quarantined.Failed(_session.ProtectedManifest, "pen's list of protected files " +
                "couldn't be read, so they weren't compared. COULD NOT RESTORE THEM: " +
                "don't run git here until you've checked them"));
            return false;
        }
        if (manifest is null)
            return true;
        foreach (var file in manifest.Files.Where(file => !file.Adopted))
            RestoreOrRecord(file);
        if (_changes.Moved.Skip(firstMoved).Any(quarantined => quarantined is Quarantined.Failed))
            return false;
        File.Move(_session.ProtectedManifest, _session.RestoredManifest, overwrite: true);
        Directory.Delete(_session.ProtectedDir, recursive: true);
        return true;
    }

    /// <summary>Puts one file back, or records that it couldn't.</summary>
    private void RestoreOrRecord(ProtectedFile file)
    {
        string? failure;
        try
        {
            failure = RestoreOne(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = Out.Describe(exception);
        }
        if (failure is not null)
        {
            _changes.Add(new Quarantined.Failed(file.Path, "a protected file that may have changed. COULD NOT " +
                $"RESTORE IT ({failure}): don't run git here until you've dealt with it"));
        }
    }

    /// <summary>Puts one file back if it's missing, changed or has another mode.</summary>
    /// <returns>
    /// Null once it's back, unchanged, or left and recorded; otherwise why it couldn't be put back.
    /// </returns>
    /// <exception cref="IOException">A file operation failed.</exception>
    /// <exception cref="UnauthorizedAccessException">A file operation was denied.</exception>
    private string? RestoreOne(ProtectedFile file)
    {
        if (Native.ReadProtected(Path.Combine(_session.ProtectedDir, file.Copy))?.Bytes is not { } original)
            return "pen's copy of it is damaged";
        var root = PathChecks.IsInside(_dir, file.Path) ? _dir : _commonGitDir;
        if (!CheckParents(file, root, out var failure))
            return failure;

        RegularFile? current;
        try
        {
            current = Native.ReadProtected(file.Path);
        }
        catch (FileNotFoundException)
        {
            Recreate(file, original);
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            current = null;
        }

        if (current is not null && current.Bytes.AsSpan().SequenceEqual(original))
            return current.Mode != file.Mode ? RestoreMode(file, current.Mode) : null;
        if (!TryWriteInPlace(file, original, current))
        {
            ReplaceAside(file, original);
            return null;
        }
        KeepChangedVersion(file, current!.Bytes);
        return null;
    }

    /// <summary>Recreates a file that was deleted during the session.</summary>
    private void Recreate(ProtectedFile file, byte[] original)
    {
        MakeParentWritable(file.Path);
        ProtectedFiles.WriteNew(file.Path, original, file.Mode);
        _changes.Add(new RestoredFile(file.Path, "it was deleted during the session, and has been recreated"));
    }

    /// <summary>
    /// Writes the original contents back into the regular file that's there now (null if it couldn't be read), with
    /// its mode.
    /// </summary>
    /// <returns>Whether it was written back; false if it isn't a regular file it can open for writing.</returns>
    [SuppressMessage("Reliability", "CA2000", Justification = "The using block disposes it once it isn't null")]
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
        MakeParentWritable(file.Path);
        var quarantined = GitQuarantine.Move(file.Path, "it's no longer the regular file it was at the " +
            "start of the session (a symlink, a directory, or too big or unreadable " +
            "to compare), so it was moved aside and the original put back", breakLayout: false);
        _changes.Add(quarantined);
        if (quarantined is not Quarantined.Failed)
            ProtectedFiles.WriteNew(file.Path, original, file.Mode);
    }

    /// <summary>Keeps the changed version of a file whose original was written back, beside it.</summary>
    private void KeepChangedVersion(ProtectedFile file, byte[] changed)
    {
        MakeParentWritable(file.Path);
        var to = GitQuarantine.FreeName(file.Path);
        try
        {
            ProtectedFiles.WriteNew(to, changed, ProtectedFiles.File600);
            _changes.Add(new Quarantined.Moved(file.Path, to, "it changed during the session, so the original " +
                "was written back and the changed version kept here (git config on this machine during a session " +
                "does this too)"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _changes.Add(new Quarantined.Failed(file.Path, "it changed during the session. The original was " +
                $"written back, but the changed version couldn't be kept ({Out.Describe(exception)})"));
        }
    }

    /// <summary>
    /// Lets this user make and rename entries beside the file, if the agent took that away (chmod 555 .git).
    /// </summary>
    /// <remarks>The mode change is reported; a directory it still can't write in is named in Blocked.</remarks>
    private void MakeParentWritable(string path)
    {
        var parent = Paths.GetParent(path);
        if (Native.CanWriteIn(parent))
            return;
        _changes.TryMakeListableRecording(parent, write: true);
        if (!Native.CanWriteIn(parent))
            _changes.AddBlocked(parent);
    }

    /// <summary>
    /// Checks that the file's directory is still the real one it was, below <paramref name="root"/>, the sandbox's
    /// mount the file is under (null if it's under neither).
    /// </summary>
    /// <returns>
    /// Whether to go on and restore it. When it's false, <paramref name="failure"/> says why the file can't be put
    /// back, or is null when a directory was moved aside or is gone, which is recorded.
    /// </returns>
    /// <remarks>
    /// Directories below the sandbox's mount can be moved and replaced from inside it, so a symlink among them is
    /// renamed aside: writing through it would land wherever the agent pointed it. A directory that's gone or is now
    /// a file is left so (a worktree removed during the session, say) and reported: git reads nothing from there, and
    /// recreating it would invent a half-made entry. One that can't be entered, <paramref name="root"/> included, is
    /// neither: what's in it may have changed, so it's a failure, and Blocked names it. So is a directory that leads
    /// somewhere else now.
    /// </remarks>
    private bool CheckParents(ProtectedFile file, string? root, out string? failure)
    {
        failure = null;
        var parent = Paths.GetParent(file.Path);
        foreach (var directory in ListDirectoriesFromRoot(parent, root))
        {
            if (root is not null && directory != root && PathChecks.IsLink(directory))
            {
                _changes.Add(GitQuarantine.Move(directory,
                    "a symlink in place of a directory holding the protected " +
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
                _changes.AddBlocked(FindTopmostShut(directory));
                // Worded exactly as Out.Describe words a file operation the OS denies, as for one in the restore
                // that throws.
                failure = Out.DescribePermissionDenied(
                    new UnauthorizedAccessException($"Access to the path '{directory}' is denied."));
                return false;
            }
        }
        if (!string.Equals(Native.RealPath(parent), parent, StringComparison.OrdinalIgnoreCase))
        {
            failure = "its directory now leads somewhere else";
            return false;
        }
        return true;
    }

    /// <summary>Lists the directories from <paramref name="root"/> down to <paramref name="parent"/>.</summary>
    /// <returns>Root first and parent last, or only the parent if it isn't inside root.</returns>
    private static Stack<string> ListDirectoriesFromRoot(string parent, string? root)
    {
        var directories = new Stack<string>();
        directories.Push(parent);
        if (root is null || !PathChecks.IsInside(root, parent))
            return directories;
        var directory = parent;
        while (directory != root)
        {
            directory = Paths.GetParent(directory);
            directories.Push(directory);
        }
        return directories;
    }

    /// <summary>Finds the first directory on the way to <paramref name="path"/> this user can't enter.</summary>
    /// <returns>That directory, or the path itself if every one can be entered.</returns>
    /// <remarks>Changing that one is what lets Pen get further.</remarks>
    private static string FindTopmostShut(string path)
    {
        var chain = new Stack<string>(Paths.EnumerateAncestors(path).Prepend(path));
        return chain.FirstOrDefault(directory => !Native.CanEnter(directory)) ?? path;
    }

    /// <summary>Records that a file wasn't put back because a directory holding it went.</summary>
    private void RecordDirectoryGone(ProtectedFile file, string directory)
    {
        var what = PathChecks.ExistsOrIsLink(directory) ? "was replaced by a file" : "was removed";
        _changes.Add(new RestoredFile(file.Path, $"not put back: {Out.Field(directory)} {what} during the session"));
    }

    /// <summary>Puts back the mode of a file whose contents didn't change.</summary>
    /// <returns>Null once it's back; otherwise why it couldn't be.</returns>
    private string? RestoreMode(ProtectedFile file, UnixFileMode was)
    {
        using var handle = Native.OpenProtected(file.Path, write: false);
        if (handle is null)
            return "it can't be opened";
        File.SetUnixFileMode(handle, file.Mode);
        _changes.Add(new RestoredFile(file.Path, "its mode was changed during the session to " +
            $"{FileModes.Octal(was)}, and is back to {FileModes.Octal(file.Mode)}"));
        return null;
    }
}
