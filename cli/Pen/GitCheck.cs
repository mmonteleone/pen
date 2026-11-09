using System.Text;

namespace Pen;

/// <summary>One rename the check made. Inner is a second rename inside the moved item, if any.</summary>
internal sealed record Quarantined(string From, string To, string Why, (string From, string To)? Inner = null);

/// <summary>
/// How a session-end check went: what it renamed before it stopped, and why it
/// couldn't finish (null if it did). Git is only safe to run if Failure is null.
/// </summary>
internal sealed record CheckResult(List<Quarantined> Moved, string? Failure, List<ModeChange>? Opened = null);

/// <summary>A directory the check made listable and left that way, with the mode it had.</summary>
internal sealed record ModeChange(string Path, UnixFileMode Was, UnixFileMode Now);

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
    public static List<Quarantined> Run(string dir, bool startedWithGit, string? commonGitDir = null, IReadOnlyCollection<string>? modulesAtStart = null) =>
        RunWithoutOpening(dir, startedWithGit, commonGitDir, modulesAtStart).Moved;

    /// <summary>
    /// <see cref="Run"/>, reporting whether it finished. It changes no modes: a
    /// session may still be running in another worktree of the repo, so a
    /// directory that can't be listed is a Failure rather than something to open.
    /// </summary>
    public static CheckResult RunWithoutOpening(string dir, bool startedWithGit, string? commonGitDir = null, IReadOnlyCollection<string>? modulesAtStart = null) =>
        Finish(dir, opened: null, (moved, unlistable, badNames) => Check(dir, startedWithGit, commonGitDir, modulesAtStart, moved, unlistable, badNames, tryOpen: null));

    /// <summary>
    /// The check for a session that's ending, against what the session recorded
    /// at its start. A directory it can enter but not list gets u+rx so it can
    /// be scanned, and keeps it (Opened). Never throws: whatever goes wrong here
    /// (or in code it reaches), a session must still be torn down, and retrying
    /// wouldn't help. The caller must not run git unless Failure is null.
    /// </summary>
    public static CheckResult RunAtSessionEnd(string dir, SessionDir session)
    {
        var opened = new List<ModeChange>();
        return Finish(dir, opened, (moved, unlistable, badNames) =>
        {
            var (startedWithGit, commonGitDir) = session.ReadGit();
            // A session from a pen that didn't record its module repos keeps none.
            Check(dir, startedWithGit, commonGitDir, session.ReadModules() ?? [], moved, unlistable, badNames, p => TryOpen(p, opened));
        });
    }

    /// <summary>Runs <paramref name="check"/> and states why the result can't be vouched for, if it can't.</summary>
    private static CheckResult Finish(string dir, List<ModeChange>? opened, Action<List<Quarantined>, List<string>, List<BadName>> check)
    {
        var moved = new List<Quarantined>();
        var unlistable = new List<string>();
        var badNames = new List<BadName>();
        var failures = new List<string>();
        try { check(moved, unlistable, badNames); }
        catch (Exception e) { failures.Add(Out.Describe(e)); }
        if (unlistable.Count > 0)
            failures.Add("pen can't list these directories, so it can't see a repo inside one: " + string.Join(", ", unlistable.Take(RepoGuard.MaxListed).Select(u => RepoGuard.Rel(dir, u)))
                + (unlistable.Count > RepoGuard.MaxListed ? $" and {unlistable.Count - RepoGuard.MaxListed} more" : ""));
        if (badNames.Count > 0)
            failures.Add("these names aren't valid UTF-8, so pen can't look inside them or rename them, though git on this machine can still enter them and run what's there (shown with octal escapes): " + string.Join(", ", RepoGuard.ListedBadNames(dir, badNames)));
        if (moved.Any(q => q.To.Length == 0))
            failures.Add("a file named above couldn't be moved out of git's way");
        return new(moved, failures.Count == 0 ? null : string.Join("; ", failures), opened);
    }

    /// <summary>
    /// Adds u+rx to a directory. Never through a symlink, which an overlapping
    /// session could have swapped in since the listing: chmod would change
    /// whatever it points to. Checking just before is the portable way, since
    /// glibc's fchmodat can't do AT_SYMLINK_NOFOLLOW.
    /// </summary>
    internal static bool TryOpen(string path, List<ModeChange> opened)
    {
        try
        {
            if (RepoGuard.IsLink(path)) return false;
            var was = File.GetUnixFileMode(path);
            var now = was | UnixFileMode.UserRead | UnixFileMode.UserExecute;
            if (now == was) return false; // the mode isn't why it can't be listed
            File.SetUnixFileMode(path, now);
            opened.Add(new(path, was, now));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Check(string dir, bool startedWithGit, string? commonGitDir, IReadOnlyCollection<string>? modulesAtStart, List<Quarantined> moved, List<string> unlistable, List<BadName> badNames, Func<string, bool>? tryOpen)
    {
        if (!Directory.Exists(dir)) return;
        // The agent could do this to a mount's root too. The user will fix the
        // mode and then run git, so "host git can't get in" doesn't excuse it.
        if (!Native.CanEnter(dir))
            throw new PenException($"pen can't enter {Out.Field(dir)}. Fixing its permissions would let git run whatever was planted there");

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
            // Likewise: nothing under it can be seen, and it's what git reads first.
            if (Directory.Exists(git) && !Native.CanList(git))
                throw new PenException($"pen can't read {Out.Field(git)}. Fixing its permissions would let git run whatever was planted there");
            var commondir = Path.Combine(git, "commondir");
            if (RepoGuard.Exists(commondir))
                Move(commondir, "a normal repo never has one: it makes git read config and hooks from another directory", moved);
            known = CheckModuleRepos(git, modulesAtStart, moved, badNames);
        }

        var scan = RepoScan.ScanNested(dir, startedWithGit ? commonGitDir : null, known, tryOpen);
        unlistable.AddRange(scan.Unlistable);
        badNames.AddRange(scan.BadNames);
        foreach (var n in scan.Found)
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
    private static HashSet<string> CheckModuleRepos(string gitDir, IReadOnlyCollection<string>? modulesAtStart, List<Quarantined> moved, List<BadName> badNames)
    {
        var listed = modulesAtStart == null ? null : new HashSet<string>(modulesAtStart, StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);
        var gone = new List<string>();
        var scan = RepoScan.ScanModules(gitDir);
        badNames.AddRange(scan.BadNames);
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
            var reason = e is UnauthorizedAccessException u ? Out.PermissionDenied(u) : e.Message; // Native.Rename's is strerror's
            moved.Add(new(path, "", $"{why}. COULD NOT MOVE IT ({reason}): don't run git here until you've dealt with it"));
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
                // Command substitution drops trailing newlines, so no quoting
                // that works in every shell can name this one.
                if (q.From.EndsWith('\n'))
                {
                    sb.Append("  pen can't write a command for ").Append(Out.Field(q.From)).AppendLine(", since the name ends in a newline: rename it back by hand");
                    continue;
                }
                sb.Append("  mv ").Append(Quote(q.To)).Append(' ').Append(Quote(q.From));
                if (q.Inner is { } i)
                    sb.Append(" && mv ").Append(Quote(Path.Combine(q.From, Path.GetFileName(i.To))))
                      .Append(' ').Append(Quote(Path.Combine(q.From, Path.GetFileName(i.From))));
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>The loud warning for a check that couldn't vouch for <paramref name="dir"/>: <paramref name="why"/> says what stopped it.</summary>
    public static string NotCheckedWarning(string dir, string why) =>
        $"""
        pen: WARNING: git wasn't checked: don't run git here ({Out.Field(dir)}).
        pen: The check for files that make git on this machine run code from the sandbox couldn't finish: {why}
        pen: Your shell prompt and IDE may run git too, so keep them out of this directory. Inspect its .git (config, hooks, modules) and any repos nested in it by hand first.
        """;

    /// <summary>Prints what a session-end check found: first what it renamed, then that it didn't finish.</summary>
    public static void Report(string dir, CheckResult check)
    {
        if (check.Moved.Count > 0) Out.Raw(Warning(dir, check.Moved));
        foreach (var c in check.Opened ?? [])
            Out.Line($"changed {RepoGuard.Rel(dir, c.Path)} from mode {Octal(c.Was)} to {Octal(c.Now)} so it could be checked, and left it that way");
        if (check.Failure != null) Out.Raw(NotCheckedWarning(dir, check.Failure));
    }

    private static string Octal(UnixFileMode mode) => Convert.ToString((int)mode, 8);

    /// <summary>
    /// A path as one word of a command the user pastes, on a name the agent
    /// chose, in sh, bash, zsh or fish 3.4+. Plain single quotes where they mean
    /// the same in all of them. Otherwise "$(printf '…')" with an octal escape
    /// for all but the plainest characters, since: a control character has to
    /// stay out of the terminal; fish reads a backslash in single quotes
    /// differently; printf reads % and \; interactive bash and zsh expand !
    /// inside double quotes. A leading - would be an option, to mv or to printf.
    /// </summary>
    internal static string Quote(string s)
    {
        if (s.StartsWith('-')) s = "./" + s;
        if (!s.Any(c => Out.IsControl(c) || c == '\\'))
            return "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var sb = new StringBuilder("\"$(printf '");
        foreach (var c in s)
        {
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or ' ' or ',' or ':' or '+' or '=' or '@'
                || (c > '\x7f' && !Out.IsControl(c)))
                sb.Append(c);
            else
                foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                    sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
        }
        return sb.Append("')\"").ToString();
    }
}
