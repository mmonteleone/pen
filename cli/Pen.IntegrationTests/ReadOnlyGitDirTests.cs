namespace Pen.IntegrationTests;

/// <summary>What the sandbox can do in .git and .pen: git works, while config, hooks, a host worktree's entry,
/// .pen/ and the files .git/config includes are read-only and .git can't be moved.</summary>
public sealed class ReadOnlyGitDirTests(ReadOnlyGitDirTests.Fixture fixture)
    : IClassFixture<ReadOnlyGitDirTests.Fixture>
{
    [Fact(DisplayName = "git commit works")]
    public void CommitWorks() => IntegrationAssert.HasLine(fixture.Probes, "ok: commit");

    [Fact(DisplayName = "branch, checkout, merge and stash work")]
    public void BranchWorkWorks() => IntegrationAssert.HasLine(fixture.Probes, "ok: branchwork");

    [Fact(DisplayName = "the session that only committed printed no warning")]
    public void NoWarning() => IntegrationAssert.Lacks(fixture.Probes.Session, "WARNING");

    [Fact(DisplayName = ".git/hooks is created when missing")]
    public void HooksDirIsCreated() => Assert.True(fixture.HooksDirMade);

    [Fact(DisplayName = "...and writing a hook fails")]
    public void HookWriteFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: hook");

    [Fact(DisplayName = "git config core.fsmonitor fails")]
    public void GitConfigFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: config");

    [Fact(DisplayName = "appending to .git/config fails")]
    public void ConfigAppendFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: configwrite");

    [Fact(DisplayName = "...and .git/config is unchanged on the host")]
    public void ConfigIsUnchanged() => Assert.Equal(fixture.ConfigHashBefore, fixture.ConfigHashAfter);

    [Fact(DisplayName = "mv .git .git2 fails")]
    public void MoveGitFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: mvgit");

    [Fact(DisplayName = "a host worktree's commondir is read-only")]
    public void WorktreeCommondirIsReadOnly() => IntegrationAssert.HasLine(fixture.Probes, "fail: wtcommondir");

    [Fact(DisplayName = "...as is the rest of its entry (its HEAD)")]
    public void WorktreeHeadIsReadOnly() => IntegrationAssert.HasLine(fixture.Probes, "fail: wthead");

    [Fact(DisplayName = "git worktree prune doesn't delete the entry of a worktree outside the project")]
    public void PruneKeepsTheEntry() => IntegrationAssert.HasLine(fixture.Probes, "fail: wtprune");

    [Fact(DisplayName = "...which is still there afterward")]
    public void EntryIsStillThere() => IntegrationAssert.HasLine(fixture.Probes, "ok: wtkept");

    [Fact(DisplayName = "mv .git/worktrees fails")]
    public void MoveWorktreesFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: mvworktrees");

    [Fact(DisplayName = "...and the worktree still works on the host")]
    public void WorktreeWorksOnTheHost() =>
        Assert.Multiple(
            () => IntegrationAssert.Succeeded(fixture.WorktreeStatus),
            () => IntegrationAssert.Succeeded(fixture.WorktreeCommit));

    [Fact(DisplayName = "...and git worktree list on the host doesn't call it prunable")]
    public void WorktreeIsntPrunable() =>
        Assert.True(
            !fixture.WorktreeList.Stdout.Contains("prunable", StringComparison.Ordinal),
            fixture.WorktreeList.Describe());

    [Fact(DisplayName = ".pen/ is created empty when missing")]
    public void PenDirIsCreatedEmpty() =>
        Assert.True(fixture.PenDirMade && fixture.PenDirEntries.Count == 0, string.Join(' ', fixture.PenDirEntries));

    [Fact(DisplayName = "...touch .pen/config.toml fails inside")]
    public void PenConfigCantBeCreated() => IntegrationAssert.HasLine(fixture.Probes, "fail: pencreate");

    [Fact(DisplayName = "...mv .pen fails")]
    public void MovePenFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: mvpen");

    [Fact(DisplayName = "...and host git status shows nothing new")]
    public void GitStatusShowsNothing()
    {
        var shown = fixture.Status.Stdout.Replace('\n', ' ');

        Assert.True(
            fixture.Status.ExitCode == 0 && shown.Length == 0,
            $"...and host git status shows nothing new (it shows: {shown}) {fixture.Status.Describe()}");
    }

    [Fact(DisplayName = "with .pen/config.toml present, it can't be written or moved")]
    public void PenConfigCantBeWrittenOrMoved() =>
        Assert.True(
            fixture.PenConfigSession.OutputComplete
                && !fixture.PenConfigSession.OutputLines.Any(line => line is "wrote" or "moved"),
            fixture.PenConfigSession.Describe());

    [Fact(DisplayName = "...and is unchanged")]
    public void PenConfigIsUnchanged() => Assert.Equal("# host-written", fixture.PenConfigAfter);

    [Fact(DisplayName = "a config file .git/config includes from the project is read-only")]
    public void IncludedConfigIsReadOnly() =>
        Assert.True(
            fixture.Include.OutputComplete && !fixture.Include.OutputLines.Contains("wrote") && !fixture.IncludeWritten,
            $"written: {fixture.IncludeWritten}; {fixture.Include.Describe()}");

    [Fact(DisplayName = "included worktreeConfig settings and recursive worktree includes are protected")]
    public void WorktreeIncludesAreProtected() =>
        Assert.True(
            fixture.WorktreeIncludes.OutputComplete && !fixture.WorktreeIncludes.HasLineStartingWith("wrote "),
            fixture.WorktreeIncludes.Describe());

    /// <summary>With a linked worktree outside the project and no .git/hooks or .pen/, runs a session of probes and
    /// checks the host after it; then, with the worktree gone, sessions that try to write .pen/config.toml and the
    /// files .git/config includes.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        internal ProbeResult Probes { get; private set; } = null!;

        internal string ConfigHashBefore { get; private set; } = null!;

        internal string ConfigHashAfter { get; private set; } = null!;

        internal bool HooksDirMade { get; private set; }

        internal CommandResult WorktreeStatus { get; private set; } = null!;

        internal CommandResult WorktreeCommit { get; private set; } = null!;

        internal CommandResult WorktreeList { get; private set; } = null!;

        internal bool PenDirMade { get; private set; }

        internal IReadOnlyList<string> PenDirEntries { get; private set; } = [];

        internal CommandResult Status { get; private set; } = null!;

        internal CommandResult PenConfigSession { get; private set; } = null!;

        /// <summary>Gets .pen/config.toml's text as $(cat) gives it, after the session.</summary>
        internal string PenConfigAfter { get; private set; } = null!;

        internal CommandResult Include { get; private set; } = null!;

        /// <summary>Gets whether the included .gitinc had anything in it after the session (test -s).</summary>
        internal bool IncludeWritten { get; private set; }

        internal CommandResult WorktreeIncludes { get; private set; } = null!;

        private string GitDir => Path.Combine(Project.Dir, ".git");

        private string PenDir => Path.Combine(Project.Dir, ".pen");

        protected override void Prepare()
        {
            var worktree = Path.Combine(Scratch, "wt");
            Project.RunGit("worktree", "add", "-q", worktree);
            RunProbes();
            CheckHost(worktree);
            Project.RunGit("worktree", "remove", "--force", worktree);
            Project.RunGit("branch", "-q", "-D", "scratch");
            RunWithPenConfig();
            RunWithInclude();
            RunWithWorktreeIncludes();
        }

        private void RunProbes()
        {
            Files.DeleteTree(Path.Combine(GitDir, "hooks"));
            Files.DeleteTree(PenDir);
            ConfigHashBefore = Files.HashFile(Path.Combine(GitDir, "config"));
            Probes = new SandboxProbe()
                .Add("commit", "git -c user.name=a -c user.email=a@example.invalid commit -q --allow-empty -m inside")
                .Add("branchwork", """
                    git checkout -q -b scratch && git checkout -q - && git merge -q --ff-only scratch && git stash list
                    """)
                .Add("hook", "echo x >> .git/hooks/pre-commit")
                .Add("config", "git config core.fsmonitor x")
                .Add("configwrite", "echo x >> .git/config")
                .Add("mvgit", "mv .git .git2")
                .Add("wtcommondir", "echo x > .git/worktrees/wt/commondir")
                .Add("wthead", "echo 0000000000000000000000000000000000000000 > .git/worktrees/wt/HEAD")
                .Add("wtprune", "git worktree prune && test ! -e .git/worktrees/wt")
                .Add("wtkept", "test -d .git/worktrees/wt")
                .Add("mvworktrees", "mv .git/worktrees .git/worktrees2")
                .Add("pencreate", "touch .pen/config.toml")
                .Add("mvpen", "mv .pen .pen2")
                .Run(Cli);
            Probes.Session.RequireSuccess();
        }

        private void CheckHost(string worktree)
        {
            HooksDirMade = Files.IsDirectory(Path.Combine(GitDir, "hooks"));
            ConfigHashAfter = Files.HashFile(Path.Combine(GitDir, "config"));
            WorktreeStatus = IntegrationGit.CreateCommand(worktree, "status", "--porcelain").Run();
            WorktreeCommit = IntegrationGit.CreateCommand(
                worktree, "-c", "user.name=s", "-c", "user.email=s@example.invalid", "-c", "commit.gpgsign=false",
                "commit", "-q", "--allow-empty", "-m", "host-side").Run();
            WorktreeList = IntegrationGit.CreateCommand(Project.Dir, "worktree", "list", "--porcelain").Run();
            PenDirMade = Files.IsDirectory(PenDir);
            PenDirEntries = Files.ListNames(PenDir);
            Status = IntegrationGit.CreateCommand(Project.Dir, "status", "--porcelain").Run();
        }

        private void RunWithPenConfig()
        {
            var config = Path.Combine(PenDir, "config.toml");
            File.WriteAllText(config, "# host-written\n");
            PenConfigSession = Cli.Run("run", "bash", "-c", """
                echo x >> .pen/config.toml && echo wrote; mv .pen .pen2 && echo moved; true
                """).RequireSuccess();
            PenConfigAfter = File.ReadAllText(config).TrimEnd('\n');
            File.Delete(config);
        }

        private void RunWithInclude()
        {
            var included = Path.Combine(Project.Dir, ".gitinc");
            File.WriteAllText(included, "");
            Project.RunGit("config", "include.path", "../.gitinc");
            Include = Cli.Run("run", "bash", "-c", """
                echo "[core] fsmonitor = x" >> .gitinc && echo wrote; true
                """).RequireSuccess();
            IncludeWritten = Files.IsNonEmpty(included);
            Project.RunGit("config", "--unset", "include.path");
            File.Delete(included);
        }

        private void RunWithWorktreeIncludes()
        {
            File.WriteAllText(Path.Combine(GitDir, "extension.inc"), "[extensions]\nworktreeConfig = true\n");
            Project.RunGit("config", "include.path", "extension.inc");
            File.WriteAllText(Path.Combine(GitDir, "config.worktree"), "[include]\npath = ../.gitinc\n");
            File.WriteAllText(Path.Combine(Project.Dir, ".gitinc"), "[include]\npath = .gitinc-nested\n");
            File.WriteAllText(Path.Combine(Project.Dir, ".gitinc-nested"), "");
            WorktreeIncludes = Cli.Run("run", "bash", "-c", """
                for f in .git/config.worktree .gitinc .gitinc-nested; do echo x >> "$f" && echo "wrote $f"; done; true
                """).RequireSuccess();
            Project.RunGit("config", "--unset", "include.path");
            File.Delete(Path.Combine(GitDir, "extension.inc"));
            File.Delete(Path.Combine(GitDir, "config.worktree"));
            File.Delete(Path.Combine(Project.Dir, ".gitinc"));
            File.Delete(Path.Combine(Project.Dir, ".gitinc-nested"));
        }
    }
}
