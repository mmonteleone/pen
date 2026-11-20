using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>pen clean: refused while a session runs, asks first, and removes the project's volumes.</summary>
/// <remarks>Cleaning logging out of claude and codex is a manual check.</remarks>
public sealed class CleanTests(CleanTests.Fixture fixture) : IClassFixture<CleanTests.Fixture>
{
    [Fact(DisplayName = "pen clean refuses while a session is open")]
    public void RefusesWhileASessionIsOpen() =>
        AssertHas(fixture.WhileOpen, "refusing to clean while a session is running");

    [Fact(DisplayName = "...naming its agent container")]
    public void NamesTheAgentContainer() => AssertHas(fixture.WhileOpen, $"  {fixture.AgentName}");

    [Fact(DisplayName = "...and removes nothing")]
    public void RemovesNothingWhileOpen() => Assert.True(fixture.HomeVolumeAfterRefusal);

    [Fact(DisplayName = "after docker rm -f, that session's pen finishes its end path")]
    public void PenFinishesItsEndPath() => Assert.Equal(0, fixture.ContainersAfterRemoval);

    [Fact(DisplayName = "pen clean asks before removing, echoing the answer read without a terminal")]
    public void AsksBeforeRemoving() => AssertHas(fixture.AnsweredNo, "pen: remove them? [y/N] n");

    [Fact(DisplayName = "answering no removes nothing")]
    public void NoRemovesNothing() =>
        Assert.Multiple(
            () => AssertHas(fixture.AnsweredNo, "nothing removed"),
            () => Assert.True(fixture.HomeVolumeAfterNo, "the home volume is gone"));

    [Fact(DisplayName = "answering yes removes the project's volumes")]
    public void YesRemovesTheVolumes() =>
        Assert.Multiple(
            () => AssertHas(fixture.AnsweredYes, $"removed {fixture.Key}_home, {fixture.Key}_nuget"),
            () => Assert.False(fixture.HomeVolumeAfterYes, "the home volume is still there"));

    [Fact(DisplayName = "...so the next session starts with a fresh home")]
    public void NextSessionHasAFreshHome() =>
        Assert.True(fixture.AfterClean.CapturedStdout == "fresh", fixture.AfterClean.Describe());

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    /// <summary>Leaves a marker in the home volume, runs pen clean while a session is open, then answers no and
    /// yes once it has ended, and reads the marker again.</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal string AgentName { get; private set; } = null!;

        internal CommandResult WhileOpen { get; private set; } = null!;

        internal bool HomeVolumeAfterRefusal { get; private set; }

        internal int ContainersAfterRemoval { get; private set; }

        internal CommandResult AnsweredNo { get; private set; } = null!;

        internal bool HomeVolumeAfterNo { get; private set; }

        internal CommandResult AnsweredYes { get; private set; } = null!;

        internal bool HomeVolumeAfterYes { get; private set; }

        internal CommandResult AfterClean { get; private set; } = null!;

        protected override void Prepare()
        {
            Cli.Run("run", "bash", "-c", "echo kept >~/.clean-marker").RequireSuccess();
            var session = Track(Cli.Start("run", "sleep", "120"));
            var agentName = new Regex($"^{Key}-.*-agent$");
            if (!Polling.WaitForContainer(agentName.ToString()))
                throw new InvalidOperationException($"session didn't start: {session.OutputSoFar}");
            AgentName = Docker.ListRunningNames().First(agentName.IsMatch);
            WhileOpen = Cli.WithStdin("y\n").Run("clean");
            HomeVolumeAfterRefusal = HasHomeVolume();
            Docker.Run("rm", "-f", AgentName);
            session.WaitForExit();
            ContainersAfterRemoval = Docker.CountContainers(Key);

            AnsweredNo = Cli.WithStdin("n\n").Run("clean");
            HomeVolumeAfterNo = HasHomeVolume();
            AnsweredYes = Cli.WithStdin("y\n").Run("clean").RequireSuccess();
            HomeVolumeAfterYes = HasHomeVolume();
            AfterClean = Cli.Run("run", "bash", "-c", "cat ~/.clean-marker 2>/dev/null || echo fresh");
        }

        private bool HasHomeVolume() => Docker.Run("volume", "inspect", $"{Key}_home").ExitCode == 0;
    }
}
