using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The GitHub token chosen by the origin's owner: the notice when there's none, GH_TOKEN, github_owner,
/// and a private fetch without a token.</summary>
/// <remarks>Checks with a real token (gh repo view, clone, push 403, per-owner tokens) are manual: use a
/// read-only PAT as described in docs/usage.md#github.</remarks>
public sealed class GitHubTokenTests(GitHubTokenTests.Fixture fixture) : IClassFixture<GitHubTokenTests.Fixture>
{
    private const string Owner = Fixture.Owner;

    [Fact(DisplayName = "no token for the origin's owner: a notice with the line to add")]
    public void NoTokenGivesANotice() =>
        AssertHas(fixture.FirstNotice, $"no GitHub token for '{Owner}': add {Owner} = \"github_pat_…\" under [github]");

    [Fact(DisplayName = "...only on the project's first session")]
    public void NoticeOnlyOnTheFirstSession() => AssertLacks(fixture.SecondSession, "no GitHub token");

    [Fact(DisplayName = "the owner's token reaches the agent as GH_TOKEN")]
    public void TokenReachesTheAgent() => AssertHas(fixture.WithToken, "[ghp_smoke_fake_token]");

    [Fact(DisplayName = "...with no notice")]
    public void TokenGivesNoNotice() => AssertLacks(fixture.WithToken, "no GitHub token");

    [Fact(DisplayName = "removing the token later shows the notice again")]
    public void RemovedTokenGivesTheNoticeAgain() => AssertHas(fixture.TokenRemoved, $"no GitHub token for '{Owner}'");

    [Fact(DisplayName = "an unapproved github_owner is refused without a terminal, pointing to pen trust")]
    public void UnapprovedOwnerIsRefused() =>
        Assert.Multiple(
            () => AssertHas(fixture.UnapprovedOwner, "github_owner = other-owner"),
            () => AssertHas(fixture.UnapprovedOwner, "pen trust"));

    [Fact(DisplayName = "...before the token goes anywhere")]
    public void UnapprovedOwnerGetsNoToken() => AssertLacks(fixture.UnapprovedOwner, "[ghp_other]");

    [Fact(DisplayName = "github_owner in .pen/config.toml picks the owner")]
    public void ProjectOwnerPicksTheToken() => AssertHas(fixture.ApprovedOwner, "[ghp_other]");

    [Fact(DisplayName = "a private fetch with no token fails with an error instead of prompting")]
    public void PrivateFetchFails() =>
        Assert.True(
            Regex.IsMatch(fixture.PrivateFetch.Text, "rc=[1-9]")
                && fixture.PrivateFetch.Text.Contains("EXIT 0", StringComparison.Ordinal),
            fixture.PrivateFetch.Describe());

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    private static void AssertLacks(CommandResult result, string text) =>
        Assert.True(
            result.OutputComplete && !result.Output.Contains(text, StringComparison.Ordinal),
            $"\"{text}\" is there; {result.Describe()}");

    /// <summary>Gives the project a GitHub origin and runs sessions with and without a token for its owner, with
    /// a github_owner before and after it's approved, and a git ls-remote of a private repo on a terminal.</summary>
    public sealed class Fixture : SmokeFixture
    {
        /// <summary>The owner in the project's origin URL.</summary>
        internal const string Owner = "pen-smoke-owner";

        private const string PrintToken = "echo \"[$GH_TOKEN]\"";

        internal CommandResult FirstNotice { get; private set; } = null!;

        internal CommandResult SecondSession { get; private set; } = null!;

        internal CommandResult WithToken { get; private set; } = null!;

        internal CommandResult TokenRemoved { get; private set; } = null!;

        internal CommandResult UnapprovedOwner { get; private set; } = null!;

        internal CommandResult ApprovedOwner { get; private set; } = null!;

        internal PtyResult PrivateFetch { get; private set; } = null!;

        protected override void Prepare()
        {
            Project.RunGit("remote", "add", "origin", $"git@github.com:{Owner}/repo.git");
            Home.MakeToolFree();
            File.Delete(Path.Combine(RuntimeDir, "token-notice"));
            FirstNotice = Cli.Run("run", "true").RequireSuccess();
            SecondSession = Cli.Run("run", "true");
            Home.WriteConfig("[github]", $"{Owner} = \"ghp_smoke_fake_token\"");
            WithToken = Cli.Run("run", "bash", "-c", PrintToken).RequireSuccess();
            Home.WriteConfig("# removed again");
            TokenRemoved = Cli.Run("run", "true");

            var penDir = Directory.CreateDirectory(Path.Combine(Project.Dir, ".pen")).FullName;
            File.WriteAllText(Path.Combine(penDir, "config.toml"), "github_owner = \"other-owner\"\n");
            Home.WriteConfig("[github]", "other-owner = \"ghp_other\"");
            UnapprovedOwner = Cli.Run("run", "bash", "-c", PrintToken);
            Cli.Run("trust", "--yes").RequireSuccess();
            ApprovedOwner = Cli.Run("run", "bash", "-c", PrintToken);
            Files.DeleteTree(penDir);

            Home.MakeToolFree();
            PrivateFetch = new Pty { TimeoutSeconds = 60 }.Run(
                Cli, "run", "bash", "-c",
                $"git ls-remote https://github.com/{Owner}/private-{Environment.ProcessId}; echo rc=$?");
            Project.RunGit("remote", "remove", "origin");
        }
    }
}
