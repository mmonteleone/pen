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

    /// <summary>Adds u+rx to a directory so it can be listed, or null when this run may not change modes.</summary>
    private readonly Func<string, bool>? _tryOpen;

    /// <summary>Adds u+rwx to a directory so it can be renamed in, or null if this run may not change modes.</summary>
    private readonly Func<string, bool>? _tryOpenForWriting;

    /// <summary>What it renamed, or couldn't rename, in order: the protected files' first.</summary>
    private readonly List<Quarantined> _moved = [];

    /// <summary>The directories it couldn't list, so what's in them can't be vouched for.</summary>
    private readonly List<string> _unlistable = [];

    /// <summary>The entries whose names aren't valid UTF-8.</summary>
    private readonly List<BadName> _badNames = [];

    /// <summary>The paused operations found, which stay in place.</summary>
    private readonly List<PausedOp> _paused = [];

    /// <summary>The directories it opened and left so, in order: the protected files' first.</summary>
    private readonly List<ModeChange> _opened = [];

    /// <summary>The protected files that changed with nothing to set aside.</summary>
    private readonly List<RestoredFile> _restored = [];

    /// <summary>The directories whose modes kept protected files from being put back.</summary>
    private readonly List<string> _blocked = [];

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
        if (mayOpen)
        {
            _tryOpen = TryOpenToRead;
            _tryOpenForWriting = TryOpenToWrite;
        }
    }

    /// <summary>Checks the dir against what's given and quarantines what it flags. Restores nothing.</summary>
    /// <param name="startedWithGit">Whether the session started with a .git there.</param>
    /// <param name="commonGitDir">The git dir it started with, or null for dir/.git.</param>
    /// <param name="modulesAtStart">The module repos in that git dir at the start, or null for those there now.</param>
    /// <returns>The result, with no Opened, Restored or RestoreBlocked.</returns>
    public CheckResult RunWithoutRestoring(
        bool startedWithGit,
        string? commonGitDir,
        IReadOnlyCollection<string>? modulesAtStart)
    {
        string? stopped = null;
        try
        {
            CheckSweepingIfItStops(startedWithGit, commonGitDir, modulesAtStart);
        }
        catch (Exception exception)
        {
            stopped = Out.Describe(exception);
        }
        return Summarize(stopped);
    }

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
    public CheckResult RunAtSessionEnd(SessionDir session)
    {
        _restoreDone = !File.Exists(session.ProtectedManifest);
        string? stopped = null;
        try
        {
            RestoreAndCheck(session);
        }
        catch (Exception exception)
        {
            stopped = Out.Describe(exception);
        }
        return Summarize(stopped) with
        {
            Opened = _opened,
            Restored = _restored,
            RestoreIncomplete = !_restoreDone,
            RestoreBlocked = _blocked.Distinct(StringComparer.Ordinal).ToList(),
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

        if (_badNames.Count > 0)
        {
            failures.Add("these names aren't valid UTF-8, so pen can't look inside them or rename them, though git on "
                + "this machine can still enter them and run what's there (shown with octal escapes): "
                + string.Join(", ", RepoGuard.FormatBadNames(_dir, _badNames)));
        }
        if (_moved.Any(quarantined => quarantined.To.Length == 0))
            failures.Add("a file named above couldn't be moved out of git's way or restored");
        var failure = failures.Count == 0 ? null : string.Join("; ", failures);
        return new() { Moved = _moved, Failure = failure, Paused = _paused };
    }

    /// <summary>Words the failure for the directories it couldn't list, naming the first few.</summary>
    private string DescribeUnlistable()
    {
        var named = _unlistable.Take(RepoGuard.MaxListed).Select(path => RepoGuard.FormatPath(_dir, path));
        var text = "pen can't list these directories, so it can't see a repo inside one: " + string.Join(", ", named);
        if (_unlistable.Count > RepoGuard.MaxListed)
            text += $" and {_unlistable.Count - RepoGuard.MaxListed} more";
        return text;
    }

    /// <summary>Runs the check for <see cref="RunWithoutRestoring"/>, sweeping prompt hazards if it stops.</summary>
    private void CheckSweepingIfItStops(
        bool startedWithGit,
        string? commonGitDir,
        IReadOnlyCollection<string>? modulesAtStart)
    {
        var swept = FindSweptGitDir(startedWithGit, commonGitDir);
        try
        {
            Check(startedWithGit, commonGitDir, modulesAtStart, atStart: null);
        }
        catch (Exception)
        {
            SweepHazardsAfterStop(swept, atStart: null);
            throw;
        }
    }

    /// <summary>
    /// Reads what the session recorded, puts its protected files back and checks the dir, sweeping prompt hazards if
    /// any of the last two stops.
    /// </summary>
    private void RestoreAndCheck(SessionDir session)
    {
        var (startedWithGit, commonGitDir) = session.ReadGit();
        var atStart = ProtectedFiles.AtStart(session);
        var swept = FindSweptGitDir(startedWithGit, commonGitDir);
        try
        {
            RestoreProtectedFiles(session, commonGitDir);
            if (!Directory.Exists(_dir))
            {
                throw new PenException($"{Out.Field(_dir)} isn't there any more or can't be reached, so pen " +
                    "couldn't check it. If it was moved, inspect its .git by hand before running git in it");
            }
            // A session from a Pen that didn't record its module repos keeps none.
            Check(startedWithGit, commonGitDir, session.ReadModules() ?? [], atStart);
        }
        catch (Exception)
        {
            SweepHazardsAfterStop(swept, atStart);
            throw;
        }
    }

    /// <summary>Puts the session's protected files back, keeping what the restore did even if it stops.</summary>
    private void RestoreProtectedFiles(SessionDir session, string? commonGitDir)
    {
        var restore = new ProtectedFileRestore(session, _dir, commonGitDir);
        try
        {
            _restoreDone = restore.Run();
        }
        finally
        {
            _moved.AddRange(restore.Moved);
            _opened.AddRange(restore.Opened);
            _restored.AddRange(restore.Restored);
            _blocked.AddRange(restore.Blocked);
        }
    }

    /// <summary>Finds the git dir whose prompt hazards are swept if the check stops.</summary>
    /// <returns>The git dir the session started with, or null if it started with none.</returns>
    private string? FindSweptGitDir(bool startedWithGit, string? commonGitDir)
    {
        if (!startedWithGit)
            return null;
        return commonGitDir ?? Path.Combine(_dir, ".git");
    }

    /// <summary>
    /// Checks the dir: a repo made during the session, the git dir's commondir and module repos, nested repos, then
    /// what a shell prompt would run and the paused operations.
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

    /// <summary>Quarantines the repos nested in the tree that aren't the project's own.</summary>
    /// <param name="git">The git dir the tree's own worktrees belong to.</param>
    /// <param name="known">The module repos whose checkouts in the tree are the project's own.</param>
    private void QuarantineNestedRepos(string git, HashSet<string> known)
    {
        var scan = RepoScan.ScanNested(_dir, git, known, _tryOpen);
        _unlistable.AddRange(scan.UnlistableDirs);
        _badNames.AddRange(scan.BadNames);
        foreach (var repo in scan.Repos)
            Quarantine(repo.Path, DescribeNested(repo.Kind), breakLayout: repo.Kind == NestedKind.GitDir);
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

    /// <summary>Quarantines a module repo that's a symlink or wasn't there at the start.</summary>
    /// <param name="repo">The module repo.</param>
    /// <param name="listed">The module repos there at the start, or null to keep all.</param>
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
            Quarantine(repo, "a submodule repo made during the session: " +
                "its config and hooks were writable from the sandbox", breakLayout: true);
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
        if (attempt.To.Length == 0 && _tryOpenForWriting?.Invoke(Path.GetDirectoryName(path)!) == true)
            attempt = GitCheck.Move(path, why, breakLayout);
        if (attempt.To.Length == 0)
            return false;
        _moved.Add(attempt);
        return true;
    }

    /// <summary>Renames <paramref name="path"/> aside and records it, or records that it couldn't.</summary>
    private void Quarantine(string path, string why, bool breakLayout) =>
        _moved.Add(GitCheck.Move(path, why, breakLayout));

    private bool TryOpenToRead(string path) => TryOpenRecording(path, write: false);

    private bool TryOpenToWrite(string path) => TryOpenRecording(path, write: true);

    /// <summary>Opens a directory with <see cref="GitCheck.TryOpen"/> and records the mode change.</summary>
    /// <returns>Whether it changed the mode.</returns>
    private bool TryOpenRecording(string path, bool write)
    {
        if (!GitCheck.TryOpen(path, write, out var change))
            return false;
        _opened.Add(change);
        return true;
    }
}
