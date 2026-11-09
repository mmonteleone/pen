using System.Text;

namespace Pen;

/// <summary>One rename the check made. Inner is a second rename inside the moved item, if any.</summary>
internal sealed record Quarantined(string From, string To, string Why, (string From, string To)? Inner = null);

/// <summary>
/// The session-end .git check. It reads
/// files directly and never runs git, and it renames whatever would make host
/// git read config or hooks the agent could write, before the shell prompt
/// or an IDE runs git there. It runs before pen's own git calls.
/// </summary>
internal static class GitCheck
{
    public const string Suffix = ".pen-quarantine";

    /// <summary>
    /// Checks <paramref name="dir"/> and quarantines what it flags.
    /// <paramref name="startedWithGit"/> is whether the session started with a .git there.
    /// <paramref name="commonGitDir"/> is the git dir it started with (default
    /// dir/.git; for a linked worktree, the main checkout's): the .git-level
    /// checks run on it, and worktrees of it in the tree are the project's own.
    /// <paramref name="modulesAtStart"/> are the submodule repos in that git
    /// dir when the session started: any other is quarantined, and the
    /// checkouts of those left are the project's own. Null (pen sign, which
    /// doesn't end a session) takes the ones there now.
    /// </summary>
    public static List<Quarantined> Run(string dir, bool startedWithGit, string? commonGitDir = null, IReadOnlyCollection<string>? modulesAtStart = null)
    {
        var moved = new List<Quarantined>();
        if (!Directory.Exists(dir)) return moved;

        var git = Path.Combine(dir, ".git");
        HashSet<string> known = [];
        if (!startedWithGit)
        {
            if (RepoGuard.Exists(git))
                Move(git, "a repo created during the session (git init): its config and hooks were writable from the sandbox", moved, breakLayout: true);
        }
        else
        {
            git = commonGitDir ?? git;
            var commondir = Path.Combine(git, "commondir");
            if (RepoGuard.Exists(commondir))
                Move(commondir, "a normal repo never has one: it makes git read config and hooks from another directory", moved);
            known = CheckModuleRepos(git, modulesAtStart, moved);
        }

        foreach (var n in RepoScan.FindNested(dir, startedWithGit ? commonGitDir : null, known))
        {
            var why = n.Kind switch
            {
                NestedKind.Bare => "a bare repo: git takes this directory as one, with config and hooks from the sandbox",
                NestedKind.GitFile => "a .git file that isn't one of this repo's worktrees or submodules: it points git at another directory",
                NestedKind.GitLink => "a .git symlink: it points git at another directory",
                _ => "a nested repo: its config and hooks were writable from the sandbox",
            };
            Move(n.Path, why, moved, breakLayout: n.Kind == NestedKind.GitDir);
        }
        return moved;
    }

    /// <summary>
    /// Quarantines the submodule repos in <paramref name="gitDir"/> that
    /// weren't there at the start (<paramref name="modulesAtStart"/>; null: all
    /// are kept), any symlink where git keeps them, and a commondir in one that
    /// stays. Repos nested in a quarantined one go with it. A directory there
    /// that can't be read is quarantined whether or not it was there at the
    /// start, since nothing vouches for what's in it now. Returns the repos
    /// left, whose checkouts in the tree are the project's own.
    /// </summary>
    private static HashSet<string> CheckModuleRepos(string gitDir, IReadOnlyCollection<string>? modulesAtStart, List<Quarantined> moved)
    {
        var listed = modulesAtStart == null ? null : new HashSet<string>(modulesAtStart, StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);
        var gone = new List<string>();
        var scan = RepoScan.ScanModules(gitDir);
        foreach (var repo in scan.Repos)
        {
            if (gone.Any(g => RepoGuard.IsInside(g, repo))) continue;
            if (RepoGuard.IsLink(repo))
            {
                Move(repo, "a symlink where git keeps a submodule's repo: it points git at config and hooks in another directory", moved);
                gone.Add(repo);
            }
            else if (listed != null && !listed.Contains(repo))
            {
                Move(repo, "a submodule repo made during the session: its config and hooks were writable from the sandbox", moved, breakLayout: true);
                gone.Add(repo);
            }
            else
            {
                var commondir = Path.Combine(repo, "commondir");
                if (RepoGuard.Exists(commondir))
                    Move(commondir, "a submodule's repo never has one: it makes git read config and hooks from another directory", moved);
                if (!RepoGuard.Exists(commondir)) known.Add(repo);
            }
        }
        foreach (var dir in scan.Unreadable)
        {
            if (gone.Any(g => RepoGuard.IsInside(g, dir))) continue;
            Move(dir, "a directory where git keeps submodule repos that can't be read, so pen can't check it: inspect its contents before you fix its permissions", moved, breakLayout: true);
        }
        return known;
    }

    /// <summary>
    /// Renames <paramref name="path"/> to a free "….pen-quarantine" name. For a
    /// .git directory (<paramref name="breakLayout"/>), its HEAD goes too:
    /// renamed, it would still look like a bare repo to git.
    /// </summary>
    private static void Move(string path, string why, List<Quarantined> moved, bool breakLayout = false)
    {
        try
        {
            var to = FreeName(path);
            Native.Rename(path, to);
            (string, string)? inner = null;
            if (breakLayout && Directory.Exists(to) && !RepoGuard.IsLink(to))
            {
                var head = Path.Combine(to, "HEAD");
                if (RepoGuard.Exists(head))
                {
                    var headTo = FreeName(head);
                    Native.Rename(head, headTo);
                    inner = (head, headTo);
                }
            }
            moved.Add(new(path, to, why, inner));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never fatal: the rest still gets checked, and the warning names it.
            moved.Add(new(path, "", $"{why}. COULD NOT MOVE IT ({e.Message}): don't run git here until you've dealt with it"));
        }
    }

    private static string FreeName(string path)
    {
        var to = path + Suffix;
        for (int i = 2; RepoGuard.Exists(to); i++) to = $"{path}{Suffix}-{i}";
        return to;
    }

    /// <summary>The loud warning: what moved, why, and how to put it back.</summary>
    public static string Warning(string dir, IReadOnlyList<Quarantined> moved)
    {
        var sb = new StringBuilder();
        sb.AppendLine("pen: WARNING: this session left git files that would make git on this machine run code from the sandbox.");
        sb.AppendLine("pen: They've been renamed out of git's way:");
        foreach (var q in moved)
        {
            sb.Append("  ").Append(RepoGuard.Rel(dir, q.From));
            if (q.To.Length > 0) sb.Append(" → ").Append(RepoGuard.Rel(dir, q.To));
            sb.AppendLine().Append("      ").AppendLine(q.Why);
        }
        var restorable = moved.Where(q => q.To.Length > 0).ToList();
        if (restorable.Count > 0)
        {
            sb.AppendLine("pen: Inspect each one before putting it back (a hook or a core.* setting in its config runs on this machine). To restore one:");
            foreach (var q in restorable)
            {
                sb.Append("  mv ").Append(Quote(q.To)).Append(' ').Append(Quote(q.From));
                if (q.Inner is { } i)
                    sb.Append(" && mv ").Append(Quote(Path.Combine(q.From, Path.GetFileName(i.To))))
                      .Append(' ').Append(Quote(Path.Combine(q.From, Path.GetFileName(i.From))));
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static string Quote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
