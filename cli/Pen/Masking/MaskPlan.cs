using Pen.Platform;

namespace Pen.Masking;

/// <summary>What a session mounts over the project to hide and replace files, and what to warn about.</summary>
/// <param name="HiddenFiles">The regular files that get an empty read-only file mounted over them.</param>
/// <param name="HiddenDirs">The directories that get an empty read-only directory mounted over them.</param>
/// <param name="Replacements">The files that get a read-only copy of another file mounted over them.</param>
/// <param name="Warnings">
/// What the user should know: what couldn't be looked in or hidden, extra hard links. Each is safe to print.
/// </param>
/// <remarks>
/// Every path is a real path in the project, with no symlink in it, and none is inside another: a file or
/// directory inside a hidden directory isn't listed, and a replaced file isn't also hidden. The hidden files and
/// directories are sorted by <see cref="MaskedPath.RelativePath"/>; the replacements are in [replace]'s order.
/// </remarks>
internal sealed record MaskPlan(
    IReadOnlyList<MaskedPath> HiddenFiles,
    IReadOnlyList<MaskedPath> HiddenDirs,
    IReadOnlyList<Replacement> Replacements,
    IReadOnlyList<string> Warnings)
{
    /// <summary>A plan that hides and replaces nothing.</summary>
    public static MaskPlan Empty { get; } = new([], [], [], []);

    /// <summary>Gets how many mounts the plan needs: one per hidden file, hidden directory and replacement.</summary>
    public int MountCount => HiddenFiles.Count + HiddenDirs.Count + Replacements.Count;

    /// <summary>Gets whether the plan hides and replaces nothing.</summary>
    public bool IsEmpty => MountCount == 0;

    /// <summary>Refuses to start if a path the plan mounts over isn't what it was when the plan was made.</summary>
    /// <exception cref="PenException">
    /// A hidden file, hidden directory or replaced file is gone, is of another type, is another file (by device and
    /// inode), or isn't its own real path any more (a directory on its way is a symlink now), naming the first.
    /// </exception>
    /// <remarks>
    /// The plan is made before the image, which can take minutes, and the mounts are made only when the session
    /// starts. Docker creates a missing mount point, and its missing parents, in the project on the host, and a
    /// mount through a symlink lands wherever that leads; so each path is looked at again, without following a
    /// symlink, just before the mounts are written.
    /// </remarks>
    public void ThrowIfChanged()
    {
        foreach (var file in HiddenFiles)
            ThrowIfChanged(file, "hides", isDirectory: false);
        foreach (var dir in HiddenDirs)
            ThrowIfChanged(dir, "hides", isDirectory: true);
        foreach (var replacement in Replacements)
            ThrowIfChanged(replacement.Target, "replaces", isDirectory: false);
    }

    /// <summary>Refuses to start if <paramref name="masked"/> isn't what it was when the plan was made.</summary>
    /// <param name="masked">The path.</param>
    /// <param name="verb">"hides" or "replaces", for the message.</param>
    /// <param name="isDirectory">Whether it was a directory; otherwise it was a regular file.</param>
    private static void ThrowIfChanged(MaskedPath masked, string verb, bool isDirectory)
    {
        var kind = isDirectory ? "directory" : "file";
        var status = Native.Stat(masked.Path, follow: false);
        string change;
        if (status is not { } now)
            change = "is gone";
        else if (now.IsSymlink)
            change = "is a symlink now";
        else if (isDirectory ? !now.IsDirectory : !now.IsRegularFile)
            change = $"is no longer a {kind}";
        else if (now.Device != masked.Device || now.Inode != masked.Inode)
            change = $"is another {kind} now";
        else if (Native.RealPath(masked.Path) is not { } real)
            change = "can't be looked at now";
        else if (real == masked.Path)
            return;
        else if (string.Equals(real, masked.Path, StringComparison.OrdinalIgnoreCase))
            change = "is spelled differently now";
        else
            change = "is reached by another path now";
        throw new PenException($"not starting: {Out.Field(masked.RelativePath)}, which this session {verb}, " +
            $"{change}: the project changed while the session was being prepared, so run pen again");
    }
}

/// <summary>A file or directory in the project that a mount covers.</summary>
/// <param name="Path">
/// Its absolute host path, also its path in the container, since the project is mounted at the same path.
/// </param>
/// <param name="RelativePath">Its path in the project, /-separated, for messages.</param>
/// <param name="Device">The device it was on when the plan was made.</param>
/// <param name="Inode">Its inode number on that device then.</param>
/// <remarks>
/// <see cref="RelativePath"/> is the name on disk as it is, which the agent can choose: pass it through
/// <see cref="Out.Field"/> before printing it.
/// </remarks>
internal sealed record MaskedPath(string Path, string RelativePath, ulong Device, ulong Inode);

/// <summary>A file in the project that the agent sees with another file's contents.</summary>
/// <param name="Target">The file covered.</param>
/// <param name="Source">The source as written in [replace], for messages.</param>
/// <param name="Bytes">The source's contents, as read when the plan was made.</param>
/// <param name="Mode">The source's read and execute bits: no write, setuid, setgid or sticky bit.</param>
internal sealed record Replacement(MaskedPath Target, string Source, byte[] Bytes, UnixFileMode Mode);
