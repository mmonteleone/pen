using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The unsigned-commit reminder at session end and pen sign: when it's refused, what it re-signs and with
/// what key, what it keeps, and the paused git state it reports or quarantines.</summary>
/// <remarks>
/// Signing uses a throwaway SSH key and ssh-keygen, set in the throwaway HOME's global git config, never the
/// user's; pen and the commit the agent signs run without SSH_AUTH_SOCK. GitHub showing the signed commits as
/// Verified after a push is a manual check.
/// </remarks>
public sealed class PenSignTests(PenSignTests.Fixture fixture) : IClassFixture<PenSignTests.Fixture>
{
    [Fact(DisplayName = "no reminder when the host's global commit.gpgsign is off")]
    public void NoReminderWhenOff() => SmokeAssert.Lacks(fixture.FirstCommit, "unsigned commit");

    [Fact(DisplayName = "with it on, session end counts the unsigned commits")]
    public void SessionEndCountsTheCommits() =>
        Assert.Multiple(
            () => SmokeAssert.Succeeded(fixture.SecondCommit),
            () => SmokeAssert.Contains(fixture.SecondCommit, "2 unsigned commits; review, then run pen sign"));

    [Fact(DisplayName = "pen sign refuses while a session is open")]
    public void RefusesWhileASessionIsOpen() =>
        SmokeAssert.Contains(fixture.SignWhileOpen, "refusing to sign while a session is running");

    [Fact(DisplayName = "...naming its agent container")]
    public void NamesTheAgentContainer() => SmokeAssert.Contains(fixture.SignWhileOpen, $"  {fixture.AgentName}");

    [Fact(DisplayName = "a session started while pen sign runs here is refused")]
    public void SessionRefusedWhileSigning() =>
        SmokeAssert.StderrContains(fixture.RunWhileSigning, "a pen sign is running in this directory");

    [Fact(DisplayName = "...as is a second pen sign here")]
    public void SecondSignRefused() =>
        SmokeAssert.StderrContains(fixture.SignWhileSigning, "another pen sign is running in this directory");

    [Fact(DisplayName = "...and pen reset (sign holds project.lock shared)")]
    public void ResetRefusedWhileSigning() =>
        SmokeAssert.StderrContains(fixture.ResetWhileSigning, "a pen sign or another pen reset is running");

    [Fact(DisplayName = "a session started while pen reset runs is refused")]
    public void SessionRefusedWhileResetting() =>
        SmokeAssert.StderrContains(fixture.RunWhileResetting, "a pen reset is running in this project");

    [Fact(DisplayName = "...as is pen sign")]
    public void SignRefusedWhileResetting() =>
        SmokeAssert.StderrContains(fixture.SignWhileResetting, "refusing to sign: a pen reset is running");

    [Fact(DisplayName = "pen sign lists the commits it rewrites")]
    public void ListsTheCommits() =>
        Assert.Multiple(
            () => SmokeAssert.Succeeded(fixture.FirstSign),
            () => SmokeAssert.ContainsAll(
                fixture.FirstSign, "re-signing 2 commits in origin/smoke-base..HEAD", " s1", " s2"));

    [Fact(DisplayName = "...signs each of them")]
    public void SignsEachCommit()
    {
        var unsigned = string.Join(' ', fixture.UnsignedAfterFirstSign);

        Assert.True(
            fixture.CommitCountAfterFirstSign > 0 && fixture.UnsignedAfterFirstSign.Count == 0,
            $"{fixture.CommitCountAfterFirstSign} commits, unsigned: {unsigned}");
    }

    [Fact(DisplayName = "...keeping their trees, authors and dates")]
    public void KeepsTreesAuthorsAndDates() =>
        Assert.Equal(fixture.CommitsBeforeFirstSign, fixture.CommitsAfterFirstSign);

    [Fact(DisplayName = "...with the host user as committer")]
    public void HostUserCommits() => Assert.Equal(FakeHome.GitName, fixture.CommitterAfterFirstSign);

    [Fact(DisplayName = "...and the signatures verify")]
    public void SignaturesVerify() => SmokeAssert.Succeeded(fixture.VerifyAfterFirstSign);

    [Fact(DisplayName = "with no allowed signers file, it says how to fix that")]
    public void SaysHowToFixVerifying() =>
        SmokeAssert.ContainsAll(
            fixture.FirstSign, "can't verify the signatures just made", "gpg.ssh.allowedSignersFile isn't set");

