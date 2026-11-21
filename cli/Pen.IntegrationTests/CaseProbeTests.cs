namespace Pen.IntegrationTests;

/// <summary>The warning about a git dir that may fold case: one per session where it does, none where it doesn't,
/// and nothing left of the probe.</summary>
public sealed class CaseProbeTests(CaseProbeTests.Fixture fixture) : IClassFixture<CaseProbeTests.Fixture>
{
    private const string CaseWarning = "isn't known to be case-sensitive";

    private int WantWarnings => fixture.WantWarnings;

    [Fact(DisplayName = "a session warns as often as case on this filesystem says")]
    public void SessionWarnsAsTheFilesystemSays() =>
        AssertWarnings(fixture.InRepo, WantWarnings, $"a session warns {WantWarnings} time(s) about case");

    [Fact(DisplayName = "the case probe leaves nothing in the git dir")]
    public void ProbeLeavesNothing() => Assert.Equal(0, fixture.ProbesLeftAfterRepo);

    /// <summary>Counts the stderr lines with the warning, as grep -cF on 2&gt;&amp;1 &gt;/dev/null does.</summary>
    private static void AssertWarnings(CommandResult session, int expected, string message)
    {
        var warnings = session.StderrLines.Count(line => line.Contains(CaseWarning, StringComparison.Ordinal));

        Assert.True(warnings == expected, $"{message}, got {warnings}; {session.Describe()}");
    }

    /// <summary>Finds whether the project's git dir folds case, then runs a session in the repo.</summary>
    /// <remarks>The other layouts (a linked worktree, a git dir the probe can't write to, a directory that isn't a
    /// repo) are covered by the unit tests; the session's call to the probe is the same in each.</remarks>
    public sealed class Fixture : IntegrationFixture
    {
        /// <summary>Gets how many warnings a session in this repo should print: 1 if its git dir folds case.</summary>
        internal int WantWarnings { get; private set; }

        internal CommandResult InRepo { get; private set; } = null!;

        internal int ProbesLeftAfterRepo { get; private set; }

        private string GitDir => Path.Combine(Project.Dir, ".git");

        protected override void Prepare()
        {
            var check = Path.Combine(GitDir, ".integration-case-check");
            File.WriteAllText(check, "");
            WantWarnings = File.Exists(Path.Combine(GitDir, ".INTEGRATION-CASE-CHECK")) ? 1 : 0;
            File.Delete(check);

            InRepo = Cli.Run("run", "true");
            ProbesLeftAfterRepo = CountProbesLeft();
        }

        private int CountProbesLeft() =>
            new DirectoryInfo(GitDir).EnumerateFileSystemInfos()
                .Count(entry => entry.Name.StartsWith(".pen-case-probe-", StringComparison.OrdinalIgnoreCase));
    }
}
