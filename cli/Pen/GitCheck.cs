using System.Text;

namespace Pen;

/// <summary>One rename the check made. Inner is a second rename inside the moved item, if any.</summary>
internal sealed record Quarantined(string From, string To, string Why, (string From, string To)? Inner = null);

/// <summary>
/// How a session-end check went: what it renamed before it stopped, and why it
/// couldn't finish (null if it did). Git is only safe to run if Failure is null.
/// </summary>
internal sealed record CheckResult(List<Quarantined> Moved, string? Failure, List<ModeChange>? Opened = null, List<RestoredFile>? Restored = null, bool RestoreIncomplete = false, List<string>? RestoreBlocked = null, List<PausedOp>? Paused = null);

/// <summary>A protected file that changed with nothing to set aside: it was missing, its directory went, or only its mode changed.</summary>
internal sealed record RestoredFile(string Path, string What);

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
    /// doesn't end a session) takes the ones there now. No config.worktree is
    /// touched: with no record of the start, one can't be told from the user's.
    /// </summary>
    public static List<Quarantined> Run(string dir, bool startedWithGit, string? commonGitDir = null, IReadOnlyCollection<string>? modulesAtStart = null) =>
        RunWithoutOpening(dir, startedWithGit, commonGitDir, modulesAtStart).Moved;

    /// <summary>
    /// <see cref="Run"/>, reporting whether it finished. It changes no modes: a
    /// session may still be running in another worktree of the repo, so a
    /// directory that can't be listed is a Failure rather than something to open.
    /// </summary>
    public static CheckResult RunWithoutOpening(string dir, bool startedWithGit, string? commonGitDir = null, IReadOnlyCollection<string>? modulesAtStart = null) =>
        Finish(dir, opened: null, (moved, unlistable, badNames, paused) =>
            SweepingHazardsIfItStops(startedWithGit ? commonGitDir ?? Path.Combine(dir, ".git") : null, moved, unlistable, tryOpen: null, tryOpenForWriting: null, atStart: null,
                () => Check(dir, startedWithGit, commonGitDir, modulesAtStart, null, moved, unlistable, badNames, paused, tryOpen: null, tryOpenForWriting: null)));

    /// <summary>
    /// The check for a session that's ending, against what the session recorded
    /// at its start. First the protected files go back to their saved copies
    /// (<see cref="ProtectedFiles.Restore"/>), so what follows reads those; if that
    /// doesn't complete, RestoreIncomplete says the session's files must be kept
    /// for a retry, since they hold the only good copies. A directory it can enter but not list gets u+rx so it can
    /// be scanned, and keeps it (Opened). Never throws: whatever goes wrong here
    /// (or in code it reaches), a session must still be torn down, and retrying
    /// wouldn't help. The caller must not run git unless Failure is null.
    /// </summary>
    public static CheckResult RunAtSessionEnd(string dir, SessionDir session)
    {
        var opened = new List<ModeChange>();
        var restored = new List<RestoredFile>();
        var blocked = new List<string>();
        var restoreDone = !File.Exists(session.ProtectedManifest);
        var result = Finish(dir, opened, (moved, unlistable, badNames, paused) =>
        {
            var (startedWithGit, commonGitDir) = session.ReadGit();
            bool OpenToRead(string p) => TryOpen(p, opened);
            bool OpenToWrite(string p) => TryOpen(p, opened, write: true);
            var atStart = ProtectedFiles.AtStart(session);
            SweepingHazardsIfItStops(startedWithGit ? commonGitDir ?? Path.Combine(dir, ".git") : null, moved, unlistable, OpenToRead, OpenToWrite, atStart, () =>
            {
                restoreDone = ProtectedFiles.Restore(session, dir, commonGitDir, moved, restored, opened, blocked);
                if (!Directory.Exists(dir))
                    throw new PenException($"{Out.Field(dir)} isn't there any more or can't be reached, so pen couldn't check it. If it was moved, inspect its .git by hand before running git in it");
                // A session from a pen that didn't record its module repos keeps none.
                Check(dir, startedWithGit, commonGitDir, session.ReadModules() ?? [], atStart, moved, unlistable, badNames, paused, OpenToRead, OpenToWrite);
            });
        });
        return result with { Restored = restored, RestoreIncomplete = !restoreDone, RestoreBlocked = blocked.Distinct(StringComparer.Ordinal).ToList() };
    }

    /// <summary>Runs <paramref name="check"/> and states why the result can't be vouched for, if it can't.</summary>
    private static CheckResult Finish(string dir, List<ModeChange>? opened, Action<List<Quarantined>, List<string>, List<BadName>, List<PausedOp>> check)
    {
        var moved = new List<Quarantined>();
        var unlistable = new List<string>();
        var badNames = new List<BadName>();
        var paused = new List<PausedOp>();
        var failures = new List<string>();
        try { check(moved, unlistable, badNames, paused); }
        catch (Exception e) { failures.Add(Out.Describe(e)); }
        if (unlistable.Count > 0)
            failures.Add("pen can't list these directories, so it can't see a repo inside one: " + string.Join(", ", unlistable.Take(RepoGuard.MaxListed).Select(u => RepoGuard.Rel(dir, u)))
                + (unlistable.Count > RepoGuard.MaxListed ? $" and {unlistable.Count - RepoGuard.MaxListed} more" : ""));
        if (badNames.Count > 0)
            failures.Add("these names aren't valid UTF-8, so pen can't look inside them or rename them, though git on this machine can still enter them and run what's there (shown with octal escapes): " + string.Join(", ", RepoGuard.ListedBadNames(dir, badNames)));
        if (moved.Any(q => q.To.Length == 0))
            failures.Add("a file named above couldn't be moved out of git's way or restored");
        return new(moved, failures.Count == 0 ? null : string.Join("; ", failures), opened, Paused: paused);
    }

    /// <summary>
    /// Adds u+rx to a directory, and u+w if <paramref name="write"/>. Never through a symlink, which an overlapping
    /// session could have swapped in since the listing: chmod would change
    /// whatever it points to. Checking just before is the portable way, since
    /// glibc's fchmodat can't do AT_SYMLINK_NOFOLLOW.
    /// </summary>
    internal static bool TryOpen(string path, List<ModeChange> opened, bool write = false)
    {
        try
        {
            if (RepoGuard.IsLink(path)) return false;
            var was = File.GetUnixFileMode(path);
            var now = was | UnixFileMode.UserRead | UnixFileMode.UserExecute | (write ? UnixFileMode.UserWrite : UnixFileMode.None);
            if (now == was) return false; // the mode isn't why it can't be listed
            File.SetUnixFileMode(path, now);
            opened.Add(new(path, was, now));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Check(string dir, bool startedWithGit, string? commonGitDir, IReadOnlyCollection<string>? modulesAtStart, ProtectedManifest? atStart, List<Quarantined> moved, List<string> unlistable, List<BadName> badNames, List<PausedOp> paused, Func<string, bool>? tryOpen, Func<string, bool>? tryOpenForWriting)
    {
        if (!Directory.Exists(dir)) return;
        // The agent could do this to a mount's root too. The user will fix the
        // mode and then run git, so "host git can't get in" doesn't excuse it.
        if (!Native.CanEnter(dir))
            throw new PenException($"pen can't enter {Out.Field(dir)}. Once its permissions are fixed, git can run whatever was planted there, until pen has checked it again");

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
            // One it can search, git and the shell prompt read through without listing it.
            if (Native.CanEnter(git) && !Native.CanList(git)) tryOpen?.Invoke(git);
            if (Directory.Exists(git) && !Native.CanList(git))
                throw new PenException($"pen can't read {Out.Field(git)}. Once its permissions are fixed, git can run whatever was planted there, until pen has checked it again");
            var commondir = Path.Combine(git, "commondir");
            if (RepoGuard.Exists(commondir))
                Move(commondir, "a normal repo never has one: it makes git read config and hooks from another directory", moved);
            known = CheckModuleRepos(git, modulesAtStart, moved, badNames, tryOpen, tryOpenForWriting);
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

        // Last, so what was just quarantined isn't looked through.
        if (startedWithGit)
        {
            SweepHazards(git, moved, unlistable, tryOpen, tryOpenForWriting, atStart);
            paused.AddRange(PausedGit.Find(git));
        }
    }

    /// <summary>
    /// Quarantines the submodule repos in <paramref name="gitDir"/> that
    /// weren't there at the start (<paramref name="modulesAtStart"/>; null: all
    /// are kept), any symlink where git keeps them, and a commondir in one that
    /// stays. Repos nested in a quarantined one go with it. A directory there
    /// that can't be read is quarantined whether or not it was there at the
    /// start, since nothing vouches for what's in it now. A worktrees dir is opened
    /// first if it can be (session end only: <paramref name="tryOpen"/>), since what's
    /// in it can be checked by name, and moving it breaks every worktree of the repo.
    /// Returns the repos left, whose checkouts in the tree are the project's own.
    /// </summary>
    private static HashSet<string> CheckModuleRepos(string gitDir, IReadOnlyCollection<string>? modulesAtStart, List<Quarantined> moved, List<BadName> badNames, Func<string, bool>? tryOpen, Func<string, bool>? tryOpenForWriting)
    {
        var listed = modulesAtStart == null ? null : new HashSet<string>(modulesAtStart, StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);
        var gone = new List<string>();
        var scan = RepoScan.ScanModules(gitDir);
        // Each can only open once, so this ends.
        while (tryOpen != null && scan.Unreadable.Any(d => Path.GetFileName(d) == "worktrees" && tryOpen(d)))
            scan = RepoScan.ScanModules(gitDir);
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
            const string why = "a directory where git keeps submodule repos that can't be read, so pen can't check it: inspect its contents before you fix its permissions";
            if (!TryRenameWithOpening(dir, why, moved, tryOpenForWriting, breakLayout: true))
                Move(dir, why, moved, breakLayout: true);
        }
        return known;
    }

    private static void SweepHazards(string git, List<Quarantined> moved, List<string> unreachable, Func<string, bool>? tryOpen, Func<string, bool>? tryOpenForWriting, ProtectedManifest? atStart)
    {
        foreach (var hazard in PausedGit.PromptHazards(git, tryOpen, unreachable, atStart))
            MoveHazard(hazard, moved, tryOpenForWriting);
    }

    /// <summary>
    /// Runs the check. If it stops early, what a shell prompt would run still has
    /// to go, since the prompt reads through a git dir pen can't list and doesn't
    /// wait for the failure to be read: every known name is tried directly. If
    /// even that stops, the check's own failure stands and says not to run git.
    /// </summary>
    private static void SweepingHazardsIfItStops(string? git, List<Quarantined> moved, List<string> unreachable, Func<string, bool>? tryOpen, Func<string, bool>? tryOpenForWriting, ProtectedManifest? atStart, Action check)
    {
        try { check(); }
        catch (Exception)
        {
            if (git != null)
            {
                try { SweepHazards(git, moved, unreachable, tryOpen, tryOpenForWriting, atStart); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PenException) { }
            }
            throw;
        }
    }

    /// <summary>
    /// Gets what a shell prompt would run out of its way, since the prompt doesn't
    /// wait for the user to read a warning, and the agent could have made the
    /// directory holding it read-only. The order: rename it; add u+w to the
    /// directory holding it and rename it (only at session end: <paramref name="tryOpenForWriting"/>);
    /// then the same for the state directory holding it. If none of that works
    /// the last Move records that it couldn't, which makes the check a Failure.
    /// </summary>
    private static void MoveHazard(PromptHazard hazard, List<Quarantined> moved, Func<string, bool>? tryOpenForWriting)
    {
        // Gone with a state directory that an earlier one in it took aside.
        if (!RepoGuard.Exists(hazard.Path)) return;
        if (TryRenameWithOpening(hazard.Path, hazard.Why, moved, tryOpenForWriting)) return;
        if (hazard.StateDir != null && TryRenameWithOpening(hazard.StateDir, hazard.Why, moved, tryOpenForWriting)) return;
        Move(hazard.StateDir ?? hazard.Path, hazard.Why, moved);
    }

    private static bool TryRenameWithOpening(string path, string why, List<Quarantined> moved, Func<string, bool>? tryOpenForWriting, bool breakLayout = false)
    {
        var held = new List<Quarantined>();
        Move(path, why, held, breakLayout);
        if (held[0].To.Length == 0 && tryOpenForWriting?.Invoke(Path.GetDirectoryName(path)!) == true)
        {
            held.Clear();
            Move(path, why, held, breakLayout);
        }
        if (held[0].To.Length == 0) return false;
        moved.AddRange(held);
        return true;
    }

    /// <summary>
    /// Renames <paramref name="path"/> to a free "….pen-quarantine" name. For a
    /// .git directory (<paramref name="breakLayout"/>), its HEAD goes too:
    /// renamed, it would still look like a bare repo to git. Returns whether it moved.
    /// </summary>
    internal static bool Move(string path, string why, List<Quarantined> moved, bool breakLayout = false)
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
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Never fatal: the rest still gets checked, and the warning names it.
            var reason = e is UnauthorizedAccessException u ? Out.PermissionDenied(u) : e.Message; // Native.Rename's is strerror's
            moved.Add(new(path, "", $"{why}. COULD NOT MOVE IT ({reason}): don't run git here until you've dealt with it"));
            return false;
        }
    }

    internal static string FreeName(string path)
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
        sb.AppendLine("pen: They've been moved out of git's way:");
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

    /// <summary>
    /// What to do about a session whose protected files couldn't all be put back
    /// and which keeps its copies: first the way that uses them, in one command so
    /// that nothing (a shell prompt) runs git between it and pen's retry. Only
    /// then the way that gives them up.
    /// </summary>
    public static string KeptCopiesAdvice(SessionDir session, IReadOnlyList<string>? blocked)
    {
        // A linked worktree's key is its main checkout's, and only there is pen sure to find this session.
        var repoDir = session.ReadRepoDir();
        var retry = (repoDir == null ? "" : $"cd {Quote(repoDir)} && ") + RetryCommand;
        var fix = blocked is { Count: > 0 }
            ? $"chmod u+rwx {string.Join(' ', blocked.Select(Quote))} && {retry}"
            : $"deal with what's named above, then: {retry}";
        return $"""
            pen: To restore the good copies pen kept, run this, as one line: {fix}
            pen: Only if that can't work: rm -r {Quote(session.Path)} discards them and accepts what's on disk. Compare first with the copies in {Quote(session.ProtectedDir)} (manifest.json lists them).
            """;
    }

    /// <summary>Starts a session that does nothing, which first finishes any session left unfinished, or refuses.</summary>
    internal const string RetryCommand = "pen true";

    /// <summary>The loud warning for a check that couldn't vouch for <paramref name="dir"/>: <paramref name="why"/> says what stopped it.</summary>
    public static string NotCheckedWarning(string dir, string why) =>
        $"""
        pen: WARNING: git wasn't checked: don't run git here ({Out.Field(dir)}).
        pen: The check for files that make git on this machine run code from the sandbox couldn't finish: {why}
        pen: Your shell prompt and IDE may run git too, so keep them out of this directory. Inspect its .git (config, hooks, modules, rebase-merge, rebase-apply, sequencer) and any repos nested in it by hand first.
        """;

    /// <summary>Prints what a session-end check found: first what it renamed, then any paused operations, then that it didn't finish.</summary>
    public static void Report(string dir, CheckResult check)
    {
        if (check.Moved.Count > 0) Out.Raw(Warning(dir, check.Moved));
        foreach (var r in check.Restored ?? [])
            Out.Line($"{RepoGuard.Rel(dir, r.Path)}: {r.What}");
        foreach (var c in check.Opened ?? [])
            Out.Line($"changed {RepoGuard.Rel(dir, c.Path)} from mode {Octal(c.Was)} to {Octal(c.Now)} so it could be checked, and left it that way");
        if (check.Paused is { Count: > 0 } paused) Out.Raw(PausedGit.Warning(dir, paused));
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
