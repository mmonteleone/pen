namespace Pen.SmokeTests;

/// <summary>What pen refuses to start in, and what it starts in anyway: forged worktrees, directories it can't
/// list, a FIFO named .git, nested and bare repos, a folder of repos, and worktrees under .claude/.</summary>
public sealed class RepoRefusalTests(RepoRefusalTests.Fixture fixture) : IClassFixture<RepoRefusalTests.Fixture>
{
    [Fact(DisplayName = "refuses a .git file that isn't a valid linked worktree (section 21 runs in one)")]
    public void ForgedWorktreeIsRefused() => SmokeAssert.Refused(fixture.Forged, "isn't a valid linked worktree");

    [Fact(DisplayName = "refuses a directory it can enter but not list, naming it")]
    public void UnlistableDirIsRefused() => SmokeAssert.Refused(fixture.Unlisted, "unlisted");

    [Fact(DisplayName = "a directory that can't be entered at all starts")]
    public void ShutDirStarts() => SmokeAssert.Succeeded(fixture.Shut);

    [Fact(DisplayName = "refuses a FIFO named .git rather than hanging on it")]
    public void FifoIsRefused() => SmokeAssert.Refused(fixture.Fifo, "fifo");

    [Fact(DisplayName = "a repo with .gitmodules starts (section 26 runs with real submodules)")]
    public void GitmodulesStarts() => SmokeAssert.Succeeded(fixture.Gitmodules);

    [Fact(DisplayName = "refuses a repo with another repo inside it, naming it")]
    public void NestedRepoIsNamed() => SmokeAssert.Contains(fixture.Nested, "  libs/sub");

    [Fact(DisplayName = "...and a bare repo inside it")]
    public void NestedBareRepoIsNamed() => SmokeAssert.Contains(fixture.Nested, "  sub2 (a bare repo)");

    [Fact(DisplayName = "...saying to run pen in that repo")]
    public void NestedRefusalSaysWhatToDo() => SmokeAssert.Contains(fixture.Nested, "run pen in each repo instead");

    [Fact(DisplayName = "...without starting anything")]
    public void NestedRefusalStartsNothing() => Assert.Equal(0, fixture.ContainersAfterNested);

    [Fact(DisplayName = "refuses a folder of repos, naming them")]
    public void FolderOfReposIsRefused() =>
        Assert.Multiple(
            () => SmokeAssert.Contains(fixture.FolderOfRepos, "contains git repos"),
            () => SmokeAssert.Contains(fixture.FolderOfRepos, "  a"),
            () => SmokeAssert.Contains(fixture.FolderOfRepos, "  b"));

    [Fact(DisplayName = "a git worktree add worktree under .claude/worktrees/ starts normally")]
    public void ClaudeWorktreeStarts() => SmokeAssert.Succeeded(fixture.ClaudeWorktree);

    /// <summary>Runs pen run true in each layout in turn, putting the project back between them.</summary>
    public sealed class Fixture : SmokeFixture
    {
        private const UnixFileMode Mode311 =
            UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

        internal CommandResult Forged { get; private set; } = null!;

        internal CommandResult Unlisted { get; private set; } = null!;

        internal CommandResult Shut { get; private set; } = null!;

        internal CommandResult Fifo { get; private set; } = null!;

        internal CommandResult Gitmodules { get; private set; } = null!;

        internal CommandResult Nested { get; private set; } = null!;

        internal int ContainersAfterNested { get; private set; }

        internal CommandResult FolderOfRepos { get; private set; } = null!;

        internal CommandResult ClaudeWorktree { get; private set; } = null!;

        protected override void Prepare()
        {
            // The worktree's .git/worktrees/wt entry is the one the forged .git file names.
            Project.RunGit("worktree", "add", "-q", Path.Combine(Scratch, "wt"));
            var forged = CreateScratchProject("forged");
            File.WriteAllText(
                Path.Combine(forged.Dir, ".git"), $"gitdir: {Path.Combine(Project.Dir, ".git", "worktrees", "wt")}\n");
            Forged = Cli.WithWorkingDirectory(forged.Dir).Run("run", "true");

            Unlisted = RunWithModeIn("unlisted", Mode311, dir => Project.RunGit("init", "-q", Path.Combine(dir, "x")));
            Shut = RunWithModeIn("shut", UnixFileMode.None, _ => { });

            var fifo = Directory.CreateDirectory(Path.Combine(Project.Dir, "fifo")).FullName;
            new HostCommand("mkfifo", [Path.Combine(fifo, ".git")]).Run().RequireSuccess();
            Fifo = Cli.Run("run", "true");
            Files.DeleteTree(fifo);

            var gitmodules = Path.Combine(Project.Dir, ".gitmodules");
            File.WriteAllText(gitmodules, "");
            Gitmodules = Cli.Run("run", "true");
            File.Delete(gitmodules);

            Project.RunGit("init", "-q", "libs/sub");
            Project.RunGit("init", "-q", "--bare", "sub2");
            Nested = Cli.Run("run", "true");
            ContainersAfterNested = Docker.CountContainers(Key);
            Files.DeleteTree(Path.Combine(Project.Dir, "libs"));
            Files.DeleteTree(Path.Combine(Project.Dir, "sub2"));

            var repos = CreateScratchProject("repos");
            SmokeGit.Run(repos.Dir, "init", "-q", "a");
            SmokeGit.Run(repos.Dir, "init", "-q", "b");
            FolderOfRepos = Cli.WithWorkingDirectory(repos.Dir).Run("run", "true");

            Project.RunGit("worktree", "add", "-q", ".claude/worktrees/x");
            ClaudeWorktree = Cli.Run("run", "true");
            Project.RunGit("worktree", "remove", "--force", ".claude/worktrees/x");
            Files.DeleteTree(Path.Combine(Project.Dir, ".claude"));
        }

        /// <summary>Makes a directory in the project, fills it, sets its mode and runs a session, then removes
        /// it.</summary>
        private CommandResult RunWithModeIn(string name, UnixFileMode mode, Action<string> fill)
        {
            var dir = Directory.CreateDirectory(Path.Combine(Project.Dir, name)).FullName;
            fill(dir);
            File.SetUnixFileMode(dir, mode);
            try
            {
                return Cli.Run("run", "true");
            }
            finally
            {
                File.SetUnixFileMode(dir, Files.Mode755);
                Files.DeleteTree(dir);
            }
        }
    }
}
