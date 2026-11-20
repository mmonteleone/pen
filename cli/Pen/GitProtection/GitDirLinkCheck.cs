using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// Decides whether a symlink in the project leads to a directory at or inside the git dir, or would once a directory
/// it names is made.
/// </summary>
/// <remarks>
/// From what a symlink leads to, the check goes up by "..", which the kernel resolves physically, as far as "/". A
/// symlink that leads nowhere now still counts if it would lead in once something is made: a later commit makes
/// objects/ab, say, and then .git/objects/ab/../x resolves.
/// </remarks>
internal sealed class GitDirLinkCheck
{
    /// <summary>
    /// How many symlinks met while resolving one are followed: past that it's a loop, or longer than the kernel
    /// follows (ELOOP), so git can't get through it either.
    /// </summary>
    private const int MaxLinks = 40;

    /// <summary>How many directories up from a target are looked at before Pen can't tell.</summary>
    private const int MaxDepth = 512;

    /// <summary>The git dirs, each by <see cref="Identify"/>.</summary>
    private readonly HashSet<string> _gitDirs;

    private GitDirLinkCheck(HashSet<string> gitDirs) => _gitDirs = gitDirs;

    /// <summary>Prepares the check for the directories <paramref name="gitDirs"/>.</summary>
    /// <returns>The check, or null if none of them is there (no symlink can lead into one).</returns>
    public static GitDirLinkCheck? For(IEnumerable<string> gitDirs)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in gitDirs)
        {
            if (Identify(dir) is { } id)
                known.Add(id);
        }
        return known.Count == 0 ? null : new(known);
    }

    /// <summary>
    /// Prepares the check for a project's tree: the common git dir, the project's own .git if it's a directory, and
    /// a linked worktree's own entry (both inside the common git dir when Pen accepted them).
    /// </summary>
    public static GitDirLinkCheck? ForProject(string root, string commonGitDir)
    {
        var dirs = new List<string> { commonGitDir };
        var own = Path.Combine(root, ".git");
        if (PathChecks.IsLink(own))
            return For(dirs);
        if (Directory.Exists(own))
            dirs.Add(own);
        else if (File.Exists(own) && RepoScan.ReadWorktreeEntry(own) is { } entry)
            dirs.Add(entry);
        return For(dirs);
    }

    /// <summary>Where a symlink leads.</summary>
    internal enum Lead
    {
        /// <summary>Nowhere in a git dir, now or once a directory it names is made.</summary>
        Elsewhere,

        /// <summary>To a directory at or inside a git dir, or there once a directory it names is made.</summary>
        Inside,

        /// <summary>
        /// Pen can't tell: its target can't be read or isn't valid UTF-8, or a directory on the way up from where it
        /// leads can't be looked at, or the way up is too long to walk.
        /// </summary>
        /// <remarks>Callers refuse this just as they refuse <see cref="Inside"/>, but word it differently.</remarks>
        Unknown,
    }

    /// <summary>
    /// Where <paramref name="link"/>, a symlink, leads: to a directory at or inside a git dir (or there once a
    /// directory it names is made), elsewhere, or somewhere Pen can't tell.
    /// </summary>
    public Lead Trace(string link)
    {
        if (Native.CanStat)
        {
            if (Native.Stat(link, follow: true) is { } status)
                return status.IsDirectory ? IsAtOrInside(link) : Lead.Elsewhere;
        }
        else if (Directory.Exists(link))
        {
            return IsAtOrInside(link);
        }
        else if (File.Exists(link))
        {
            return Lead.Elsewhere;
        }
        return MayLeadIn(link);
    }

    /// <summary>
    /// Where <paramref name="link"/>, which leads nowhere that can be looked at now, leads as far as it resolves, or
    /// would once what's missing is made.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It's resolved component by component as the kernel resolves it. A symlink met on the way that leads nowhere
    /// has its target put in place of its name, before the components still to come, as the kernel does: so "x/../.."
    /// after x, a symlink to a/b, is a/b/../.., which leads somewhere else once a/b is made than x's own target does.
    /// Past <see cref="MaxLinks"/> of them it's a loop, or longer than git can follow.
    /// </para>
    /// <para>
    /// Past a missing component (or one that isn't a directory) the names that follow are directories yet to be made
    /// below the directory reached, each ".." leaving one of them; once it has left them all it's back in that
    /// directory and resolves from there again, so a ".." after a symlink goes up from where the symlink leads. It
    /// leads in if the directory reached is at or inside a git dir at any missing component or at its end.
    /// </para>
    /// <para>
    /// The directory reached is kept as its real path, so the path looked at never outgrows PATH_MAX however long the
    /// target; ".." is resolved physically either way.
    /// </para>
    /// </remarks>
    private Lead MayLeadIn(string link)
    {
        var target = ReadTarget(link);
        if (target is null)
            return Lead.Unknown;
        var start = target.StartsWith('/') ? "/" : Native.RealPath(Paths.GetParent(link));
        if (start is null)
            return Lead.Unknown;

        var walk = new TargetWalk(start, target);
        var looked = new HashSet<string>(StringComparer.Ordinal);
        var unknown = false;
        while (true)
        {
            var stop = walk.ResolveExisting();
            if (stop == WalkStop.Unknown)
                return Lead.Unknown;
            if (stop == WalkStop.TooManyLinks)
                break;
            var lead = LookOnce(walk.Reached, looked);
            if (lead == Lead.Inside)
                return Lead.Inside;
            unknown |= lead == Lead.Unknown;
            if (stop == WalkStop.End || !walk.LeaveUnmade())
                break;
        }
        return unknown ? Lead.Unknown : Lead.Elsewhere;
    }

    /// <summary>
    /// Whether <paramref name="dir"/> is at or inside a git dir (<see cref="IsAtOrInside"/>), unless it's in
    /// <paramref name="looked"/> already, which it's added to.
    /// </summary>
    /// <returns><see cref="Lead.Elsewhere"/> for a directory looked at before, which told all it could then.</returns>
    private Lead LookOnce(string dir, HashSet<string> looked) =>
        looked.Add(dir) ? IsAtOrInside(dir) : Lead.Elsewhere;

    private static string[] Split(string target) => target.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Why <see cref="TargetWalk.ResolveExisting"/> stopped.</summary>
    private enum WalkStop
    {
        /// <summary>Every component is resolved.</summary>
        End,

        /// <summary>A component is missing, or isn't a directory: it and those after it are yet to be made.</summary>
        Missing,

        /// <summary>A component can't be looked at, or a symlink met can't be read.</summary>
        Unknown,

        /// <summary>More than <see cref="MaxLinks"/> symlinks were met.</summary>
        TooManyLinks,
    }

    /// <summary>
    /// A symlink's target being resolved from the directory reached so far, as a real path, with the components still
    /// to come.
    /// </summary>
    private sealed class TargetWalk(string start, string target)
    {
        private List<string> _parts = [.. Split(target)];
        private int _next;
        private int _linksMet;

        /// <summary>The directory reached so far, as a real path.</summary>
        public string Reached { get; private set; } = start;

        /// <summary>
        /// Resolves the components that are there, descending into each directory and putting each symlink's target
        /// in place of its name, up to the first that's missing (or isn't a directory) or the end.
        /// </summary>
        public WalkStop ResolveExisting()
        {
            while (_next < _parts.Count)
            {
                var part = _parts[_next++];
                if (part == ".")
                    continue;
                var path = Reached == "/" ? "/" + part : Reached + "/" + part;
                if (IsDirectory(path))
                {
                    if (Native.RealPath(path) is not { } real)
                        return WalkStop.Unknown;
                    Reached = real;
                    continue;
                }
                if (part == "..")
                    return WalkStop.Unknown; // Reached's parent can't be looked at
                if (!PathChecks.IsLink(path))
                    return WalkStop.Missing;
                if (Splice(path) is { } stop)
                    return stop;
            }
            return WalkStop.End;
        }

        /// <summary>
        /// Puts the target of <paramref name="link"/>, a symlink met on the way, in place of its name.
        /// </summary>
        /// <returns>Why the walk stops, or null if it goes on.</returns>
        private WalkStop? Splice(string link)
        {
            if (++_linksMet > MaxLinks)
                return WalkStop.TooManyLinks;
            if (ReadTarget(link) is not { } spliced)
                return WalkStop.Unknown;
            if (spliced.StartsWith('/'))
                Reached = "/";
            _parts = [.. Split(spliced), .. _parts[_next..]];
            _next = 0;
            return null;
        }

        /// <summary>
        /// Takes the components after a missing one, each naming a directory yet to be made below
        /// <see cref="Reached"/> and each ".." leaving one, up to the ".." that leaves the last of them.
        /// </summary>
        /// <returns>
        /// Whether it left them all, so the walk is back in <see cref="Reached"/>; false if the target ends first.
        /// </returns>
        public bool LeaveUnmade()
        {
            var unmade = 1;
            while (_next < _parts.Count)
            {
                var part = _parts[_next++];
                if (part == ".")
                    continue;
                unmade += part == ".." ? -1 : 1;
                if (unmade == 0)
                    return true;
            }
            return false;
        }
    }

    /// <summary>Whether <paramref name="dir"/> is a git dir or inside one, going up by "..".</summary>
    /// <returns>
    /// <see cref="Lead.Unknown"/> if a directory on the way up can't be looked at, or the way up is too long to walk.
    /// </returns>
    /// <remarks>
    /// Each step up is looked at as the kernel goes there, so a directory that can't be searched stops the walk; the
    /// walk then goes on from that directory's real path, so the path looked at never outgrows PATH_MAX.
    /// </remarks>
    private Lead IsAtOrInside(string dir)
    {
        var path = dir;
        var id = Identify(path);
        for (var depth = 0; depth < MaxDepth; depth++)
        {
            if (id is null)
                return Lead.Unknown;
            if (_gitDirs.Contains(id))
                return Lead.Inside;
            var up = path == "/" ? "/.." : path + "/..";
            var upId = Identify(up);
            if (upId == id)
                return Lead.Elsewhere; // "/", whose ".." is itself
            if (upId is null || Native.RealPath(up) is not { } real)
                return Lead.Unknown;
            path = real;
            id = upId;
        }
        return Lead.Unknown;
    }

    /// <summary>Names the directory at <paramref name="path"/> by which file it is, or by its real path.</summary>
    /// <returns>The name, or null if it can't be looked at.</returns>
    /// <remarks>
    /// A directory is compared by device and inode, not by how its path is spelled: realpath(3) leaves a macOS
    /// firmlink as written, so /System/Volumes/Data/Users/x/.git is the git dir with a different path. Where stat(2)
    /// can't be called (<see cref="Native.CanStat"/>), the real path stands in, which is the same on Linux except
    /// across bind mounts.
    /// </remarks>
    internal static string? Identify(string path)
    {
        if (!Native.CanStat)
            return Native.RealPath(path);
        return Native.Stat(path, follow: true) is { } status ? $"{status.Device}:{status.Inode}" : null;
    }

    private static bool IsDirectory(string path) =>
        Native.CanStat ? Native.Stat(path, follow: true) is { IsDirectory: true } : Directory.Exists(path);

    /// <summary>Reads what <paramref name="link"/> holds.</summary>
    /// <returns>The text, or null if it can't be read or isn't valid UTF-8.</returns>
    private static string? ReadTarget(string link)
    {
        if (!Native.CanStat)
        {
            try
            {
                var text = new FileInfo(link).LinkTarget;
                return text is null || text.Contains('\uFFFD') ? null : text;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        if (Native.ReadLink(link) is not { Length: > 0 } bytes)
            return null;
        return StrictUtf8.DecodeOrNull(bytes);
    }
}
