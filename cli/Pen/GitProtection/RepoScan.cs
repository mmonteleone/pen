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

    /// <summary>Finds every repo below <paramref name="root"/>, the project dir as a real path.</summary>
    /// <returns>
    /// The repos <see cref="ScanNested(string, string)"/> finds, with root/.git as the common git dir.
    /// </returns>
    public static List<NestedRepo> FindNested(string root) =>
        ScanNested(root, Path.Combine(root, ".git")).Repos;

    /// <summary>
    /// Finds every repo below <paramref name="root"/>, the project dir as a real path, taking the checkouts of
    /// <paramref name="moduleRepos"/> as the project's own.
    /// </summary>
    /// <returns>The repos the scan finds, with root/.git as the common git dir and no directory opened.</returns>
    public static List<NestedRepo> FindNested(string root, IReadOnlyCollection<string> moduleRepos) =>
        ScanNested(root, Path.Combine(root, ".git"), moduleRepos, tryOpen: null).Repos;

    /// <summary>
    /// Scans the tree below <paramref name="root"/> for repos, taking the checkouts of the submodule repos now in
    /// <paramref name="commonGitDir"/> (other than symlinks) as the project's own.
    /// </summary>
    /// <param name="root">The project dir, a real path.</param>
    /// <param name="commonGitDir">
    /// The project's common git dir: root/.git, or for a linked worktree the main checkout's.
    /// </param>
    /// <returns>What the walk found, as for the overload taking the module repos.</returns>
    public static NestedScan ScanNested(string root, string commonGitDir)
    {
        var moduleRepos = FindModuleRepos(commonGitDir).Where(repo => !IsLinkPath(repo)).ToList();
        return ScanNested(root, commonGitDir, moduleRepos, tryOpen: null);
    }

    /// <summary>
    /// Scans the tree below <paramref name="root"/> for repos, plus a bare layout in the root itself.
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
    /// <returns>
    /// The repos found, the directories it couldn't list although they can be entered (chmod 311), and the entries
    /// with names that aren't valid UTF-8 that could hold a repo.
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
    /// </remarks>
    public static NestedScan ScanNested(
        string root,
        string commonGitDir,
        IReadOnlyCollection<string> moduleRepos,
        Func<string, bool>? tryOpen)
    {
        var modules = new HashSet<string>(moduleRepos, StringComparer.Ordinal);
        return new NestedWalk(root, commonGitDir, modules, tryOpen).Run();
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

    /// <summary>Whether <paramref name="gitFile"/> is the .git file of a worktree of the project itself.</summary>
    /// <remarks>
    /// That's one made by `git worktree add` inside the project (Claude Code puts these in .claude/worktrees/):
    /// one whose common git dir is the project's own .git. Anything else could point host git at config and hooks
    /// the agent controls.
    /// </remarks>
    internal static bool IsProjectWorktree(string root, string gitFile) =>
        ResolveWorktree(gitFile) == Path.Combine(root, ".git");

    /// <summary>
    /// Finds the common git dir of the linked worktree whose .git file is <paramref name="gitFile"/>.
    /// </summary>
    /// <returns>
    /// The common git dir (the main checkout's .git, or a bare repo), or null unless it has exactly the layout
    /// `git worktree add` makes:
    /// <list type="bullet">
    /// <item>the file says "gitdir: &lt;common&gt;/worktrees/&lt;name&gt;", with no symlink anywhere along that
    /// path;</item>
    /// <item>&lt;name&gt;/commondir is "../..";</item>
    /// <item>&lt;name&gt;/gitdir points back to this .git file.</item>
    /// </list>
    /// </returns>
    /// <remarks>Reads the files directly; never runs git.</remarks>
    internal static string? ResolveWorktree(string gitFile)
    {
        try
        {
            if (IsLinkPath(gitFile))
                return null;
            var entry = ReadRealWorktreeEntry(gitFile);
            if (entry == null)
                return null;
            var worktrees = Path.GetDirectoryName(entry);
            if (worktrees == null || Path.GetFileName(worktrees) != "worktrees")
                return null;
            var common = Path.GetDirectoryName(worktrees);
            if (common == null || common == "/")
                return null;
            if (ReadPathFile(Path.Combine(entry, "commondir"), "") != "../..")
                return null;
            return PointsBackTo(entry, gitFile) ? common : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads the worktree entry a .git file names, requiring it to be a real path already.</summary>
    /// <returns>The entry's full path, or null if the file doesn't name one or a symlink is on the way.</returns>
    private static string? ReadRealWorktreeEntry(string gitFile)
    {
        var gitdir = ReadPathFile(gitFile, "gitdir: ");
        if (gitdir == null)
            return null;
        if (!Path.IsPathRooted(gitdir))
            gitdir = Path.Combine(Path.GetDirectoryName(gitFile)!, gitdir);

        // git resolves the gitdir with realpath, so a symlink anywhere along
        // it would lead elsewhere than it reads: require it to be real already.
        var full = Path.GetFullPath(gitdir).TrimEnd('/');
        var real = Native.RealPath(full);
        if (real == null || real != full)
            return null;
        return real;
    }

    /// <summary>Whether the worktree entry's gitdir file names <paramref name="gitFile"/>.</summary>
    private static bool PointsBackTo(string entry, string gitFile)
    {
        var back = ReadPathFile(Path.Combine(entry, "gitdir"), "");
        if (back == null)
            return false;
        if (!Path.IsPathRooted(back))
            back = Path.Combine(entry, back);
        var backReal = Native.RealPath(back);
        return backReal != null && backReal == Native.RealPath(gitFile);
    }

    /// <summary>
    /// Reads the git dir a validated linked worktree's .git file names: &lt;common&gt;/worktrees/&lt;name&gt;.
    /// </summary>
    /// <returns>The entry's full path, or null if the file can't be read.</returns>
    internal static string? ReadWorktreeEntry(string gitFile)
    {
        try
        {
            var gitdir = ReadPathFile(gitFile, "gitdir: ");
            if (gitdir == null)
                return null;
            if (!Path.IsPathRooted(gitdir))
                gitdir = Path.Combine(Path.GetDirectoryName(gitFile)!, gitdir);
            return Path.GetFullPath(gitdir).TrimEnd('/');
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
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
    /// <item>the file says "gitdir: &lt;repo&gt;", with no symlink anywhere along that path, and &lt;repo&gt; is one of
    /// <paramref name="moduleRepos"/>;</item>
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
            if (moduleRepos.Count == 0 || IsLinkPath(gitFile))
                return null;
            var gitdir = ReadPathFile(gitFile, "gitdir: ");
            if (gitdir == null)
                return null;
            var checkout = Path.GetDirectoryName(gitFile)!;
            if (!Path.IsPathRooted(gitdir))
                gitdir = Path.Combine(checkout, gitdir);
            var full = Path.GetFullPath(gitdir).TrimEnd('/');
            var real = Native.RealPath(full);
            if (real == null || real != full || !moduleRepos.Contains(real))
                return null;
            return HasWorktreeAt(real, checkout) ? real : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (PenException)
        {
            // An unreadable config: not a checkout Pen can vouch for.
            return null;
        }
    }

    /// <summary>
    /// Whether the core.worktree in <paramref name="repo"/>'s own config leads to <paramref name="checkout"/>.
    /// </summary>
    private static bool HasWorktreeAt(string repo, string checkout)
    {
        var config = Path.Combine(repo, "config");
        if (IsLinkPath(config))
            return false;
        var worktree = GitConfigFile.ParseFile(config)
            .Where(e => e.Section == "core" && e.Subsection == null && e.Key == "worktree")
            .Select(e => e.Value)
            .LastOrDefault();
        if (string.IsNullOrEmpty(worktree))
            return false;
        if (!Path.IsPathRooted(worktree))
            worktree = Path.Combine(repo, worktree); // git resolves it from the git dir
        var worktreeReal = Native.RealPath(Path.GetFullPath(worktree));
        return worktreeReal != null && worktreeReal == Native.RealPath(checkout);
    }

    /// <summary>Finds the submodule repos git keeps in <paramref name="gitDir"/>.</summary>
    /// <returns>The module repos, parents before their nested repos and siblings in ordinal order.</returns>
    /// <remarks>
    /// <para>
    /// That's every git dir under modules/ (a submodule's name may contain "/", so they can sit at any depth) and
    /// under worktrees/*/modules/ (a linked worktree's own submodules), and recursively those of each one found
    /// (nested submodules). A git dir is a directory with HEAD plus objects/ and refs/, or HEAD plus commondir
    /// (which callers refuse).
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

    /// <summary>git's is_git_directory: HEAD, plus objects/ and refs/ or a commondir naming them.</summary>
    private static bool IsGitDirLayout(string dir) =>
        File.Exists(Path.Combine(dir, "HEAD")) &&
        ((Directory.Exists(Path.Combine(dir, "objects")) && Directory.Exists(Path.Combine(dir, "refs")))
         || Path.Exists(Path.Combine(dir, "commondir")) || IsLinkPath(Path.Combine(dir, "commondir")));

    /// <summary>Reads a one-line file's value after <paramref name="prefix"/>.</summary>
    /// <returns>The value, or null if the file doesn't have that shape.</returns>
    private static string? ReadPathFile(string path, string prefix)
    {
        if (!File.Exists(path) || IsLinkPath(path))
            return null;
        var text = Native.ReadRegularFile(path);
        if (text == null || !text.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var value = text[prefix.Length..].TrimEnd('\n', '\r');
        return value.Length == 0 || value.Contains('\n') || value.Contains('\0') ? null : value;
    }

    private static bool IsLinkPath(string path) => new FileInfo(path).LinkTarget != null;

    /// <summary>One walk of a project's tree, collecting what it finds.</summary>
    private sealed class NestedWalk(
        string root,
        string commonGitDir,
        IReadOnlySet<string> moduleRepos,
        Func<string, bool>? tryOpen)
    {
        private readonly List<NestedRepo> _repos = [];
        private readonly List<string> _unlistable = [];
        private readonly List<BadName> _badNames = [];
        private readonly Stack<string> _pending = new();

        /// <summary>Walks the tree from the root, depth first.</summary>
        /// <returns>What it found, each list in ordinal order.</returns>
        public NestedScan Run()
        {
            _pending.Push(root);
            while (_pending.TryPop(out var dir))
                ScanDirectory(dir);
            _repos.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            _unlistable.Sort(StringComparer.Ordinal);
            _badNames.Sort((a, b) => string.CompareOrdinal(a.DisplayPath, b.DisplayPath));
            return new(_repos, _unlistable, _badNames);
        }

        /// <summary>Notes the repos in one directory and queues its subdirectories.</summary>
        private void ScanDirectory(string dir)
        {
            var entries = ListOrOpen(dir);
            if (entries == null)
                return;
            var undecodable = FindUndecodable(dir, entries);
            if (undecodable == null)
                return;
            if (ScanEntries(dir, entries, undecodable))
            {
                var head = entries.First(e => IsNamed(e.Name, "HEAD")).Name;
                _repos.Add(new(Path.Combine(dir, head), NestedKind.Bare));
            }
        }

        /// <summary>Lists <paramref name="dir"/>, trying to open it once if it can be entered but not listed.</summary>
        /// <returns>
        /// The entries, or null if there's nothing to scan: the directory is gone, shut or noted unlistable.
        /// </returns>
        private List<Entry>? ListOrOpen(string dir)
        {
            var entries = ListEntries(dir);
            if (entries != null)
                return entries;
            // Gone, or shut: nothing host git could reach.
            if (Native.IsShutOrGone(dir))
                return null;
            if (tryOpen != null && tryOpen(dir))
                entries = ListEntries(dir);
            if (entries == null)
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
            if (raw == null)
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

        /// <summary>Notes each .git in <paramref name="dir"/> and queues its subdirectories.</summary>
        /// <returns>Whether <paramref name="dir"/> itself has a git dir's layout (git's is_git_directory).</returns>
        private bool ScanEntries(string dir, List<Entry> entries, HashSet<string> undecodable)
        {
            bool head = false;
            bool objects = false;
            bool refs = false;
            bool commondir = false;
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
                if (IsNamed(entry.Name, "HEAD"))
                    head |= !entry.IsDir;
                else if (IsNamed(entry.Name, "objects"))
                    objects |= entry.IsDir;
                else if (IsNamed(entry.Name, "refs"))
                    refs |= entry.IsDir;
                else if (IsNamed(entry.Name, "commondir"))
                    commondir |= !entry.IsDir;

                if (entry.IsDir && !entry.IsLink)
                    _pending.Push(path);
            }
            // git's is_git_directory: HEAD, plus objects/ and refs/ or a commondir naming them.
            return head && ((objects && refs) || commondir);
        }

        /// <summary>
        /// Notes the .git at <paramref name="path"/> unless it's the project's own, a worktree of it or a known
        /// submodule's checkout.
        /// </summary>
        private void NoteGitEntry(string dir, Entry entry, string path)
        {
            if (dir == root)
                return; // the project's own; never descended into
            if (entry.IsLink)
                _repos.Add(new(path, NestedKind.GitLink));
            else if (entry.IsDir)
                _repos.Add(new(path, NestedKind.GitDir));
            else if (ResolveWorktree(path) != commonGitDir && ResolveModuleCheckout(path, moduleRepos) == null)
                _repos.Add(new(path, NestedKind.GitFile));
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
            if (IsLinkPath(worktrees) || !Directory.Exists(worktrees))
                return; // a symlinked worktrees/ is refused by the mounts
            var entries = ListOrNote(worktrees);
            if (entries == null)
                return;
            var undecodable = NoteBadNames(worktrees, entries);
            if (undecodable == null)
                return;
            foreach (var worktree in entries)
            {
                if (!undecodable.Contains(Path.GetFileName(worktree)) && !IsLinkPath(worktree))
                    WalkModules(Path.Combine(worktree, "modules"));
            }
        }

        /// <summary>
        /// Adds the module repos at or below <paramref name="dir"/>, a modules/ dir or a level in one.
        /// </summary>
        private void WalkModules(string dir)
        {
            if (IsLinkPath(dir))
            {
                _repos.Add(dir);
                return;
            }
            if (!Directory.Exists(dir))
                return;
            var entries = ListOrNote(dir);
            if (entries == null)
                return;
            var undecodable = NoteBadNames(dir, entries);
            if (undecodable == null)
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
            if (IsLinkPath(entry))
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
            if (raw == null)
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
                return Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal).ToList();
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
