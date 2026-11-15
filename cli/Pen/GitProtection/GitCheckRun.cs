using Pen.Platform;
using Pen.Sessions;

namespace Pen.GitProtection;

/// <summary>One run of the .git check over a project dir, which keeps what it finds and changes as it goes.</summary>
/// <remarks>
/// <para>
/// It reads files directly and never runs git, and it renames whatever would make host git read config or hooks the
/// agent could write, before the shell prompt or an IDE runs git there.
/// </para>
/// <para>
/// What it finds goes into its fields as it's found, so a run that stops part way still reports everything it found
/// and renamed before it stopped, then says why it stopped. Use each instance for one run.
/// </para>
/// </remarks>
internal sealed class GitCheckRun
{
    /// <summary>The project dir; messages are relative to it.</summary>
    private readonly string _dir;

    /// <summary>Whether this run may add u+rx (or u+rwx) to a directory and keep that.</summary>
    private readonly bool _mayOpen;

    /// <summary>Adds u+rx to a directory so it can be listed, or null when this run may not change modes.</summary>
    private readonly Func<string, bool>? _tryOpen;

    /// <summary>What it renamed or couldn't, put back and opened, in order: the protected files' first.</summary>
    private readonly CheckChanges _changes = new();

    /// <summary>The directories it couldn't list, so what's in them can't be vouched for.</summary>
    private readonly List<string> _unlistable = [];

    /// <summary>The entries whose names aren't valid UTF-8.</summary>
    private readonly List<BadName> _badNames = [];

    /// <summary>The paused operations found, which stay in place.</summary>
    private readonly List<PausedOp> _paused = [];

    /// <summary>Whether the protected files are all back, or there were none to put back.</summary>
    private bool _restoreDone;

    /// <summary>Prepares a run over <paramref name="dir"/>.</summary>
    /// <param name="dir">The project dir.</param>
    /// <param name="mayOpen">
    /// Whether it may add u+rx (or u+rwx) to a directory it can enter but not list (or rename in), and keep that:
    /// only at a session's end, when no session of the repo can still be running.
    /// </param>
    public GitCheckRun(string dir, bool mayOpen)
    {
        _dir = dir;
        _mayOpen = mayOpen;
        if (mayOpen)
            _tryOpen = TryOpenToRead;
    }

    /// <summary>Checks the dir against what's given and quarantines what it flags. Restores nothing.</summary>
    /// <param name="startedWithGit">Whether the session started with a .git there.</param>
    /// <param name="commonGitDir">The git dir it started with, or null for dir/.git.</param>
    /// <param name="modulesAtStart">The module repos in that git dir at the start, or null for those there now.</param>
    /// <returns>The result, with Restored and RestoreBlocked empty, and Opened empty if mayOpen was false.</returns>
    public CheckResult RunWithoutRestoring(
        bool startedWithGit,
        string? commonGitDir,
        IReadOnlyCollection<string>? modulesAtStart) =>
        Run(session: null, () => (startedWithGit, commonGitDir), () => modulesAtStart);

    /// <summary>
    /// Checks the dir for a session that's ending, against what <paramref name="session"/> recorded at its start.
    /// </summary>
    /// <returns>The result; the caller must not run git unless its Failure is null.</returns>
    /// <remarks>
    /// <para>
    /// First the protected files go back to their saved copies (<see cref="ProtectedFileRestore"/>), so what follows
    /// reads those. If that doesn't complete, RestoreIncomplete says the session's files must be kept for a retry,
    /// since they hold the only good copies. A directory it can enter but not list gets u+rx so it can be scanned,
    /// and keeps it (Opened).
    /// </para>
    /// <para>
    /// Never throws: whatever goes wrong here (or in code it reaches), a session must still be torn down, and
    /// retrying wouldn't help.
    /// </para>
    /// </remarks>
    public CheckResult RunAtSessionEnd(SessionDir session) =>
        // A session from a Pen that didn't record its module repos keeps none.
        Run(session, session.ReadGit, () => session.ReadModules() ?? []);

