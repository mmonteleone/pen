using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

/// <summary>Pen in a linked worktree outside the project: what the sandbox can write in the common git dir and
/// the main checkout, the shared key and runtime dir, the session-end check of the common dir (also for a dead
/// worktree session), sessions in parallel with the main checkout's, pen ps, pen allow and pen sign there, and
/// the worktree's own project layer.</summary>
/// <remarks>
/// <see cref="WorktreeConfigQuarantineTests"/> has the config.worktree an agent plants in a worktree it makes.
/// Running pen claude in a worktree (git worktree add ../x -b feat &amp;&amp; cd ../x &amp;&amp; pen claude) is a
/// manual check.
/// </remarks>
public sealed class LinkedWorktreeTests(LinkedWorktreeTests.Fixture fixture)
    : IClassFixture<LinkedWorktreeTests.Fixture>
{
    private const string Warning = "pen: WARNING";

    [Fact(DisplayName = "pen starts in a linked worktree, and git commit works there")]
    public void CommitWorks() => IntegrationAssert.HasLine(fixture.Probes, "ok: commit");

    [Fact(DisplayName = "...as does switching branches (its own worktrees/ entry is writable)")]
    public void CheckoutWorks() => IntegrationAssert.HasLine(fixture.Probes, "ok: checkout");

    [Fact(DisplayName = "...and the host sees the commit")]
    public void HostSeesTheCommit() =>
        Assert.True(fixture.HostSubject.CapturedStdout == "in-wt", fixture.HostSubject.Describe());

    [Fact(DisplayName = "the session printed no warning")]
    public void NoWarning() => IntegrationAssert.Lacks(fixture.Probes.Session, "WARNING");

    [Fact(DisplayName = "writing the common .git/config fails")]
    public void ConfigWriteFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: configwrite");

    [Fact(DisplayName = "...as does git config")]
    public void GitConfigFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: config");

    [Fact(DisplayName = "writing a hook in the common .git/hooks fails")]
    public void HookWriteFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: hook");

    [Fact(DisplayName = "writing the worktree's .git file fails")]
    public void GitFileWriteFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: gitfile");

    [Fact(DisplayName = "mv of the common git dir fails")]
    public void MoveCommonDirFails() => IntegrationAssert.HasLine(fixture.Probes, "fail: mvcommon");

    [Fact(DisplayName = "the main checkout's HEAD is read-only")]
    public void MainHeadIsReadOnly() => IntegrationAssert.HasLine(fixture.Probes, "fail: mainhead");

    [Fact(DisplayName = "...and stays as it was")]
    public void MainHeadIsUnchanged() =>
        Assert.True(
            fixture.MainHeadAfterProbes.CapturedStdout == fixture.MainHead, fixture.MainHeadAfterProbes.Describe());

    [Fact(DisplayName = "the main checkout's files aren't visible, only its .git")]
    public void MainFilesArentVisible() => IntegrationAssert.HasLine(fixture.Probes, "ok: mainhidden");

    [Fact(DisplayName = "the worktree's own .pen/ is created")]
    public void WorktreePenDirIsCreated() => Assert.True(fixture.WorktreePenDirMade);

    [Fact(DisplayName = "...and read-only inside")]
    public void WorktreePenDirIsReadOnly() => IntegrationAssert.HasLine(fixture.Probes, "fail: pen");

    [Fact(DisplayName = "the worktree shares the main checkout's key: no state of its own")]
    public void WorktreeHasNoStateOfItsOwn() =>
        Assert.True(
            fixture.WorktreeRuntimeDirs.Count == 0 && fixture.WorktreeVolumes.Count == 0,
            $"runtime dirs: {string.Join(' ', fixture.WorktreeRuntimeDirs)}; "
                + $"volumes: {string.Join(' ', fixture.WorktreeVolumes)}");

    [Fact(DisplayName = "...and its session ran under the main checkout's runtime dir, with its own worktree lock")]
    public void WorktreeLockIsInTheMainRuntimeDir() => Assert.True(fixture.WorktreeLockMade);

    [Fact(DisplayName = "...whose project-dir still names the main checkout (clean --stale D1)")]
    public void ProjectDirNamesTheMainCheckout() => Assert.Equal(fixture.Project.Dir, fixture.ProjectDirFile);

    [Fact(DisplayName = "a commondir planted in the common dir is quarantined when a worktree session ends")]
    public void PlantedCommondirIsQuarantined() =>
        Assert.True(fixture.PlantedCommondirQuarantined, fixture.PlantedCommondir.Describe());

    [Fact(DisplayName = "...with the warning")]
    public void PlantedCommondirWarns() => IntegrationAssert.Contains(fixture.PlantedCommondir, Warning);

    [Fact(DisplayName = "...and the worktree's .git file is left alone")]
    public void WorktreeGitFileIsLeftAlone() => Assert.True(fixture.WorktreeGitFileKept);

    [Fact(DisplayName = "a pen in the main checkout ends a dead worktree session")]
    public void MainCheckoutEndsTheDeadSession() =>
        Assert.Multiple(
            () => IntegrationAssert.Succeeded(fixture.AfterDeadSession),
            () => IntegrationAssert.Contains(fixture.AfterDeadSession, "whose pen exited without ending it"));

    [Fact(DisplayName = "...checking it with its own saved dirs (the commondir is quarantined)")]
    public void DeadSessionsCommondirIsQuarantined() => Assert.True(fixture.DeadSessionCommondirQuarantined);

    [Fact(DisplayName = "...and takes its stack down")]
    public void DeadSessionsStackIsDown() => Assert.Equal(0, fixture.ContainersAfterDeadSession);

    [Fact(DisplayName = "sessions run in parallel in the main checkout and the worktree")]
    public void SessionsRunInParallel() => Assert.True(fixture.ParallelSessionsRunning);

    [Fact(DisplayName = "pen ps lists both")]
    public void PsListsBoth()
    {
        var ps = fixture.Ps;

        Assert.True(
            ps.ExitCode == 0 && ListsSleep(ps, fixture.Project.Dir) && ListsSleep(ps, fixture.Worktree), ps.Describe());
    }

    [Fact(DisplayName = "pen allow in the worktree adds to the worktree's .pen/config.toml only")]
    public void AllowWritesTheWorktreesConfigOnly() =>
        Assert.True(
            fixture.WorktreeConfigLines.Contains("  \"example.org\",") && !fixture.MainConfigHasDomain,
            $"worktree's .pen/config.toml:\n{string.Join('\n', fixture.WorktreeConfigLines)}\n"
                + $"main checkout's has example.org: {fixture.MainConfigHasDomain}; {fixture.Allow.Describe()}");

    [Fact(DisplayName = "...and applies only to the worktree's session, not the main checkout's")]
    public void AllowAppliesToTheWorktreesSession() =>
        Assert.Multiple(
            () => IntegrationAssert.Succeeded(fixture.Allow),
            () => IntegrationAssert.Contains(fixture.Allow, "applied to 1 running session in this directory"));

    [Fact(DisplayName = "...whose allowlist alone now has the domain")]
    public void OnlyTheWorktreesAllowlistHasTheDomain()
    {
        var sessions = fixture.OpenSessions;

        Assert.True(
            sessions.Count == 2 && sessions.All(session => session.InWorktree == session.HasDomain),
            string.Join("; ", sessions));
    }

    [Fact(DisplayName = "pen sign in the worktree is refused while the worktree has a session")]
    public void SignRefusedInTheWorktree() =>
        IntegrationAssert.Contains(
            fixture.SignWhileWorktreeOpen, "refusing to sign while a session is running in this directory");

    [Fact(DisplayName = "...naming only that session")]
    public void SignNamesOnlyThatSession() =>
        Assert.Multiple(
            () => IntegrationAssert.Contains(fixture.SignWhileWorktreeOpen, $"  {fixture.WorktreeAgent}"),
            () => IntegrationAssert.Lacks(fixture.SignWhileWorktreeOpen, fixture.MainAgent));

    [Fact(DisplayName = "pen sign in the worktree succeeds while the main checkout has a session")]
    public void SignSucceedsInTheWorktree() =>
        Assert.Multiple(
            () => IntegrationAssert.Succeeded(fixture.SignWhileMainOpen),
            () => IntegrationAssert.Contains(fixture.SignWhileMainOpen, "re-signing 1 commit"));

    [Fact(DisplayName = "...and signs the worktree's branch")]
    public void SignsTheWorktreesBranch() => Assert.True(fixture.WorktreeBranchSigned);

    [Fact(DisplayName = "...leaving the main checkout's HEAD alone")]
    public void LeavesTheMainHeadAlone() => Assert.Equal(fixture.MainHead, fixture.MainHeadAfterSign);

    [Fact(DisplayName = "a worktree's .pen/Dockerfile needs its own approval")]
    public void DockerfileNeedsApproval() => IntegrationAssert.Contains(fixture.UnapprovedLayer, "pen trust");

    [Fact(DisplayName = "a worktree's .pen/Dockerfile builds its own layer, <key>-<worktree hash>:latest")]
    public void DockerfileBuildsItsOwnLayer() =>
        Assert.True(
            fixture.ApprovedLayer.ExitCode == 0 && fixture.WorktreeLayerExists,
            $"{fixture.WorktreeLayer} there: {fixture.WorktreeLayerExists}; {fixture.ApprovedLayer.Describe()}");

    private static bool ListsSleep(CommandResult ps, string dir)
    {
        var line = new Regex($"^{Regex.Escape(dir)} +[0-9a-f]{{8}} .*sleep 120$");
        return ps.StdoutLines.Any(line.IsMatch);
    }

    /// <summary>An open session's dir when pen allow ran in the worktree.</summary>
    /// <param name="Dir">The session's dir.</param>
    /// <param name="InWorktree">Whether its session.env has PROJECT_DIR set to the worktree.</param>
    /// <param name="HasDomain">Whether its allowlist.txt has example.org in it.</param>
    internal sealed record OpenSession(string Dir, bool InWorktree, bool HasDomain);

    /// <summary>Adds a linked worktree in the scratch directory and, from it, runs a session of probes, a session
    /// that plants a commondir and one whose pen is killed after planting it; then, with a throwaway signing key,
    /// sessions in parallel in the main checkout and the worktree, with pen ps, pen allow and pen sign; then a
    /// .pen/Dockerfile in the worktree.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        private PenCli _pen = null!;

        private PenCli _worktreePen = null!;

        private IntegrationProject _worktreeProject = null!;

        internal string Worktree { get; private set; } = null!;

        internal string MainHead { get; private set; } = null!;

        internal ProbeResult Probes { get; private set; } = null!;

        /// <summary>Gets git log -1 --format=%s integration-wt on the host, after the probes.</summary>
        internal CommandResult HostSubject { get; private set; } = null!;

        internal CommandResult MainHeadAfterProbes { get; private set; } = null!;

        internal bool WorktreePenDirMade { get; private set; }

        /// <summary>Gets the runtime dirs made for the worktree's own path, under either HOME.</summary>
        internal IReadOnlyList<string> WorktreeRuntimeDirs { get; private set; } = [];

        /// <summary>Gets the home volumes made for a key of the worktree's own path.</summary>
        internal IReadOnlyList<string> WorktreeVolumes { get; private set; } = [];

        internal bool WorktreeLockMade { get; private set; }

        /// <summary>Gets the runtime dir's project-dir, as $(cat) gives it, or null if it's missing.</summary>
        internal string? ProjectDirFile { get; private set; }

        internal CommandResult PlantedCommondir { get; private set; } = null!;

        internal bool PlantedCommondirQuarantined { get; private set; }

        internal bool WorktreeGitFileKept { get; private set; }

        internal CommandResult AfterDeadSession { get; private set; } = null!;

        internal bool DeadSessionCommondirQuarantined { get; private set; }

        internal int ContainersAfterDeadSession { get; private set; }

        internal bool ParallelSessionsRunning { get; private set; }

        internal string WorktreeAgent { get; private set; } = null!;

        internal string MainAgent { get; private set; } = null!;

        internal CommandResult Ps { get; private set; } = null!;

        internal CommandResult Allow { get; private set; } = null!;

        internal IReadOnlyList<string> WorktreeConfigLines { get; private set; } = [];

        internal bool MainConfigHasDomain { get; private set; }

        internal IReadOnlyList<OpenSession> OpenSessions { get; private set; } = [];

        internal CommandResult SignWhileWorktreeOpen { get; private set; } = null!;

        internal CommandResult SignWhileMainOpen { get; private set; } = null!;

        internal bool WorktreeBranchSigned { get; private set; }

        internal string MainHeadAfterSign { get; private set; } = null!;

        internal CommandResult UnapprovedLayer { get; private set; } = null!;

        internal string WorktreeLayer { get; private set; } = null!;

        internal CommandResult ApprovedLayer { get; private set; } = null!;

        internal bool WorktreeLayerExists { get; private set; }

        private string GitDir => Path.Combine(Project.Dir, ".git");

        private string Commondir => Path.Combine(GitDir, "commondir");

        private string AgentPattern => $"^{Key}-.*-agent$";

        protected override void Prepare()
        {
            // pen sign runs below; nothing here signs through the user's SSH agent.
            _pen = SigningKey.WithoutAgentFor(Cli);
            Files.DeleteTree(Path.Combine(Project.Dir, ".pen"));
            // A tracked scratch project, so any state pen made for the worktree's own path goes with the fixture.
            _worktreeProject = CreateScratchProject("wt21");
            Worktree = _worktreeProject.Dir;
            Project.RunGit("worktree", "add", "-q", "-b", "integration-wt", Worktree);
            _worktreePen = _pen.WithWorkingDirectory(Worktree);
            var worktreeBase = IntegrationGit.Run(Worktree, "rev-parse", "HEAD").TrimEnd('\n');
            MainHead = Project.RunGit("rev-parse", "HEAD").TrimEnd('\n');
            File.WriteAllText(Path.Combine(Project.Dir, "main-only.txt"), "main-only\n");

            RunProbes();
            RunPlantedCommondir();
            RunDeadSession();
            RunInParallel(worktreeBase);
            RunWithDockerfile();
        }

        private void RunProbes()
        {
            Probes = new SandboxProbe()
                .Add("commit", "git -c user.name=a -c user.email=a@example.invalid commit -q --allow-empty -m in-wt")
                .Add("checkout", "git checkout -q -b integration-wt2 && git checkout -q integration-wt")
                .Add("configwrite", $"echo x >> \"{GitDir}/config\"")
                .Add("config", "git config core.fsmonitor x")
                .Add("hook", $"echo x >> \"{GitDir}/hooks/pre-commit\"")
                .Add("gitfile", "echo \"gitdir: /tmp\" > .git")
                .Add("mvcommon", $"mv \"{GitDir}\" \"{GitDir}.2\"")
                .Add("mainhead", $"echo 0000000000000000000000000000000000000000 > \"{GitDir}/HEAD\"")
                .Add("mainhidden", $"test \"$(ls -A \"{Project.Dir}\")\" = .git")
                .Add("pen", "touch .pen/x")
                .Run(_worktreePen);
            Probes.Session.RequireSuccess();

            HostSubject = IntegrationGit.CreateCommand(Project.Dir, "log", "-1", "--format=%s", "integration-wt").Run();
            MainHeadAfterProbes = IntegrationGit.CreateCommand(Project.Dir, "rev-parse", "HEAD").Run();
            WorktreePenDirMade = Files.IsDirectory(Path.Combine(Worktree, ".pen"));
            var worktreeHash = Files.HashPath(Worktree);
            WorktreeRuntimeDirs =
            [
                .. new[] { IntegrationEnvironment.RealHome, Home.Path }
                    .SelectMany(home => _worktreeProject.FindRuntimeDirs(home)),
            ];
            // Any key ending in the worktree's hash is checked, such as pen-wt21-<hash>_home.
            WorktreeVolumes =
            [
                .. Docker.ReadLines("volume", "ls", "-q")
                    .Where(volume => volume.EndsWith($"-{worktreeHash}_home", StringComparison.Ordinal)),
            ];
            WorktreeLockMade = Files.Exists(Path.Combine(RuntimeDir, $"worktree-{worktreeHash}.lock"));
            var projectDir = Path.Combine(RuntimeDir, "project-dir");
            ProjectDirFile = File.Exists(projectDir) ? File.ReadAllText(projectDir).TrimEnd('\n') : null;
            IntegrationGit.Run(Worktree, "branch", "-q", "-D", "integration-wt2");
        }

        private void RunPlantedCommondir()
        {
            PlantedCommondir = _worktreePen.Run("run", "bash", "-c", $"touch '{Commondir}'");
            PlantedCommondirQuarantined = !Files.Exists(Commondir) && Files.Exists(Commondir + ".pen-quarantine");
            WorktreeGitFileKept = Files.IsRegularFile(Path.Combine(Worktree, ".git"));
            File.Delete(Commondir + ".pen-quarantine");
        }

        /// <summary>Starts a worktree session that plants a commondir and waits, SIGKILLs its pen once the file is
        /// there, then runs pen in the main checkout.</summary>
        private void RunDeadSession()
        {
            var session = Track(_worktreePen.Start("run", "bash", "-c", $"touch '{Commondir}'; sleep 120"));
            if (!Polling.WaitForContainer(AgentPattern))
                throw new InvalidOperationException($"worktree session didn't start: {session.OutputSoFar}");
            Polling.WaitUntil(() => Files.Exists(Commondir), TimeSpan.FromSeconds(10));
            session.Signal("KILL");
            // Only pen has to be gone: its compose child, still running, holds the output pipes open.
            if (!Polling.WaitUntil(() => session.HasExited, TimeSpan.FromSeconds(30)))
                throw new InvalidOperationException("pen didn't end on SIGKILL");
            AfterDeadSession = _pen.Run("run", "true");
            DeadSessionCommondirQuarantined = !Files.Exists(Commondir) && Files.Exists(Commondir + ".pen-quarantine");
            ContainersAfterDeadSession = Docker.CountContainers(Key);
            File.Delete(Commondir + ".pen-quarantine");
        }

        /// <summary>With a throwaway signing key, starts a session in the worktree and one in the main checkout,
        /// runs pen ps, pen allow and pen sign in the worktree, ends the worktree's session and signs again.</summary>
        /// <param name="worktreeBase">The commit the worktree started from, pen sign's base there.</param>
        private void RunInParallel(string worktreeBase)
        {
            Home.UseSigningKey(SigningKey.Create(Path.Combine(Scratch, "signkey"), "pen-integration"));
            Home.MakeToolFree();
            var agent = new Regex(AgentPattern);
            var worktreeSession = Track(_worktreePen.Start("run", "sleep", "120"));
            if (!Polling.WaitForContainer(AgentPattern))
                throw new InvalidOperationException($"worktree session didn't start: {worktreeSession.OutputSoFar}");
            WorktreeAgent = Docker.ListRunningNames().First(agent.IsMatch);
            var mainSession = Track(_pen.Start("run", "sleep", "120"));
            ParallelSessionsRunning = Polling.WaitForContainers(AgentPattern, 2);
            MainAgent = Docker.ListRunningNames().FirstOrDefault(name => agent.IsMatch(name) && name != WorktreeAgent)
                ?? throw new InvalidOperationException(
                    $"the main checkout's session didn't start: {mainSession.OutputSoFar}");

            Ps = _pen.Run("ps");
            Allow = _worktreePen.Run("allow", "example.org");
            var worktreeConfig = Path.Combine(Worktree, ".pen", "config.toml");
            WorktreeConfigLines = File.Exists(worktreeConfig) ? File.ReadAllLines(worktreeConfig) : [];
            var mainConfig = Path.Combine(Project.Dir, ".pen", "config.toml");
            MainConfigHasDomain = File.Exists(mainConfig)
                && File.ReadAllText(mainConfig).Contains("example.org", StringComparison.Ordinal);
            OpenSessions = ReadOpenSessions();

            SignWhileWorktreeOpen = _worktreePen.Run("sign", worktreeBase);
            Docker.Run("rm", "-f", WorktreeAgent).RequireSuccess();
            worktreeSession.WaitForExit();
            SignWhileMainOpen = _worktreePen.Run("sign", worktreeBase);
            WorktreeBranchSigned =
                IntegrationGit.HasSignatureHeader(Project.RunGit("cat-file", "commit", "integration-wt"));
            MainHeadAfterSign = Project.RunGit("rev-parse", "HEAD").TrimEnd('\n');
            Docker.Run("rm", "-f", MainAgent).RequireSuccess();
            mainSession.WaitForExit();
        }

        /// <summary>Reads each open session's dir (one with a compose.yaml) under the runtime dir.</summary>
        private List<OpenSession> ReadOpenSessions()
        {
            List<OpenSession> sessions = [];
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(RuntimeDir, "sessions")))
            {
                if (!File.Exists(Path.Combine(dir, "compose.yaml")))
                    continue;
                var environment = Path.Combine(dir, "session.env");
                var inWorktree = File.Exists(environment)
                    && File.ReadAllLines(environment).Contains($"PROJECT_DIR={Worktree}");
                var allowlist = Path.Combine(dir, "allowlist.txt");
                var hasDomain = File.Exists(allowlist)
                    && File.ReadAllText(allowlist).Contains("example.org", StringComparison.Ordinal);
                sessions.Add(new OpenSession(dir, inWorktree, hasDomain));
            }
            return sessions;
        }

        /// <summary>Adds a .pen/Dockerfile to the worktree, runs a session before and after approving it, and
        /// removes the layer it built.</summary>
        private void RunWithDockerfile()
        {
            Home.MakeToolFree();
            var penDir = Path.Combine(Worktree, ".pen");
            Directory.CreateDirectory(penDir);
            File.WriteAllText(Path.Combine(penDir, "Dockerfile"), "FROM pen:latest\n");
            UnapprovedLayer = _worktreePen.Run("run", "true");
            _worktreePen.Run("trust", "--yes").RequireSuccess();
            // Without tools, the layer is <key>-<dir hash>:latest.
            WorktreeLayer = $"{Key}-{Files.HashPath(Worktree)}:latest";
            TrackImage(WorktreeLayer);
            ApprovedLayer = _worktreePen.Run("run", "true");
            WorktreeLayerExists = Docker.Run("image", "inspect", WorktreeLayer).ExitCode == 0;
            Docker.Run("image", "rm", "-f", WorktreeLayer);
        }
    }
}
