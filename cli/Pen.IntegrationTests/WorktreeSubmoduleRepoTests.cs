namespace Pen.IntegrationTests;

/// <summary>A submodule repo in a linked worktree's entry (.git/worktrees/&lt;n&gt;/modules/), with its checkout
/// deinitialized: protected like .git itself, and refused at start with a commondir or as a symlink.</summary>
/// <remarks>
/// With the checkout deinitialized, nothing in the tree points at the module repo, so pen starts, and the repo
/// must still be protected. <see cref="ProjectSubmoduleTests"/> has the project's own submodules.
/// </remarks>
public sealed class WorktreeSubmoduleRepoTests(WorktreeSubmoduleRepoTests.Fixture fixture)
    : IClassFixture<WorktreeSubmoduleRepoTests.Fixture>
{
    [Fact(DisplayName = "the worktree's submodule repo exists, with its checkout deinitialized")]
    public void ModuleRepoExists() => Assert.True(fixture.ModuleRepoReady);

    [Fact(DisplayName = "pen starts with a submodule repo in a worktree's entry")]
    public void PenStarts() => IntegrationAssert.HasLine(fixture.Probes, "ok: ref");

    [Fact(DisplayName = "writing the submodule repo's config fails")]
    public void ConfigWriteFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: configwrite");

    [Fact(DisplayName = "...as does git config there")]
    public void GitConfigFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: config");

    [Fact(DisplayName = "writing a hook in the submodule repo fails")]
    public void HookWriteFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: hook");

    [Fact(DisplayName = "mv of the submodule repo fails")]
    public void MoveFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: mvrepo");

    [Fact(DisplayName = "the session printed no warning")]
    public void NoWarning() => IntegrationAssert.Lacks(fixture.Probes.Session, "WARNING");

    [Fact(DisplayName = "a submodule repo with a commondir is refused at start")]
    public void CommondirIsRefused() => IntegrationAssert.Refused(fixture.WithCommondir, "libs/x/commondir exists");

    [Fact(DisplayName = "a symlinked submodule repo is refused at start")]
    public void SymlinkIsRefused() => IntegrationAssert.Refused(fixture.WithSymlink, "modules/evil is a symlink");

    [Fact(DisplayName = "nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    /// <summary>Adds a worktree under .claude/worktrees/ with a submodule, deinitializes its checkout, runs a
    /// session of probes on the module repo, then sessions with a commondir in it and a symlinked module repo
    /// beside it.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        internal bool ModuleRepoReady { get; private set; }

        internal ProbeResult Probes { get; private set; } = null!;

        internal CommandResult WithCommondir { get; private set; } = null!;

        internal CommandResult WithSymlink { get; private set; } = null!;

        internal int ContainersAtEnd { get; private set; }

        protected override void Prepare()
        {
            var library = Path.Combine(Scratch, "smlib");
            IntegrationGit.CreateRepo(library, "lib");
            Project.RunGit("worktree", "add", "-q", ".claude/worktrees/sm");
            var worktree = Path.Combine(Project.Dir, ".claude", "worktrees", "sm");
            IntegrationGit.Run(
                worktree, "-c", "protocol.file.allow=always", "submodule", "add", "-q", library, "libs/x");
            IntegrationGit.Run(worktree, "submodule", "deinit", "-q", "-f", "libs/x");
            var modules = Path.Combine(Project.Dir, ".git", "worktrees", "sm", "modules");
            var module = Path.Combine(modules, "libs", "x");
            ModuleRepoReady = Files.IsRegularFile(Path.Combine(module, "HEAD"))
                && !Files.Exists(Path.Combine(worktree, "libs", "x", ".git"));

            Probes = new SandboxProbe()
                .Add("configwrite", $"echo x >> \"{module}/config\"")
                .Add("config", $"git --git-dir=\"{module}\" config core.fsmonitor x")
                .Add("hook", $"echo x >> \"{module}/hooks/post-checkout\"")
                .Add("mvrepo", $"mv \"{module}\" \"{module}.2\"")
                .Add("ref", $"git --git-dir=\"{module}\" update-ref refs/heads/integration HEAD")
                .Run(Cli);
            Probes.Session.RequireSuccess();
            // A repo the sandbox could move (which fails its fact) is put back, so the checks after it still mean
            // something.
            if (!Directory.Exists(module) && Directory.Exists(module + ".2"))
                Directory.Move(module + ".2", module);
            IntegrationGit.Run(Project.Dir, $"--git-dir={module}", "update-ref", "-d", "refs/heads/integration");

            var commondir = Path.Combine(module, "commondir");
            File.WriteAllText(commondir, "");
            WithCommondir = Cli.Run("run", "true");
            File.Delete(commondir);
            var evil = Path.Combine(modules, "evil");
            File.CreateSymbolicLink(evil, Path.Combine(library, ".git"));
            WithSymlink = Cli.Run("run", "true");
            File.Delete(evil);
            ContainersAtEnd = Docker.CountContainers(Key);
        }
    }
}