    [Fact(DisplayName = "...and each pen sign re-signs")]
    public void EachSignReSigns() =>
        Assert.Multiple(
            () => SmokeAssert.Succeeded(fixture.SecondSign),
            () => Assert.NotEqual(fixture.HeadBeforeSecondSign, fixture.HeadAfterSecondSign));

    [Fact(DisplayName = "...saying so again")]
    public void SaysSoAgain() => SmokeAssert.Contains(fixture.SecondSign, "can't verify the signatures just made");

    [Fact(DisplayName = "the reminder is gone after signing")]
    public void ReminderIsGone() =>
        Assert.Multiple(
            () => SmokeAssert.Succeeded(fixture.SessionAfterSigning),
            () => SmokeAssert.Lacks(fixture.SessionAfterSigning, "unsigned"));

    [Fact(DisplayName = "running pen sign again changes nothing")]
    public void SigningAgainChangesNothing() => SmokeAssert.Contains(fixture.ThirdSign, "are signed");

    [Fact(DisplayName = "...keeping the validly signed commits' hashes")]
    public void KeepsTheHashes() => Assert.Equal(fixture.SignedHead, fixture.HeadAfterThirdSign);

    [Fact(DisplayName = "a forged signature header is re-signed")]
    public void ForgedHeaderIsReSigned() =>
        Assert.Multiple(
            () => SmokeAssert.Succeeded(fixture.ForgedSign),
            () => SmokeAssert.Contains(fixture.ForgedSign, "re-signing 3 commits"));

    [Fact(DisplayName = "...and verifies now")]
    public void ForgedNowVerifies() => SmokeAssert.Succeeded(fixture.VerifyAfterForgedSign);

    [Fact(DisplayName = "...leaving the valid ones below it")]
    public void ValidOnesBelowAreKept() => Assert.Equal(fixture.SignedHead, fixture.ParentAfterForgedSign);

    [Fact(DisplayName = "a commit signed by the agent verifies by the repo's allowedSignersFile")]
    public void AgentCommitVerifiesByTheRepo() =>
        Assert.True(fixture.AgentCommitStatus.CapturedStdout == "G", fixture.AgentCommitStatus.Describe());

    [Fact(DisplayName = "...but pen sign ignores that file and re-signs it")]
    public void AgentCommitIsReSigned() =>
        Assert.Multiple(
            () => SmokeAssert.Succeeded(fixture.AgentSign),
            () => SmokeAssert.Contains(fixture.AgentSign, "agent-signed"));

    [Fact(DisplayName = "...with the host's key")]
    public void ReSignedWithTheHostsKey() => SmokeAssert.Succeeded(fixture.VerifyAfterAgentSign);

    [Fact(DisplayName = "...replacing it")]
    public void AgentCommitIsReplaced() => Assert.NotEqual(fixture.AgentCommit, fixture.HeadAfterAgentSign);

    [Fact(DisplayName = "...and leaves the valid commit below it as it was")]
    public void CommitBelowAgentCommitIsKept() =>
        Assert.Equal(fixture.ParentBeforeAgentSign, fixture.ParentAfterAgentSign);

    [Fact(DisplayName = "pen sign refuses when a commit is already on a remote branch")]
    public void RefusesWhenOnARemoteBranch() => SmokeAssert.Contains(fixture.OnRemoteBranch, "on a remote branch");

    [Fact(DisplayName = "pen sign reports a paused rebase with its exec line")]
    public void ReportsAPausedRebase() =>
        SmokeAssert.ContainsAll(fixture.PausedRebase, ".git/rebase-merge: a paused rebase", "exec planted-by-a-rebase");

    [Fact(DisplayName = "...and doesn't refuse over it")]
    public void DoesntRefuseOverIt() => SmokeAssert.Lacks(fixture.PausedRebase, "not signing");

    [Fact(DisplayName = "...and leaves it in place")]
    public void LeavesItInPlace() => Assert.True(fixture.RebaseTodoKept);

    [Fact(DisplayName = "pen sign refuses over a planted count, having quarantined it")]
    public void RefusesOverAPlantedCount() =>
        SmokeAssert.ContainsAll(fixture.PlantedCount, "msgnum.pen-quarantine", "not signing");

    /// <summary>With a throwaway signing key in the throwaway HOME and an origin holding the first commit, runs
    /// sessions that commit, pen sign while a session or a lock is held, then pen sign over plain, forged and
    /// agent-signed commits, a commit already on a remote branch, a paused rebase and a planted msgnum.</summary>
    public sealed class Fixture : SmokeFixture
    {
        private const string Base = "origin/smoke-base";

