using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

/// <summary>Ctrl-C during the automatic rebuild of a stale pen:latest: the launch stops and leaves nothing.</summary>
public sealed class ImageBuildInterruptTests(ImageBuildInterruptTests.Fixture fixture)
    : IClassFixture<ImageBuildInterruptTests.Fixture>
{
    [Fact(DisplayName = "Ctrl-C during the image build aborts the launch")]
    public void CtrlCAbortsTheLaunch() =>
        Assert.True(
            fixture.Interrupted.Text.Contains("rebuilding", StringComparison.Ordinal)
                && !Regex.IsMatch(fixture.Interrupted.Text, "EXIT 0|TIMEOUT"),
            fixture.Interrupted.Describe());

    [Fact(DisplayName = "...leaving no session dir")]
    public void LeavesNoSessionDir() => Assert.Equal(fixture.SessionsBefore, fixture.SessionsAfter);

    [Fact(DisplayName = "...and no lock held")]
    public void LeavesNoProjectLock() => Assert.True(fixture.ProjectLockFree);

    [Fact(DisplayName = "...not even the worktree lock")]
    public void LeavesNoWorktreeLock() => Assert.True(fixture.WorktreeLockFree);

    /// <summary>Makes pen:latest stale (the same image, under a wrong label), interrupts the rebuild that the next
    /// session starts, then puts the real image back.</summary>
    /// <remarks>
    /// The real image is kept under a pen:integration-backup-… tag meanwhile. The fixture puts it back right after the
    /// launch, and cleanup does if anything fails first, so pen:latest ends up with its own ID and
    /// labels whatever happens, and the stale image is removed. An image that the interrupted rebuild left under the
    /// tag isn't named, so it stays, untagged.
    /// </remarks>
    public sealed class Fixture : IntegrationFixture
    {
        private const string Image = "pen:latest";

        internal PtyResult Interrupted { get; private set; } = null!;

        internal int SessionsBefore { get; private set; }

        internal int SessionsAfter { get; private set; }

        internal bool ProjectLockFree { get; private set; }

        internal bool WorktreeLockFree { get; private set; }

        private string SessionsDir => Path.Combine(RuntimeDir, "sessions");

        protected override void Prepare()
        {
            var backup = Track(ImageBackup.Create(Image, "pen"));
            var stale = new HostCommand(
                IntegrationEnvironment.DockerPath,
                ["build", "-q", "--label", "pen.image-hash=stale", "-t", Image, "-"])
            {
                Stdin = $"FROM {backup.Backup}\n",
            }.Run();
            if (stale.ExitCode != 0)
                throw new InvalidOperationException($"building the stale {Image}: {stale.Describe()}");
            backup.RemoveWithRestore(stale.CapturedStdout);

            SessionsBefore = CountSessions();
            Interrupted = new Pty().SendOn("rebuilding", "\\x03").Run(Cli, "run", "true");
            backup.Restore();

            SessionsAfter = CountSessions();
            ProjectLockFree = Locks.IsFree(Path.Combine(RuntimeDir, "project.lock"), exclusive: true);
            WorktreeLockFree = Locks.IsFree(Path.Combine(RuntimeDir, $"worktree-{Project.Hash}.lock"), exclusive: true);
        }

        /// <summary>Counts the session dirs, as ls | wc -l does.</summary>
        private int CountSessions() =>
            new DirectoryInfo(SessionsDir).EnumerateFileSystemInfos().Count(entry => !entry.Name.StartsWith('.'));
    }
}
