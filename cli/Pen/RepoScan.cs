using System.IO.Enumeration;

namespace Pen;

internal enum NestedKind
{
    /// <summary>A .git directory: a repo with its own config and hooks.</summary>
    GitDir,
    /// <summary>A .git file (gitfile) that isn't one of the project's own worktrees or submodule checkouts.</summary>
    GitFile,
    /// <summary>A .git symlink.</summary>
    GitLink,
    /// <summary>A directory git takes for a bare repo: HEAD plus objects/ and refs/, or HEAD plus commondir.</summary>
    Bare,
}

/// <summary>A repo below the project root. Path is the entry that makes it one: the .git, or a bare repo's HEAD.</summary>
internal sealed record NestedRepo(string Path, NestedKind Kind)
{
    /// <summary>The repo's directory, for messages.</summary>
    public string RepoDir => System.IO.Path.GetDirectoryName(Path)!;
}

/// <summary>
/// An entry a scan can't look inside, since its name isn't valid UTF-8: .NET's
/// listing has U+FFFD in place of the bad bytes, so that string names nothing
/// (or another entry), while git on this machine can still enter it. Shown is
/// its path for messages, with the name exact (<see cref="UndecodableEntry.Escaped"/>).
/// EndsInNewline: no command pen could print would name it.
/// </summary>
internal sealed record BadName(string Shown, bool EndsInNewline)
{
    public static BadName At(string dir, UndecodableEntry entry) => new(Path.Combine(dir, entry.Escaped), entry.EndsInNewline);
}

/// <summary>What the walk of a git dir's submodule repos found (Repos), couldn't read (Unreadable) and couldn't name (BadNames).</summary>
internal sealed record ModuleScan(List<string> Repos, List<string> Unreadable, List<BadName> BadNames);

/// <summary>What the walk of the tree found (Found), the directories it could enter but not list (Unlistable), and the entries it couldn't name (BadNames).</summary>
internal sealed record NestedScan(List<NestedRepo> Found, List<string> Unlistable, List<BadName> BadNames);