    /// <summary>Runs the check, first putting back the protected files of the session that's ending, if any.</summary>
    /// <param name="session">The session that's ending, or null when no session's end is being checked.</param>
    /// <param name="readGit">Reads whether there was a .git there and its git dir (null for dir/.git).</param>
    /// <param name="readModules">
    /// Reads the module repos in that git dir to keep, or null for those there now. It's called after the restore,
    /// and not if the restore stops.
    /// </param>
    /// <returns>
    /// The result; without a session, Restored and RestoreBlocked are empty and RestoreIncomplete is false.
    /// </returns>
    /// <remarks>Never throws: what stops the check is its Failure.</remarks>
    private CheckResult Run(
        SessionDir? session,
        Func<(bool StartedWithGit, string? CommonGitDir)> readGit,
        Func<IReadOnlyCollection<string>?> readModules)
    {
        // Decides RestoreIncomplete if the run stops before the restore does.
        _restoreDone = session == null || !File.Exists(session.ProtectedManifest);
        string? stopped = null;
        try
        {
            RestoreAndCheck(session, readGit, readModules);
        }
        catch (Exception exception)
        {
            stopped = Out.Describe(exception);
        }
        return Summarize(stopped) with
        {
            Opened = _changes.Opened,
            Restored = _changes.Restored,
            RestoreIncomplete = !_restoreDone,
            RestoreBlocked = _changes.Blocked.Distinct(StringComparer.Ordinal).ToList(),
        };
    }

    /// <summary>Builds the result of the run, stating why it can't be vouched for, if it can't.</summary>
    /// <param name="stopped">What stopped the run early, or null if nothing did.</param>
    /// <returns>The result, with Moved, Failure and Paused.</returns>
    private CheckResult Summarize(string? stopped)
    {
        var failures = new List<string>();
        if (stopped != null)
            failures.Add(stopped);
        if (_unlistable.Count > 0)
            failures.Add(DescribeUnlistable());

        // The walk of the git dir meets the names the module walk reports too.
        var badNames = _badNames.DistinctBy(n => n.DisplayPath, StringComparer.Ordinal).ToList();
        if (badNames.Count > 0)
        {
            failures.Add("these names aren't valid UTF-8, so pen can't look inside them or rename them, though git on "
                + "this machine can still enter them and run what's there (shown with octal escapes): "
                + string.Join(", ", RepoGuard.FormatBadNames(_dir, badNames)));
        }
        if (_changes.Moved.Any(quarantined => quarantined.Failed))
            failures.Add("a file named above couldn't be moved out of git's way, removed or restored");
        var failure = failures.Count == 0 ? null : string.Join("; ", failures);
        return new() { Moved = _changes.Moved, Failure = failure, Paused = _paused };
    }

    /// <summary>Words the failure for the directories it couldn't list, naming the first few.</summary>
    private string DescribeUnlistable()
    {
        // The walk of the git dir and the prompt-hazard scan can both meet a worktrees dir, or an entry in one.
        var unlistable = _unlistable.Distinct(StringComparer.Ordinal).ToList();
        var named = unlistable.Take(RepoGuard.MaxListed).Select(path => RepoGuard.FormatPath(_dir, path));
        var text = "pen can't list these directories, so it can't see a repo inside one: " + string.Join(", ", named);
        if (unlistable.Count > RepoGuard.MaxListed)
            text += $" and {unlistable.Count - RepoGuard.MaxListed} more";
        return text;
    }

    /// <summary>
    /// Reads what the check is against, puts the session's protected files back if there's a session, and checks
    /// the dir, sweeping prompt hazards if either of the last two stops.
    /// </summary>
    private void RestoreAndCheck(
        SessionDir? session,
        Func<(bool StartedWithGit, string? CommonGitDir)> readGit,
        Func<IReadOnlyCollection<string>?> readModules)
    {
        var (startedWithGit, commonGitDir) = readGit();
        var atStart = session == null ? null : ProtectedFiles.AtStart(session);
        var swept = FindSweptGitDir(startedWithGit, commonGitDir);
        try
        {
            if (session != null)
            {
                RestoreProtectedFiles(session, commonGitDir);
                if (!Directory.Exists(_dir))
                {
                    throw new PenException($"{Out.Field(_dir)} isn't there any more or can't be reached, so pen " +
                        "couldn't check it. If it was moved, inspect its .git by hand before running git in it");
                }
            }
            Check(startedWithGit, commonGitDir, readModules(), atStart);
        }
        catch (Exception)
        {
            SweepHazardsAfterStop(swept, atStart);
            throw;
        }
    }

