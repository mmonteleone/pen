namespace Pen.SmokeTests;

/// <summary>Submodules in the project: git works in an initialized one while its repo in .git/modules stays
/// protected, a repointed checkout .git file is quarantined, and the old layout with its own .git dir is refused
/// until absorbgitdirs.</summary>
public sealed class ProjectSubmoduleTests(ProjectSubmoduleTests.Fixture fixture)
    : IClassFixture<ProjectSubmoduleTests.Fixture>
{
    [Fact(DisplayName = "the submodule is initialized, its repo in .git/modules")]
    public void SubmoduleIsInitialized() => Assert.True(fixture.Initialized);

    [Fact(DisplayName = "pen starts with an initialized submodule")]
    public void PenStarts() => SmokeAssert.HasLine(fixture.Probes, "ok: commit");

    [Fact(DisplayName = "...and committing inside it works")]
    public void CommitInsideWorks() => Assert.Equal("in-sub", fixture.SubmoduleSubject);

    [Fact(DisplayName = "...as does committing the bump in the superproject")]
    public void BumpWorks() => Assert.Equal("bump", fixture.ProjectSubject);

    [Fact(DisplayName = "writing the submodule repo's config fails")]
    public void ConfigWriteFails() => SmokeAssert.HasLine(fixture.Probes, "fail: configwrite");

    [Fact(DisplayName = "...as does git config in the submodule")]
    public void GitConfigFails() => SmokeAssert.HasLine(fixture.Probes, "fail: config");

    [Fact(DisplayName = "writing a hook in the submodule repo fails")]
    public void HookWriteFails() => SmokeAssert.HasLine(fixture.Probes, "fail: hook");

    [Fact(DisplayName = "the session printed no warning")]
    public void NoWarning() => SmokeAssert.Lacks(fixture.Probes.Session, "WARNING");

    [Fact(DisplayName = "repointing the checkout's .git file is quarantined at the end")]
    public void RepointedGitFileIsQuarantined() =>
        Assert.True(
            fixture.Repointed.Output.Contains("libs/x/.git → ", StringComparison.Ordinal)
                && fixture.RepointedQuarantined,
            $"quarantined: {fixture.RepointedQuarantined}; {fixture.Repointed.Describe()}");

    [Fact(DisplayName = "with it put back, git in the submodule works on the host")]
    public void GitWorksWhenPutBack() => SmokeAssert.Succeeded(fixture.StatusPutBack);

    [Fact(DisplayName = "...and the next session ends without a warning")]
    public void NextSessionDoesntWarn() => SmokeAssert.Lacks(fixture.AfterPutBack, "WARNING");

    [Fact(DisplayName = "a submodule with its own .git dir is refused, naming it")]
    public void OwnGitDirIsRefused() =>
        SmokeAssert.Refused(fixture.OwnGitDir, "libs/y (a submodule with its own .git dir)");

    [Fact(DisplayName = "...suggesting git submodule absorbgitdirs")]
    public void RefusalSuggestsAbsorbgitdirs() =>
        SmokeAssert.Refused(fixture.OwnGitDir, "git submodule absorbgitdirs");

    [Fact(DisplayName = "after absorbgitdirs, pen starts")]
    public void StartsAfterAbsorbgitdirs() => SmokeAssert.Succeeded(fixture.Absorbed);

    [Fact(DisplayName = "nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    /// <summary>Adds and commits a submodule, runs a session of probes in it and one that repoints its .git file,
    /// then replaces it with a clone that has its own .git dir.</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal bool Initialized { get; private set; }

        internal ProbeResult Probes { get; private set; } = null!;

        /// <summary>Gets the subject of the submodule's last commit, as $(git -C libs/x log -1 --format=%s) gives
        /// it.</summary>
        internal string SubmoduleSubject { get; private set; } = null!;

        /// <summary>Gets the subject of the project's last commit.</summary>
        internal string ProjectSubject { get; private set; } = null!;

        internal CommandResult Repointed { get; private set; } = null!;

        internal bool RepointedQuarantined { get; private set; }

        internal CommandResult StatusPutBack { get; private set; } = null!;

        internal CommandResult AfterPutBack { get; private set; } = null!;

        internal CommandResult OwnGitDir { get; private set; } = null!;

        internal CommandResult Absorbed { get; private set; } = null!;

        internal int ContainersAtEnd { get; private set; }

        private string Checkout => Path.Combine(Project.Dir, "libs", "x");

        protected override void Prepare()
        {
            var library = Path.Combine(Scratch, "smlib");
            SmokeGit.CreateRepo(library, "lib");
            SmokeGit.RunHostGit(Project.Dir, "submodule", "add", "-q", library, "libs/x");
            SmokeGit.RunHostGit(Project.Dir, "commit", "-q", "-m", "add libs/x");
            var module = Path.Combine(Project.Dir, ".git", "modules", "libs", "x");
            Initialized = Files.IsRegularFile(Path.Combine(Checkout, ".git"))
                && Files.IsRegularFile(Path.Combine(module, "HEAD"));
            RunProbes(module);
            RunRepointed();
            RunOwnGitDir(library);
            ContainersAtEnd = Docker.CountContainers(Key);
        }

        private void RunProbes(string module)
        {
            Probes = new SandboxProbe { Setup = """g() { git -c user.name=a -c user.email=a@example.invalid "$@"; }""" }
                .Add("commit", "g -C libs/x commit -q --allow-empty -m in-sub")
                .Add("bump", "g add libs/x && g commit -q -m bump")
                .Add("configwrite", $"echo x >> \"{module}/config\"")
                .Add("config", "git -C libs/x config core.fsmonitor x")
                .Add("hook", $"echo x >> \"{module}/hooks/post-checkout\"")
                .Run(Cli);
            Probes.Session.RequireSuccess();
            SubmoduleSubject = SmokeGit.CreateCommand(Checkout, "log", "-1", "--format=%s").Run().CapturedStdout;
            ProjectSubject = SmokeGit.CreateCommand(Project.Dir, "log", "-1", "--format=%s").Run().CapturedStdout;
        }

        /// <summary>Has the sandbox repoint the checkout's .git file, which is the project's own (writable, like any
        /// file), then puts it back.</summary>
        private void RunRepointed()
        {
            var gitFile = Path.Combine(Checkout, ".git");
            Repointed = Cli.Run("run", "bash", "-c", """echo "gitdir: /tmp" > libs/x/.git""");
            RepointedQuarantined = Files.IsRegularFile(gitFile + ".pen-quarantine");
            File.Delete(gitFile + ".pen-quarantine");
            File.WriteAllText(gitFile, "gitdir: ../../.git/modules/libs/x\n");
            StatusPutBack = SmokeGit.CreateCommand(Checkout, "status", "--porcelain").Run();
            AfterPutBack = Cli.Run("run", "bash", "-c", "true");
        }

        /// <summary>Replaces the submodule with one in the old layout, with its own .git dir in the checkout, and
        /// runs pen before and after git submodule absorbgitdirs.</summary>
        private void RunOwnGitDir(string library)
        {
            SmokeGit.RunHostGit(Project.Dir, "submodule", "deinit", "-q", "-f", "libs/x");
            Project.RunGit("rm", "-q", "-f", "libs/x");
            Files.DeleteTree(Path.Combine(Project.Dir, ".git", "modules"));
            SmokeGit.RunHostGit(Project.Dir, "commit", "-q", "-m", "remove libs/x");
            Project.RunGit("clone", "-q", library, "libs/y");
            SmokeGit.RunHostGit(Project.Dir, "submodule", "add", "-q", library, "libs/y");
            OwnGitDir = Cli.Run("run", "true");
            SmokeGit.RunHostGit(Project.Dir, "submodule", "absorbgitdirs");
            Absorbed = Cli.Run("run", "true");
        }
    }
}