        private PenCli _pen = null!;

        private SigningKey _key = null!;

        private string _signers = null!;

        internal CommandResult FirstCommit { get; private set; } = null!;

        internal CommandResult SecondCommit { get; private set; } = null!;

        internal CommandResult SignWhileOpen { get; private set; } = null!;

        internal string AgentName { get; private set; } = null!;

        internal CommandResult RunWhileSigning { get; private set; } = null!;

        internal CommandResult SignWhileSigning { get; private set; } = null!;

        internal CommandResult ResetWhileSigning { get; private set; } = null!;

        internal CommandResult RunWhileResetting { get; private set; } = null!;

        internal CommandResult SignWhileResetting { get; private set; } = null!;

        /// <summary>Gets each commit's tree, author and date, one line each, before the first pen sign.</summary>
        internal string CommitsBeforeFirstSign { get; private set; } = null!;

        internal CommandResult FirstSign { get; private set; } = null!;

        /// <summary>Gets the commits in origin/smoke-base..HEAD without a gpgsig header after the first pen
        /// sign.</summary>
        internal IReadOnlyList<string> UnsignedAfterFirstSign { get; private set; } = [];

        internal int CommitCountAfterFirstSign { get; private set; }

        internal string CommitsAfterFirstSign { get; private set; } = null!;

        internal string CommitterAfterFirstSign { get; private set; } = null!;

        internal CommandResult VerifyAfterFirstSign { get; private set; } = null!;

        internal string HeadBeforeSecondSign { get; private set; } = null!;

        internal CommandResult SecondSign { get; private set; } = null!;

        internal string HeadAfterSecondSign { get; private set; } = null!;

        internal CommandResult SessionAfterSigning { get; private set; } = null!;

        /// <summary>Gets HEAD once its commits are validly signed, before the third pen sign.</summary>
        internal string SignedHead { get; private set; } = null!;

        internal CommandResult ThirdSign { get; private set; } = null!;

        internal string HeadAfterThirdSign { get; private set; } = null!;

        internal CommandResult ForgedSign { get; private set; } = null!;

        internal CommandResult VerifyAfterForgedSign { get; private set; } = null!;

        internal string ParentAfterForgedSign { get; private set; } = null!;

        internal string AgentCommit { get; private set; } = null!;

        internal string ParentBeforeAgentSign { get; private set; } = null!;

        internal string ParentAfterAgentSign { get; private set; } = null!;

        /// <summary>Gets git log -1 --format=%G? for the agent-signed commit, with the repo's allowed
        /// signers.</summary>
        internal CommandResult AgentCommitStatus { get; private set; } = null!;

        internal CommandResult AgentSign { get; private set; } = null!;

        internal CommandResult VerifyAfterAgentSign { get; private set; } = null!;

        internal string HeadAfterAgentSign { get; private set; } = null!;

        internal CommandResult OnRemoteBranch { get; private set; } = null!;

        internal CommandResult PausedRebase { get; private set; } = null!;

        internal bool RebaseTodoKept { get; private set; }

        internal CommandResult PlantedCount { get; private set; } = null!;

        private string GitDir => Path.Combine(Project.Dir, ".git");

        protected override void Prepare()
        {
            _pen = Cli.WithEnvironment("SSH_AUTH_SOCK", null);
            Files.DeleteTree(Path.Combine(Project.Dir, ".pen"));
            _key = SigningKey.Create(Path.Combine(Scratch, "signkey"), "pen-smoke");
            Home.UseSigningKey(_key);
            Home.MakeToolFree();
            var origin = Path.Combine(Scratch, "origin.git");
            SmokeGit.Run(Scratch, "init", "-q", "--bare", origin);
            Project.RunGit("remote", "add", "origin", origin);
            Project.RunGit("push", "-q", "origin", "HEAD:refs/heads/smoke-base");
            Project.RunGit("branch", "-q", "-u", Base);
            // The repo's own setting doesn't matter, only the host's global one.
            Project.RunGit("config", "commit.gpgsign", "true");

            RunReminders();
            RunWhileSessionOpen();
            RunWhileLocked();
            RunFirstSign();
            RunSignAgain();
            RunForged();
            RunAgentSigned();
            RunRefusals();
            Project.RunGit("remote", "remove", "origin");
        }