    /// <summary>Puts the session's protected files back, recording what the restore does even if it stops.</summary>
    private void RestoreProtectedFiles(SessionDir session, string? commonGitDir) =>
        _restoreDone = new ProtectedFileRestore(session, _dir, commonGitDir, _changes).Run();

    /// <summary>Finds the git dir whose prompt hazards are swept if the check stops.</summary>
    /// <returns>The git dir the session started with, or null if it started with none.</returns>
    private string? FindSweptGitDir(bool startedWithGit, string? commonGitDir)
    {
        if (!startedWithGit)
            return null;
        return commonGitDir ?? Path.Combine(_dir, ".git");
    }

    /// <summary>
    /// Checks the dir: a repo made during the session, the git dir's commondir and module repos, repos in the git dir
    /// where git keeps none, nested repos and symlinks into the git dir, then what a shell prompt would run and the
    /// paused operations.
    /// </summary>
    /// <param name="startedWithGit">Whether the session started with a .git there.</param>
    /// <param name="commonGitDir">The git dir it started with, or null for dir/.git.</param>
    /// <param name="modulesAtStart">The module repos in that git dir at the start, or null for those there now.</param>
    /// <param name="atStart">What the session saved at its start, or null when there's no such record.</param>
    private void Check(
        bool startedWithGit,
        string? commonGitDir,
        IReadOnlyCollection<string>? modulesAtStart,
        ProtectedManifest? atStart)
    {
        if (!Directory.Exists(_dir))
            return;
        // The agent could do this to a mount's root too. The user will fix the
        // mode and then run git, so "host git can't get in" doesn't excuse it.
        if (!Native.CanEnter(_dir))
        {
            throw new PenException($"pen can't enter {Out.Field(_dir)}. Once its permissions are " +
                "fixed, git can run whatever was planted there, until pen has checked it again");
        }

        var git = Path.Combine(_dir, ".git");
        HashSet<string> known = [];
        if (!startedWithGit)
        {
            QuarantineNewRepo(git);
        }
        else
        {
            git = commonGitDir ?? git;
            known = CheckGitDir(git, modulesAtStart);
            QuarantineGitDirRepos(git, known);
        }

        QuarantineNestedRepos(git, known);

        // Last, so what was just quarantined isn't looked through.
        if (startedWithGit)
        {
            SweepHazards(git, atStart);
            _paused.AddRange(PausedGit.Find(git));
        }
    }

    /// <summary>Quarantines a .git made in a dir that the session started without one.</summary>
    private void QuarantineNewRepo(string git)
    {
        if (RepoGuard.Exists(git))
        {
            Quarantine(git, "a repo created during the session (git init): " +
                "its config and hooks were writable from the sandbox", breakLayout: true);
        }
    }

    /// <summary>Checks the git dir the session started with: that it can be read, its commondir and modules.</summary>
    /// <returns>The module repos left, whose checkouts in the tree are the project's own.</returns>
    private HashSet<string> CheckGitDir(string git, IReadOnlyCollection<string>? modulesAtStart)
    {
        // Like the dir itself: nothing under it can be seen, and it's what git reads first.
        // One it can search, git and the shell prompt read through without listing it.
        if (Native.CanEnter(git) && !Native.CanList(git))
            _tryOpen?.Invoke(git);
        if (Directory.Exists(git) && !Native.CanList(git))
        {
            throw new PenException($"pen can't read {Out.Field(git)}. Once its permissions are " +
                "fixed, git can run whatever was planted there, until pen has checked it again");
        }

        var commondir = Path.Combine(git, "commondir");
        if (RepoGuard.Exists(commondir))
        {
            Quarantine(commondir, "a normal repo never has one: it makes " +
                "git read config and hooks from another directory", breakLayout: false);
        }

        return CheckModuleRepos(git, modulesAtStart);
    }

    /// <summary>
    /// Quarantines the repos nested in the tree that aren't the project's own, and removes the symlinks in it that
    /// lead into the git dir.
    /// </summary>
    /// <param name="git">The git dir the tree's own worktrees belong to.</param>
    /// <param name="known">The module repos whose checkouts in the tree are the project's own.</param>
    private void QuarantineNestedRepos(string git, HashSet<string> known) =>
        QuarantineFound(
            RepoScan.ScanNested(_dir, git, known, _tryOpen),
            repo => DescribeNested(repo.Kind),
            movedAside => RepoScan.ScanNested(_dir, git, known, _tryOpen, from: movedAside));

