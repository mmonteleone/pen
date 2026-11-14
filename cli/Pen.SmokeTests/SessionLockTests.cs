using System.Globalization;
using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>A running session's locks and its line in pen ps, and how the next pen ends a session whose pen was
/// killed.</summary>
/// <remarks>
/// These sessions run with the real HOME, so that pen ps shows the project, which is under
/// it, as ~/…. The real ~/.config/pen's tools go into them, which the TOOLS cell allows for.
/// </remarks>
public sealed class SessionLockTests(SessionLockTests.Fixture fixture) : IClassFixture<SessionLockTests.Fixture>
{
    [Fact(DisplayName = "pen ps lists the running session: project, id, tools, age and command")]
    public void PsListsTheRunningSession()
    {
        var project = fixture.Project.Dir;
        var line = new Regex(
            $"^{Regex.Escape(PsTable.FormatProjectDir(project))} +[0-9a-f]{{8}} +{fixture.ToolsCell}"
            + "[0-9]+[smh][0-9m]* +sleep 120$",
            RegexOptions.Multiline);

        Assert.Matches(line, fixture.Ps.CapturedStdout);
    }

    [Fact(DisplayName = "project.lock is held shared: another shared lock is fine")]
    public void ProjectLockIsShared() => Assert.True(fixture.ProjectLockSharedFree);

    [Fact(DisplayName = "...and an exclusive one (pen reset) is refused")]
    public void ProjectLockRefusesExclusive() => Assert.False(fixture.ProjectLockExclusiveFree);

    [Fact(DisplayName = "this dir's worktree lock is held shared too")]
    public void WorktreeLockIsShared() => Assert.True(fixture.WorktreeLockSharedFree);

    [Fact(DisplayName = "...so pen sign's exclusive hold on it is refused")]
    public void WorktreeLockRefusesExclusive() => Assert.False(fixture.WorktreeLockExclusiveFree);

    [Fact(DisplayName = "the running session's session.lock is held")]
    public void SessionLockIsHeld() => Assert.False(fixture.SessionLockFreeWhileRunning);

    [Fact(DisplayName = "the compose child holds no lock fds (lsof)")]
    public void ComposeChildHoldsNoLocks()
    {
        var lsof = RequireLsof();

        Assert.True(!lsof.ChildHoldsALock, $"the compose child's open files:\n{lsof.ChildFiles}");
    }

    [Fact(DisplayName = "while pen itself holds all three locks (project, worktree, session)")]
    public void PenHoldsAllThreeLocks()
    {
        var lsof = RequireLsof();

        Assert.True(lsof.PenLocks == 3, $"pen holds {lsof.PenLocks} locks; its open files:\n{lsof.PenFiles}");
    }

    [Fact(DisplayName = "after a SIGKILL'd pen, its stack is still up")]
    public void StackOutlivesAKilledPen() =>
        Assert.True(fixture.ContainersAfterKill >= 2, $"{fixture.ContainersAfterKill} containers");

    [Fact(DisplayName = "...and its session.lock is free")]
    public void SessionLockIsFreedByTheKill() => Assert.True(fixture.SessionLockFreeAfterKill);

    [Fact(DisplayName = "the next pen ends the dead session")]
    public void NextPenEndsTheDeadSession() =>
        Assert.True(
            fixture.Next.Output.Contains("whose pen exited without ending it", StringComparison.Ordinal),
            fixture.Next.Describe());

    [Fact(DisplayName = "...taking its stack down")]
    public void DeadSessionsStackIsTakenDown() => Assert.Equal(0, fixture.ContainersAfterNext);

    [Fact(DisplayName = "...and leaving only its proxy.log")]
    public void DeadSessionKeepsOnlyItsProxyLog() => Assert.Equal(["proxy.log"], fixture.DeadSessionEntries);

    private LsofResult RequireLsof()
    {
        Assert.SkipWhen(fixture.Lsof == null, "lsof not installed: skipped the fd checks");
        return fixture.Lsof;
    }

    /// <summary>What lsof showed of the running session's pen and its compose child.</summary>
    /// <param name="ChildFiles">lsof -p of pen's first child process.</param>
    /// <param name="PenFiles">lsof -p of pen.</param>
    internal sealed record LsofResult(string ChildFiles, string PenFiles)
    {
        /// <summary>Gets whether a line of the child's open files has .lock in it (grep -q '\.lock').</summary>
        public bool ChildHoldsALock => ChildFiles.Contains(".lock", StringComparison.Ordinal);