        private void RunReminders()
        {
            FirstCommit = CommitInSession("s1").RequireSuccess();
            Home.SetGitConfig("commit.gpgsign", "true");
            SecondCommit = CommitInSession("s2");
            Project.RunGit("config", "--unset", "commit.gpgsign");
        }

        /// <summary>Runs pen sign while a session of this project (pen sign runs with the same HOME) is open, then
        /// removes its agent container and waits for its pen.</summary>
        private void RunWhileSessionOpen()
        {
            var session = Track(_pen.Start("run", "sleep", "120"));
            var agent = new Regex($"^{Key}-.*-agent$");
            if (!Polling.WaitForContainer(agent.ToString()))
                throw new InvalidOperationException($"session didn't start: {session.OutputSoFar}");
            AgentName = Docker.ListRunningNames().First(agent.IsMatch);
            SignWhileOpen = _pen.Run("sign");
            Docker.Run("rm", "-f", AgentName).RequireSuccess();
            session.WaitForExit();
        }

        /// <summary>Runs pen while the test holds the locks pen sign (this dir's worktree lock exclusive and
        /// project.lock shared) and pen reset (project.lock exclusive) hold.</summary>
        private void RunWhileLocked()
        {
            var worktreeLock = Path.Combine(RuntimeDir, $"worktree-{Project.Hash}.lock");
            var projectLock = Path.Combine(RuntimeDir, "project.lock");
            using (Locks.Hold(worktreeLock, exclusive: true))
                RunWhileSigning = _pen.Run("run", "true");
            using (Locks.Hold(worktreeLock, exclusive: true))
                SignWhileSigning = _pen.Run("sign");
            using (Locks.Hold(projectLock, exclusive: false))
                ResetWhileSigning = _pen.Run("reset");
            using (Locks.Hold(projectLock, exclusive: true))
                RunWhileResetting = _pen.Run("run", "true");
            using (Locks.Hold(projectLock, exclusive: true))
                SignWhileResetting = _pen.Run("sign");
        }

        private void RunFirstSign()
        {
            CommitsBeforeFirstSign = DescribeCommits();
            FirstSign = _pen.Run("sign");
            var commits = ListCommits();
            CommitCountAfterFirstSign = commits.Count;
            UnsignedAfterFirstSign = [.. commits.Where(commit => !IsSigned(commit))];
            CommitsAfterFirstSign = DescribeCommits();
            CommitterAfterFirstSign = Project.RunGit("log", "-1", "--format=%cn").TrimEnd('\n');
            _signers = _key.WriteAllowedSigners(FakeHome.GitEmail);
            VerifyAfterFirstSign = Verify(_signers);
        }

        private void RunSignAgain()
        {
            HeadBeforeSecondSign = ReadHead();
            // So a re-signed commit gets a new committer date, and so a new hash.
            Thread.Sleep(TimeSpan.FromSeconds(1.1));
            SecondSign = _pen.Run("sign");
            HeadAfterSecondSign = ReadHead();
            Home.SetGitConfig("gpg.ssh.allowedSignersFile", _signers);
            SessionAfterSigning = _pen.Run("run", "true");
            SignedHead = ReadHead();
            ThirdSign = _pen.Run("sign");
            HeadAfterThirdSign = ReadHead();
        }

        /// <summary>Puts a commit with a signature header that verifies nothing on top, as Pen once took any
        /// gpgsig header for signed, and runs pen sign.</summary>
        private void RunForged()
        {
            var tree = Project.RunGit("rev-parse", "HEAD^{tree}").TrimEnd('\n');
            var text = $"tree {tree}\nparent {SignedHead}\n"
                + "author Agent <agent@example.invalid> 1700000000 +0000\n"
                + "committer Agent <agent@example.invalid> 1700000000 +0000\n"
                + "gpgsig -----BEGIN SSH SIGNATURE-----\n Zm9v\n -----END SSH SIGNATURE-----\n\nforged\n";
            var hashObject = SmokeGit.CreateCommand(Project.Dir, "hash-object", "-t", "commit", "-w", "--stdin");
            var forged = (hashObject with { Stdin = text }).Run().RequireSuccess().CapturedStdout;
            Project.RunGit("update-ref", "HEAD", forged);
            ForgedSign = _pen.Run("sign");
            VerifyAfterForgedSign = Verify(_signers);
            ParentAfterForgedSign = Project.RunGit("rev-parse", "HEAD~1").TrimEnd('\n');
        }