/// <summary>
/// The nested-repo scan, shared by the start-of-session refusal and the
/// session-end check. It only lists names (one readdir per directory) and
/// never follows symlinks, so a large node_modules stays well under a second.
/// </summary>
internal static class RepoScan
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0, // dot files are "hidden" on Unix, and .git is one
        IgnoreInaccessible = false, // a directory that can't be listed has to be noticed
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    private readonly record struct Entry(string Name, bool IsDir, bool IsLink);

    /// <summary>
    /// Every repo below <paramref name="root"/> (which must be a real path),
    /// plus a bare layout in the root itself. The root's own .git is the
    /// project's and is skipped. Names compare case-insensitively, because on
    /// the Mac's file system .GIT is .git. <paramref name="commonGitDir"/> is
    /// the project's common git dir (default root/.git; for a linked worktree,
    /// the main checkout's): worktrees of it below the root are the project's own.
    /// So are the checkouts of <paramref name="moduleRepos"/> (default: the
    /// module repos in the common git dir now; see <see cref="ResolveModuleCheckout"/>).
    /// </summary>
    public static List<NestedRepo> FindNested(string root, string? commonGitDir = null, IReadOnlyCollection<string>? moduleRepos = null) =>
        ScanNested(root, commonGitDir, moduleRepos).Found;

    /// <summary>
    /// <see cref="FindNested"/>'s walk, with the directories it couldn't list
    /// although they can be entered (chmod 311): host git could run in a repo
    /// in one, so callers must not treat the scan as complete. One that can't
    /// be entered at all (a root-owned 700 database directory) isn't reported,
    /// since host git can't get in either. <paramref name="tryOpen"/>, if given,
    /// gets each such directory and returns whether it changed its mode, after
    /// which the listing is tried again. The access(2) call is only made after
    /// a listing fails, so a healthy tree pays nothing for it. Likewise, only a
    /// listed name with U+FFFD in it costs a look at the directory's raw names.
    /// BadNames has each entry there that could hold a repo; a plain file with
    /// such a name can't, so it's left alone.
    /// </summary>
    public static NestedScan ScanNested(string root, string? commonGitDir = null, IReadOnlyCollection<string>? moduleRepos = null, Func<string, bool>? tryOpen = null)
    {
        commonGitDir ??= Path.Combine(root, ".git");
        var modules = new HashSet<string>(moduleRepos ?? FindModuleRepos(commonGitDir).Where(r => !IsLinkPath(r)), StringComparer.Ordinal);
        var found = new List<NestedRepo>();
        var unlistable = new List<string>();
        var badNames = new List<BadName>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var dir))
        {
            var entries = List(dir);
            if (entries == null)
            {
                // Gone, or shut: nothing host git could reach.
                if (Native.IsShutOrGone(dir)) continue;
                if (tryOpen != null && tryOpen(dir)) entries = List(dir);
                if (entries == null)
                {
                    unlistable.Add(dir);
                    continue;
                }
            }

            HashSet<string> undecodable = [];
            if (entries.Any(e => HasReplacement(e.Name)))
            {
                var raw = Native.ListUndecodable(dir);
                if (raw == null)
                {
                    unlistable.Add(dir);
                    continue;
                }
                foreach (var u in raw)
                {
                    undecodable.Add(u.Decoded);
                    if (u.CouldHoldRepo) badNames.Add(BadName.At(dir, u));
                }
            }

            bool head = false, objects = false, refs = false, commondir = false;
            foreach (var e in entries)
            {
                var path = Path.Combine(dir, e.Name);
                // Unless a real entry has the name it decodes to, which is scanned as usual.
                if (undecodable.Contains(e.Name) && !Path.Exists(path)) continue;
                if (Is(e.Name, ".git"))
                {
                    if (dir == root) continue; // the project's own; never descended into
                    if (e.IsLink) found.Add(new(path, NestedKind.GitLink));
                    else if (e.IsDir) found.Add(new(path, NestedKind.GitDir));
                    else if (ResolveWorktree(path) != commonGitDir && ResolveModuleCheckout(path, modules) == null)
                        found.Add(new(path, NestedKind.GitFile));
                    continue; // a .git's contents belong to that repo
                }
                if (Is(e.Name, "HEAD")) head |= !e.IsDir;
                else if (Is(e.Name, "objects")) objects |= e.IsDir;
                else if (Is(e.Name, "refs")) refs |= e.IsDir;
                else if (Is(e.Name, "commondir")) commondir |= !e.IsDir;

                if (e.IsDir && !e.IsLink) pending.Push(path);
            }
            // git's is_git_directory: HEAD, plus objects/ and refs/ or a commondir naming them.
            if (head && ((objects && refs) || commondir))
                found.Add(new(Path.Combine(dir, entries.First(e => Is(e.Name, "HEAD")).Name), NestedKind.Bare));
        }
        found.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        unlistable.Sort(StringComparer.Ordinal);
        badNames.Sort((a, b) => string.CompareOrdinal(a.Shown, b.Shown));
        return new(found, unlistable, badNames);
    }

    private static bool HasReplacement(string name) => name.Contains('\uFFFD');

    private static List<Entry>? List(string dir)
    {
        try
        {
            return new FileSystemEnumerable<Entry>(dir,
                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                Options).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static bool Is(string name, string wanted) => name.Equals(wanted, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A worktree of the project itself, made by `git worktree add` inside the
    /// project (Claude Code puts these in .claude/worktrees/): one whose common
    /// git dir is the project's own .git. Anything else could point host git at
    /// config and hooks the agent controls.
    /// </summary>
    internal static bool IsProjectWorktree(string root, string gitFile) =>
        ResolveWorktree(gitFile) == Path.Combine(root, ".git");

    /// <summary>
    /// The common git dir (the main checkout's .git, or a bare repo) of the
    /// linked worktree whose .git file is <paramref name="gitFile"/>, or null
    /// unless it has exactly the layout `git worktree add` makes:
    /// <list type="bullet">
    /// <item>the file says "gitdir: &lt;common&gt;/worktrees/&lt;name&gt;", with no symlink anywhere along that path;</item>
    /// <item>&lt;name&gt;/commondir is "../..";</item>
    /// <item>&lt;name&gt;/gitdir points back to this .git file.</item>
    /// </list>
    /// Reads the files directly; never runs git.
    /// </summary>
    internal static string? ResolveWorktree(string gitFile)
    {
        try
        {
            if (IsLinkPath(gitFile)) return null;
            var gitdir = ReadPathFile(gitFile, "gitdir: ");
            if (gitdir == null) return null;
            if (!Path.IsPathRooted(gitdir)) gitdir = Path.Combine(Path.GetDirectoryName(gitFile)!, gitdir);

            // git resolves the gitdir with realpath, so a symlink anywhere along
            // it would lead elsewhere than it reads: require it to be real already.
            var full = Path.GetFullPath(gitdir).TrimEnd('/');
            var real = Native.RealPath(full);
            if (real == null || real != full) return null;
            var worktrees = Path.GetDirectoryName(real);
            if (worktrees == null || Path.GetFileName(worktrees) != "worktrees") return null;
            var common = Path.GetDirectoryName(worktrees);
            if (common == null || common == "/") return null;

            if (ReadPathFile(Path.Combine(real, "commondir"), "") != "../..") return null;

            var back = ReadPathFile(Path.Combine(real, "gitdir"), "");
            if (back == null) return null;
            if (!Path.IsPathRooted(back)) back = Path.Combine(real, back);
            var backReal = Native.RealPath(back);
            if (backReal == null || backReal != Native.RealPath(gitFile)) return null;
            return common;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The git dir a validated linked worktree's .git file names: &lt;common&gt;/worktrees/&lt;name&gt;. Null if it can't be read.</summary>
    internal static string? WorktreeEntry(string gitFile)
    {
        try
        {
            var gitdir = ReadPathFile(gitFile, "gitdir: ");
            if (gitdir == null) return null;
            if (!Path.IsPathRooted(gitdir)) gitdir = Path.Combine(Path.GetDirectoryName(gitFile)!, gitdir);
            return Path.GetFullPath(gitdir).TrimEnd('/');
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// The module repo (one of <paramref name="moduleRepos"/>) whose checkout
    /// has <paramref name="gitFile"/> as its .git file, or null unless it has
    /// the layout `git submodule` makes:
    /// <list type="bullet">
    /// <item>the file says "gitdir: &lt;repo&gt;", with no symlink anywhere along that path, and &lt;repo&gt; is one of <paramref name="moduleRepos"/>;</item>
    /// <item>&lt;repo&gt;/config's core.worktree resolves back to the .git file's dir.</item>
    /// </list>
    /// The repo's config and hooks are protected (and its list is the one taken
    /// at the session's start), so host git there runs nothing the sandbox wrote.
    /// Reads the files directly; never runs git.
    /// </summary>
    internal static string? ResolveModuleCheckout(string gitFile, IReadOnlySet<string> moduleRepos)
    {
        try
        {
            if (moduleRepos.Count == 0 || IsLinkPath(gitFile)) return null;
            var gitdir = ReadPathFile(gitFile, "gitdir: ");
            if (gitdir == null) return null;
            var checkout = Path.GetDirectoryName(gitFile)!;
            if (!Path.IsPathRooted(gitdir)) gitdir = Path.Combine(checkout, gitdir);
            var full = Path.GetFullPath(gitdir).TrimEnd('/');
            var real = Native.RealPath(full);
            if (real == null || real != full || !moduleRepos.Contains(real)) return null;

            var config = Path.Combine(real, "config");
            if (IsLinkPath(config)) return null;
            var worktree = GitConfigFile.ParseFile(config)
                .Where(e => e.Section == "core" && e.Subsection == null && e.Key == "worktree").Select(e => e.Value).LastOrDefault();
            if (string.IsNullOrEmpty(worktree)) return null;
            if (!Path.IsPathRooted(worktree)) worktree = Path.Combine(real, worktree); // git resolves it from the git dir
            var worktreeReal = Native.RealPath(Path.GetFullPath(worktree));
            return worktreeReal != null && worktreeReal == Native.RealPath(checkout) ? real : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (PenException) { return null; } // an unreadable config: not a checkout pen can vouch for
    }

    /// <summary>
    /// The submodule repos git keeps in <paramref name="gitDir"/>: every git
    /// dir under modules/ (a submodule's name may contain "/", so they can sit
    /// at any depth) and under worktrees/*/modules/ (a linked worktree's own
    /// submodules), and recursively those of each one found (nested
    /// submodules). A git dir is a directory with HEAD plus objects/ and refs/,
    /// or HEAD plus commondir (which callers refuse). Any symlink met along the
    /// way is listed as is and not followed: git would follow it to a repo
    /// elsewhere, so callers refuse it too. A directory pen quarantined
    /// (named …<see cref="GitCheck.Suffix"/>…) isn't walked: repos nested in a
    /// quarantined one are no submodule's. Parents come before their nested
    /// repos; siblings in ordinal order. Reads directories only; never runs git.
    /// </summary>
    internal static List<string> FindModuleRepos(string gitDir) => ScanModules(gitDir).Repos;

    /// <summary>
    /// <see cref="FindModuleRepos"/>'s walk, with the directories it couldn't
    /// read: what's in those is unknown, so callers refuse or quarantine them.
    /// BadNames has every entry whose name isn't valid UTF-8, of any type, since
    /// git keeps only directories with names it chose here: callers refuse those
    /// or report "couldn't check", because renaming the parent would take every
    /// submodule repo with it.
    /// </summary>
    internal static ModuleScan ScanModules(string gitDir)
    {
        var scan = new ModuleScan([], [], []);
        AddModuleRepos(gitDir, scan);
        return scan;
    }

    private static void AddModuleRepos(string gitDir, ModuleScan scan)
    {
        WalkModules(Path.Combine(gitDir, "modules"), scan);
        var worktrees = Path.Combine(gitDir, "worktrees");
        if (IsLinkPath(worktrees) || !Directory.Exists(worktrees)) return; // a symlinked worktrees/ is refused by the mounts
        var entries = ListOrNote(worktrees, Directory.EnumerateFileSystemEntries, scan);
        if (entries == null) return;
        var undecodable = NoteBadNames(worktrees, entries, scan);
        if (undecodable == null) return;
        foreach (var wt in entries)
            if (!undecodable.Contains(Path.GetFileName(wt)) && !IsLinkPath(wt)) WalkModules(Path.Combine(wt, "modules"), scan);
    }

    private static void WalkModules(string dir, ModuleScan scan)
    {
        if (IsLinkPath(dir)) { scan.Repos.Add(dir); return; }
        if (!Directory.Exists(dir)) return;
        var entries = ListOrNote(dir, Directory.EnumerateFileSystemEntries, scan);
        if (entries == null) return;
        var undecodable = NoteBadNames(dir, entries, scan);
        if (undecodable == null) return;
        foreach (var entry in entries)
        {
            // Unless a real entry has the name it decodes to, which is walked as usual.
            if (undecodable.Contains(Path.GetFileName(entry)) && !Path.Exists(entry)) continue;
            // Before the symlink test, so a quarantined symlink isn't flagged and renamed again.
            if (Path.GetFileName(entry).Contains(GitCheck.Suffix, StringComparison.Ordinal)) continue;
            if (IsLinkPath(entry)) scan.Repos.Add(entry);
            else if (!Path.Exists(entry))
            {
                // Listed but not there to stat: dir can be read but not entered.
                scan.Unreadable.Add(dir);
                return;
            }
            else if (!Directory.Exists(entry)) continue; // git keeps only directories here
            else if (IsGitDirLayout(entry))
            {
                scan.Repos.Add(entry);
                AddModuleRepos(entry, scan);
            }
            else WalkModules(entry, scan); // a level of a name with "/" in it
        }
    }

    /// <summary>
    /// Notes the entries of <paramref name="dir"/> whose names aren't valid
    /// UTF-8, returning their names as listed; null, noting the directory, if
    /// its raw names can't be read.
    /// </summary>
    private static HashSet<string>? NoteBadNames(string dir, List<string> entries, ModuleScan scan)
    {
        if (!entries.Any(e => HasReplacement(Path.GetFileName(e)))) return [];
        var raw = Native.ListUndecodable(dir);
        if (raw == null)
        {
            scan.Unreadable.Add(dir);
            return null;
        }
        scan.BadNames.AddRange(raw.Select(u => BadName.At(dir, u)));
        return raw.Select(u => u.Decoded).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The sorted entries of <paramref name="dir"/>; none if it's gone; null, noting it, if it can't be read.</summary>
    private static List<string>? ListOrNote(string dir, Func<string, IEnumerable<string>> list, ModuleScan scan)
    {
        try { return list(dir).Order(StringComparer.Ordinal).ToList(); }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            scan.Unreadable.Add(dir);
            return null;
        }
    }

    /// <summary>git's is_git_directory: HEAD, plus objects/ and refs/ or a commondir naming them.</summary>
    private static bool IsGitDirLayout(string dir) =>
        File.Exists(Path.Combine(dir, "HEAD")) &&
        ((Directory.Exists(Path.Combine(dir, "objects")) && Directory.Exists(Path.Combine(dir, "refs")))
         || Path.Exists(Path.Combine(dir, "commondir")) || IsLinkPath(Path.Combine(dir, "commondir")));

    /// <summary>A one-line file's value after <paramref name="prefix"/>, or null if it doesn't have that shape.</summary>
    private static string? ReadPathFile(string path, string prefix)
    {
        if (!File.Exists(path) || IsLinkPath(path)) return null;
        var text = Native.ReadRegularFile(path);
        if (text == null || !text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var value = text[prefix.Length..].TrimEnd('\n', '\r');
        return value.Length == 0 || value.Contains('\n') || value.Contains('\0') ? null : value;
    }

    private static bool IsLinkPath(string path) => new FileInfo(path).LinkTarget != null;
}
