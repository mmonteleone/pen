using System.IO.Enumeration;
using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>Finds the repos inside a project and the submodule repos in a git dir, without running git.</summary>
/// <remarks>
/// The nested-repo scan is shared by the start-of-session refusal and the session-end check. It only lists names
/// (one readdir per directory) and never follows symlinks, so a large node_modules stays well under a second.
/// </remarks>
internal static class RepoScan
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0, // dot files are "hidden" on Unix, and .git is one
        IgnoreInaccessible = false, // a directory that can't be listed has to be noticed
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>One name in a directory listing, as the listing reports it (no symlink followed).</summary>
    private readonly record struct Entry(string Name, bool IsDir, bool IsLink);

    /// <summary>
    /// Scans the tree below <paramref name="root"/> for repos, plus a bare layout in the root itself, and for symlinks
    /// into the git dir.
    /// </summary>
    /// <param name="root">The project dir, a real path. Its own .git is the project's and is skipped.</param>
    /// <param name="commonGitDir">
    /// The project's common git dir (root/.git, or for a linked worktree the main checkout's): worktrees of it below
    /// the root are the project's own.
    /// </param>
    /// <param name="moduleRepos">
    /// The module repos whose checkouts are the project's own too (see <see cref="ResolveModuleCheckout"/>).
    /// </param>
    /// <param name="tryOpen">
    /// If given, gets each directory that can be entered but not listed and returns whether it changed its mode,
    /// after which the listing is tried again.
    /// </param>
    /// <param name="from">
    /// Where to walk from instead of the root: directories below it, such as a nested .git directory just moved
    /// aside, whose contents the walk that found it didn't enter.
    /// </param>
    /// <returns>
    /// The repos found, the directories it couldn't list although they can be entered (chmod 311), the entries
    /// with names that aren't valid UTF-8 that could hold a repo, and the symlinks that lead into the git dir.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Names compare case-insensitively, because on the Mac's file system .GIT is .git.
    /// </para>
    /// <para>
    /// Host git could run in a repo in a directory that can't be listed, so callers must not treat the scan as
    /// complete when there are any. One that can't be entered at all (a root-owned 700 database directory) isn't
    /// reported, since host git can't get in either. The access(2) call is only made after a listing fails, so a
    /// healthy tree pays nothing for it. Likewise, only a listed name with U+FFFD in it costs a look at the
    /// directory's raw names. A plain file with such a name can't hold a repo, so it's left alone.
    /// </para>
    /// <para>
    /// A symlink is never followed, but one that leads to a directory at or inside the git dir, or would once a
    /// directory it names is made, is reported whatever its name, a .git symlink included (not as a repo, then)
    /// (<see cref="GitDirLinkCheck"/>): git run through it starts in the git dir. One Pen can't trace is reported
    /// apart, as it may lead there. What's there is the walk of the
    /// git dir's to find (<see cref="ScanGitDir"/>); this is a second line, against a repo put there after that walk
    /// (by a commit that makes objects/ab, say) or that the walk couldn't see. A symlink committed on a branch only
    /// arrives at checkout, so only the walk of the git dir covers that.
    /// </para>
    /// <para>
    /// A nested .git directory isn't entered: it's reported, and the end check walks it once it's moved aside.
    /// </para>
    /// </remarks>
    public static NestedScan ScanNested(
        string root,
        string commonGitDir,
        IReadOnlyCollection<string> moduleRepos,
        Func<string, bool>? tryOpen,
        IEnumerable<string>? from = null)
    {
        var modules = new HashSet<string>(moduleRepos, StringComparer.Ordinal);
        var links = GitDirLinkCheck.ForProject(root, commonGitDir);
        return new NestedWalk(root, commonGitDir, modules, tryOpen, links).Run(from ?? [root]);
    }

    /// <summary>
    /// Scans all of <paramref name="gitDir"/> for repos where git keeps none: anything that makes git, run from a
    /// directory in it, use a repo other than the git dir's own.
    /// </summary>
    /// <param name="gitDir">The common git dir.</param>
    /// <param name="moduleRepos">
    /// The module repos kept in it, which are its own (<see cref="FindModuleRepos"/>).
    /// </param>
    /// <param name="tryOpen">As for <see cref="ScanNested"/>.</param>
    /// <param name="from">
    /// Where to walk from instead of the git dir: directories in it, such as a .git directory just moved aside.
    /// </param>
    /// <returns>
    /// What <see cref="ScanNested"/> returns, with no symlinks: the repos found, the directories it couldn't list and
    /// the names that aren't valid UTF-8 that could hold a repo.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Git, started in a directory, looks there for a .git, then takes the directory itself for a git dir if it has
    /// the layout (is_git_directory), then does the same in each parent. Started inside a git dir, the first one it
    /// meets is the git dir's own only if nothing is put in the way. So, walking the git dir as
    /// <see cref="ScanNested"/> walks the tree, a .git anywhere in it is a repo, its own .git included, under the
    /// same rules: a .git file that's a validated worktree of the git dir (git-subrepo makes those under tmp/) or
    /// a known submodule's checkout is allowed. A directory with a git dir's layout is a repo unless it's one of the
    /// git dir's own (<see cref="NestedWalk"/>'s IsOwnGitDir): the git dir, a module repo, or a worktree entry of
    /// either whose commondir leads back to it.
    /// </para>
    /// <para>
    /// Nothing is skipped, by place or by name: git can be started anywhere in the git dir, through a symlink that
    /// arrives later (committed on a branch, it comes with a checkout), or one that leads nowhere until a commit
    /// makes the directory it names. That includes the object stores, objects/, lfs/objects/ and annex/objects/, so
    /// a git dir with many Git LFS or git-annex objects takes longer to walk (a little over a second for 50,000
    /// directories), and what Pen put aside (named …<see cref="GitCheck.Suffix"/>…): its layout was broken when it
    /// was moved, so only a repo still inside one is reported. Symlinks in the git dir aren't followed: what they
    /// lead to in it is walked anyway.
    /// </para>
    /// </remarks>
    public static NestedScan ScanGitDir(
        string gitDir,
        IReadOnlyCollection<string> moduleRepos,
        Func<string, bool>? tryOpen,
        IEnumerable<string>? from = null)
    {
        var modules = new HashSet<string>(moduleRepos, StringComparer.Ordinal);
        return new NestedWalk(gitDir, gitDir, modules, tryOpen, links: null).Run(from ?? [gitDir]);
    }

    private static bool HasReplacement(string name) => name.Contains('\uFFFD');

    /// <summary>Lists <paramref name="dir"/>'s entries without following symlinks.</summary>
    /// <returns>The entries, or null if the directory can't be listed.</returns>
    private static List<Entry>? ListEntries(string dir)
    {
        try
        {
            return new FileSystemEnumerable<Entry>(dir,
                (ref FileSystemEntry e) =>
                    new Entry(e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                Options).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsNamed(string name, string wanted) =>
        name.Equals(wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the common git dir of the linked worktree whose .git file is <paramref name="gitFile"/>.
    /// </summary>
    /// <returns>
    /// The common git dir (the main checkout's .git, or a bare repo), or null unless it has exactly the layout
    /// `git worktree add` makes:
    /// <list type="bullet">
    /// <item>the file says "gitdir: &lt;common&gt;/worktrees/&lt;name&gt;", and that's where git resolves it: the
    /// real path of the value as written is that path, with no symlink in it;</item>
    /// <item>&lt;name&gt;/commondir is "../..";</item>
    /// <item>&lt;name&gt;/gitdir points back to this .git file.</item>
    /// </list>
    /// </returns>
    /// <remarks>Reads the files directly; never runs git.</remarks>
    internal static string? ResolveWorktree(string gitFile)
    {
        try
        {
            if (RepoGuard.IsLink(gitFile))
                return null;
            var entry = ReadGitFileTarget(gitFile, requireReal: true);
            if (entry is null)
                return null;
            var worktrees = Path.GetDirectoryName(entry);
            if (worktrees is null || Path.GetFileName(worktrees) != "worktrees")
                return null;
            var common = Path.GetDirectoryName(worktrees);
            if (common is null || common == "/")
                return null;
            if (ReadPathFile(Path.Combine(entry, "commondir"), "") != "../..")
                return null;
            return PointsBackTo(entry, gitFile) ? common : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether the worktree entry's gitdir file names <paramref name="gitFile"/>.</summary>
    private static bool PointsBackTo(string entry, string gitFile)
    {
        var back = ReadPathFile(Path.Combine(entry, "gitdir"), "");
        if (back is null)
            return false;
        if (!Path.IsPathRooted(back))
            back = Path.Combine(entry, back);
        var backReal = Native.RealPath(back);
        return backReal is not null && backReal == Native.RealPath(gitFile);
    }

    /// <summary>
    /// Whether the worktree whose entry is <paramref name="entry"/> (a &lt;git dir&gt;/worktrees/&lt;name&gt;) is
    /// inside <paramref name="dir"/>, by the path its gitdir file names.
    /// </summary>
    /// <returns>False for an entry without a usable gitdir file: it counts as outside.</returns>
    /// <remarks>
    /// A path that exists is classified by its real path, resolved as written (a symlink before a ".." is followed
    /// first). Otherwise, unlike <see cref="PointsBackTo"/>, this takes "." and ".." as text, then resolves what
    /// exists of the path and keeps the rest, so a worktree that's gone is still classified by where it was.
    /// </remarks>
    internal static bool IsWorktreeInside(string dir, string entry)
    {
        try
        {
            var gitdir = ReadPathFile(Path.Combine(entry, "gitdir"), "");
            if (gitdir is null)
                return false;
            if (!Path.IsPathRooted(gitdir))
                gitdir = Path.Combine(entry, gitdir);
            var real = Native.RealPath(gitdir) ?? RepoGuard.ResolveExisting(Path.GetFullPath(gitdir));
            return RepoGuard.IsInside(dir, real);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads the git dir a validated linked worktree's .git file names: &lt;common&gt;/worktrees/&lt;name&gt;.
    /// </summary>
    /// <returns>The entry's full path, or null if the file can't be read.</returns>
    internal static string? ReadWorktreeEntry(string gitFile)
    {
        try
        {
            return ReadGitFileTarget(gitFile, requireReal: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Finds the module repo whose checkout has <paramref name="gitFile"/> as its .git file.</summary>
    /// <param name="gitFile">A .git file in the project.</param>
    /// <param name="moduleRepos">The module repos whose checkouts are the project's own.</param>
    /// <returns>
    /// The module repo, or null unless it has the layout `git submodule` makes:
    /// <list type="bullet">
    /// <item>the file says "gitdir: &lt;repo&gt;", and that's where git resolves it (the real path of the value as
    /// written is that path, with no symlink in it), and &lt;repo&gt; is one of <paramref name="moduleRepos"/>;</item>
    /// <item>&lt;repo&gt;/config's core.worktree resolves back to the .git file's dir.</item>
    /// </list>
    /// </returns>
    /// <remarks>
    /// The repo's config and hooks are protected (and its list is the one taken at the session's start), so host git
    /// there runs nothing the sandbox wrote. Reads the files directly; never runs git.
    /// </remarks>
    internal static string? ResolveModuleCheckout(string gitFile, IReadOnlySet<string> moduleRepos)
    {
        try
        {
            if (moduleRepos.Count == 0 || RepoGuard.IsLink(gitFile))
                return null;
            var repo = ReadGitFileTarget(gitFile, requireReal: true);
            if (repo is null || !moduleRepos.Contains(repo))
                return null;
            return HasWorktreeAt(repo, Paths.GetParent(gitFile)) ? repo : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the core.worktree in <paramref name="repo"/>'s own config leads to <paramref name="checkout"/>.
    /// </summary>
    /// <returns>Whether it does; false if the config can't be read or parsed.</returns>
    private static bool HasWorktreeAt(string repo, string checkout)
    {
        var config = Path.Combine(repo, "config");
        if (RepoGuard.IsLink(config))
            return false;
        // An unreadable config: not a checkout Pen can vouch for.
        if (GitConfigFile.TryParseFile(config) is not { } entries)
            return false;
        var worktree = GitConfigFile.FindLast(entries, "core", "worktree");
        if (string.IsNullOrEmpty(worktree))
            return false;
        if (!Path.IsPathRooted(worktree))
            worktree = Path.Combine(repo, worktree); // git resolves it from the git dir
        // git chdirs into the git dir and resolves the value physically, so ".." after a symlink leaves its target.
        var worktreeReal = Native.RealPath(worktree);
        return worktreeReal is not null && worktreeReal == Native.RealPath(checkout);
    }

    /// <summary>Finds the submodule repos git keeps in <paramref name="gitDir"/>.</summary>
    /// <returns>The module repos, parents before their nested repos and siblings in ordinal order.</returns>
    /// <remarks>
    /// <para>
    /// That's every git dir under modules/ (a submodule's name may contain "/", so they can sit at any depth) and
    /// under worktrees/*/modules/ (a linked worktree's own submodules), and recursively those of each one found
    /// (nested submodules). A git dir is a directory with HEAD plus objects and refs, or HEAD plus commondir
    /// (which callers refuse), as <see cref="IsGitDirLayout(string)"/> takes them.
    /// </para>
    /// <para>
    /// Any symlink met along the way is listed as is and not followed: git would follow it to a repo elsewhere, so
    /// callers refuse it too. A directory Pen quarantined (named …<see cref="GitCheck.Suffix"/>…) isn't walked:
    /// repos nested in a quarantined one are no submodule's. Reads directories only; never runs git.
    /// </para>
    /// </remarks>
    internal static List<string> FindModuleRepos(string gitDir) => ScanModules(gitDir).Repos;

    /// <summary>Walks <paramref name="gitDir"/>'s submodule repos as <see cref="FindModuleRepos"/> does.</summary>
    /// <returns>
    /// The repos, with the directories it couldn't read (what's in those is unknown, so callers refuse or quarantine
    /// them) and every entry whose name isn't valid UTF-8, of any type.
    /// </returns>
    /// <remarks>
    /// Git keeps only directories with names it chose here, so callers refuse a bad name or report "couldn't check",
    /// because renaming the parent would take every submodule repo with it.
    /// </remarks>
    internal static ModuleScan ScanModules(string gitDir) => new ModuleWalk().Run(gitDir);

    /// <summary>
    /// Whether <paramref name="dir"/> has a git dir's layout, as git's is_git_directory takes it or more broadly.
    /// </summary>
    /// <remarks>
    /// Git wants a HEAD that's a symlink whose text starts with "refs/" (whatever it points to, a directory
    /// included) or a file reading as a ref or an object id, then objects and refs that pass access(X_OK), which an
    /// executable file does too; those of the commondir's target when there's a commondir. So: a HEAD that's a
    /// symlink or anything but a directory, and objects and refs of any type, or a commondir of any type. Git skips
    /// the objects check when GIT_OBJECT_DIRECTORY is set, which Pen doesn't follow: logs/ has HEAD and refs/ in
    /// every repo.
    /// </remarks>
    private static bool IsGitDirLayout(string dir) =>
        IsGitDirLayout(
            head: IsHead(Path.Combine(dir, "HEAD")),
            objects: RepoGuard.Exists(Path.Combine(dir, "objects")),
            refs: RepoGuard.Exists(Path.Combine(dir, "refs")),
            commondir: RepoGuard.Exists(Path.Combine(dir, "commondir")));

    /// <summary>Whether a git dir's layout is there, given which of its entries are.</summary>
    /// <param name="head">Whether there's a HEAD git could take for one (<see cref="IsHead(bool, bool)"/>).</param>
    /// <param name="objects">Whether there's an objects entry, of any type.</param>
    /// <param name="refs">Whether there's a refs entry, of any type.</param>
    /// <param name="commondir">Whether there's a commondir entry, of any type.</param>
    private static bool IsGitDirLayout(bool head, bool objects, bool refs, bool commondir) =>
        head && ((objects && refs) || commondir);

    /// <summary>Whether <paramref name="path"/> is a HEAD git could take for one: a symlink, or no directory.</summary>
    private static bool IsHead(string path) => RepoGuard.IsLink(path) || File.Exists(path);

    /// <summary>
    /// Whether a listed HEAD is one git could take for one: a symlink (to a directory too), or anything but a
    /// directory.
    /// </summary>
    private static bool IsHead(bool isDir, bool isLink) => isLink || !isDir;

    /// <summary>Reads the path a .git file's "gitdir: " line names, as a full path.</summary>
    /// <param name="gitFile">The .git file; a relative path in it is from the file's own dir.</param>
    /// <param name="requireReal">
    /// Whether the value as written must resolve, as git resolves it, to the path it reads as.
    /// </param>
    /// <returns>
    /// The full path, without a trailing slash, or null if the file doesn't name one, or if
    /// <paramref name="requireReal"/> and the value's real path isn't that path (nothing there, or a symlink in the
    /// way that leads elsewhere, such as "l/../x" with l a symlink).
    /// </returns>
    /// <remarks>
    /// Without <paramref name="requireReal"/>, the path is the value with "." and ".." taken as text, which is where
    /// git goes only when no symlink comes before a "..".
    /// </remarks>
    private static string? ReadGitFileTarget(string gitFile, bool requireReal)
    {
        var gitdir = ReadPathFile(gitFile, "gitdir: ");
        if (gitdir is null)
            return null;
        if (!Path.IsPathRooted(gitdir))
            gitdir = Path.Combine(Paths.GetParent(gitFile), gitdir);
        var full = Path.GetFullPath(gitdir).TrimEnd('/');
        if (!requireReal)
            return full;

        // git opens the value as written and takes its realpath, following any
        // symlink before a later "..", so resolve that, not the text form.
        return Native.RealPath(gitdir) == full ? full : null;
    }

    /// <summary>Reads a one-line file's value after <paramref name="prefix"/>.</summary>
    /// <returns>
    /// The value, or null if the file doesn't have that shape, starts with a byte order mark, isn't valid UTF-8, or
    /// isn't a regular file (a symlink isn't followed) of a reasonable size.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The read itself (<see cref="ReadPathText"/>) doesn't follow a symlink, so one swapped in after the check
    /// isn't read through; the check stays because O_NOFOLLOW's value isn't known on every architecture.
    /// </para>
    /// <para>
    /// Git never writes a byte order mark here, and reads one as part of the file: a gitdir or commondir file's path
    /// then starts with U+FEFF, so it's relative to the file's dir, through a name only the sandbox would have made
    /// ("\uFEFF../.." is the file's dir itself once a directory named "\uFEFF.." is there). Rather than classify a
    /// worktree by that path (<see cref="IsWorktreeInside"/>), the file is refused.
    /// </para>
    /// </remarks>
    private static string? ReadPathFile(string path, string prefix)
    {
        if (!File.Exists(path) || RepoGuard.IsLink(path))
            return null;
        var text = ReadPathText(path);
        if (text is null || text.StartsWith('\uFEFF') || !text.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var value = text[prefix.Length..].TrimEnd('\n', '\r');
        return value.Length == 0 || value.Contains('\n') || value.Contains('\0') ? null : value;
    }

    /// <summary>Reads a path file's text, never through a symlink at <paramref name="path"/>.</summary>
    /// <returns>The text, or null if it isn't valid UTF-8, or isn't a regular file of a reasonable size.</returns>
    /// <remarks>
    /// Git reads these files as bytes, so the text is exactly those bytes as UTF-8: a byte order mark stays, as
    /// U+FEFF, rather than being skipped or picking another encoding, so a value git reads differently doesn't match
    /// the one Pen expects. Bytes that aren't valid UTF-8 would decode to U+FFFD, naming a different path from the
    /// one git reads, so the file is unreadable instead.
    /// </remarks>
    internal static string? ReadPathText(string path)
    {
        if (Native.ReadProtected(path) is not { } file)
            return null;
        return StrictUtf8.DecodeOrNull(file.Bytes);
    }

    /// <summary>One walk of a project's tree, or of its git dir, collecting what it finds.</summary>
    /// <param name="root">The project dir, or the git dir.</param>
    /// <param name="commonGitDir">The project's common git dir; the git dir itself when walking that.</param>
    /// <param name="moduleRepos">The module repos whose checkouts are the project's own.</param>
    /// <param name="tryOpen">See <see cref="ScanNested"/>.</param>
    /// <param name="links">
    /// What decides whether a symlink in the tree leads into the git dir, or null in a walk of the git dir or of a
    /// tree with no git dir: then symlinks aren't looked at. One in the git dir isn't followed, as what it leads to
    /// there is walked anyway, and what it leads to in the tree is the tree walk's.
    /// </param>
    /// <remarks>
    /// In a walk of the git dir (<see cref="ScanGitDir"/>), the root's own .git counts and the git dir's own git dirs
    /// aren't repos; nothing is skipped.
    /// </remarks>
    private sealed class NestedWalk(
        string root,
        string commonGitDir,
        IReadOnlySet<string> moduleRepos,
        Func<string, bool>? tryOpen,
        GitDirLinkCheck? links)
    {
        private readonly List<NestedRepo> _repos = [];
        private readonly List<string> _unlistable = [];
        private readonly List<BadName> _badNames = [];
        private readonly List<string> _gitDirLinks = [];
        private readonly List<string> _untracedLinks = [];
        private readonly Stack<string> _pending = new();

        /// <summary>Whether this is a walk of the git dir: the root is the common git dir.</summary>
        private readonly bool _insideGitDir = root == commonGitDir;

        /// <summary>Walks from each of <paramref name="starts"/>: the root, or directories below it.</summary>
        /// <returns>What it found, each list in ordinal order.</returns>
        public NestedScan Run(IEnumerable<string> starts)
        {
            foreach (var start in starts)
            {
                _pending.Push(start);
                while (_pending.TryPop(out var dir))
                    ScanDirectory(dir);
            }
            return Result();
        }

        private NestedScan Result()
        {
            var repos = _repos.DistinctBy(r => r.Path, StringComparer.Ordinal).ToList();
            repos.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            var unlistable = _unlistable.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var badNames = _badNames.DistinctBy(n => n.DisplayPath, StringComparer.Ordinal).ToList();
            badNames.Sort((a, b) => string.CompareOrdinal(a.DisplayPath, b.DisplayPath));
            _gitDirLinks.Sort(StringComparer.Ordinal);
            _untracedLinks.Sort(StringComparer.Ordinal);
            return new(repos, unlistable, badNames, _gitDirLinks, _untracedLinks);
        }

        /// <summary>Notes the repos in one directory and queues its subdirectories.</summary>
        private void ScanDirectory(string dir)
        {
            var entries = ListOrOpen(dir);
            if (entries is null)
                return;
            var undecodable = FindUndecodable(dir, entries);
            if (undecodable is null)
                return;
            if (ScanEntries(dir, entries, undecodable) && !(_insideGitDir && IsOwnGitDir(dir)))
            {
                // The HEAD that counts, git's own spelling first: renaming it breaks the layout.
                var head = entries.Where(e => IsNamed(e.Name, "HEAD") && IsHead(e.IsDir, e.IsLink))
                    .OrderBy(e => e.Name != "HEAD").First().Name;
                AddRepo(dir, Path.Combine(dir, head), NestedKind.Bare);
            }
        }

        /// <summary>Notes the repo in <paramref name="dir"/> that <paramref name="path"/> makes.</summary>
        private void AddRepo(string dir, string path, NestedKind kind) =>
            _repos.Add(new(path, kind, _insideGitDir ? FindPlace(dir) : GitDirPlace.Elsewhere));

        /// <summary>Where <paramref name="dir"/>, in the git dir, is, for the advice about a repo there.</summary>
        private GitDirPlace FindPlace(string dir)
        {
            for (var at = dir;
                 at is not null && at != root && RepoGuard.IsInside(root, at);
                 at = Path.GetDirectoryName(at))
            {
                var parent = Path.GetDirectoryName(at);
                if (parent is not null && Path.GetFileName(at) is "refs" or "logs" && IsOwnGitDir(parent))
                    return GitDirPlace.Refs;
            }
            var worktrees = Path.GetDirectoryName(dir);
            if (worktrees is not null && Path.GetFileName(worktrees) == "worktrees"
                && Path.GetDirectoryName(worktrees) is { } owner && IsOwnRepo(owner))
            {
                return GitDirPlace.WorktreeEntry;
            }
            return GitDirPlace.Elsewhere;
        }

        /// <summary>
        /// Whether <paramref name="dir"/>, in the git dir, is one of its own git dirs: the git dir itself, a module
        /// repo kept in it, or a worktree entry of either whose commondir leads back to it, so git started there
        /// reads the config and hooks of one of those.
        /// </summary>
        /// <remarks>
        /// The commondir is resolved as git resolves it: a relative value from the entry, then the real path of that
        /// as written, so ".." after a symlink leaves the symlink's target. <c>git worktree add</c> writes "../..";
        /// libgit2 writes the common dir's full path, with a trailing slash. A file git would read differently (a
        /// symlink, a byte order mark, bytes that aren't UTF-8) leads nowhere Pen can vouch for.
        /// </remarks>
        private bool IsOwnGitDir(string dir)
        {
            if (IsOwnRepo(dir))
                return true;
            var worktrees = Path.GetDirectoryName(dir);
            if (worktrees is null || Path.GetFileName(worktrees) != "worktrees")
                return false;
            var owner = Path.GetDirectoryName(worktrees);
            if (owner is null || !IsOwnRepo(owner))
                return false;
            var common = ReadPathFile(Path.Combine(dir, "commondir"), "");
            if (common is null)
                return false;
            var real = Native.RealPath(Path.IsPathRooted(common) ? common : Path.Combine(dir, common));
            return real is not null && real == Native.RealPath(owner);
        }

        /// <summary>Whether <paramref name="dir"/> is the git dir or a module repo kept in it.</summary>
        private bool IsOwnRepo(string dir) => dir == root || moduleRepos.Contains(dir);

        /// <summary>
        /// Notes <paramref name="link"/>, a symlink in the tree, if it leads into the git dir, or would once a
        /// directory it names is made, or if Pen can't tell where it leads, whatever its name.
        /// </summary>
        /// <returns>Whether it was noted.</returns>
        private bool NoteLink(string link)
        {
            switch (links?.Trace(link))
            {
                case GitDirLinkCheck.Lead.Inside:
                    _gitDirLinks.Add(link);
                    return true;
                case GitDirLinkCheck.Lead.Unknown:
                    _untracedLinks.Add(link);
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Lists <paramref name="dir"/>, trying to open it once if it can be entered but not listed.</summary>
        /// <returns>
        /// The entries, or null if there's nothing to scan: the directory is gone, shut or noted unlistable.
        /// </returns>
        private List<Entry>? ListOrOpen(string dir)
        {
            var entries = ListEntries(dir);
            if (entries is not null)
                return entries;
            // Gone, or shut: nothing host git could reach.
            if (Native.IsShutOrGone(dir))
                return null;
            if (tryOpen is not null && tryOpen(dir))
                entries = ListEntries(dir);
            if (entries is null)
                _unlistable.Add(dir);
            return entries;
        }

        /// <summary>
        /// Notes the entries of <paramref name="dir"/> whose names aren't valid UTF-8 and could hold a repo.
        /// </summary>
        /// <returns>
        /// The names of all its undecodable entries as listed, or null, noting the directory unlistable, if its raw
        /// names can't be read.
        /// </returns>
        private HashSet<string>? FindUndecodable(string dir, List<Entry> entries)
        {
            HashSet<string> undecodable = [];
            if (!entries.Any(e => HasReplacement(e.Name)))
                return undecodable;
            var raw = Native.ListUndecodable(dir);
            if (raw is null)
            {
                _unlistable.Add(dir);
                return null;
            }
            foreach (var entry in raw)
            {
                undecodable.Add(entry.DecodedName);
                if (entry.CouldHoldRepo)
                    _badNames.Add(BadName.FromEntry(dir, entry));
            }
            return undecodable;
        }

        /// <summary>
        /// Notes each .git in <paramref name="dir"/> and, in the tree, each symlink into the git dir, and queues its
        /// subdirectories.
        /// </summary>
        /// <returns>
        /// Whether <paramref name="dir"/> itself has a git dir's layout (<see cref="IsGitDirLayout(string)"/>).
        /// </returns>
        private bool ScanEntries(string dir, List<Entry> entries, HashSet<string> undecodable)
        {
            var head = false;
            var objects = false;
            var refs = false;
            var commondir = false;
            foreach (var entry in entries)
            {
                var path = Path.Combine(dir, entry.Name);
                // Unless a real entry has the name it decodes to, which is scanned as usual.
                if (undecodable.Contains(entry.Name) && !Path.Exists(path))
                    continue;
                if (IsNamed(entry.Name, ".git"))
                {
                    NoteGitEntry(dir, entry, path);
                    continue; // a .git's contents belong to that repo
                }
                // Of any type, as IsGitDirLayout says.
                if (IsNamed(entry.Name, "HEAD"))
                    head |= IsHead(entry.IsDir, entry.IsLink);
                else if (IsNamed(entry.Name, "objects"))
                    objects = true;
                else if (IsNamed(entry.Name, "refs"))
                    refs = true;
                else if (IsNamed(entry.Name, "commondir"))
                    commondir = true;

                if (entry.IsLink)
                    NoteLink(path);
                else if (entry.IsDir)
                    _pending.Push(path);
            }
            return IsGitDirLayout(head, objects, refs, commondir);
        }

        /// <summary>
        /// Notes the .git at <paramref name="path"/> unless it's the project's own, a worktree of it or a known
        /// submodule's checkout. In the tree, a .git symlink that leads into the git dir is noted as such a symlink.
        /// </summary>
        private void NoteGitEntry(string dir, Entry entry, string path)
        {
            if (dir == root && !_insideGitDir)
                return; // the project's own; never descended into
            if (entry.IsLink)
            {
                if (!NoteLink(path))
                    AddRepo(dir, path, NestedKind.GitLink);
            }
            else if (entry.IsDir)
            {
                AddRepo(dir, path, NestedKind.GitDir);
            }
            else if (ResolveWorktree(path) != commonGitDir && ResolveModuleCheckout(path, moduleRepos) is null)
            {
                AddRepo(dir, path, NestedKind.GitFile);
            }
        }
    }

    /// <summary>One walk of a git dir's submodule repos, collecting what it finds.</summary>
    private sealed class ModuleWalk
    {
        private readonly List<string> _repos = [];
        private readonly List<string> _unreadable = [];
        private readonly List<BadName> _badNames = [];

        /// <summary>Walks the submodule repos in <paramref name="gitDir"/>, recursively.</summary>
        public ModuleScan Run(string gitDir)
        {
            AddModuleRepos(gitDir);
            return new(_repos, _unreadable, _badNames);
        }

        /// <summary>
        /// Adds the module repos in <paramref name="gitDir"/>'s modules/ and its worktrees' modules/.
        /// </summary>
        private void AddModuleRepos(string gitDir)
        {
            WalkModules(Path.Combine(gitDir, "modules"));
            var worktrees = Path.Combine(gitDir, "worktrees");
            if (RepoGuard.IsLink(worktrees) || !Directory.Exists(worktrees))
                return; // a symlinked worktrees/ is refused by the mounts
            var entries = ListOrNote(worktrees);
            if (entries is null)
                return;
            var undecodable = NoteBadNames(worktrees, entries);
            if (undecodable is null)
                return;
            foreach (var worktree in entries)
            {
                if (!undecodable.Contains(Path.GetFileName(worktree)) && !RepoGuard.IsLink(worktree))
                    WalkModules(Path.Combine(worktree, "modules"));
            }
        }

        /// <summary>
        /// Adds the module repos at or below <paramref name="dir"/>, a modules/ dir or a level in one.
        /// </summary>
        private void WalkModules(string dir)
        {
            if (RepoGuard.IsLink(dir))
            {
                _repos.Add(dir);
                return;
            }
            if (!Directory.Exists(dir))
                return;
            var entries = ListOrNote(dir);
            if (entries is null)
                return;
            var undecodable = NoteBadNames(dir, entries);
            if (undecodable is null)
                return;
            foreach (var entry in entries)
            {
                // Unless a real entry has the name it decodes to, which is walked as usual.
                if (undecodable.Contains(Path.GetFileName(entry)) && !Path.Exists(entry))
                    continue;
                if (!WalkModuleEntry(dir, entry))
                    return;
            }
        }

        /// <summary>Adds the module repos at or below one entry of a modules/ level.</summary>
        /// <returns>False if <paramref name="dir"/> turned out unreadable, which ends its walk.</returns>
        private bool WalkModuleEntry(string dir, string entry)
        {
            // Before the symlink test, so a quarantined symlink isn't flagged and renamed again.
            if (Path.GetFileName(entry).Contains(GitCheck.Suffix, StringComparison.Ordinal))
                return true;
            if (RepoGuard.IsLink(entry))
            {
                _repos.Add(entry);
            }
            else if (!Path.Exists(entry))
            {
                // Listed but not there to stat: dir can be read but not entered.
                _unreadable.Add(dir);
                return false;
            }
            else if (!Directory.Exists(entry))
            {
                return true; // git keeps only directories here
            }
            else if (IsGitDirLayout(entry))
            {
                _repos.Add(entry);
                AddModuleRepos(entry);
            }
            else
            {
                WalkModules(entry); // a level of a name with "/" in it
            }
            return true;
        }

        /// <summary>Notes the entries of <paramref name="dir"/> whose names aren't valid UTF-8.</summary>
        /// <returns>
        /// Their names as listed, or null, noting the directory unreadable, if its raw names can't be read.
        /// </returns>
        private HashSet<string>? NoteBadNames(string dir, List<string> entries)
        {
            if (!entries.Any(e => HasReplacement(Path.GetFileName(e))))
                return [];
            var raw = Native.ListUndecodable(dir);
            if (raw is null)
            {
                _unreadable.Add(dir);
                return null;
            }
            _badNames.AddRange(raw.Select(entry => BadName.FromEntry(dir, entry)));
            return raw.Select(entry => entry.DecodedName).ToHashSet(StringComparer.Ordinal);
        }

        /// <summary>Lists <paramref name="dir"/>'s entries in ordinal order.</summary>
        /// <returns>The entries; none if it's gone; null, noting it unreadable, if it can't be read.</returns>
        private List<string>? ListOrNote(string dir)
        {
            try
            {
                return [.. Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal)];
            }
            catch (DirectoryNotFoundException)
            {
                return [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _unreadable.Add(dir);
                return null;
            }
        }
    }
}

/// <summary>What the walk of a project's tree, or of its git dir, found.</summary>
/// <param name="Repos">The repos below the root, in ordinal order of their paths.</param>
/// <param name="UnlistableDirs">The directories it could enter but not list, in ordinal order.</param>
/// <param name="BadNames">The entries it couldn't name that could hold a repo, in ordinal order.</param>
/// <param name="GitDirLinks">
/// The symlinks in the tree that lead into the common git dir, or would once a directory they name is made, in
/// ordinal order; none in a walk of the git dir.
/// </param>
/// <param name="UntracedLinks">
/// The symlinks in the tree that Pen can't tell lead elsewhere (<see cref="GitDirLinkCheck.Lead.Unknown"/>), in
/// ordinal order; none in a walk of the git dir.
/// </param>
internal sealed record NestedScan(
    List<NestedRepo> Repos,
    List<string> UnlistableDirs,
    List<BadName> BadNames,
    List<string> GitDirLinks,
    List<string> UntracedLinks);

/// <summary>A repo below the root of a walk: the project dir, or its git dir.</summary>
/// <param name="Path">The entry that makes it one: the .git, or a bare repo's HEAD.</param>
/// <param name="Kind">What sort of entry <paramref name="Path"/> is.</param>
/// <param name="Place">Where in the git dir it is, for the advice; elsewhere for one in the tree.</param>
internal sealed record NestedRepo(string Path, NestedKind Kind, GitDirPlace Place = GitDirPlace.Elsewhere)
{
    /// <summary>Gets the repo's directory, for messages.</summary>
    public string RepoDir => Paths.GetParent(Path);
}

/// <summary>What makes a directory below the project root a repo.</summary>
internal enum NestedKind
{
    /// <summary>A .git directory: a repo with its own config and hooks.</summary>
    GitDir,
    /// <summary>A .git file (gitfile) that isn't one of the project's own worktrees or submodule checkouts.</summary>
    GitFile,
    /// <summary>A .git symlink.</summary>
    GitLink,
    /// <summary>
    /// A directory git takes for a bare repo: HEAD plus objects and refs, or HEAD plus commondir (as
    /// <see cref="RepoScan"/>'s IsGitDirLayout takes them).
    /// </summary>
    Bare,
}

/// <summary>Where in the git dir a repo the walk of it found is, for the advice about it.</summary>
internal enum GitDirPlace
{
    /// <summary>Nowhere in particular.</summary>
    Elsewhere,
    /// <summary>
    /// In refs/ or logs/ of the git dir or one of its own: ref names (a branch named HEAD beside ones under objects/
    /// and refs/, say) lay it out as a git dir.
    /// </summary>
    Refs,
    /// <summary>
    /// In an entry of the git dir's or a module repo's worktrees/: as a repo, one whose commondir doesn't lead back.
    /// </summary>
    WorktreeEntry,
}

/// <summary>What the walk of a git dir's submodule repos found.</summary>
/// <param name="Repos">The module repos, parents before their nested repos.</param>
/// <param name="UnreadableDirs">The directories it couldn't read, so what's in them is unknown.</param>
/// <param name="BadNames">The entries whose names aren't valid UTF-8, of any type.</param>
internal sealed record ModuleScan(List<string> Repos, List<string> UnreadableDirs, List<BadName> BadNames);

/// <summary>An entry a scan can't look inside, since its name isn't valid UTF-8.</summary>
/// <param name="DisplayPath">
/// The entry's path for messages, with its name exact (<see cref="UndecodableEntry.EscapedName"/>).
/// </param>
/// <param name="EndsInNewline">Whether its name ends in a newline, so no command Pen could print would name it.</param>
/// <remarks>
/// .NET's listing has U+FFFD in place of the bad bytes, so that string names nothing (or another entry), while git
/// on this machine can still enter it.
/// </remarks>
internal sealed record BadName(string DisplayPath, bool EndsInNewline)
{
    /// <summary>Creates the bad name for <paramref name="entry"/>, listed in <paramref name="dir"/>.</summary>
    public static BadName FromEntry(string dir, UndecodableEntry entry) =>
        new(Path.Combine(dir, entry.EscapedName), entry.EndsInNewline);
}
