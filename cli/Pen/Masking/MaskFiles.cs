using Pen.GitProtection;

namespace Pen.Masking;

/// <summary>Writes the files a session mounts over what it hides and replaces, and plans those mounts.</summary>
/// <remarks>
/// The files live in the session's own directory (700, deleted with the session's files), so file sharing reaches
/// them as it does squid.conf. None is writable, and every mount is read-only, so the agent can't change what it
/// sees there, and host files are never touched.
/// </remarks>
internal static class MaskFiles
{
    /// <summary>The empty file mounted over each hidden file.</summary>
    public const string EmptyFile = "empty";

    /// <summary>The empty directory mounted over each hidden directory.</summary>
    public const string EmptyDir = "empty-dir";

    /// <summary>The start of the name of each replacement's copy, followed by its number from 1.</summary>
    public const string ReplacementPrefix = "replace-";

    private const UnixFileMode Mode700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode Mode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode Mode444 = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    private const UnixFileMode Mode555 = Mode444 | UnixFileMode.UserExecute | UnixFileMode.GroupExecute
        | UnixFileMode.OtherExecute;

    /// <summary>
    /// Writes <paramref name="plan"/>'s files to <paramref name="dir"/>, the session's masks directory, which mustn't
    /// exist yet, and lists the mounts over the project.
    /// </summary>
    /// <returns>
    /// One read-only mount per hidden file, hidden directory and replacement, in that order; none, and nothing
    /// written, for an empty plan.
    /// </returns>
    /// <remarks>
    /// <paramref name="dir"/> gets <see cref="EmptyFile"/> (0444, no content), <see cref="EmptyDir"/> (0555) and a
    /// copy of each replacement's bytes, with its mode, named by <see cref="ReplacementPrefix"/> and its place in
    /// [replace].
    /// </remarks>
    public static IReadOnlyList<Mount> Write(string dir, MaskPlan plan)
    {
        if (plan.IsEmpty)
            return [];
        // Created new: what's in it is only ever what's written here. Only another of the user's processes could have
        // put something there (the session dir is 700), and CreateDirectory wouldn't fail on it, so it's thrown as a
        // failed file operation, as a failed write below is: the session's start stops and its files are deleted
        // (SessionFiles.WriteOrDelete).
        if (Directory.Exists(dir) || File.Exists(dir))
            throw new IOException($"{dir} already exists");
        Directory.CreateDirectory(dir, Mode700);

        var empty = Path.Combine(dir, EmptyFile);
        WriteNew(empty, [], Mode444);
        var emptyDir = Path.Combine(dir, EmptyDir);
        Directory.CreateDirectory(emptyDir, Mode555);
        // CreateDirectory's mode is narrowed by the umask; this one's mode is set as is.
        File.SetUnixFileMode(emptyDir, Mode555);

        var mounts = new List<Mount>(plan.MountCount);
        foreach (var file in plan.HiddenFiles)
            mounts.Add(new(file.Path, ReadOnly: true, Source: empty));
        foreach (var hidden in plan.HiddenDirs)
            mounts.Add(new(hidden.Path, ReadOnly: true, Source: emptyDir));
        var number = 0;
        foreach (var replacement in plan.Replacements)
        {
            number++;
            var copy = Path.Combine(dir, ReplacementPrefix + number);
            WriteNew(copy, replacement.Bytes, replacement.Mode);
            mounts.Add(new(replacement.Target.Path, ReadOnly: true, Source: copy));
        }
        return mounts;
    }

    /// <summary>Creates <paramref name="path"/> holding <paramref name="bytes"/>, then gives it its mode.</summary>
    /// <remarks>
    /// Fails if anything is there, a dangling symlink included. The mode is set through the open file, never by
    /// path, so a symlink swapped in after the file was created can't pass the mode on to what it leads to.
    /// </remarks>
    internal static void WriteNew(string path, byte[] bytes, UnixFileMode mode)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            UnixCreateMode = Mode600,
        });
        stream.Write(bytes);
        stream.Flush();
        File.SetUnixFileMode(stream.SafeFileHandle, mode);
    }
}