    /// <summary>
    /// Quarantines the repos anywhere in the git dir where git keeps none (<see cref="RepoScan.ScanGitDir"/>).
    /// </summary>
    /// <param name="git">The git dir.</param>
    /// <param name="known">The module repos kept in it, which are its own.</param>
    private void QuarantineGitDirRepos(string git, HashSet<string> known) =>
        QuarantineFound(
            RepoScan.ScanGitDir(git, known, _tryOpen),
            DescribeInGitDir,
            movedAside => RepoScan.ScanGitDir(git, known, _tryOpen, from: movedAside));

    /// <summary>
    /// Quarantines what a walk found and removes the symlinks it found, then walks each .git directory it moved
    /// aside, and so on.
    /// </summary>
    /// <param name="scan">What the walk found.</param>
    /// <param name="describe">Words why a repo it found is quarantined.</param>
    /// <param name="walkFrom">Walks again from the directories given, in the same way.</param>
    /// <remarks>
    /// A walk doesn't enter a .git directory, whose contents are that repo's. Once it's moved aside, with its HEAD,
    /// what's inside is reachable all the same (by a symlink to its new, predictable name, or one in it), so it's
    /// walked before the check ends: each round is inside the last one's, so it ends.
    /// </remarks>
    private void QuarantineFound(NestedScan scan, Func<NestedRepo, string> describe,
        Func<List<string>, NestedScan> walkFrom)
    {
        while (true)
        {
            _unlistable.AddRange(scan.UnlistableDirs);
            _badNames.AddRange(scan.BadNames);
            var movedAside = new List<string>();
            foreach (var repo in scan.Repos)
            {
                var isDir = repo.Kind == NestedKind.GitDir;
                var moved = GitCheck.Move(repo.Path, describe(repo), breakLayout: isDir);
                _changes.Moved.Add(moved);
                if (isDir && !moved.Failed)
                    movedAside.Add(moved.To);
            }
            foreach (var link in scan.GitDirLinks)
            {
                RemoveLink(link, "a symlink into the git dir, or one that would lead there once a directory it " +
                    "names is made: git run through it would use whatever repo the sandbox put there, and renamed " +
                    "it would lead there still, so it's removed (a symlink holds no data)");
            }
            foreach (var link in scan.UntracedLinks)
            {
                RemoveLink(link, "a symlink pen can't trace (a directory on the way can't be searched, or its " +
                    "target can't be read or isn't valid UTF-8), so it may lead into the git dir, where git run " +
                    "through it would use whatever repo the sandbox put there: it's removed (a symlink holds no data)");
            }
            if (movedAside.Count == 0)
                return;
            scan = walkFrom(movedAside);
        }
    }

    /// <summary>
    /// Removes the symlink <paramref name="link"/> and records it, adding u+w to the directory holding it first if
    /// that's what stops it and this run may, or records that it couldn't.
    /// </summary>
    private void RemoveLink(string link, string why)
    {
        var attempt = GitCheck.RemoveLink(_dir, link, why);
        if (attempt.Failed && _mayOpen && _changes.TryOpenRecording(Path.GetDirectoryName(link)!, write: true))
            attempt = GitCheck.RemoveLink(_dir, link, why);
        _changes.Moved.Add(attempt);
    }

    /// <summary>Words why a nested repo of this kind is quarantined.</summary>
    private static string DescribeNested(NestedKind kind) => kind switch
    {
        NestedKind.Bare => "a bare repo: git takes this directory " +
            "as one, with config and hooks from the sandbox",
        NestedKind.GitFile => "a .git file that isn't one of this repo's " +
            "worktrees or submodules: it points git at another directory",
        NestedKind.GitLink => "a .git symlink: it points git at another directory",
        _ => "a nested repo: its config and hooks were writable from the sandbox",
    };

    /// <summary>Words why a repo in the git dir is quarantined.</summary>
    private static string DescribeInGitDir(NestedRepo repo) => repo switch
    {
        { Place: GitDirPlace.Refs, Kind: NestedKind.Bare } =>
            "ref names (a branch named HEAD beside ones under objects/ and refs/, say) that lay this directory " +
            "out as a git dir: git run from it (through a symlink in the project, say) takes it for one and uses " +
            "its config and hooks, which were writable from the sandbox. Renaming its HEAD breaks that; rename " +
            "those refs before putting it back",
        _ => DescribeInGitDir(repo.Kind),
    };

