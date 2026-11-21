using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

/// <summary>pen allow with no arguments: the domains the project's sessions had blocked, and pen allow
/// --defaults.</summary>
public sealed class BlockedListingTests(BlockedListingTests.Fixture fixture)
    : IClassFixture<BlockedListingTests.Fixture>
{
    [Fact(DisplayName = "lists blocked domains while the session runs")]
    public void ListsWhileTheSessionRuns() =>
        Assert.Multiple(
            () => AssertStdoutHas(fixture.WhileRunning, "example.org"),
            () => AssertStdoutHas(fixture.WhileRunning, "example.edu"));

    [Fact(DisplayName = "...newest first")]
    public void ListsNewestFirst() =>
        Assert.Equal("example.org", Regex.Match(fixture.WhileRunning.Stdout, @"example\.(org|edu)").Value);

    [Fact(DisplayName = "...with the line to paste")]
    public void ListsTheLineToPaste() => AssertStdoutHas(fixture.WhileRunning, "pen allow example.org example.edu");

    [Fact(DisplayName = "...leaving out domains allowed since (example.com, by AllowCommandTests)")]
    public void LeavesOutDomainsAllowedSince() =>
        Assert.True(
            !fixture.WhileRunning.Stdout.Contains("example.com", StringComparison.Ordinal),
            fixture.WhileRunning.Describe());

    [Fact(DisplayName = "...after a SIGKILL'd session (which it ends)")]
    public void ListsAfterAKilledSession() =>
        Assert.True(
            fixture.AfterKill.Output.Contains("example.org", StringComparison.Ordinal)
                && fixture.AfterKill.Output.Contains("whose pen exited", StringComparison.Ordinal),
            fixture.AfterKill.Describe());

    [Fact(DisplayName = "...and once the session has exited normally")]
    public void ListsAfterANormalExit() => AssertStdoutHas(fixture.AfterExit, "example.org");

    [Fact(DisplayName =
        "pen allow --defaults lists GitHub but no AI providers (agents bring those)")]
    public void DefaultsHaveNoProviders()
    {
        var defaults = fixture.Defaults;

        Assert.True(
            defaults.OutputLines.Contains("github.com")
                && !Regex.IsMatch(
                    defaults.Output,
                    @"anthropic|openai|chatgpt|claude\.|opencode|models\.dev|AI provider",
                    RegexOptions.IgnoreCase),
            defaults.Describe());
    }

    private static void AssertStdoutHas(CommandResult result, string text) =>
        Assert.True(result.Stdout.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    /// <summary>Has a session's example.com blocked and then allowed, then runs pen allow while a session that
    /// was blocked from example.edu and then example.org waits, after it's SIGKILL'd, and after a session that
    /// exits normally.</summary>
    /// <remarks>
    /// example.com is left out only after a session was blocked from it and it was then allowed. The fixture does
    /// that first, so the check always runs.
    /// </remarks>
    public sealed class Fixture : IntegrationFixture
    {
        internal CommandResult WhileRunning { get; private set; } = null!;

        internal CommandResult AfterKill { get; private set; } = null!;

        internal CommandResult AfterExit { get; private set; } = null!;

        internal CommandResult Defaults { get; private set; } = null!;

        protected override void Prepare()
        {
            Home.MakeToolFree();
            var precondition = Cli.Run("run", "bash", "-c", "curl -s -o /dev/null -I https://example.com");
            if (!precondition.Output.Contains("blocked: example.com", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"the session wasn't blocked from example.com: {precondition.Describe()}");
            }

            var allowed = Cli.Run("allow", "example.com");
            if (allowed.ExitCode != 0)
                throw new InvalidOperationException($"pen allow example.com: {allowed.Describe()}");

            var session = Track(Cli.Start("run", "bash", "-c", """
                curl -s -o /dev/null -I https://example.edu; sleep 1.5; curl -s -o /dev/null -I https://example.org; echo READY; sleep 120
                """));
            Polling.WaitUntil(
                () => session.OutputSoFar.Contains("READY", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
            WhileRunning = Cli.Run("allow").RequireSuccess();
            session.Signal("KILL");
            session.WaitForExit();
            AfterKill = Cli.Run("allow").RequireSuccess();
            Cli.Run("run", "true");
            AfterExit = Cli.Run("allow");
            Defaults = Cli.Run("allow", "--defaults").RequireSuccess();
        }
    }
}