        /// <summary>Commits on the host with a key of the agent's own that the repo's config trusts, and runs pen
        /// sign, which must count only the host's global verification settings.</summary>
        private void RunAgentSigned()
        {
            var agentKey = SigningKey.Create(Path.Combine(Scratch, "agentkey"), "pen-smoke-agent");
            var agentSigners = agentKey.WriteAllowedSigners("agent@example.invalid");
            Project.RunGit("config", "gpg.ssh.allowedSignersFile", agentSigners);
            var commit = SmokeGit.CreateCommand(
                Project.Dir, "-c", "user.name=Agent", "-c", "user.email=agent@example.invalid", "-c", "gpg.format=ssh",
                "-c", "gpg.ssh.program=ssh-keygen", "-c", $"user.signingkey={agentKey.Path}",
                "commit", "-q", "-S", "--allow-empty", "-m", "agent-signed");
            (commit with { Environment = SigningKey.WithoutAgent }).Run().RequireSuccess();
            AgentCommit = ReadHead();
            // The host-signed commit the forged one was re-signed into, valid and so not to be touched.
            ParentBeforeAgentSign = Project.RunGit("rev-parse", "HEAD~1").TrimEnd('\n');
            AgentCommitStatus = SmokeGit.CreateCommand(
                Project.Dir, "-c", "gpg.ssh.program=ssh-keygen", "log", "-1", "--format=%G?").Run();
            AgentSign = _pen.Run("sign");
            VerifyAfterAgentSign = Verify(_signers);
            HeadAfterAgentSign = ReadHead();
            ParentAfterAgentSign = Project.RunGit("rev-parse", "HEAD~1").TrimEnd('\n');
            Project.RunGit("config", "--unset", "gpg.ssh.allowedSignersFile");
        }

        private void RunRefusals()
        {
            CommitInSession("s3").RequireSuccess();
            Project.RunGit("update-ref", "refs/remotes/fork/s3", "HEAD");
            OnRemoteBranch = _pen.Run("sign");
            Project.RunGit("update-ref", "-d", "refs/remotes/fork/s3");

            var rebase = Path.Combine(GitDir, "rebase-merge");
            Directory.CreateDirectory(rebase);
            var todo = Path.Combine(rebase, "git-rebase-todo");
            File.WriteAllText(todo, "exec planted-by-a-rebase\n");
            PausedRebase = _pen.Run("sign");
            RebaseTodoKept = Files.IsRegularFile(todo);
            Files.DeleteTree(rebase);

            Directory.CreateDirectory(rebase);
            File.WriteAllText(Path.Combine(rebase, "msgnum"), "$(touch prompt-canary)\n");
            PlantedCount = _pen.Run("sign");
            Files.DeleteTree(rebase);
        }

        private CommandResult CommitInSession(string message) =>
            _pen.Run(
                "run", "git", "-c", "user.name=Agent", "-c", "user.email=agent@example.invalid",
                "commit", "-q", "--allow-empty", "-m", message);

        private IReadOnlyList<string> ListCommits() =>
            Project.RunGit("rev-list", "--reverse", $"{Base}..HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        /// <summary>Describes each commit in origin/smoke-base..HEAD, oldest first, by tree, author and author
        /// date.</summary>
        private string DescribeCommits() =>
            string.Join(
                '\n',
                ListCommits().Select(commit =>
                    Project.RunGit("log", "-1", "--format=%T %an <%ae> %ad", commit).TrimEnd('\n')));

        /// <summary>Checks whether a commit's header has a gpgsig line, as grep -q "^gpgsig" on git cat-file
        /// commit does, looking only at the header.</summary>
        private bool IsSigned(string commit) =>
            Project.RunGit("cat-file", "commit", commit).Split('\n').TakeWhile(line => line.Length > 0)
                .Any(line => line.StartsWith("gpgsig ", StringComparison.Ordinal));

        /// <summary>Verifies HEAD's signature with ssh-keygen and the given allowed signers file.</summary>
        private CommandResult Verify(string signers) =>
            SmokeGit.CreateCommand(
                Project.Dir, "-c", "gpg.ssh.program=ssh-keygen", "-c", $"gpg.ssh.allowedSignersFile={signers}",
                "verify-commit", "HEAD").Run();

        private string ReadHead() => Project.RunGit("rev-parse", "HEAD").TrimEnd('\n');
    }
}
