using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

/// <summary>A config.worktree the agent plants in a worktree it makes, which host git there would run: quarantined
/// at session end whatever the entry's mode, not taken as a baseline by a session started alongside, and gone for
/// good once the planting session ends; the repo's own config.worktree stays.</summary>
/// <remarks>
/// A linked worktree outside the project is there too, and extensions.worktreeConfig is on. The
/// plant sets core.fsmonitor to a host script that touches a canary, so "git there runs it" is seen, not
/// guessed. <see cref="LinkedWorktreeTests"/> has the rest of the linked worktree checks.
/// </remarks>
public sealed class WorktreeConfigQuarantineTests(WorktreeConfigQuarantineTests.Fixture fixture)
    : IClassFixture<WorktreeConfigQuarantineTests.Fixture>
{
    private const string Warning = "pen: WARNING";

    [Fact(DisplayName = "a config.worktree in a worktree the agent made (entry mode as made) is quarantined")]
    public void AsMadeIsQuarantined() => AssertQuarantined(fixture.AsMade);

    [Fact(DisplayName = "...with the warning")]
    public void AsMadeWarns() => IntegrationAssert.Contains(fixture.AsMade.Session, Warning);

    [Fact(DisplayName = "...and git there no longer runs it")]
    public void AsMadeNoLongerRuns() => Assert.False(fixture.AsMade.Fires);

    [Fact(DisplayName = "a config.worktree in a worktree the agent made (entry mode 555) is quarantined")]
    public void Mode555IsQuarantined() => AssertQuarantined(fixture.Mode555);

    [Fact(DisplayName = "...with the warning")]
    public void Mode555Warns() => IntegrationAssert.Contains(fixture.Mode555.Session, Warning);

    [Fact(DisplayName = "...and git there no longer runs it")]
    public void Mode555NoLongerRuns() => Assert.False(fixture.Mode555.Fires);

    [Fact(DisplayName = "a config.worktree in a worktree the agent made (entry mode 311) is quarantined")]
    public void Mode311IsQuarantined() => AssertQuarantined(fixture.Mode311);

    [Fact(DisplayName = "...with the warning")]
    public void Mode311Warns() => IntegrationAssert.Contains(fixture.Mode311.Session, Warning);

    [Fact(DisplayName = "...and git there no longer runs it")]
    public void Mode311NoLongerRuns() => Assert.False(fixture.Mode311.Fires);

    [Fact(DisplayName = "a config.worktree in a worktree the agent made (entry mode 000) is quarantined")]
    public void Mode000IsQuarantined() => AssertQuarantined(fixture.Mode000);

    [Fact(DisplayName = "...with the warning")]
    public void Mode000Warns() => IntegrationAssert.Contains(fixture.Mode000.Session, Warning);

    [Fact(DisplayName = "...and git there no longer runs it")]
    public void Mode000NoLongerRuns() => Assert.False(fixture.Mode000.Fires);

    [Fact(DisplayName = "the replay is real: while that session runs, git in its worktree runs the planted config")]
    public void ReplayIsReal() => Assert.True(fixture.FiresWhilePlanting);

    [Fact(DisplayName = "a second session can't take the unchanged plant as its baseline: it's quarantined at its end")]
    public void SecondSessionQuarantinesThePlant() =>
        Assert.True(
            fixture.SecondSession.ExitCode == 0 && fixture.SecondSessionQuarantined, fixture.SecondSession.Describe());

    [Fact(DisplayName = "...with the warning")]
    public void SecondSessionWarns() => IntegrationAssert.Contains(fixture.SecondSession, Warning);

    [Fact(DisplayName = "...and git there no longer runs it")]
    public void SecondSessionsPlantNoLongerRuns() => Assert.False(fixture.FiresAfterSecondSession);

    [Fact(DisplayName = "...started alongside the planting session")]
    public void StartedAlongside() => Assert.True(fixture.StartedAlongside);

    [Fact(DisplayName = "when the planting session ends first, the plant goes then")]
    public void PlantGoesWithThePlanter() => Assert.False(fixture.FiresAfterPlanterEnds);

    [Fact(DisplayName = "...and the second's end doesn't write it back")]
    public void SecondsEndDoesntWriteItBack() => Assert.False(fixture.ConfigWorktreeAfterBoth);

    [Fact(DisplayName = "...so git there still doesn't run it")]
    public void StillDoesntRun() => Assert.False(fixture.FiresAfterBoth);

    [Fact(DisplayName = "the repo's own config.worktree stays, with no warning")]
    public void OwnConfigWorktreeStays()
    {
        var session = fixture.OwnConfigWorktreeSession;

        Assert.True(
            session.ExitCode == 0 && fixture.OwnUserName.CapturedStdout == "mine" && session.OutputComplete
                && !session.Output.Contains(Warning, StringComparison.Ordinal),
            $"user.name: {fixture.OwnUserName.Describe()}\nsession: {session.Describe()}");
    }

    [Fact(DisplayName = "nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    private static void AssertQuarantined(Plant plant) =>
        Assert.True(
            plant.Session.ExitCode == 0 && plant.Quarantined,
            $"config.worktree quarantined: {plant.Quarantined}; {plant.Session.Describe()}");

    /// <summary>A session that planted a config.worktree in a worktree it made, and what was left after it.</summary>
    /// <param name="Session">The session.</param>
    /// <param name="Quarantined">Whether config.worktree was gone and config.worktree.pen-quarantine there
    /// after it.</param>
    /// <param name="Fires">Whether host git in the worktree then ran the planted hook.</param>
    internal sealed record Plant(CommandResult Session, bool Quarantined, bool Fires);

    /// <summary>With a linked worktree outside the project and extensions.worktreeConfig on, runs a planting
    /// session per entry mode, a session after a planting one that's still running, a session alongside a planting
    /// one that ends first, and a session with the repo's own config.worktree.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        private string _hook = null!;

        private string _canary = null!;

        internal Plant AsMade { get; private set; } = null!;

        internal Plant Mode555 { get; private set; } = null!;

        internal Plant Mode311 { get; private set; } = null!;

        internal Plant Mode000 { get; private set; } = null!;

        internal bool FiresWhilePlanting { get; private set; }

        internal CommandResult SecondSession { get; private set; } = null!;

        internal bool SecondSessionQuarantined { get; private set; }

        internal bool FiresAfterSecondSession { get; private set; }

        internal bool StartedAlongside { get; private set; }

        internal bool FiresAfterPlanterEnds { get; private set; }

        /// <summary>Gets whether config.worktree was there (test -e) after both sessions ended.</summary>
        internal bool ConfigWorktreeAfterBoth { get; private set; }

        internal bool FiresAfterBoth { get; private set; }

        internal CommandResult OwnConfigWorktreeSession { get; private set; } = null!;

        /// <summary>Gets git config --worktree user.name after that session.</summary>
        internal CommandResult OwnUserName { get; private set; } = null!;

        internal int ContainersAtEnd { get; private set; }

        private string GitDir => Path.Combine(Project.Dir, ".git");

        private string Entry => Path.Combine(GitDir, "worktrees", "forged21");

        private string ConfigWorktree => Path.Combine(Entry, "config.worktree");

        private string AgentPattern => $"^{Key}-.*-agent$";

        /// <summary>Gets the agent's command that adds the worktree forged21 and plants a config.worktree in its
        /// entry, whose core.fsmonitor is the host's hook.</summary>
        private string PlantScript => $$"""
            git worktree add -q --detach forged21 && printf '[core]\n\tfsmonitor = {{_hook}}\n' >'{{ConfigWorktree}}'
            """;

        protected override void Prepare()
        {
            Files.DeleteTree(Path.Combine(Project.Dir, ".pen"));
            // A linked worktree outside the project is still there.
            var worktree = CreateScratchProject("wt21").Dir;
            Project.RunGit("worktree", "add", "-q", "-b", "integration-wt", worktree);
            var mainOnly = Path.Combine(Project.Dir, "main-only.txt");
            File.WriteAllText(mainOnly, "main-only\n");
            _hook = Path.Combine(Scratch, "hook21");
            _canary = Path.Combine(Scratch, "canary21");
            File.WriteAllText(_hook, $"#!/bin/sh\ntouch {_canary}\n");
            File.SetUnixFileMode(_hook, Files.Mode755);
            Project.RunGit("config", "extensions.worktreeConfig", "true");

            AsMade = RunPlant("");
            Mode555 = RunPlant("555");
            Mode311 = RunPlant("311");
            Mode000 = RunPlant("000");
            RunAfterPlanter();
            RunAlongsidePlanter();
            RunWithOwnConfigWorktree();

            ContainersAtEnd = Docker.CountContainers(Key);
            File.Delete(mainOnly);
            Project.RunGit("worktree", "remove", "--force", worktree);
            Project.RunGit("branch", "-q", "-D", "integration-wt");
        }

        /// <summary>Runs a session that plants the config.worktree and, with a mode, chmods the entry to it.</summary>
        private Plant RunPlant(string mode)
        {
            var script = mode.Length == 0 ? PlantScript : $"{PlantScript} && chmod {mode} '{Entry}'";
            var session = Cli.Run("run", "bash", "-c", script);
            var plant = new Plant(session, IsQuarantined(), Fires());
            Unforge();
            return plant;
        }

        /// <summary>Starts a planting session, checks the plant works on the host, then runs a whole second session
        /// while the first is still running.</summary>
        private void RunAfterPlanter()
        {
            var planter = StartPlanter();
            Polling.WaitUntil(
                () => Files.Exists(ConfigWorktree) && Files.IsNonEmpty(ConfigWorktree), TimeSpan.FromSeconds(10));
            var planterAgent = FindAgent(except: null);
            FiresWhilePlanting = Fires();
            SecondSession = Cli.Run("run", "true");
            SecondSessionQuarantined = IsQuarantined();
            FiresAfterSecondSession = Fires();
            Docker.Run("rm", "-f", planterAgent).RequireSuccess();
            planter.WaitForExit();
            Unforge();
        }

        /// <summary>Starts a planting session and a second one beside it, then ends the planting one first.</summary>
        private void RunAlongsidePlanter()
        {
            var planter = StartPlanter();
            Polling.WaitUntil(() => Files.IsNonEmpty(ConfigWorktree), TimeSpan.FromSeconds(10));
            var planterAgent = FindAgent(except: null);
            var other = Track(Cli.Start("run", "sleep", "120"));
            StartedAlongside = Polling.WaitForContainers(AgentPattern, 2);
            var otherAgent = FindAgent(except: planterAgent);
            Docker.Run("rm", "-f", planterAgent).RequireSuccess();
            planter.WaitForExit();
            FiresAfterPlanterEnds = Fires();
            Docker.Run("rm", "-f", otherAgent).RequireSuccess();
            other.WaitForExit();
            ConfigWorktreeAfterBoth = Files.Exists(ConfigWorktree);
            FiresAfterBoth = Fires();
            Unforge();
        }

        private void RunWithOwnConfigWorktree()
        {
            Project.RunGit("config", "--worktree", "user.name", "mine");
            OwnConfigWorktreeSession = Cli.Run("run", "true");
            OwnUserName = IntegrationGit.CreateCommand(Project.Dir, "config", "--worktree", "user.name").Run();
            Project.RunGit("config", "--unset", "extensions.worktreeConfig");
            File.Delete(Path.Combine(GitDir, "config.worktree"));
        }

        private RunningCommand StartPlanter()
        {
            var planter = Track(Cli.Start("run", "bash", "-c", $"{PlantScript}; sleep 120"));
            if (!Polling.WaitForContainer(AgentPattern))
                throw new InvalidOperationException($"planting session didn't start: {planter.OutputSoFar}");
            return planter;
        }

        /// <summary>Finds a running agent container of the project, as docker ps | grep | head -1 does.</summary>
        /// <param name="except">An agent to leave out (grep -vxF), or null.</param>
        /// <exception cref="InvalidOperationException">There's none.</exception>
        private string FindAgent(string? except) =>
            Docker.ListRunningNames().FirstOrDefault(
                name => Regex.IsMatch(name, AgentPattern) && name != except)
            ?? throw new InvalidOperationException($"no running agent of {Key} other than {except}");

        private bool IsQuarantined() =>
            !Files.Exists(ConfigWorktree) && Files.Exists(ConfigWorktree + ".pen-quarantine");

        /// <summary>Runs host git status in forged21 and checks whether it ran the planted hook (fires21).</summary>
        private bool Fires()
        {
            File.Delete(_canary);
            IntegrationGit.CreateCommand(Path.Combine(Project.Dir, "forged21"), "status").Run();
            return Files.Exists(_canary);
        }

        /// <summary>Removes forged21 and its entry, whatever mode the entry was left in, and prunes
        /// (unforge21).</summary>
        private void Unforge()
        {
            new HostCommand("chmod", ["-R", "u+rwx", Path.Combine(GitDir, "worktrees")]).Run().RequireSuccess();
            Files.DeleteTree(Path.Combine(Project.Dir, "forged21"));
            foreach (var entry in Directory.EnumerateFileSystemEntries(Path.Combine(GitDir, "worktrees"), "forged21*"))
                Files.DeleteTree(entry);
            Project.RunGit("worktree", "prune");
        }
    }
}
