using System.Security.Cryptography;

namespace Pen.Platform;

/// <summary>
/// Path checks shared across Pen: symlinks, existence, containment by name, real paths, the case-folding probe, and
/// paths formatted for messages.
/// </summary>
/// <remarks>
/// They read the file system directly and never run git, so git protection can use them on directories the agent can
/// write.
/// </remarks>
internal static class PathChecks
{
    /// <summary>Whether <paramref name="path"/> is a symlink, dangling or not.</summary>
    internal static bool IsLink(string path) => new FileInfo(path).LinkTarget is not null;

    /// <summary>Whether <paramref name="path"/> exists, including as a dangling symlink.</summary>
    internal static bool Exists(string path) => Path.Exists(path) || IsLink(path);

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="dir"/> or below it, comparing names exactly.
    /// </summary>
    internal static bool IsInside(string dir, string path) =>
        path == dir || path.StartsWith(dir.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>
    /// Formats <paramref name="path"/> for a message: relative to <paramref name="dir"/> when it's inside, and safe to
    /// print (<see cref="Out.Field"/>).
    /// </summary>
    internal static string FormatPath(string dir, string path)
    {
        if (path == dir)
            return Out.Field(".");
        if (IsInside(dir, path))
            return Out.Field(path[(dir.TrimEnd('/').Length + 1)..]);
        return Out.Field(path);
    }

    /// <summary>
    /// Resolves the real path of <paramref name="path"/>, or of its deepest existing ancestor plus the rest.
    /// </summary>
    /// <returns>The resolved path, or <paramref name="path"/> itself if no ancestor resolves.</returns>
    internal static string ResolveExisting(string path)
    {
        var rest = new Stack<string>();
        var current = path;
        while (true)
        {
            var real = Native.RealPath(current);
            if (real is not null)
                return rest.Aggregate(real, Path.Combine);
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
                return path;
            rest.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    /// <summary>Whether <paramref name="gitDir"/> may fold case, by probing it with a new random name.</summary>
    internal static bool MayFoldCase(string gitDir) => MayFoldCase(gitDir, GenerateCaseProbeName());

    /// <summary>Generates a random name for the case probe.</summary>
    /// <remarks>
    /// It's a name git has no use for: the files it reads in a git dir have fixed names, and the only other names it
    /// takes there as refs are all caps with no dot. Lower case, so the upper-case form is a different name.
    /// </remarks>
    internal static string GenerateCaseProbeName() =>
        ".pen-case-probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>
    /// Whether <paramref name="gitDir"/> may fold case: an empty file created there is also found under its
    /// upper-case name.
    /// </summary>
    /// <returns>False only for a clean "no".</returns>
    /// <remarks>
    /// Anything short of a clean "no" counts as yes: either name there already (it isn't this probe's to touch), the
    /// file not being created, or it having gone by the time it's looked for. Exclusive creation doesn't follow a
    /// symlink, and the file is removed on every path once creating it has been tried.
    /// </remarks>
    internal static bool MayFoldCase(string gitDir, string name)
    {
        var probe = Path.Combine(gitDir, name);
        var upper = Path.Combine(gitDir, name.ToUpperInvariant());
        try
        {
            if (Exists(probe) || Exists(upper))
                return true;
            try
            {
                new FileStream(probe, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }).Dispose();
                return Exists(upper) || !Exists(probe);
            }
            finally
            {
                File.Delete(probe);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
