using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

/// <summary>pen --docker: a dind daemon per session, what inner containers can write, builds through the proxy,
/// one docker session per project, and docker = true in .pen/config.toml.</summary>
public sealed class DockerInDockerTests(DockerInDockerTests.Fixture fixture)
    : IClassFixture<DockerInDockerTests.Fixture>
{
    [Fact(DisplayName = "pen --docker: docker run hello-world works")]
    public void HelloWorldWorks() => AssertHas(fixture.HelloWorld, "Hello from Docker!");

    [Fact(DisplayName = "...and no dind is left after exit")]
    public void NoDindIsLeft() => Assert.Equal(0, fixture.ContainersAfterHelloWorld);

    [Fact(DisplayName = "...nor its network")]
    public void NoNetworkIsLeft() => Assert.Empty(fixture.NetworksAfterHelloWorld);

    [Fact(DisplayName = "the project's dind volume is named by its key")]
    public void DindVolumeIsNamedByTheKey() => Assert.True(fixture.HasDindVolume);

    [Fact(DisplayName = "an inner container (-v $PWD:/src) can't write .git/config")]
    public void InnerContainerCantWriteGitConfig() => AssertLine(fixture.Inner, "fail: config");

    [Fact(DisplayName = "...or .git/hooks")]
    public void InnerContainerCantWriteHooks() => AssertLine(fixture.Inner, "fail: hook");

    [Fact(DisplayName = "...or .pen/")]
    public void InnerContainerCantWritePenDir() => AssertLine(fixture.Inner, "fail: pen");

    [Fact(DisplayName = "...or move .git")]
    public void InnerContainerCantMoveGit() => AssertLine(fixture.Inner, "fail: mvgit");

    [Fact(DisplayName = "...but can write the project")]
    public void InnerContainerCanWriteTheProject() =>
        Assert.True(
            fixture.Inner.HasLine("ok: write") && fixture.InnerFile == "inner",
            $"inner-file: {fixture.InnerFile}; {fixture.Inner.Describe()}");

    [Fact(DisplayName = "...and the file an inner root container wrote belongs to the host user")]
    public void InnerFileBelongsToTheHostUser()
    {
        Assert.SkipUnless(
            IntegrationEnvironment.IsRootless || OperatingSystem.IsMacOS(),
            $"rootful Linux: the file an inner root container wrote is owned by UID {fixture.InnerFileOwner} "
                + "(expected: root; see docs/usage.md#docker-in-docker)");

        Assert.Equal(IntegrationEnvironment.HostUid, fixture.InnerFileOwner);
    }

    [Fact(DisplayName = "a compose stack with a bind mount from the project works")]
    public void ComposeStackWorks() => AssertHas(fixture.Compose, "hello-compose");

    [Fact(DisplayName = "a docker build RUN step reaches an allowed domain")]
    public void BuildReachesAnAllowedDomain() =>
        Assert.Matches(new Regex("^#[0-9]+ [0-9.]+ allowed-ok", RegexOptions.Multiline), fixture.Build.Output);

    [Fact(DisplayName = "...and not one that isn't allowed")]
    public void BuildDoesntReachOthers() =>
        Assert.True(
            Regex.IsMatch(fixture.Build.Output, "^#[0-9]+ [0-9.]+ blocked-ok", RegexOptions.Multiline)
                && fixture.Build.OutputComplete
                && !Regex.IsMatch(fixture.Build.Output, "^#[0-9]+ [0-9.]+ SHOULD-FAIL", RegexOptions.Multiline),
            fixture.Build.Describe());

    [Fact(DisplayName = "...which shows up in the blocked line")]
    public void BuildShowsInTheBlockedLine() => AssertHas(fixture.Build, "blocked: example.com");

    [Fact(DisplayName = "a container inside dind reaches an allowed domain through the proxy")]
    public void InnerContainerReachesAnAllowedDomain() => AssertHas(fixture.InnerCurl, "200");

    [Fact(DisplayName = "without --docker, docker ps fails")]
    public void WithoutDockerDockerPsFails() =>
        Assert.True(fixture.PlainDockerPs.ExitCode != 0, fixture.PlainDockerPs.Describe());

    [Fact(DisplayName = "...and the agent has no DOCKER_HOST")]
    public void PlainSessionHasNoDockerHost() =>
        Assert.True(fixture.PlainDockerHost.CapturedStdout == "[]", fixture.PlainDockerHost.Describe());

    [Fact(DisplayName = "the docker session's dind is running")]
    public void DindIsRunning() => Assert.True(fixture.DindRunning);

    [Fact(DisplayName = "the dind lock is held")]
    public void DindLockIsHeld() => Assert.False(fixture.DindLockFreeWhileRunning);

    [Fact(DisplayName = "a plain session alongside can't reach dind:2375")]
    public void PlainSessionCantReachDind() => AssertHas(fixture.PlainReachesDind, "unreachable");

    [Fact(DisplayName = "a second --docker session is refused")]
    public void SecondDockerSessionIsRefused() =>
        AssertRefused(fixture.SecondDockerSession, "another --docker session is running");

    [Fact(DisplayName = "after a plain session came and went, the docker session's builds still work")]
    public void BuildsStillWork() =>
        Assert.True(
            fixture.Background.Contains("build-after-plain:ok", StringComparison.Ordinal), fixture.Background);

    [Fact(DisplayName = "after a SIGKILL'd docker session, its dind is still up")]
    public void DindOutlivesAKilledPen() => Assert.True(fixture.DindAfterKill);

    [Fact(DisplayName = "the next --docker session ends it and starts")]
    public void NextSessionEndsTheDeadOne() => AssertHas(fixture.AfterKill, "whose pen exited without ending it");

    [Fact(DisplayName = "...leaving nothing running")]
    public void NothingIsLeftAfterTheNext() => Assert.Equal(0, fixture.ContainersAfterKill);

    [Fact(DisplayName = "a container still using the dind volume is refused, named")]
    public void ContainerUsingTheVolumeIsRefused() =>
        AssertRefused(fixture.StaleUser, $"{fixture.Key}-integration-stale still uses");

    [Fact(DisplayName = "...and the dind lock is free again")]
    public void DindLockIsFreeAgain() => Assert.True(fixture.DindLockFreeAfterStale);

    [Fact(DisplayName = "an inner container's .git/commondir is quarantined at session end")]
    public void InnerCommondirIsQuarantined() =>
        Assert.True(
            !fixture.CommondirAfter && fixture.CommondirQuarantined
                && fixture.Commondir.Output.Contains("pen: WARNING", StringComparison.Ordinal),
            $"commondir there: {fixture.CommondirAfter}, quarantined: {fixture.CommondirQuarantined}; "
                + fixture.Commondir.Describe());

    [Fact(DisplayName = "docker = true isn't used until approved")]
    public void DockerTrueNeedsApproval() => AssertHas(fixture.DockerTrueUnapproved, "docker = true");

    [Fact(DisplayName = "docker = true in .pen/config.toml turns dind on")]
    public void DockerTrueTurnsDindOn() =>
        Assert.Multiple(
            () => AssertHas(fixture.DockerTrue, "[tcp://dind:2375]"),
            () => AssertHas(fixture.DockerTrue, "server "));

    [Fact(DisplayName = "nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    private static void AssertHas(CommandResult result, string text) =>
        Assert.True(result.Output.Contains(text, StringComparison.Ordinal), $"no \"{text}\"; {result.Describe()}");

    private static void AssertLine(ProbeResult probes, string line) =>
        Assert.True(probes.HasLine(line), $"no line \"{line}\"; {probes.Describe()}");

    private static void AssertRefused(CommandResult result, string message) =>
        Assert.True(result.IsRefusal(message), $"expected a refusal saying \"{message}\"; {result.Describe()}");

    /// <summary>Runs docker sessions one after another: hello-world, inner containers writing the project, a
    /// compose stack, builds, then one that waits while plain and docker sessions start beside it and is
    /// SIGKILL'd, then the refusals and docker = true.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        private const string BuildDockerfile = "Dockerfile.dind-integration";

        internal CommandResult HelloWorld { get; private set; } = null!;

        internal int ContainersAfterHelloWorld { get; private set; }

        internal IReadOnlyList<string> NetworksAfterHelloWorld { get; private set; } = [];

        internal bool HasDindVolume { get; private set; }

        internal ProbeResult Inner { get; private set; } = null!;

        /// <summary>Gets inner-file's text without its trailing newlines, or null if it wasn't written.</summary>
        internal string? InnerFile { get; private set; }

        internal uint? InnerFileOwner { get; private set; }

        internal CommandResult Compose { get; private set; } = null!;

        internal CommandResult Build { get; private set; } = null!;

        internal CommandResult InnerCurl { get; private set; } = null!;

        internal CommandResult PlainDockerPs { get; private set; } = null!;

        internal CommandResult PlainDockerHost { get; private set; } = null!;

        internal bool DindRunning { get; private set; }

        internal bool DindLockFreeWhileRunning { get; private set; }

        internal CommandResult PlainReachesDind { get; private set; } = null!;

        internal CommandResult SecondDockerSession { get; private set; } = null!;

        /// <summary>Gets what the background docker session wrote before it was killed.</summary>
        internal string Background { get; private set; } = null!;

        internal bool DindAfterKill { get; private set; }

        internal CommandResult AfterKill { get; private set; } = null!;

        internal int ContainersAfterKill { get; private set; }

        internal CommandResult StaleUser { get; private set; } = null!;

        internal bool DindLockFreeAfterStale { get; private set; }

        internal CommandResult Commondir { get; private set; } = null!;

        internal bool CommondirAfter { get; private set; }

        internal bool CommondirQuarantined { get; private set; }

        internal CommandResult DockerTrueUnapproved { get; private set; } = null!;

        internal CommandResult DockerTrue { get; private set; } = null!;

        internal int ContainersAtEnd { get; private set; }

        private string PenDir => Path.Combine(Project.Dir, ".pen");

        private string DindLock => Path.Combine(RuntimeDir, "dind.lock");

        private string DindName => $"^{Key}-.*-dind$";

        protected override void Prepare()
        {
            Files.DeleteTree(PenDir);
            Directory.CreateDirectory(PenDir);
            RunHelloWorld();
            RunInnerContainers();
            RunComposeAndBuilds();
            PlainDockerPs = Cli.Run("run", "docker", "ps");
            PlainDockerHost = Cli.Run("run", "bash", "-c", "echo \"[${DOCKER_HOST:-}]\"");
            RunAlongside();
            RunStaleVolumeUser();
            RunInnerCommondir();
            RunDockerTrue();
        }

        private CommandResult RunDocker(params string[] command) => Cli.Run(["--docker", "run", .. command]);

        private void RunHelloWorld()
        {
            HelloWorld = RunDocker("docker", "run", "--rm", "hello-world");
            ContainersAfterHelloWorld = Docker.CountContainers(Key);
            NetworksAfterHelloWorld = Docker.ReadLines("network", "ls", "-q", "--filter", $"name=^{Key}-");
            HasDindVolume = Docker.Run("volume", "inspect", $"{Key}_dind").ExitCode == 0;
            RunDocker("true").RequireSuccess();
        }

        private void RunInnerContainers()
        {
            Inner = new SandboxProbe { Runner = """docker run --rm -v "$PWD:/src" alpine sh -c "$1" """ }
                .Add("config", "echo x >> /src/.git/config")
                .Add("hook", "echo x > /src/.git/hooks/pre-commit")
                .Add("pen", "touch /src/.pen/x")
                .Add("mvgit", "mv /src/.git /src/.git2")
                .Add("write", "echo inner > /src/inner-file")
                .Run(Cli, "--docker");
            Inner.Session.RequireSuccess();
            var innerFile = Path.Combine(Project.Dir, "inner-file");
            InnerFile = File.Exists(innerFile) ? File.ReadAllText(innerFile).TrimEnd('\n') : null;
            InnerFileOwner = Files.GetOwnerUid(innerFile);
            try
            {
                File.Delete(innerFile);
            }
            catch (UnauthorizedAccessException)
            {
                RunDocker(
                    "docker", "run", "--rm", "-v", $"{Project.Dir}:/src", "alpine", "rm", "-f", "/src/inner-file");
            }
        }

        private void RunComposeAndBuilds()
        {
            var compose = Directory.CreateDirectory(Path.Combine(Project.Dir, "dind-compose")).FullName;
            File.WriteAllText(Path.Combine(compose, "msg"), "hello-compose\n");
            File.WriteAllText(Path.Combine(compose, "compose.yaml"), """
                services:
                  cat:
                    image: alpine
                    volumes: ["./:/w:ro"]
                    command: ["cat", "/w/msg"]

                """);
            Compose = RunDocker(
                "bash", "-c", "cd dind-compose && docker compose run --rm -T cat; docker compose down -t 1");
            Files.DeleteTree(compose);

            var dockerfile = Path.Combine(Project.Dir, BuildDockerfile);
            File.WriteAllText(dockerfile, """
                FROM curlimages/curl
                RUN curl -sSf -o /dev/null https://github.com && echo allowed-ok
                RUN curl -sSf -o /dev/null https://example.com && echo SHOULD-FAIL || echo blocked-ok

                """);
            Build = RunDocker("docker", "build", "--no-cache", "--progress", "plain", "-f", BuildDockerfile, ".");
            File.Delete(dockerfile);
            InnerCurl = RunDocker(
                "docker", "run", "--rm", "curlimages/curl", "-s", "-o", "/dev/null", "-w", "%{http_code}", "-I",
                "https://github.com");
        }

        /// <summary>Starts a docker session that waits, runs plain and docker sessions beside it, lets it build,
        /// then SIGKILLs its pen and runs the next docker session.</summary>
        private void RunAlongside()
        {
            File.WriteAllText(
                Path.Combine(Project.Dir, BuildDockerfile),
                "FROM curlimages/curl\nRUN curl -sSf -o /dev/null https://github.com\n");
            var session = Track(Cli.Start("--docker", "run", "bash", "-c", """
                for i in $(seq 600); do [ -e .dind-go ] && break; sleep 0.1; done
                docker build -q --no-cache -f Dockerfile.dind-integration . >/dev/null 2>&1 && echo build-after-plain:ok || echo build-after-plain:fail
                sleep 120
                """));
            if (!Polling.WaitForContainer($"^{Key}-.*-agent$"))
                throw new InvalidOperationException($"docker session didn't start: {session.OutputSoFar}");
            DindRunning = Polling.WaitForContainer(DindName);
            DindLockFreeWhileRunning = Locks.IsFree(DindLock, exclusive: true);
            PlainReachesDind = Cli.Run("run", "bash", "-c", """
                curl -s --max-time 5 http://dind:2375/_ping && echo reached || echo unreachable
                """).RequireSuccess();
            SecondDockerSession = RunDocker("true");
            File.WriteAllText(Path.Combine(Project.Dir, ".dind-go"), "");
            Polling.WaitUntil(
                () => session.OutputSoFar.Contains("build-after-plain", StringComparison.Ordinal),
                TimeSpan.FromSeconds(90));
            Background = session.OutputSoFar;
            session.Signal("KILL");
            session.WaitForExit();
            File.Delete(Path.Combine(Project.Dir, ".dind-go"));
            File.Delete(Path.Combine(Project.Dir, BuildDockerfile));
            DindAfterKill = Polling.WaitForContainer(DindName);
            AfterKill = RunDocker("true");
            ContainersAfterKill = Docker.CountContainers(Key);
        }

        private void RunStaleVolumeUser()
        {
            var stale = $"{Key}-integration-stale";
            var started = Docker.Run(
                "run", "-d", "--name", stale, "--entrypoint", "sleep", "-v", $"{Key}_dind:/x", "pen:latest", "120");
            if (started.ExitCode != 0)
                throw new InvalidOperationException($"starting {stale}: {started.Describe()}");
            StaleUser = RunDocker("true");
            Docker.Run("rm", "-f", stale).RequireSuccess();
            DindLockFreeAfterStale = Locks.IsFree(DindLock, exclusive: true);
        }

        private void RunInnerCommondir()
        {
            Commondir = RunDocker("bash", "-c", """docker run --rm -v "$PWD:/src" alpine touch /src/.git/commondir""");
            var gitDir = Path.Combine(Project.Dir, ".git");
            CommondirAfter = Path.Exists(Path.Combine(gitDir, "commondir"));
            CommondirQuarantined = Path.Exists(Path.Combine(gitDir, "commondir.pen-quarantine"));
            File.Delete(Path.Combine(gitDir, "commondir.pen-quarantine"));
        }

        private void RunDockerTrue()
        {
            var config = Path.Combine(PenDir, "config.toml");
            File.WriteAllText(config, "docker = true\n");
            DockerTrueUnapproved = Cli.Run("run", "true");
            Cli.Run("trust", "--yes").RequireSuccess();
            DockerTrue = Cli.Run("run", "bash", "-c", """
                echo "[$DOCKER_HOST]"; docker version --format "server {{.Server.Version}}"
                """);
            File.Delete(config);
            ContainersAtEnd = Docker.CountContainers(Key);
        }
    }
}
