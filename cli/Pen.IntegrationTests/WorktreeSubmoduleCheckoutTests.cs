namespace Pen.IntegrationTests;

/// <summary>A linked worktree's submodule, checked out: git works in it, its repo and the levels above it stay
/// protected, and the session-end check quarantines only module repos and .git files made during the
/// session.</summary>
/// <remarks>
/// As in <see cref="WorktreeSubmoduleRepoTests"/>, but the checkout's .git file points at the module repo, whose
/// core.worktree points back, so pen starts.
/// </remarks>
public sealed class WorktreeSubmoduleCheckoutTests(WorktreeSubmoduleCheckoutTests.Fixture fixture)
    : IClassFixture<WorktreeSubmoduleCheckoutTests.Fixture>
{
    [Fact(DisplayName = "the worktree's submodule is checked out")]
    public void SubmoduleIsCheckedOut() => Assert.True(fixture.CheckedOut);

    [Fact(DisplayName = "pen starts with a submodule checked out in a worktree")]
    public void PenStarts() => IntegrationAssert.HasLine(fixture.Probes, "ok: ref");

    [Fact(DisplayName = "git in the submodule checkout works in the sandbox")]
    public void GitWorksInTheCheckout() => IntegrationAssert.Succeeded(fixture.RefOnTheHost);

    [Fact(DisplayName = "writing the submodule repo's config still fails")]
    public void ConfigWriteFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: configwrite");

    [Fact(DisplayName = "mv of a level above the submodule repo fails")]
    public void MoveLevelFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: mvlevel");

    [Fact(DisplayName = "mv of modules/ fails")]
    public void MoveModulesFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: mvmodules");

    [Fact(DisplayName = "the sandbox could make a new module repo and a .git file")]
    public void SandboxMadeTheRepoAndFile() =>
        Assert.Multiple(
            () => IntegrationAssert.HasLine(fixture.Probes, "ok: newrepo"),
            () => IntegrationAssert.HasLine(fixture.Probes, "ok: forged"));

    [Fact(DisplayName = "the session warned")]
    public void SessionWarned() => IntegrationAssert.Contains(fixture.Probes.Session, "WARNING");

    [Fact(DisplayName = "...about the new module repo")]
    public void WarnedAboutTheNewRepo() =>
        IntegrationAssert.Contains(fixture.Probes.Session, "worktrees/sm/modules/new → ");

    [Fact(DisplayName = "...and the forged .git file")]
    public void WarnedAboutTheForgedFile() => IntegrationAssert.Contains(fixture.Probes.Session, "forged/.git → ");

    [Fact(DisplayName = "...and nothing about the submodule that was there")]
    public void NothingAboutTheExistingSubmodule() => IntegrationAssert.Lacks(fixture.Probes.Session, "libs/x");

    [Fact(DisplayName = "the new module repo was quarantined")]
    public void NewRepoIsQuarantined() => Assert.True(fixture.NewRepoQuarantined);

    [Fact(DisplayName = "the existing module repo and its checkout's .git file are untouched")]
    public void ExistingRepoIsUntouched() =>
        Assert.True(fixture.ExistingUntouched, fixture.StatusOnTheHost.Describe());

    [Fact(DisplayName = "a second session ends without a warning")]
    public void SecondSessionDoesntWarn() => IntegrationAssert.Lacks(fixture.Second, "WARNING");

    [Fact(DisplayName = "nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    /// <summary>Adds a worktree under .claude/worktrees/ with a submodule checked out, runs a session of probes that
    /// also make a module repo and a forged .git file, then a second session.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        internal bool CheckedOut { get; private set; }

        internal ProbeResult Probes { get; private set; } = null!;

        internal CommandResult RefOnTheHost { get; private set; } = null!;

        internal bool NewRepoQuarantined { get; private set; }

        internal bool ExistingUntouched { get; private set; }

        internal CommandResult StatusOnTheHost { get; private set; } = null!;

        internal CommandResult Second { get; private set; } = null!;

        internal int ContainersAtEnd { get; private set; }

        protected override void Prepare()
        {
            var library = Path.Combine(Scratch, "smlib");
            IntegrationGit.CreateRepo(library, "lib");
            Project.RunGit("worktree", "add", "-q", ".claude/worktrees/sm");
            var worktree = Path.Combine(Project.Dir, ".claude", "worktrees", "sm");
            IntegrationGit.Run(
                worktree, "-c", "protocol.file.allow=always", "submodule", "add", "-q", library, "libs/x");
            var modules = Path.Combine(Project.Dir, ".git", "worktrees", "sm", "modules");
            var module = Path.Combine(modules, "libs", "x");
            var checkout = Path.Combine(worktree, "libs", "x");
            CheckedOut = Files.IsRegularFile(Path.Combine(checkout, ".git"));

            Probes = new SandboxProbe()
                .Add("ref", $"git -C \"{checkout}\" update-ref refs/heads/integration HEAD")
                .Add("configwrite", $"echo x >> \"{module}/config\"")
                .Add("mvlevel", $"mv \"{modules}/libs\" \"{modules}/libs.2\"")
                .Add("mvmodules", $"mv \"{modules}\" \"{modules}.2\"")
                .Add("newrepo", $"git init -q --bare \"{modules}/new\"")
                .Add("forged", $"""
                    mkdir -p "{worktree}/forged" && echo "gitdir: {module}" > "{worktree}/forged/.git"
                    """)
                .Run(Cli);
            Probes.Session.RequireSuccess();
            RefOnTheHost = IntegrationGit.CreateCommand(
                checkout, "rev-parse", "-q", "--verify", "refs/heads/integration").Run();
            NewRepoQuarantined = !Files.Exists(Path.Combine(modules, "new"))
                && Files.IsDirectory(Path.Combine(modules, "new.pen-quarantine"))
                && Files.IsRegularFile(Path.Combine(worktree, "forged", ".git.pen-quarantine"));
            StatusOnTheHost = IntegrationGit.CreateCommand(checkout, "status", "--porcelain").Run();
            ExistingUntouched = Files.IsRegularFile(Path.Combine(module, "HEAD"))
                && Files.IsRegularFile(Path.Combine(checkout, ".git")) && StatusOnTheHost.ExitCode == 0;

            Second = Cli.Run("run", "bash", "-c", "true").RequireSuccess();
            ContainersAtEnd = Docker.CountContainers(Key);
        }
    }
}
