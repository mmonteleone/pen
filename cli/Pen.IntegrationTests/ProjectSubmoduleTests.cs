using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

/// <summary>Submodules in the project: git works in an initialized one while its repo in .git/modules stays
/// protected, a repointed checkout .git file is quarantined, a submodule repo the agent makes isn't kept by a session
/// started alongside, and the old layout with its own .git dir is refused until absorbgitdirs.</summary>
public sealed class ProjectSubmoduleTests(ProjectSubmoduleTests.Fixture fixture)
    : IClassFixture<ProjectSubmoduleTests.Fixture>
{
    [Fact(DisplayName = "the submodule is initialized, its repo in .git/modules")]
    public void SubmoduleIsInitialized() => Assert.True(fixture.Initialized);

    [Fact(DisplayName = "pen starts with an initialized submodule")]
    public void PenStarts() => IntegrationAssert.HasLine(fixture.Probes, "ok: commit");

    [Fact(DisplayName = "...and committing inside it works")]
    public void CommitInsideWorks() => Assert.Equal("in-sub", fixture.SubmoduleSubject);

    [Fact(DisplayName = "...as does committing the bump in the superproject")]
    public void BumpWorks() => Assert.Equal("bump", fixture.ProjectSubject);

    [Fact(DisplayName = "writing the submodule repo's config fails")]
    public void ConfigWriteFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: configwrite");

    [Fact(DisplayName = "...as does git config in the submodule")]
    public void GitConfigFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: config");

    [Fact(DisplayName = "writing a hook in the submodule repo fails")]
    public void HookWriteFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: hook");

    [Fact(DisplayName = "the session printed no warning")]
    public void NoWarning() => IntegrationAssert.Lacks(fixture.Probes.Session, "WARNING");

    [Fact(DisplayName = "repointing the checkout's .git file is quarantined at the end")]
    public void RepointedGitFileIsQuarantined() =>
        Assert.True(
            fixture.Repointed.Output.Contains("libs/x/.git → ", StringComparison.Ordinal)
                && fixture.RepointedQuarantined,
            $"quarantined: {fixture.RepointedQuarantined}; {fixture.Repointed.Describe()}");

    [Fact(DisplayName = "with it put back, git in the submodule works on the host")]
    public void GitWorksWhenPutBack() => IntegrationAssert.Succeeded(fixture.StatusPutBack);

    [Fact(DisplayName = "...and the next session ends without a warning")]
    public void NextSessionDoesntWarn() => IntegrationAssert.Lacks(fixture.AfterPutBack, "WARNING");

    [Fact(DisplayName =
        "the replay is real: while that session runs, git in the checkout of a submodule repo its agent made runs its config")]
    public void PlantFiresWhilePlanting() => Assert.True(fixture.FiresWhilePlanting);

    [Fact(DisplayName = "a second session starts alongside the planting one")]
    public void SecondStartsAlongside() => Assert.True(fixture.StartedAlongside);

    [Fact(DisplayName = "the planting session's end quarantines that repo")]
    public void PlanterQuarantinesIt() =>
        Assert.True(fixture.Planter.Output.Contains(".git/modules/evil → ", StringComparison.Ordinal),
            fixture.Planter.Describe());

    [Fact(DisplayName = "the second session's agent makes it again, and git in the checkout runs it again")]
    public void PlantFiresWhenMadeAgain() => Assert.True(fixture.FiresWhenMadeAgain);

    [Fact(DisplayName = "...but the second session didn't take it as its own: its end quarantines it")]
    public void AlongsideQuarantinesIt() =>
        Assert.True(
            fixture.Alongside.ExitCode == 0
                && fixture.Alongside.Output.Contains(".git/modules/evil → ", StringComparison.Ordinal),
            fixture.Alongside.Describe());

    [Fact(DisplayName = "...so git in the checkout no longer runs it")]
    public void PlantNoLongerFires() => Assert.False(fixture.FiresAfterBoth);

    [Fact(DisplayName = "a submodule with its own .git dir is refused, naming it")]
    public void OwnGitDirIsRefused() =>
        IntegrationAssert.Refused(fixture.OwnGitDir, "libs/y (a submodule with its own .git dir)");

    [Fact(DisplayName = "...suggesting git submodule absorbgitdirs")]
    public void RefusalSuggestsAbsorbgitdirs() =>
        IntegrationAssert.Refused(fixture.OwnGitDir, "git submodule absorbgitdirs");

    [Fact(DisplayName = "after absorbgitdirs, pen starts")]
    public void StartsAfterAbsorbgitdirs() => IntegrationAssert.Succeeded(fixture.Absorbed);

    [Fact(DisplayName = "nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    /// <summary>Adds and commits a submodule, runs a session of probes in it and one that repoints its .git file,
    /// then replaces it with a clone that has its own .git dir.</summary>
    public sealed class Fixture : IntegrationFixture
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

        internal bool FiresWhilePlanting { get; private set; }

        internal bool StartedAlongside { get; private set; }

        internal CommandResult Planter { get; private set; } = null!;

        internal bool FiresWhenMadeAgain { get; private set; }

        internal CommandResult Alongside { get; private set; } = null!;

        internal bool FiresAfterBoth { get; private set; }

        internal CommandResult OwnGitDir { get; private set; } = null!;

        internal CommandResult Absorbed { get; private set; } = null!;

        internal int ContainersAtEnd { get; private set; }

        private string Checkout => Path.Combine(Project.Dir, "libs", "x");

        private string EvilCheckout => Path.Combine(Project.Dir, "evil");

        private string EvilConfig => Path.Combine(Project.Dir, ".git", "modules", "evil", "config");

        private string AgentPattern => $"^{Key}-.*-agent$";

        private string Hook => Path.Combine(Scratch, "hook-sm");

        private string Canary => Path.Combine(Scratch, "canary-sm");

        /// <summary>Gets the agent's command that makes the submodule repo .git/modules/evil, with the host's hook as
        /// its core.fsmonitor, and its checkout evil, which git and Pen both take for a submodule's.</summary>
        private string PlantScript => $$"""
            git init -q --separate-git-dir="$PWD/.git/modules/evil" evil && printf '[core]\n\tworktree = %s\n\tfsmonitor = %s\n' "$PWD/evil" '{{Hook}}' >>.git/modules/evil/config
            """;

        /// <summary>Gets the second agent's command: once the planting session's end has moved the repo aside, it
        /// makes it again, says so (replanted), and exits once the host has looked (go).</summary>
        private string AlongsideScript => $$"""
            for i in $(seq 600); do [ -e .git/modules/evil ] || break; sleep 0.2; done; rm -f evil/.git && {{PlantScript}} && touch replanted && for i in $(seq 600); do [ -e go ] && break; sleep 0.2; done
            """;

        protected override void Prepare()
        {
            var library = Path.Combine(Scratch, "smlib");
            IntegrationGit.CreateRepo(library, "lib");
            IntegrationGit.RunHostGit(Project.Dir, "submodule", "add", "-q", library, "libs/x");
            IntegrationGit.RunHostGit(Project.Dir, "commit", "-q", "-m", "add libs/x");
            var module = Path.Combine(Project.Dir, ".git", "modules", "libs", "x");
            Initialized = Files.IsRegularFile(Path.Combine(Checkout, ".git"))
                && Files.IsRegularFile(Path.Combine(module, "HEAD"));
            RunProbes(module);
            RunRepointed();
            RunAlongsidePlanter();
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
            SubmoduleSubject = IntegrationGit.CreateCommand(Checkout, "log", "-1", "--format=%s").Run().CapturedStdout;
            ProjectSubject = IntegrationGit.CreateCommand(Project.Dir, "log", "-1", "--format=%s").Run().CapturedStdout;
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
            StatusPutBack = IntegrationGit.CreateCommand(Checkout, "status", "--porcelain").Run();
            AfterPutBack = Cli.Run("run", "bash", "-c", "true");
        }

        /// <summary>Starts a session whose agent makes a submodule repo and a second one beside it, ends the planting
        /// one, has the second's agent make the repo again, then ends the second.</summary>
        private void RunAlongsidePlanter()
        {
            File.WriteAllText(Hook, $"#!/bin/sh\ntouch {Canary}\n");
            File.SetUnixFileMode(Hook, Files.Mode755);
            var planter = Track(Cli.Start("run", "bash", "-c", $"{PlantScript}; sleep 120"));
            if (!Polling.WaitForContainer(AgentPattern))
                throw new InvalidOperationException($"planting session didn't start: {planter.OutputSoFar}");
            Polling.WaitUntil(
                () => Files.Exists(EvilConfig) && File.ReadAllText(EvilConfig).Contains(Hook, StringComparison.Ordinal),
                TimeSpan.FromSeconds(10));
            var planterAgent = FindAgent(except: null);
            FiresWhilePlanting = Fires();
            var other = Track(Cli.Start("run", "bash", "-c", AlongsideScript));
            StartedAlongside = Polling.WaitForContainers(AgentPattern, 2);
            Docker.Run("rm", "-f", planterAgent).RequireSuccess();
            Planter = planter.WaitForExit();
            Polling.WaitUntil(() => Files.Exists(Path.Combine(Project.Dir, "replanted")), TimeSpan.FromSeconds(60));
            FiresWhenMadeAgain = Fires();
            File.WriteAllText(Path.Combine(Project.Dir, "go"), "");
            Alongside = other.WaitForExit();
            FiresAfterBoth = Fires();

            foreach (var name in new[] { "evil", "replanted", "go" })
                Files.DeleteTree(Path.Combine(Project.Dir, name));
            var modules = Path.Combine(Project.Dir, ".git", "modules");
            foreach (var entry in Directory.EnumerateFileSystemEntries(modules, "evil*"))
                Files.DeleteTree(entry);
        }

        /// <summary>Finds a running agent container of the project, as docker ps | grep | head -1 does.</summary>
        /// <param name="except">An agent to leave out, or null.</param>
        /// <exception cref="InvalidOperationException">There's none.</exception>
        private string FindAgent(string? except) =>
            Docker.ListRunningNames().FirstOrDefault(
                name => Regex.IsMatch(name, AgentPattern) && name != except)
            ?? throw new InvalidOperationException($"no running agent of {Key} other than {except}");

        /// <summary>Runs host git status in the planted checkout and checks whether it ran the hook.</summary>
        private bool Fires()
        {
            File.Delete(Canary);
            IntegrationGit.CreateCommand(EvilCheckout, "status").Run();
            return Files.Exists(Canary);
        }

        /// <summary>Replaces the submodule with one in the old layout, with its own .git dir in the checkout, and
        /// runs pen before and after git submodule absorbgitdirs.</summary>
        private void RunOwnGitDir(string library)
        {
            IntegrationGit.RunHostGit(Project.Dir, "submodule", "deinit", "-q", "-f", "libs/x");
            Project.RunGit("rm", "-q", "-f", "libs/x");
            Files.DeleteTree(Path.Combine(Project.Dir, ".git", "modules"));
            IntegrationGit.RunHostGit(Project.Dir, "commit", "-q", "-m", "remove libs/x");
            Project.RunGit("clone", "-q", library, "libs/y");
            IntegrationGit.RunHostGit(Project.Dir, "submodule", "add", "-q", library, "libs/y");
            OwnGitDir = Cli.Run("run", "true");
            IntegrationGit.RunHostGit(Project.Dir, "submodule", "absorbgitdirs");
            Absorbed = Cli.Run("run", "true");
        }
    }
}
