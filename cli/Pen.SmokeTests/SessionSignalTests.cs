namespace Pen.SmokeTests;

/// <summary>How signals end a session or reach its agent: SIGTERM to pen, a closed terminal, Ctrl-C and
/// Ctrl-\.</summary>
public sealed class SessionSignalTests(SessionSignalTests.Fixture fixture) : IClassFixture<SessionSignalTests.Fixture>
{
    [Fact(DisplayName = "SIGTERM: pen removes the container and tears down the stack")]
    public void SigtermTearsDownTheStack() => Assert.Equal(0, fixture.ContainersAfterTerm);

    [Fact(DisplayName = "closing the terminal ends the session")]
    public void HangupEndsTheSession() =>
        Assert.True(
            fixture.Hangup.Text.Contains("AFTER-HANGUP", StringComparison.Ordinal), fixture.Hangup.Describe());

    [Fact(DisplayName = "...within 10 s")]
    public void HangupEndsItWithinTenSeconds() =>
        Assert.True(fixture.Hangup.AfterHangupSeconds < 10, fixture.Hangup.Describe());

    [Fact(DisplayName = "...with the container removed and the stack down")]
    public void HangupTakesTheStackDown() => Assert.Equal(0, fixture.ContainersAfterHangup);

    [Fact(DisplayName = "Ctrl-C reaches the agent, not pen")]
    public void CtrlCReachesTheAgent() =>
        Assert.True(fixture.Keys.Text.Contains("GOT-INT", StringComparison.Ordinal), fixture.Keys.Describe());

    [Fact(DisplayName = "Ctrl-\\ reaches the agent, not pen")]
    public void CtrlBackslashReachesTheAgent() =>
        Assert.True(fixture.Keys.Text.Contains("GOT-QUIT", StringComparison.Ordinal), fixture.Keys.Describe());

    [Fact(DisplayName = "...and the session carries on to a normal exit")]
    public void SessionCarriesOn() =>
        Assert.True(
            fixture.Keys.Text.Contains("DONE", StringComparison.Ordinal)
                && fixture.Keys.Text.Contains("EXIT 0", StringComparison.Ordinal),
            fixture.Keys.Describe());

    /// <summary>Sends SIGTERM to a running pen, hangs up the terminal of another, and types Ctrl-C and Ctrl-\ into
    /// a third, recording what each leaves.</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal int ContainersAfterTerm { get; private set; }

        internal PtyResult Hangup { get; private set; } = null!;

        internal int ContainersAfterHangup { get; private set; }

        internal PtyResult Keys { get; private set; } = null!;

        protected override void Prepare()
        {
            var session = Track(Cli.Start("run", "sleep", "120"));
            if (!Polling.WaitForContainer($"^{Key}-.*-agent$"))
                throw new InvalidOperationException($"session didn't start: {session.OutputSoFar}");
            session.Signal("TERM");
            session.WaitForExit();
            ContainersAfterTerm = Docker.CountContainers(Key);

            Hangup = new Pty { HangupOn = "READY" }.Run(Cli, "run", "bash", "-c", "echo READY; sleep 60");
            ContainersAfterHangup = Docker.CountContainers(Key);

            // (bash ends a loop whose child dies of the signal, so each signal gets its own loop.)
            Keys = new Pty().SendOn("READY1", "\\x03").SendOn("READY2", "\\x1c").Run(Cli, "run", "bash", "-c", """
                trap "echo GOT-INT" INT; trap "echo GOT-QUIT" QUIT; wait_a_bit() { for i in $(seq 25); do sleep 0.2; done; }; echo READY1; wait_a_bit; echo READY2; wait_a_bit; echo DONE
                """);
        }
    }
}