    /// <summary>Words why a repo of this kind in the git dir is quarantined.</summary>
    private static string DescribeInGitDir(NestedKind kind) => kind switch
    {
        NestedKind.Bare => "a directory inside the git dir that git takes for a git dir of its own, where git keeps " +
            "none outside modules/ and worktrees/: git run from it (through a symlink in the project, say) uses " +
            "its config and hooks, which were writable from the sandbox",
        NestedKind.GitFile => "a .git file inside the git dir that isn't one of this repo's worktrees or " +
            "submodules: git run from that directory (through a symlink in the project, say) follows it to " +
            "another directory",
        NestedKind.GitLink => "a .git symlink inside the git dir: git run from that directory (through a " +
            "symlink in the project, say) follows it to another directory",
        _ => "a repo inside the git dir, where git keeps none outside modules/: git run from that directory " +
            "(through a symlink in the project, say) uses it, and its config and hooks were writable from the sandbox",
    };

    /// <summary>
    /// Quarantines the submodule repos in <paramref name="gitDir"/> that weren't there at the start, any symlink
    /// where git keeps them, and a commondir in one that stays.
    /// </summary>
    /// <param name="gitDir">The git dir.</param>
    /// <param name="modulesAtStart">The module repos there at the start, or null to keep all.</param>
    /// <returns>The repos left, whose checkouts in the tree are the project's own.</returns>
    /// <remarks>
    /// Repos nested in a quarantined one go with it. A directory there that can't be read is quarantined whether or
    /// not it was there at the start, since nothing vouches for what's in it now.
    /// </remarks>
    private HashSet<string> CheckModuleRepos(string gitDir, IReadOnlyCollection<string>? modulesAtStart)
    {
        var listed = modulesAtStart == null ? null : new HashSet<string>(modulesAtStart, StringComparer.Ordinal);
        var known = new HashSet<string>(StringComparer.Ordinal);
        var gone = new List<string>();
        var scan = ScanModules(gitDir);
        _badNames.AddRange(scan.BadNames);
        foreach (var repo in scan.Repos)
        {
            if (gone.Any(quarantined => RepoGuard.IsInside(quarantined, repo)))
                continue;
            if (TryQuarantineModuleRepo(repo, listed))
                gone.Add(repo);
            else if (KeepsModuleRepo(repo))
                known.Add(repo);
        }
        foreach (var unreadable in scan.UnreadableDirs)
        {
            if (gone.Any(quarantined => RepoGuard.IsInside(quarantined, unreadable)))
                continue;
            const string why = "a directory where git keeps submodule repos that can't be read, " +
                "so pen can't check it: inspect its contents before you fix its permissions";
            if (!TryRenameWithOpening(unreadable, why, breakLayout: true))
                Quarantine(unreadable, why, breakLayout: true);
        }
        return known;
    }

    /// <summary>Scans the module repos in <paramref name="gitDir"/>, opening the worktrees dirs it can first.</summary>
    /// <remarks>
    /// A worktrees dir is opened if it can be (session end only), since what's in it can be checked by name, and
    /// moving it breaks every worktree of the repo.
    /// </remarks>
    private ModuleScan ScanModules(string gitDir)
    {
        var scan = RepoScan.ScanModules(gitDir);
        // Each can only open once, so this ends.
        while (_tryOpen is { } tryOpen
            && scan.UnreadableDirs.Any(dir => Path.GetFileName(dir) == "worktrees" && tryOpen(dir)))
        {
            scan = RepoScan.ScanModules(gitDir);
        }
        return scan;
    }

    /// <summary>Quarantines a module repo that's a symlink or the session didn't list at its start.</summary>
    /// <param name="repo">The module repo.</param>
    /// <param name="listed">
    /// The module repos the session listed at its start (<see cref="ModuleBaseline"/>), or null to keep all.
    /// </param>
    /// <returns>Whether it's one of those, so it's gone (or named as couldn't be moved).</returns>
    private bool TryQuarantineModuleRepo(string repo, HashSet<string>? listed)
    {
        if (RepoGuard.IsLink(repo))
        {
            Quarantine(repo, "a symlink where git keeps a submodule's repo: " +
                "it points git at config and hooks in another directory", breakLayout: false);
            return true;
        }
        if (listed != null && !listed.Contains(repo))
        {
            Quarantine(repo, "a submodule repo that wasn't there at the session's start, or that another " +
                "session could have made: its config and hooks were writable from the sandbox", breakLayout: true);
            return true;
        }
        return false;
    }

