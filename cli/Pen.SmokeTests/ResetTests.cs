using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>pen reset: refused while a session runs, asks first, and removes the project's volumes.</summary>
/// <remarks>Reset logging out of claude and codex is a manual check.</remarks>
public sealed class ResetTests(ResetTests.Fixture fixture) : IClassFixture<ResetTests.Fixture>
{
    [Fact(DisplayName = "pen reset refuses while a session is open")]
    public void RefusesWhileASessionIsOpen() =>
        AssertHas(fixture.WhileOpen, "refusing to reset while a session is running");

    [Fact(DisplayName = "...naming its agent container")]
    public void NamesTheAgentContainer() => AssertHas(fixture.WhileOpen, $"  {fixture.AgentName}");

    [Fact(DisplayName = "...and removes nothing")]
    public void RemovesNothingWhileOpen() => Assert.True(fixture.HomeVolumeAfterRefusal);

    [Fact(DisplayName = "after docker rm -f, that session's pen finishes its end path")]
    public void PenFinishesItsEndPath() => Assert.Equal(0, fixture.ContainersAfterRemoval);

    [Fact(DisplayName = "pen reset asks before removing, echoing the answer read without a terminal")]
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
        Assert.True(fixture.AfterReset.CapturedStdout == "fresh", fixture.AfterReset.Describe());

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    /// <summary>Leaves a marker in the home volume, runs pen reset while a session is open, then answers no and
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

        internal CommandResult AfterReset { get; private set; } = null!;

        protected override void Prepare()
        {
            Cli.Run("run", "bash", "-c", "echo kept >~/.reset-marker").RequireSuccess();
            var session = Track(Cli.Start("run", "sleep", "120"));
            var agentName = new Regex($"^{Key}-.*-agent$");
            if (!Polling.WaitForContainer(agentName.ToString()))
                throw new InvalidOperationException($"session didn't start: {session.OutputSoFar}");
            AgentName = Docker.ListRunningNames().First(agentName.IsMatch);
            WhileOpen = Cli.WithStdin("y\n").Run("reset");
            HomeVolumeAfterRefusal = HasHomeVolume();
            Docker.Run("rm", "-f", AgentName);
            session.WaitForExit();
            ContainersAfterRemoval = Docker.CountContainers(Key);

            AnsweredNo = Cli.WithStdin("n\n").Run("reset");
            HomeVolumeAfterNo = HasHomeVolume();
            AnsweredYes = Cli.WithStdin("y\n").Run("reset").RequireSuccess();
            HomeVolumeAfterYes = HasHomeVolume();
            AfterReset = Cli.Run("run", "bash", "-c", "cat ~/.reset-marker 2>/dev/null || echo fresh");
        }

        private bool HasHomeVolume() => Docker.Run("volume", "inspect", $"{Key}_home").ExitCode == 0;
    }
}
