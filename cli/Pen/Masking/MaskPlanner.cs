using Pen.GitProtection;
using Pen.Platform;

namespace Pen.Masking;

/// <summary>Plans what a session hides and replaces in the project: what hide matches and [replace] names.</summary>
/// <remarks>
/// <para>
/// It walks the project without following symlinks, never entering or matching a .git (any case, at any depth),
/// the top-level .pen (any case, since a folding file system takes .PEN for it) or the common git dir, and doesn't
/// descend into a directory it hides, since that's hidden whole. When every pattern is literal it doesn't walk:
/// it follows each pattern's path down from the project dir by the same rules, comparing names as
/// <see cref="HidePattern.IsMatch"/> does, so it finds what the walk would.
/// </para>
/// <para>
/// A symlink that matches hides what it leads to, if that's in the project and not off limits; what's outside the
/// project isn't in the sandbox, so it's left alone. The planner reads the tree and the [replace] sources, and
/// never runs git or writes anything.
/// </para>
/// <para>
/// Names come from the disk, which the agent writes, so every one is passed through <see cref="Out.Field"/> before
/// it goes into a warning or an error.
/// </para>
/// </remarks>
internal sealed class MaskPlanner
{
    /// <summary>The most mounts a plan may need.</summary>
    public const int MaxMounts = 1000;

    /// <summary>The most warnings a plan lists; the rest are counted in one more line.</summary>
    public const int MaxWarnings = 20;

    private const UnixFileMode ReadAndExecuteBits = UnixFileMode.UserRead | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private readonly string _projectDir;
    private readonly string? _commonGitDir;

    /// <summary>The git-protection mounts inside the project (not the project dir itself), by real path.</summary>
    private readonly List<string> _protected;

    private readonly IReadOnlyList<HidePattern> _hide;
    private readonly IReadOnlyList<KeyValuePair<string, string>> _replace;

    /// <summary>The files to hide found so far, by real path, with what matched them.</summary>
    private readonly Dictionary<string, Found> _files = new(StringComparer.Ordinal);

    /// <summary>The directories to hide found so far, by real path, with what matched them.</summary>
    private readonly Dictionary<string, Found> _dirs = new(StringComparer.Ordinal);

    /// <summary>The listings taken by the literal lookup, by directory; null for one that couldn't be listed.</summary>
    private readonly Dictionary<string, List<Entry>?> _listings = new(StringComparer.Ordinal);

    /// <summary>The first <see cref="MaxWarnings"/> warnings, in the order given, each once.</summary>
    private readonly List<string> _warnings = [];