    /// <summary>Quarantines a commondir in a module repo that stays.</summary>
    /// <returns>Whether the repo is the project's own: it has no commondir left.</returns>
    private bool KeepsModuleRepo(string repo)
    {
        var commondir = Path.Combine(repo, "commondir");
        if (RepoGuard.Exists(commondir))
        {
            Quarantine(commondir, "a submodule's repo never has one: it " +
                "makes git read config and hooks from another directory", breakLayout: false);
        }

        return !RepoGuard.Exists(commondir);
    }

    /// <summary>Quarantines what a shell prompt would run in <paramref name="git"/>.</summary>
    /// <param name="git">The git dir.</param>
    /// <param name="atStart">What the session saved at its start, or null when there's no such record.</param>
    /// <remarks>
    /// What the scan couldn't reach is kept even if it stops, as the directories in it can't be vouched for.
    /// </remarks>
    private void SweepHazards(string git, ProtectedManifest? atStart)
    {
        var scan = new PromptHazardScan(git, _tryOpen, atStart);
        try
        {
            scan.Run();
        }
        finally
        {
            _unlistable.AddRange(scan.UnreachableDirs);
        }
        foreach (var hazard in scan.Hazards)
            MoveHazard(hazard);
    }

    /// <summary>Sweeps prompt hazards for a check that stopped early, if there's a git dir to sweep.</summary>
    /// <param name="git">The git dir the session started with, or null if none.</param>
    /// <param name="atStart">What the session saved at its start, or null when there's no such record.</param>
    /// <remarks>
    /// If the check stops early, what a shell prompt would run still has to go, since the prompt reads through a git
    /// dir Pen can't list and doesn't wait for the failure to be read: every known name is tried directly. If even
    /// that stops, the check's own failure stands and says not to run git, so the caller rethrows it.
    /// </remarks>
    private void SweepHazardsAfterStop(string? git, ProtectedManifest? atStart)
    {
        if (git == null)
            return;
        try
        {
            SweepHazards(git, atStart);
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or PenException)
        {
            // The check's own failure stands, and says not to run git.
        }
    }

    /// <summary>Gets what a shell prompt would run out of its way.</summary>
    /// <remarks>
    /// The prompt doesn't wait for the user to read a warning, and the agent could have made the directory holding
    /// it read-only. The order: rename it; add u+w to the directory holding it and rename it (only at session end);
    /// then the same for the state directory holding it. If none of that works the last move records that it
    /// couldn't, which makes the check a Failure.
    /// </remarks>
    private void MoveHazard(PromptHazard hazard)
    {
        // Gone with a state directory that an earlier one in it took aside.
        if (!RepoGuard.Exists(hazard.Path))
            return;
        if (TryRenameWithOpening(hazard.Path, hazard.Why, breakLayout: false))
            return;
        if (hazard.StateDir != null && TryRenameWithOpening(hazard.StateDir, hazard.Why, breakLayout: false))
            return;
        Quarantine(hazard.StateDir ?? hazard.Path, hazard.Why, breakLayout: false);
    }

    /// <summary>
    /// Renames <paramref name="path"/> aside, adding u+w to the directory holding it first if that's what stops it
    /// and this run may.
    /// </summary>
    /// <returns>Whether it moved; if not, nothing is recorded, so the caller can try something else.</returns>
    private bool TryRenameWithOpening(string path, string why, bool breakLayout)
    {
        var attempt = GitCheck.Move(path, why, breakLayout);
        if (attempt.Failed && _mayOpen && _changes.TryOpenRecording(Path.GetDirectoryName(path)!, write: true))
            attempt = GitCheck.Move(path, why, breakLayout);
        if (attempt.Failed)
            return false;
        _changes.Moved.Add(attempt);
        return true;
    }

    /// <summary>Renames <paramref name="path"/> aside and records it, or records that it couldn't.</summary>
    private void Quarantine(string path, string why, bool breakLayout) =>
        _changes.Moved.Add(GitCheck.Move(path, why, breakLayout));

    private bool TryOpenToRead(string path) => _changes.TryOpenRecording(path, write: false);
}
