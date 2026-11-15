namespace Pen.SmokeTests;

/// <summary>The core.hooksPath warning: one per session where the hooks are in the sandbox's reach, none
/// elsewhere.</summary>
public sealed class HooksPathWarningTests(HooksPathWarningTests.Fixture fixture)
    : IClassFixture<HooksPathWarningTests.Fixture>
{
    [Fact(DisplayName = "core.hooksPath in the project warns once")]
    public void InProjectWarnsOnce() => AssertWarnings(fixture.InProject, 1);

    [Fact(DisplayName = "...in plain words")]
    public void WarnsInPlainWords() =>
        Assert.True(
            fixture.InProject.Stderr.Contains("can be changed from the sandbox", StringComparison.Ordinal),
            fixture.InProject.Describe());

    [Fact(DisplayName = "...and once in a linked worktree, where it's that worktree's")]
    public void InWorktreeWarnsOnce() => AssertWarnings(fixture.InWorktree, 1);

    [Fact(DisplayName = "global config that leads into the project warns once")]
    public void GlobalConfigWarnsOnce() => AssertWarnings(fixture.Global, 1);

    [Fact(DisplayName = "no core.hooksPath, no warning")]
    public void NoHooksPathNoWarning() => AssertWarnings(fixture.None, 0);

    /// <summary>Counts the stderr lines that name core.hooksPath, as grep -cF on 2&gt;&amp;1 &gt;/dev/null
    /// does.</summary>
    private static void AssertWarnings(CommandResult session, int expected)
    {
        var warnings = session.StderrLines.Count(line => line.Contains("core.hooksPath", StringComparison.Ordinal));

        Assert.True(warnings == expected, $"expected {expected} warning(s), got {warnings}; {session.Describe()}");
    }

    /// <summary>Runs pen run true with core.hooksPath set each way in turn: in the project, from a linked worktree,
    /// in a global config, and unset.</summary>
    /// <remarks>Paths that don't warn (.git/hooks by its full path, outside the project) are covered by the unit
    /// tests.</remarks>
    public sealed class Fixture : SmokeFixture
    {
        internal CommandResult InProject { get; private set; } = null!;

        internal CommandResult InWorktree { get; private set; } = null!;

        internal CommandResult Global { get; private set; } = null!;

        internal CommandResult None { get; private set; } = null!;

        protected override void Prepare()
        {
            Project.RunGit("config", "core.hooksPath", ".husky");
            InProject = RunSession(Cli);
            var worktree = Path.Combine(Scratch, "hooks-wt");
            Project.RunGit("worktree", "add", "-q", worktree);
            InWorktree = RunSession(Cli.WithWorkingDirectory(worktree));
            Project.RunGit("worktree", "remove", "--force", worktree);
            Project.RunGit("config", "--unset", "core.hooksPath");
            var global = Path.Combine(Scratch, "hooks-global");
            File.WriteAllText(global, $"[core]\n\thooksPath = {Project.Dir}/hooks\n");
            Global = RunSession(Cli.WithEnvironment("GIT_CONFIG_GLOBAL", global));
            None = RunSession(Cli);
        }

        /// <summary>Runs pen run true, which must succeed.</summary>
        private static CommandResult RunSession(PenCli pen) => pen.Run("run", "true").RequireSuccess();
    }
}