        /// <summary>Gets how many of pen's open files end in .lock (grep -c '\.lock$').</summary>
        public int PenLocks => PenFiles.Split('\n').Count(line => line.EndsWith(".lock", StringComparison.Ordinal));
    }

    /// <summary>Starts pen run sleep 120 with the real HOME and records its pen ps line and locks while it runs,
    /// then SIGKILLs pen and runs the next pen, recording what each leaves.</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal CommandResult Ps { get; private set; } = null!;

        /// <summary>Gets the regex for the session's TOOLS cell and the spaces after it: the tools it recorded,
        /// or nothing.</summary>
        internal string ToolsCell { get; private set; } = null!;

        internal bool ProjectLockSharedFree { get; private set; }

        internal bool ProjectLockExclusiveFree { get; private set; }

        internal bool WorktreeLockSharedFree { get; private set; }

        internal bool WorktreeLockExclusiveFree { get; private set; }

        internal bool SessionLockFreeWhileRunning { get; private set; }

        /// <summary>Gets what lsof showed, or null if it isn't installed.</summary>
        internal LsofResult? Lsof { get; private set; }

        internal int ContainersAfterKill { get; private set; }

        internal bool SessionLockFreeAfterKill { get; private set; }

        internal CommandResult Next { get; private set; } = null!;

        internal int ContainersAfterNext { get; private set; }

        internal IReadOnlyList<string> DeadSessionEntries { get; private set; } = [];

        protected override void Prepare()
        {
            var pen = PenCli.CreateForRealHome(Project.Dir);
            var runtime = Path.Combine(SmokeEnvironment.RealHome, ".local", "state", "pen", Key);
            var session = Track(pen.Start("run", "sleep", "120"));
            if (!Polling.WaitForContainer($"^{Key}-.*-agent$"))
                throw new InvalidOperationException($"session didn't start: {session.OutputSoFar}");

            Ps = pen.Run("ps");
            var sessionDir = SmokeProject.FindLastSession(runtime);
            ToolsCell = PsTable.BuildToolsCell(sessionDir);
            var projectLock = Path.Combine(runtime, "project.lock");
            var worktreeLock = Path.Combine(runtime, $"worktree-{Project.Hash}.lock");
            ProjectLockSharedFree = Locks.IsFree(projectLock, exclusive: false);
            ProjectLockExclusiveFree = Locks.IsFree(projectLock, exclusive: true);
            WorktreeLockSharedFree = Locks.IsFree(worktreeLock, exclusive: false);
            WorktreeLockExclusiveFree = Locks.IsFree(worktreeLock, exclusive: true);
            var sessionLock = Path.Combine(sessionDir, "session.lock");
            SessionLockFreeWhileRunning = Locks.IsFree(sessionLock, exclusive: true);
            Lsof = ReadLsof(session.Pid);

            session.Signal("KILL");
            // Only pen has to be gone: its compose child, still running, holds the output pipes open.
            if (!Polling.WaitUntil(() => session.HasExited, TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("pen didn't end on SIGKILL");
            ContainersAfterKill = Docker.CountContainers(Key);
            SessionLockFreeAfterKill = Locks.IsFree(sessionLock, exclusive: true);

            Next = pen.Run("run", "true");
            ContainersAfterNext = Docker.CountContainers(Key);
            DeadSessionEntries =
                [.. Directory.EnumerateFileSystemEntries(sessionDir).Select(entry => Path.GetFileName(entry))];
        }

        private static LsofResult? ReadLsof(int penPid)
        {
            if (SmokeEnvironment.FindOnPath("lsof") is not { } lsof)
                return null;
            var pid = penPid.ToString(CultureInfo.InvariantCulture);
            var child = new HostCommand("pgrep", ["-P", pid]).Run().StdoutLines[0];
            if (child.Length == 0)
                throw new InvalidOperationException($"pen ({pid}) has no child process");
            return new LsofResult(
                new HostCommand(lsof, ["-p", child]).Run().Stdout,
                new HostCommand(lsof, ["-p", pid]).Run().Stdout);
        }
    }
}
