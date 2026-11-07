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
/// The nested-repo scan, shared by the start-of-session refusal and the
/// session-end check. It only lists names (one readdir per directory) and
/// never follows symlinks, so a large node_modules stays well under a second.
/// </summary>
internal static class RepoScan
{
    private static readonly EnumerationOptions Options = new()
    {
        AttributesToSkip = 0, // dot files are "hidden" on Unix, and .git is one
        IgnoreInaccessible = true,
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
    public static List<NestedRepo> FindNested(string root, string? commonGitDir = null, IReadOnlyCollection<string>? moduleRepos = null)
    {
        commonGitDir ??= Path.Combine(root, ".git");
        var modules = new HashSet<string>(moduleRepos ?? FindModuleRepos(commonGitDir).Where(r => !IsLinkPath(r)), StringComparer.Ordinal);
        var found = new List<NestedRepo>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var dir))
        {
            List<Entry> entries;
            try
            {
                entries = new FileSystemEnumerable<Entry>(dir,
                    (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                    Options).ToList();
            }
            catch (IOException) { continue; } // gone, or not a directory any more
            catch (UnauthorizedAccessException) { continue; }

            bool head = false, objects = false, refs = false, commondir = false;
            foreach (var e in entries)
            {
                var path = Path.Combine(dir, e.Name);
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
        return found;
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
    internal static List<string> FindModuleRepos(string gitDir)
    {
        var found = new List<string>();
        AddModuleRepos(gitDir, found);
        return found;
    }

    private static void AddModuleRepos(string gitDir, List<string> found)
    {
        WalkModules(Path.Combine(gitDir, "modules"), found);
        var worktrees = Path.Combine(gitDir, "worktrees");
        if (IsLinkPath(worktrees) || !Directory.Exists(worktrees)) return; // a symlinked worktrees/ is refused by the mounts
        foreach (var wt in Directory.EnumerateDirectories(worktrees).Order(StringComparer.Ordinal))
            if (!IsLinkPath(wt)) WalkModules(Path.Combine(wt, "modules"), found);
    }

    private static void WalkModules(string dir, List<string> found)
    {
        if (IsLinkPath(dir)) { found.Add(dir); return; }
        if (!Directory.Exists(dir)) return;
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir).Order(StringComparer.Ordinal))
        {
            if (IsLinkPath(entry)) found.Add(entry);
            else if (!Directory.Exists(entry)) continue; // git keeps only directories here
            else if (Path.GetFileName(entry).Contains(GitCheck.Suffix, StringComparison.Ordinal)) continue;
            else if (IsGitDirLayout(entry))
            {
                found.Add(entry);
                AddModuleRepos(entry, found);
            }
            else WalkModules(entry, found); // a level of a name with "/" in it
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
        var text = File.ReadAllText(path);
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var value = text[prefix.Length..].TrimEnd('\n', '\r');
        return value.Length == 0 || value.Contains('\n') || value.Contains('\0') ? null : value;
    }

    private static bool IsLinkPath(string path) => new FileInfo(path).LinkTarget != null;
}
