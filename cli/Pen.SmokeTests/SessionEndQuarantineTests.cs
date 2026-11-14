namespace Pen.SmokeTests;

/// <summary>What the session-end check quarantines (a commondir, new module repos, nested repos, a repo in a
/// directory left unlistable, a .git made in a project without one) and what it leaves alone (a git worktree add
/// worktree), also when the next pen ends a session whose pen was killed.</summary>
public sealed class SessionEndQuarantineTests(SessionEndQuarantineTests.Fixture fixture)
    : IClassFixture<SessionEndQuarantineTests.Fixture>
{
    private const string Warning = "pen: WARNING";

    [Fact(DisplayName = "touch .git/commondir: warned at session end")]
    public void CommondirWarns() => SmokeAssert.Contains(fixture.Commondir.Session, Warning);

    [Fact(DisplayName = "...and moved to .git/commondir.pen-quarantine before pen returned")]
    public void CommondirIsMoved() => AssertMoved(fixture.Commondir);

    [Fact(DisplayName = "...with how to put it back")]
    public void CommondirSaysHowToPutItBack() => AssertSaysHowToPutItBack(fixture.Commondir);

    [Fact(DisplayName = "git init -q --bare .git/modules/x: warned at session end")]
    public void ModuleRepoWarns() => SmokeAssert.Contains(fixture.ModuleRepo.Session, Warning);

    [Fact(DisplayName = "...and moved to .git/modules/x.pen-quarantine before pen returned")]
    public void ModuleRepoIsMoved() => AssertMoved(fixture.ModuleRepo);

    [Fact(DisplayName = "...with how to put it back")]
    public void ModuleRepoSaysHowToPutItBack() => AssertSaysHowToPutItBack(fixture.ModuleRepo);

    [Fact(DisplayName = "git init -q sub: warned at session end")]
    public void NestedRepoWarns() => SmokeAssert.Contains(fixture.NestedRepo.Session, Warning);

    [Fact(DisplayName = "...and moved to sub/.git.pen-quarantine before pen returned")]
    public void NestedRepoIsMoved() => AssertMoved(fixture.NestedRepo);

    [Fact(DisplayName = "...with how to put it back")]
    public void NestedRepoSaysHowToPutItBack() => AssertSaysHowToPutItBack(fixture.NestedRepo);

    [Fact(DisplayName = "...and the moved .git no longer looks like a repo (its HEAD moved too)")]
    public void MovedGitDirsHeadIsMoved() => Assert.True(fixture.NestedHeadMoved);

    [Fact(DisplayName = "git init -q --bare sub2: warned at session end")]
    public void NestedBareRepoWarns() => SmokeAssert.Contains(fixture.NestedBareRepo.Session, Warning);

    [Fact(DisplayName = "...and moved to sub2/HEAD.pen-quarantine before pen returned")]
    public void NestedBareRepoIsMoved() => AssertMoved(fixture.NestedBareRepo);

    [Fact(DisplayName = "...with how to put it back")]
    public void NestedBareRepoSaysHowToPutItBack() => AssertSaysHowToPutItBack(fixture.NestedBareRepo);

    [Fact(DisplayName = "the next session starts normally")]
    public void NextSessionStarts() => SmokeAssert.Succeeded(fixture.AfterQuarantines);

    [Fact(DisplayName = "no warning for a git worktree add worktree")]
    public void WorktreeAddDoesntWarn() => SmokeAssert.Lacks(fixture.WorktreeAdd, "WARNING");

    [Fact(DisplayName = "...and the next session starts normally")]
    public void SessionAfterWorktreeAddStarts() => SmokeAssert.Succeeded(fixture.AfterWorktreeAdd);

    [Fact(DisplayName = "...and can commit in it (its entry is writable, being inside the project)")]
    public void CanCommitInTheWorktree() => SmokeAssert.Succeeded(fixture.WorktreeCommit);

    [Fact(DisplayName = "...but not write its commondir")]
    public void CantWriteTheWorktreesCommondir() => SmokeAssert.Succeeded(fixture.WorktreeCommondir);

    [Fact(DisplayName = "after a SIGKILL'd pen, .git/commondir is still there and its stack is up")]
    public void KilledSessionLeavesItsCommondir() =>
        Assert.True(
            fixture.CommondirAfterKill && fixture.ContainersAfterKill >= 2,
            $"commondir there: {fixture.CommondirAfterKill}, containers: {fixture.ContainersAfterKill}");

    [Fact(DisplayName = "the next pen command ends that session")]
    public void NextPenEndsTheKilledSession() =>
        SmokeAssert.Contains(fixture.AfterKill, "whose pen exited without ending it");

    [Fact(DisplayName = "...quarantining .git/commondir")]
    public void NextPenQuarantinesTheCommondir() =>
        Assert.True(
            !fixture.CommondirAfterNext && fixture.CommondirQuarantinedByNext,
            $"commondir there: {fixture.CommondirAfterNext}, quarantined: {fixture.CommondirQuarantinedByNext}");

    [Fact(DisplayName = "...with the warning")]
    public void NextPenWarns() => SmokeAssert.Contains(fixture.AfterKill, Warning);

    [Fact(DisplayName = "...and takes the stack down")]
    public void NextPenTakesTheStackDown() => Assert.Equal(0, fixture.ContainersAfterNext);

    [Fact(DisplayName = "a repo hidden in a directory left unlistable is quarantined")]
    public void HiddenRepoIsQuarantined() => Assert.True(fixture.HiddenRepoQuarantined);

    [Fact(DisplayName = "...saying pen left the directory's mode changed")]
    public void SaysTheModeWasChanged() => SmokeAssert.Contains(fixture.HiddenRepo, "changed unlisted from mode 311");

    [Fact(DisplayName = "a .git made by git init in a project without one is quarantined")]
    public void NewGitDirIsQuarantined() => Assert.True(fixture.NewGitDirQuarantined);

    [Fact(DisplayName = "...with the warning")]
    public void NewGitDirWarns() => SmokeAssert.Contains(fixture.NewGitDir, "git init");

    [Fact(DisplayName = "...and a project without .git gets no .git mounts")]
    public void ProjectWithoutGitGetsNoMounts() => SmokeAssert.Succeeded(fixture.WithoutGit);

    private static void AssertMoved(Quarantine quarantine) =>
        Assert.True(
            !quarantine.PathAfter && quarantine.QuarantinedAfter,
            $"{quarantine.Path} there: {quarantine.PathAfter}, {quarantine.Quarantined} there: "
                + $"{quarantine.QuarantinedAfter}; {quarantine.Session.Describe()}");

    private void AssertSaysHowToPutItBack(Quarantine quarantine)
    {
        var dir = fixture.Project.Dir;

        SmokeAssert.Contains(quarantine.Session, $"mv '{dir}/{quarantine.Quarantined}' '{dir}/{quarantine.Path}'");
    }

    /// <summary>A session that made something the end check should quarantine, and what was left after it.</summary>
    /// <param name="Session">The session (pen run bash -c command).</param>
    /// <param name="Path">What it made, relative to the project.</param>
    /// <param name="Quarantined">Where pen should have moved it, relative to the project.</param>
    /// <param name="PathAfter">Whether <paramref name="Path"/> existed after the session (test -e).</param>
    /// <param name="QuarantinedAfter">Whether <paramref name="Quarantined"/> existed after it.</param>
    internal sealed record Quarantine(
        CommandResult Session, string Path, string Quarantined, bool PathAfter, bool QuarantinedAfter);

    /// <summary>Runs one session per thing to quarantine, then a git worktree add session, a session whose pen is
    /// SIGKILLed after it planted a commondir and the next pen, a session that hides a repo, and git init in a
    /// project without .git.</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal Quarantine Commondir { get; private set; } = null!;

        internal Quarantine ModuleRepo { get; private set; } = null!;

        internal Quarantine NestedRepo { get; private set; } = null!;

        internal bool NestedHeadMoved { get; private set; }

        internal Quarantine NestedBareRepo { get; private set; } = null!;

        internal CommandResult AfterQuarantines { get; private set; } = null!;

        internal CommandResult WorktreeAdd { get; private set; } = null!;

        internal CommandResult AfterWorktreeAdd { get; private set; } = null!;

        internal CommandResult WorktreeCommit { get; private set; } = null!;

        internal CommandResult WorktreeCommondir { get; private set; } = null!;

        internal bool CommondirAfterKill { get; private set; }

        internal int ContainersAfterKill { get; private set; }

        internal CommandResult AfterKill { get; private set; } = null!;

        internal bool CommondirAfterNext { get; private set; }

        internal bool CommondirQuarantinedByNext { get; private set; }

        internal int ContainersAfterNext { get; private set; }

        internal CommandResult HiddenRepo { get; private set; } = null!;

        internal bool HiddenRepoQuarantined { get; private set; }

        internal CommandResult NewGitDir { get; private set; } = null!;

        internal bool NewGitDirQuarantined { get; private set; }

        internal CommandResult WithoutGit { get; private set; } = null!;

        private string GitDir => Path.Combine(Project.Dir, ".git");

        private string CommondirPath => Path.Combine(GitDir, "commondir");

        protected override void Prepare()
        {
            RunQuarantines();
            RunWorktreeAdd();
            RunKilledSession();
            RunHiddenRepo();
            RunGitInit();
        }

        private void RunQuarantines()
        {
            Commondir = RunQuarantine("touch .git/commondir", ".git/commondir", ".git/commondir.pen-quarantine");
            ModuleRepo = RunQuarantine(
                "git init -q --bare .git/modules/x", ".git/modules/x", ".git/modules/x.pen-quarantine");
            Files.DeleteTree(Path.Combine(GitDir, "modules"));
            NestedRepo = RunQuarantine("git init -q sub", "sub/.git", "sub/.git.pen-quarantine");
            NestedHeadMoved =
                Files.Exists(Path.Combine(Project.Dir, "sub", ".git.pen-quarantine", "HEAD.pen-quarantine"));
            NestedBareRepo = RunQuarantine("git init -q --bare sub2", "sub2/HEAD", "sub2/HEAD.pen-quarantine");
            AfterQuarantines = Cli.Run("run", "true");
            File.Delete(Path.Combine(GitDir, "commondir.pen-quarantine"));
            Files.DeleteTree(Path.Combine(Project.Dir, "sub"));
            Files.DeleteTree(Path.Combine(Project.Dir, "sub2"));
        }

        /// <summary>Runs <paramref name="command"/> in a session, as smoke.sh's quarantine_case did, and records
        /// what's left.</summary>
        private Quarantine RunQuarantine(string command, string path, string quarantined)
        {
            var session = Cli.Run("run", "bash", "-c", command);
            return new Quarantine(
                session, path, quarantined, Files.Exists(Path.Combine(Project.Dir, path)),
                Files.Exists(Path.Combine(Project.Dir, quarantined)));
        }

        private void RunWorktreeAdd()
        {
            WorktreeAdd = Cli.Run("run", "bash", "-c", "git worktree add -q .claude/worktrees/y").RequireSuccess();
            AfterWorktreeAdd = Cli.Run("run", "true");
            WorktreeCommit = Cli.Run("run", "bash", "-c", """
                git -C .claude/worktrees/y -c user.name=a -c user.email=a@example.invalid commit -q --allow-empty -m in-y
                """);
            WorktreeCommondir = Cli.Run("run", "bash", "-c", "! echo x 2>/dev/null > .git/worktrees/y/commondir");
            Project.RunGit("worktree", "remove", "--force", ".claude/worktrees/y");
            Files.DeleteTree(Path.Combine(Project.Dir, ".claude"));
        }

        /// <summary>Starts a session that plants .git/commondir and waits, SIGKILLs its pen once the file is there,
        /// then runs the next pen.</summary>
        private void RunKilledSession()
        {
            var session = Track(Cli.Start("run", "bash", "-c", "touch .git/commondir; sleep 120"));
            if (!Polling.WaitForContainer($"^{Key}-.*-agent$"))
                throw new InvalidOperationException($"session didn't start: {session.OutputSoFar}");
            Polling.WaitUntil(() => File.Exists(CommondirPath), TimeSpan.FromSeconds(10));
            session.Signal("KILL");
            // Only pen has to be gone: its compose child, still running, holds the output pipes open.
            if (!Polling.WaitUntil(() => session.HasExited, TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("pen didn't end on SIGKILL");
            CommondirAfterKill = Files.Exists(CommondirPath);
            ContainersAfterKill = Docker.CountContainers(Key);

            AfterKill = Cli.Run("run", "true").RequireSuccess();
            CommondirAfterNext = Files.Exists(CommondirPath);
            CommondirQuarantinedByNext = Files.Exists(CommondirPath + ".pen-quarantine");
            ContainersAfterNext = Docker.CountContainers(Key);
            File.Delete(CommondirPath + ".pen-quarantine");
        }

        private void RunHiddenRepo()
        {
            HiddenRepo = Cli.Run("run", "bash", "-c", "mkdir unlisted && git init -q unlisted/x && chmod 311 unlisted");
            var repo = Path.Combine(Project.Dir, "unlisted", "x");
            HiddenRepoQuarantined = !Files.Exists(Path.Combine(repo, ".git"))
                && Files.IsDirectory(Path.Combine(repo, ".git.pen-quarantine"));
            Files.DeleteTree(Path.Combine(Project.Dir, "unlisted"));
        }

        private void RunGitInit()
        {
            var noGit = CreateEmptyProject(".pen-smoke-nogit.");
            var pen = Cli.WithWorkingDirectory(noGit.Dir);
            NewGitDir = pen.Run("run", "git", "init", "-q");
            NewGitDirQuarantined = !Files.Exists(Path.Combine(noGit.Dir, ".git"))
                && Files.IsDirectory(Path.Combine(noGit.Dir, ".git.pen-quarantine"));
            Files.DeleteTree(Path.Combine(noGit.Dir, ".git.pen-quarantine"));
            WithoutGit = pen.Run("run", "bash", "-c", "[ ! -e .git ] && ! touch .pen/x");
        }
    }
}
