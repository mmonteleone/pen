namespace Pen.SmokeTests;

/// <summary>The warning about a git dir that may fold case: one per session where it does, none elsewhere, and
/// nothing left of the probe.</summary>
public sealed class CaseProbeTests(CaseProbeTests.Fixture fixture) : IClassFixture<CaseProbeTests.Fixture>
{
    private const string CaseWarning = "isn't known to be case-sensitive";

    private int WantWarnings => fixture.WantWarnings;

    [Fact(DisplayName = "a session warns $want_warnings time(s) about case on this filesystem")]
    public void SessionWarnsAsTheFilesystemSays() =>
        AssertWarnings(fixture.InRepo, WantWarnings, $"a session warns {WantWarnings} time(s) about case");

    [Fact(DisplayName = "the case probe leaves nothing in the git dir")]
    public void ProbeLeavesNothing() => Assert.Equal(0, fixture.ProbesLeftAfterRepo);

    [Fact(DisplayName = "a linked worktree's session warns as the common git dir says")]
    public void WorktreeSessionWarnsAsTheCommonGitDirSays() =>
        AssertWarnings(fixture.InWorktree, WantWarnings, $"expected {WantWarnings} warning(s)");

    [Fact(DisplayName = "...and its probe leaves nothing there either")]
    public void WorktreeProbeLeavesNothing() => Assert.Equal(0, fixture.ProbesLeftAfterWorktree);

    [Fact(DisplayName = "a git dir the probe can't write to gets the one warning")]
    public void UnwritableGitDirGetsTheWarning()
    {
        Assert.SkipWhen(fixture.InReadOnlyGitDir == null, "running as root: the probe can write anywhere");

        AssertWarnings(fixture.InReadOnlyGitDir, 1, "expected the one warning");
    }

    [Fact(DisplayName = "a project that isn't a repo gets no case warning")]
    public void NonRepoGetsNoWarning() => AssertWarnings(fixture.InNonRepo, 0, "expected no warning");

    /// <summary>Counts the stderr lines with the warning, as grep -cF on 2&gt;&amp;1 &gt;/dev/null does.</summary>
    private static void AssertWarnings(CommandResult session, int expected, string message)
    {
        var warnings = session.StderrLines.Count(line => line.Contains(CaseWarning, StringComparison.Ordinal));

        Assert.True(warnings == expected, $"{message}, got {warnings}; {session.Describe()}");
    }

    /// <summary>Finds whether the project's git dir folds case, then runs a session in the repo, in a linked
    /// worktree of it, in the repo with its git dir read-only (unless the tests run as root) and in a directory
    /// that isn't a repo.</summary>
    public sealed class Fixture : SmokeFixture
    {
        private const UnixFileMode ReadAndExecute =
            UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

        /// <summary>Gets how many warnings a session in this repo should print: 1 if its git dir folds case.</summary>
        internal int WantWarnings { get; private set; }

        internal CommandResult InRepo { get; private set; } = null!;

        internal int ProbesLeftAfterRepo { get; private set; }

        internal CommandResult InWorktree { get; private set; } = null!;

        internal int ProbesLeftAfterWorktree { get; private set; }

        /// <summary>Gets the session whose git dir was mode 555, or null when the tests run as root.</summary>
        internal CommandResult? InReadOnlyGitDir { get; private set; }

        internal CommandResult InNonRepo { get; private set; } = null!;

        private string GitDir => Path.Combine(Project.Dir, ".git");

        protected override void Prepare()
        {
            var check = Path.Combine(GitDir, ".smoke-case-check");
            File.WriteAllText(check, "");
            WantWarnings = File.Exists(Path.Combine(GitDir, ".SMOKE-CASE-CHECK")) ? 1 : 0;
            File.Delete(check);

            InRepo = Cli.Run("run", "true");
            ProbesLeftAfterRepo = CountProbesLeft();

            var worktree = Path.Combine(Scratch, "case-wt");
            Project.RunGit("worktree", "add", "-q", worktree);
            try
            {
                InWorktree = Cli.WithWorkingDirectory(worktree).Run("run", "true");
                ProbesLeftAfterWorktree = CountProbesLeft();
            }
            finally
            {
                Project.RunGit("worktree", "remove", "--force", worktree);
            }

            if (SmokeEnvironment.HostUid != 0)
            {
                File.SetUnixFileMode(GitDir, ReadAndExecute);
                try
                {
                    InReadOnlyGitDir = Cli.Run("run", "true");
                }
                finally
                {
                    File.SetUnixFileMode(GitDir, ReadAndExecute | UnixFileMode.UserWrite);
                }
            }

            var nonRepo = CreateEmptyProject(".pen-smoke-norepo.");
            InNonRepo = Cli.WithWorkingDirectory(nonRepo.Dir).Run("run", "true");
        }

        private int CountProbesLeft() =>
            new DirectoryInfo(GitDir).EnumerateFileSystemInfos()
                .Count(entry => entry.Name.StartsWith(".pen-case-probe-", StringComparison.OrdinalIgnoreCase));
    }
}
