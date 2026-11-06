using System.IO.Enumeration;

namespace Pen;

internal enum NestedKind
{
    /// <summary>A .git directory: a repo with its own config and hooks.</summary>
    GitDir,
    /// <summary>A .git file (gitfile) that isn't one of the project's own worktrees.</summary>
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
    /// the Mac's file system .GIT is .git.
    /// </summary>
    public static List<NestedRepo> FindNested(string root)
    {
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
                    else if (!IsProjectWorktree(root, path)) found.Add(new(path, NestedKind.GitFile));
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
    /// project (Claude Code puts these in .claude/worktrees/). Its .git file
    /// says "gitdir: &lt;project&gt;/.git/worktrees/&lt;name&gt;", and that
    /// directory's commondir is "../..", which is what git writes. Anything
    /// else could point host git at config and hooks the agent controls.
    /// </summary>
    internal static bool IsProjectWorktree(string root, string gitFile)
    {
        try
        {
            var text = File.ReadAllText(gitFile);
            const string prefix = "gitdir: ";
            if (!text.StartsWith(prefix, StringComparison.Ordinal)) return false;
            var gitdir = text[prefix.Length..].TrimEnd('\n', '\r');
            if (gitdir.Length == 0 || gitdir.Contains('\n')) return false;
            if (!Path.IsPathRooted(gitdir)) gitdir = Path.Combine(Path.GetDirectoryName(gitFile)!, gitdir);

            // git resolves the gitdir with realpath, so compare the real path:
            // a symlink anywhere along it would otherwise lead elsewhere.
            var real = Native.RealPath(gitdir);
            if (real == null || Path.GetDirectoryName(real) != Path.Combine(root, ".git", "worktrees")) return false;
            return File.ReadAllText(Path.Combine(real, "commondir")).TrimEnd('\n', '\r') == "../..";
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