    /// <summary>Every warning given, to give each once.</summary>
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);

    /// <summary>How many warnings were given after the first <see cref="MaxWarnings"/>.</summary>
    private int _moreWarnings;

    /// <summary>Creates a planner for one project.</summary>
    /// <param name="projectDir">The project dir, a real path.</param>
    /// <param name="commonGitDir">
    /// The project's common git dir, or null outside a repo; for a linked worktree it's outside the project.
    /// </param>
    /// <param name="protectedPaths">
    /// The paths the session mounts read-only to protect git (<see cref="Mount.Path"/> of what
    /// <see cref="RepoGuard.PrepareMounts"/> returns); only those inside the project are looked at.
    /// </param>
    /// <param name="hide">The hide patterns: the user's, then the project's.</param>
    /// <param name="replace">The project's [replace] entries (target, source), as checked by Config.</param>
    public MaskPlanner(
        string projectDir,
        string? commonGitDir,
        IReadOnlyCollection<string> protectedPaths,
        IReadOnlyList<HidePattern> hide,
        IReadOnlyList<KeyValuePair<string, string>> replace)
    {
        _projectDir = projectDir;
        _commonGitDir = commonGitDir is null ? null : Native.RealPath(commonGitDir) ?? commonGitDir;
        _protected = [.. protectedPaths.Select(path => Native.RealPath(path) ?? path)
            .Where(path => path != projectDir && RepoGuard.IsInside(projectDir, path))
            .Distinct(StringComparer.Ordinal)];
        _hide = hide;
        _replace = replace;
    }

    /// <summary>A directory entry as its listing reports it (no symlink followed).</summary>
    /// <param name="Name">The name, with U+FFFD in place of bytes that aren't valid UTF-8.</param>
    /// <param name="IsDirectory">Whether it's a directory, or a symlink to one.</param>
    /// <param name="IsLink">Whether it's a symlink.</param>
    /// <param name="IsUndecodable">
    /// Whether its name isn't valid UTF-8 and lists the same as another entry's that is, so the path made from it
    /// names the other entry.
    /// </param>
    private readonly record struct Entry(string Name, bool IsDirectory, bool IsLink, bool IsUndecodable = false);

    /// <summary>
    /// A directory to look in: its path, its path in the project ("" for the project dir), and its device and inode
    /// when it was found.
    /// </summary>
    private readonly record struct Place(string Path, string Relative, ulong Device, ulong Inode);

    /// <summary>
    /// A path to hide: the pattern that matched it first, its link count (0 for a directory), and its device and
    /// inode when it was found.
    /// </summary>
    private readonly record struct Found(HidePattern Pattern, ulong Links, ulong Device, ulong Inode);

    /// <summary>Plans the project's hidden and replaced files. Call it once per planner.</summary>
    /// <returns>The plan; <see cref="MaskPlan.Empty"/> when there are no patterns or replacements.</returns>
    /// <exception cref="PenException">
    /// A [replace] entry can't be used (its source is hidden, say), a hidden path or [replace] target is, is in or
    /// holds a git-protection mount, or the plan needs more than <see cref="MaxMounts"/> mounts.
    /// </exception>
    public MaskPlan Plan()
    {
        if (_hide.Count == 0 && _replace.Count == 0)
            return MaskPlan.Empty;
        if (!Native.CanStat)
            throw new PenException("pen can't hide or replace files on this platform: remove hide and [replace]");
        // Each [replace] source is read into memory, and .pen/config.toml is the agent's to write.
        if (_replace.Count > MaxMounts)
        {
            throw new PenException($"[replace] needs {_replace.Count} mounts, and pen allows at most {MaxMounts}: " +
                "replace fewer files");
        }
        if (_hide.Count > 0 && _hide.All(pattern => pattern.IsLiteral))
            FindLiterals();
        else if (_hide.Count > 0)
            Walk();

        // Whichever was found first, a path inside a hidden directory is hidden by its mount already.
        var files = _files.Where(file => FindHiddenDir(file.Key) is null).ToDictionary(StringComparer.Ordinal);
        var dirs = _dirs.Where(dir => FindHiddenDir(dir.Key) is null).ToDictionary(StringComparer.Ordinal);
        var replacements = PlanReplacements(files);
        DropChanged(files, "hide");
        DropChanged(dirs, "hide");
        replacements.RemoveAll(replacement => IsChanged(replacement.Target.Path, "replace"));
        RefuseProtected(files.Concat(dirs));
        var count = files.Count + dirs.Count + replacements.Count;
        if (count > MaxMounts)
            throw new PenException(FormatTooMany(count, files.Values.Concat(dirs.Values)));

        foreach (var (path, found) in files.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            if (found.Links > 1)
            {
                Warn($"{Format(path)} has {found.Links} hard links: the agent may read it under another name");
            }
        }
        List<string> warnings = [.. _warnings];
        if (_moreWarnings > 0)
            warnings.Add($"and {_moreWarnings} more warning{(_moreWarnings == 1 ? "" : "s")}");
        return new MaskPlan(ToMasked(files), ToMasked(dirs), replacements, warnings);
    }

    /// <summary>Gives a warning, once, keeping the first <see cref="MaxWarnings"/> and counting the rest.</summary>
    /// <param name="warning">The warning, already safe to print.</param>
    /// <remarks>A tree the agent writes can hold any number of names to warn about.</remarks>
    private void Warn(string warning)
    {
        if (!_warned.Add(warning))
            return;
        if (_warnings.Count < MaxWarnings)
            _warnings.Add(warning);
        else
            _moreWarnings++;
    }

    /// <summary>Drops each planned path that isn't its own real path any more, with a warning.</summary>
    /// <remarks>
    /// The project can change while it's walked: a directory swapped for a symlink after it was listed would put a
    /// symlink in a path found inside it, and the mount would land wherever that leads.
    /// </remarks>
    private void DropChanged(Dictionary<string, Found> planned, string verb)
    {
        foreach (var path in planned.Keys.Where(path => IsChanged(path, verb)).ToList())
            planned.Remove(path);
    }

    /// <summary>Tells whether a planned path isn't its own real path any more, warning if so.</summary>
    /// <param name="path">The planned path.</param>
    /// <param name="verb">"hide" or "replace", for the warning.</param>
    private bool IsChanged(string path, string verb)
    {
        if (Native.RealPath(path) == path)
            return false;
        Warn($"couldn't {verb} {Format(path)}: it changed while pen looked at it");
        return true;
    }

    /// <summary>Refuses to hide a path equal to, inside or holding a git-protection mount.</summary>
    /// <exception cref="PenException">One does, naming it, the mount and the pattern.</exception>
    private void RefuseProtected(IEnumerable<KeyValuePair<string, Found>> hidden)
    {
        foreach (var (path, found) in hidden.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            if (DescribeProtected(path) is { } overlap)
            {
                throw new PenException($"hide \"{Out.Field(found.Pattern.Text)}\" matches {overlap}, so it can't " +
                    $"be hidden as well: anchor or narrow the pattern so it leaves {Format(path)} out (hide has no " +
                    "negation)");
            }
        }
    }

    /// <summary>Describes how <paramref name="path"/> overlaps a git-protection mount, for an error.</summary>
    /// <returns>The description, starting with the path, or null if it doesn't overlap one.</returns>
    /// <remarks>
    /// Docker can't mount over a mount point it was given already, nor make one inside a read-only mount, so the
    /// session wouldn't start; and hiding a directory that holds the mount would hide what git's config reads.
    /// </remarks>
    private string? DescribeProtected(string path)
    {
        const string Why = "which pen mounts read-only to protect git's config";
        foreach (var mount in _protected)
        {
            if (path == mount)
                return $"{Format(path)}, {Why}";
            if (RepoGuard.IsInside(mount, path))
                return $"{Format(path)}, which is in {Format(mount)}, {Why}";
            if (RepoGuard.IsInside(path, mount))
                return $"{Format(path)}, which holds {Format(mount)}, {Why}";
        }
        return null;
    }

    /// <summary>Walks the whole project, hiding what the patterns match.</summary>
    private void Walk()
    {
        var pending = new Stack<Place>();
        if (Enter(_projectDir, "") is { } root)
            pending.Push(root);
        while (pending.TryPop(out var dir))
        {
            if (List(dir) is not { } entries)
                continue;
            foreach (var entry in entries)
            {
                if (Visit(dir, entry) is { } subdir)
                    pending.Push(subdir);
            }
        }
    }

    /// <summary>Follows each literal pattern's path down from the project dir, hiding what it reaches.</summary>
    /// <remarks>
    /// Each step takes every entry whose name is the segment as <see cref="HidePattern.IsMatch"/> compares (so on a
    /// file system that tells case apart, a segment can lead to several entries), and goes on only into a directory
    /// the walk would enter: not a symlink, not off limits, not hidden. So it reaches exactly the entries the walk
    /// would test against the pattern, and <see cref="Visit"/> tests each the same way.
    /// </remarks>
    private void FindLiterals()
    {
        if (Enter(_projectDir, "") is not { } root)
            return;
        foreach (var pattern in _hide)
        {
            var segments = pattern.LiteralPath!.Split('/');
            List<Place> level = [root];
            foreach (var segment in segments)
            {
                var next = new List<Place>();
                foreach (var dir in level)
                {
                    if (ListOnce(dir) is not { } entries)
                        continue;
                    foreach (var entry in entries)
                    {
                        if (IsSameName(entry.Name, segment) && Visit(dir, entry) is { } subdir)
                            next.Add(subdir);
                    }
                }
                level = next;
            }
        }
    }

    /// <summary>
    /// Tells whether a listed name is a literal pattern's segment, ignoring case, and normalization on macOS.
    /// </summary>
    private static bool IsSameName(string name, string segment) =>
        name.Equals(segment, StringComparison.OrdinalIgnoreCase)
        || Native.NormalizeFormC(name).Equals(segment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Hides <paramref name="entry"/> of <paramref name="dir"/> if a pattern matches it.</summary>
    /// <returns>
    /// The entry as a directory to look in, or null when there's nothing to look in: it isn't a directory (a
    /// symlink to one isn't), it's hidden, it's off limits, or its name can't be used.
    /// </returns>
    private Place? Visit(Place dir, Entry entry)
    {
        if (IsOffLimitsName(entry.Name, dir.Relative))
            return null;
        var path = Path.Join(dir.Path, entry.Name);
        var relative = dir.Relative.Length == 0 ? entry.Name : dir.Relative + "/" + entry.Name;
        if (entry.IsUndecodable)
        {
            WarnUndecodable(entry, relative);
            return null;
        }

        if (entry.Name.Contains('\uFFFD'))
        {
            // The listing shows a name that isn't valid UTF-8 with U+FFFD in place of the bad bytes, so the path
            // made from it names nothing there (or another entry). A real U+FFFD in a name is found by lstat.
            if (Native.Stat(path, follow: false) is not { } status)
            {
                WarnUndecodable(entry, relative);
                return null;
            }

            entry = entry with { IsDirectory = status.IsDirectory, IsLink = status.IsSymlink };
        }

        var isDirectory = entry.IsDirectory && !entry.IsLink;
        if (isDirectory && path == _commonGitDir)
            return null;
        if (entry.IsLink)
            WarnLiteralsThrough(path, relative);
        var match = FindMatch(relative, isDirectory);
        // A directory-only pattern matches a symlink that leads to a directory in the project.
        var dirMatch = entry.IsLink ? match ?? FindMatch(relative, isDirectory: true) : match;
        if (dirMatch is null)
            return isDirectory ? Enter(path, relative) : null;
        if (isDirectory && _commonGitDir is not null && RepoGuard.IsInside(path, _commonGitDir))
        {
            // Hiding it would hide the git dir, so it's looked in instead, for what else matches.
            Warn($"couldn't hide {Out.Field(relative)}: the repo's git dir is in it, which pen doesn't hide");
            return Enter(path, relative);
        }

        Hide(path, relative, match, dirMatch);
        return null;
    }

    /// <summary>Takes a directory to look in, with its device and inode as lstat finds them now.</summary>
    /// <param name="path">The directory.</param>
    /// <param name="relative">Its path in the project ("" for the project dir).</param>
    /// <returns>The place, or null, with a warning, if lstat can't look at it or it isn't a directory now.</returns>
    private Place? Enter(string path, string relative)
    {
        if (Native.Stat(path, follow: false) is not { } status)
        {
            Warn($"couldn't look in {Name(relative)} for files to hide");
            return null;
        }

        if (!status.IsDirectory || status.IsSymlink)
        {
            WarnChangedDir(relative);
            return null;
        }

        return new Place(path, relative, status.Device, status.Inode);
    }

    /// <summary>Tells whether a place is still the directory it was when it was found, by lstat.</summary>
    private static bool IsUnchanged(Place dir) =>
        Native.Stat(dir.Path, follow: false) is { IsDirectory: true, IsSymlink: false } status
        && status.Device == dir.Device && status.Inode == dir.Inode;

    /// <summary>Warns that a directory changed while Pen looked at it, so it isn't looked in.</summary>
    private void WarnChangedDir(string relative) =>
        Warn($"couldn't look in {Name(relative)} for files to hide: it changed while pen looked at it");

    /// <summary>Names a directory in the project for a warning, safe to print.</summary>
    private static string Name(string relative) => relative.Length == 0 ? "the project" : Out.Field(relative);

    /// <summary>
    /// Warns about each literal pattern whose path goes through the symlink at <paramref name="relative"/>, since
    /// neither the walk nor the literal lookup follows it, so the pattern hides nothing there.
    /// </summary>
    /// <remarks>A symlink that leads nowhere or out of the project leads to nothing in the sandbox to hide.</remarks>
    private void WarnLiteralsThrough(string path, string relative)
    {
        var through = relative.Split('/');
        string? real = null;
        foreach (var pattern in _hide)
        {
            if (pattern.LiteralPath is not { } literal)
                continue;
            var segments = literal.Split('/');
            if (segments.Length <= through.Length
                || !through.Select((name, index) => IsSameName(name, segments[index])).All(same => same))
            {
                continue;
            }

            real ??= Native.RealPath(path) ?? "";
            if (real.Length == 0 || !RepoGuard.IsInside(_projectDir, real))
                return;
            Warn($"couldn't hide {Out.Field(literal)}: {Out.Field(relative)} on the way is a symlink, which pen " +
                "doesn't follow; hide the path it leads to");
        }
    }

    /// <summary>Warns that an entry whose name isn't valid UTF-8 couldn't be hidden or looked in.</summary>
    private void WarnUndecodable(Entry entry, string relative)
    {
        var isDirectory = entry.IsDirectory && !entry.IsLink;
        if ((entry.IsLink ? FindMatch(relative, isDirectory: true) : FindMatch(relative, isDirectory)) is not null)
            Warn($"couldn't hide {Out.Field(relative)}: its name isn't valid UTF-8");
        else if (isDirectory)
            Warn($"couldn't look in {Out.Field(relative)} for files to hide: its name isn't valid UTF-8");
    }

    /// <summary>Gets the first pattern that matches an entry.</summary>
    /// <returns>The pattern, or null if none matches.</returns>
    private HidePattern? FindMatch(string relative, bool isDirectory)
    {
        foreach (var pattern in _hide)
        {
            if (pattern.IsMatch(relative, isDirectory))
                return pattern;
        }
        return null;
    }

    /// <summary>Hides the matched entry at <paramref name="path"/>, or what it leads to if it's a symlink.</summary>
    /// <param name="path">The entry.</param>
    /// <param name="relative">The entry's path in the project, for messages.</param>
    /// <param name="fileMatch">The pattern that matched it as listed, or null if only a directory would match.</param>
    /// <param name="dirMatch">The pattern that matched it, taken as a directory.</param>
    /// <remarks>
    /// The type comes from lstat, not the listing, in case it changed since. A FIFO, socket or device is left
    /// alone, as is a symlink that leads nowhere, out of the project or somewhere off limits. What lstat can't look
    /// at (a path too long, a real path that isn't valid UTF-8) is warned about.
    /// </remarks>
    private void Hide(string path, string relative, HidePattern? fileMatch, HidePattern dirMatch)
    {
        if (Native.Stat(path, follow: false) is not { } found)
        {
            Warn($"couldn't hide {Out.Field(relative)}: pen can't look at it");
            return;
        }

        if (found.IsSymlink)
        {
            var real = Native.RealPath(path);
            if (real == _projectDir)
                Warn($"{Out.Field(relative)} leads to the project dir itself, which can't be hidden");
            if (real is not null && IsInGitDir(real))
            {
                Warn($"couldn't hide {Out.Field(relative)}: it leads into the repo's git dir, which pen doesn't hide");
                return;
            }

            if (real is null || !IsHideable(real))
                return;
            if (Native.Stat(real, follow: false) is not { } leadsTo)
            {
                Warn($"couldn't hide {Out.Field(relative)}: pen can't look at what it leads to");
                return;
            }

            path = real;
            found = leadsTo;
        }
        if (!IsHideable(path))
            return;
        if (found.IsDirectory)
            _dirs.TryAdd(path, new(dirMatch, 0, found.Device, found.Inode));
        else if (found.IsRegularFile && fileMatch is not null)
            _files.TryAdd(path, new(fileMatch, found.Links, found.Device, found.Inode));
    }

    /// <summary>
    /// Tells whether <paramref name="path"/>, a real path, is at or in the common git dir, or in the project with a
    /// .git segment (any case).
    /// </summary>
    private bool IsInGitDir(string path) =>
        (_commonGitDir is not null && RepoGuard.IsInside(_commonGitDir, path))
        || (path != _projectDir && RepoGuard.IsInside(_projectDir, path)
            && Relative(path).Split('/').Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)));

    /// <summary>
    /// Tells whether <paramref name="path"/>, a real path, may be hidden: it's in the project but not the project dir,
    /// and not in, at or around a .git, the top-level .pen or the common git dir.
    /// </summary>
    private bool IsHideable(string path)
    {
        if (path == _projectDir || !RepoGuard.IsInside(_projectDir, path))
            return false;
        if (_commonGitDir is not null
            && (RepoGuard.IsInside(_commonGitDir, path) || RepoGuard.IsInside(path, _commonGitDir)))
        {
            return false;
        }

        return !HidePattern.IsOffLimits(Relative(path));
    }

    /// <summary>Tells whether an entry is a .git (any case) or, at the top, .pen (any case).</summary>
    private static bool IsOffLimitsName(string name, string parentRelative) =>
        name.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || (parentRelative.Length == 0 && name.Equals(".pen", StringComparison.OrdinalIgnoreCase));

    /// <summary>Lists a directory's entries without following symlinks, warning if it can't be listed.</summary>
    /// <returns>The entries, or null if it can't be listed or changed.</returns>
    /// <remarks>
    /// The listing follows a symlink, so the directory is lstat'ed before and after it and must be the one found,
    /// by device and inode: one swapped for a symlink after its parent was listed would lead the walk anywhere.
    /// </remarks>
    private List<Entry>? List(Place dir)
    {
        if (!IsUnchanged(dir))
        {
            WarnChangedDir(dir.Relative);
            return null;
        }

        if (DirectoryListing.ReadOrNull(dir.Path) is not { } listed)
        {
            Warn($"couldn't look in {Name(dir.Relative)} for files to hide");
            return null;
        }
        var entries = listed.ConvertAll(entry => new Entry(entry.Name, entry.IsDirectory, entry.IsLink));
        if (!IsUnchanged(dir))
        {
            WarnChangedDir(dir.Relative);
            return null;
        }

        MarkUndecodable(dir, entries);
        return entries;
    }

    /// <summary>
    /// Marks the entries whose names aren't valid UTF-8 but list the same as a valid name there (bad\xff.env and
    /// bad\uFFFD.env both list as the latter).
    /// </summary>
    /// <remarks>
    /// The path made from either names the valid one, so of each such set the entry whose listed type is what lstat
    /// finds there is taken for the valid one, and the rest are marked. With no valid name there, lstat finds
    /// nothing and <see cref="Visit"/> warns about each entry already.
    /// </remarks>
    private static void MarkUndecodable(Place dir, List<Entry> entries)
    {
        var collisions = Enumerable.Range(0, entries.Count)
            .Where(index => entries[index].Name.Contains('\uFFFD'))
            .GroupBy(index => entries[index].Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1);
        foreach (var group in collisions)
        {
            if (Native.Stat(Path.Join(dir.Path, group.Key), follow: false) is not { } status)
                continue;
            var valid = group.FirstOrDefault(index => entries[index].IsLink == status.IsSymlink
                && (entries[index].IsDirectory && !entries[index].IsLink) == status.IsDirectory, group.First());
            foreach (var index in group.Where(index => index != valid))
                entries[index] = entries[index] with { IsUndecodable = true };
        }
    }

    /// <summary>Lists a directory as <see cref="List"/> does, once however often it's asked.</summary>
    private List<Entry>? ListOnce(Place dir)
    {
        if (!_listings.TryGetValue(dir.Path, out var entries))
        {
            entries = List(dir);
            _listings[dir.Path] = entries;
        }
        return entries;
    }

    /// <summary>Gets the hidden directory that <paramref name="path"/> is inside, if any.</summary>
    /// <returns>The directory, or null.</returns>
    private string? FindHiddenDir(string path)
    {
        for (var parent = Path.GetDirectoryName(path);
             parent is not null && parent.Length > _projectDir.Length;
             parent = Path.GetDirectoryName(parent))
        {
            if (_dirs.ContainsKey(parent))
                return parent;
        }
        return null;
    }

    /// <summary>Checks each [replace] entry and reads its source.</summary>
    /// <param name="files">The files to hide; a replaced one is taken out, since its replacement covers it.</param>
    /// <returns>The replacements, in [replace]'s order.</returns>
    private List<Replacement> PlanReplacements(Dictionary<string, Found> files)
    {
        var replacements = new List<Replacement>();
        if (_replace.Count == 0)
            return replacements;
        var hidden = FindHiddenIdentities();
        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (target, source) in _replace)
        {
            var (path, found) = FindTarget(target);
            if (FindHiddenDir(path) is { } dir)
            {
                throw new PenException($"replace: target \"{Out.Field(target)}\" is in {Format(dir)}/, which hide " +
                    "hides whole: stop hiding the directory, or replace a file outside it");
            }

            if (DescribeProtected(path) is { } overlap)
            {
                throw new PenException($"replace: target \"{Out.Field(target)}\" is {overlap}, so it can't be " +
                    "replaced as well: replace another file");
            }

            if (!targets.TryAdd(path, target))
            {
                throw new PenException($"replace: \"{Out.Field(targets[path])}\" and \"{Out.Field(target)}\" are " +
                    "the same file");
            }

            files.Remove(path);
            var copy = ReadSource(target, source, hidden);
            if (Native.Stat(path, follow: false) is { Links: > 1 } status)
            {
                Warn($"{Format(path)} has {status.Links} hard links: the agent may read what it holds under " +
                    "another name");
            }

            var masked = new MaskedPath(path, Relative(path), found.Device, found.Inode);
            replacements.Add(new(masked, copy.Bytes, copy.Mode & ReadAndExecuteBits));
        }
        return replacements;
    }

    /// <summary>Gets which files and directories hide found, by device and inode.</summary>
    /// <returns>Each one's real path, by its device and inode.</returns>
    /// <remarks>
    /// Every one found counts, whether or not it's mounted itself: one in a hidden directory, or a replaced file, is
    /// still kept from the agent. Device and inode are from the lstat taken when each was found, not looked up again
    /// by name, which could find something moved there since.
    /// </remarks>
    private Dictionary<(ulong Device, ulong Inode), string> FindHiddenIdentities()
    {
        var identities = new Dictionary<(ulong, ulong), string>();
        foreach (var (path, found) in _files.Concat(_dirs))
            identities.TryAdd((found.Device, found.Inode), path);
        return identities;
    }

    /// <summary>Finds a [replace] target: a regular file in the project, with no symlink on the way.</summary>
    /// <returns>Its real path, in the case it has on disk, and what lstat said about it.</returns>
    /// <exception cref="PenException">It isn't there, isn't a regular file, or a symlink is on the way.</exception>
    /// <remarks>
    /// A target that doesn't exist is refused because Docker would create an empty file on the host as the mount
    /// point. The real path is the one to mount: on a folding file system the target may be spelled in another case.
    /// </remarks>
    private (string Path, FileStatus Status) FindTarget(string target)
    {
        var parts = target.Split('/');
        if (parts.Any(part => part is "" or "." or "..") || HidePattern.IsOffLimits(target))
            throw OutsideProject(target);
        var path = _projectDir;
        FileStatus? status = null;
        foreach (var part in parts)
        {
            path = Path.Join(path, part);
            status = Native.Stat(path, follow: false)
                ?? throw new PenException($"replace: target \"{Out.Field(target)}\" doesn't exist: pen only replaces " +
                    "a file that's there (Docker would create an empty one in its place)");
            if (status.Value.IsSymlink)
            {
                throw new PenException($"replace: target \"{Out.Field(target)}\": {Format(path)} is a symlink, " +
                    "which pen doesn't follow here: name the file it leads to instead");
            }
        }
        if (status is not { IsRegularFile: true })
            throw new PenException($"replace: target \"{Out.Field(target)}\" isn't a regular file");
        var real = Native.RealPath(path)
            ?? throw new PenException($"replace: target \"{Out.Field(target)}\" changed while pen looked at it");
        if (!IsHideable(real))
            throw OutsideProject(target);
        return (real, status.Value);
    }

    /// <summary>Refuses a [replace] target that's off limits.</summary>
    private static PenException OutsideProject(string target) =>
        new($"replace: target \"{Out.Field(target)}\" must be a file in the project, outside .git and .pen");

    /// <summary>Reads a [replace] source, never through a symlink, refusing one that hide hides.</summary>
    /// <param name="target">The entry's target, for messages.</param>
    /// <param name="source">The source, as written in [replace].</param>
    /// <param name="hidden">What hide found, by device and inode (<see cref="FindHiddenIdentities"/>).</param>
    /// <exception cref="PenException">It can't be read, or it's hidden, naming the entry.</exception>
    /// <remarks>
    /// [replace] can be in .pen/, which the agent can write, so a hidden source would let it show itself what a
    /// user's hide keeps out. A source is hidden if a pattern matches it or a directory on its way, or if what was
    /// read, or a directory it was read through, is something hide found: compared by device and inode from the
    /// descriptors read, that holds for another spelling (case, on a folding file system), a hard link, a directory
    /// hidden through a symlink, and a file moved there after the walk.
    /// </remarks>
    private ProjectFile ReadSource(string target, string source, Dictionary<(ulong, ulong), string> hidden)
    {
        var parts = source.Split('/');
        if (parts.Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            throw new PenException($"replace: source \"{Out.Field(source)}\" (for \"{Out.Field(target)}\") can't " +
                "be in .git");
        }

        for (var count = 1; count <= parts.Length; count++)
        {
            var isDirectory = count < parts.Length;
            var prefix = string.Join('/', parts[..count]);
            if (FindMatch(prefix, isDirectory) is { } pattern)
            {
                var shown = Out.Field(prefix) + (isDirectory ? "/" : "");
                throw HiddenSource(target, source, $"hide \"{Out.Field(pattern.Text)}\" matches {shown}");
            }
        }

        ProjectFile file;
        try
        {
            file = Native.ReadInsideProject(_projectDir, source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PenException($"replace: source \"{Out.Field(source)}\" (for \"{Out.Field(target)}\") can't " +
                "be used: " + Out.Field(exception.Message));
        }

        for (var index = 0; index < file.Steps.Count; index++)
        {
            var step = file.Steps[index];
            if (!hidden.TryGetValue((step.Device, step.Inode), out var path))
                continue;
            var why = index == file.Steps.Count - 1
                ? $"it's {Format(path)}, which hide hides"
                : $"it's in {Format(path)}/, which hide hides whole";
            throw HiddenSource(target, source, why);
        }

        // What it's hidden by is only known for the names the walk saw, and it never looks in a hidden directory.
        if (file.Steps[^1].Links > 1)
        {
            throw new PenException($"replace: source \"{Out.Field(source)}\" (for \"{Out.Field(target)}\") has " +
                "another name (a hard link), so pen can't tell whether hide hides it: use a copy instead");
        }

        return file;
    }

    /// <summary>Refuses a [replace] source that hide hides.</summary>
    /// <param name="target">The entry's target.</param>
    /// <param name="source">The entry's source.</param>
    /// <param name="why">How it's hidden, already safe to print.</param>
    private static PenException HiddenSource(string target, string source, string why) =>
        new($"replace: source \"{Out.Field(source)}\" (for \"{Out.Field(target)}\") is hidden: {why}, and " +
            "[replace] can't show the agent what hide keeps from it");

    /// <summary>Formats the refusal of a plan that needs more than <see cref="MaxMounts"/> mounts.</summary>
    private static string FormatTooMany(int count, IEnumerable<Found> hidden)
    {
        var message = $"hide and [replace] need {count} mounts, and pen allows at most {MaxMounts}";
        var top = hidden.GroupBy(found => found.Pattern).MaxBy(group => group.Count());
        if (top is not null)
            message += $" (hide \"{Out.Field(top.Key.Text)}\" matches {top.Count()})";
        return message + ": hide the directory the files are in instead (secrets/ rather than secrets/*.env)";
    }

    /// <summary>Gets a path's path in the project, as it is: not safe to print.</summary>
    private string Relative(string path) => path == _projectDir ? "" : path[(_projectDir.TrimEnd('/').Length + 1)..];

    /// <summary>Gets a path's path in the project, safe to print (<see cref="Out.Field"/>).</summary>
    private string Format(string path) => Out.Field(Relative(path));

    /// <summary>Lists found paths as masked paths, sorted by their path in the project.</summary>
    private List<MaskedPath> ToMasked(Dictionary<string, Found> paths) =>
        [.. paths.Select(path => new MaskedPath(path.Key, Relative(path.Key), path.Value.Device, path.Value.Inode))
            .OrderBy(masked => masked.RelativePath, StringComparer.Ordinal)];
}
