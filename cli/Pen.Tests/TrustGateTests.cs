namespace Pen.Tests;

/// <summary>
/// A hostile .pen/ must not reach the token lookup, the allowlist or docker
/// build without approval, by any route. The fake docker records every call, and
/// what a session's compose run would hand the agent.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public class TrustGateTests
{
    private const string HostileToml = "allow = [\"evil.example\"]\ndocker = true\ngithub_owner = \"attacker\"\n";

    /// <summary>
    /// Succeeds at everything; compose run records the session's allowlist and
    /// token. A project-layer build keeps a copy of what it was given, and first
    /// puts the file "swap-in", if there is one, over .pen/Dockerfile: a swap
    /// after pen's last look.
    /// </summary>
    private const string RecordingDocker = """
        if [ "$1" = build ]; then
          for a; do ctx=$a; done
          if [ "$ctx" != - ]; then
            [ ! -f swap-in ] || cp swap-in repo/.pen/Dockerfile
            printf '%s' "$ctx" >context-path
            rm -rf seen-context
            cp -RPp "$ctx" seen-context
          fi
        fi
        case " $* " in
          *" run --rm "*)
            printf '%s' "${GH_TOKEN:-none}" >token-seen
            find home -name allowlist.txt -exec cp {} allowlist-seen \;
            ;;
        esac
        exit 0

        """;

    private sealed class Fixture : IDisposable
    {
        public readonly TempDir T = new();
        public readonly HostEnv Host;
        public readonly Project Project;
        public readonly Config User;
        private readonly FakeDocker _docker;

        public Fixture(string projectToml = HostileToml, string dockerfile = "FROM pen:latest\nRUN evil\n", string dockerBody = RecordingDocker)
        {
            Host = T.Host();
            var repo = Git.Repo(T.Sub("repo"));
            Git.Run(repo, "remote", "add", "origin", "https://github.com/acme/repo.git");
            Project = Project.Resolve(repo, Host);
            Directory.CreateDirectory(Project.PenDir);
            File.WriteAllText(Project.ProjectConfigPath, projectToml);
            if (dockerfile.Length > 0) File.WriteAllText(Path.Combine(Project.PenDir, "Dockerfile"), dockerfile);
            User = Config.Parse("[github]\nattacker = \"tok_attacker\"\nacme = \"tok_acme\"\n", "u", ConfigKind.User);
            Directory.CreateDirectory(Host.ConfigDir);
            File.WriteAllText(Host.UserConfigPath, "[github]\nattacker = \"tok_attacker\"\nacme = \"tok_acme\"\n");
            _docker = new FakeDocker(T, dockerBody);
        }

        public Config ProjectConfig => Config.Load(Project.PenDir, ".pen", ConfigKind.Project);
        public string CallsFile => Path.Combine(T.Path, "calls");
        public string[] Calls => File.Exists(CallsFile) ? File.ReadAllLines(CallsFile) : [];
        public string Seen(string name) => File.ReadAllText(Path.Combine(T.Path, name));
        public bool HasSeen(string name) => File.Exists(Path.Combine(T.Path, name));

        public int Run(Answers answers) =>
            new Session(Host, Project, User, ProjectConfig).Run(["true"], docker: ProjectConfig.Docker == true, answers: answers);

        public void AssertNothingReached()
        {
            Assert.Empty(Calls); // nothing at all ran, so no token or allowlist went anywhere
            Assert.False(HasSeen("token-seen"));
            Assert.False(File.Exists(Path.Combine(Host.RuntimeDir(Project.Key), "token-notice")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(Host.RuntimeDir(Project.Key), "sessions")));
        }

        public void Dispose()
        {
            _docker.Dispose();
            T.Dispose();
        }
    }

    private static Answers Saying(string answer) => new(new StringReader(answer + "\n"), IsTerminal: true);
    private static Answers NoTerminal => new(new StringReader("y\n"), IsTerminal: false);

    [Theory]
    [InlineData("n")]
    [InlineData("")]
    [InlineData("yep")]
    public void DecliningStartsNothing(string answer)
    {
        using var f = new Fixture();
        var e = Assert.Throws<PenException>(() => f.Run(Saying(answer)));
        Assert.Contains("not starting", e.Message);
        f.AssertNothingReached();
        Assert.False(File.Exists(TrustStore.PathFor(f.Host.RuntimeDir(f.Project.Key), f.Project.Dir)));
    }

    [Fact]
    public void WithoutATerminalItsRefused_ListingTheItems_EvenWithAYesWaitingOnStdin()
    {
        using var f = new Fixture();
        var e = Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Contains("pen trust", e.Message);
        Assert.Contains("allow evil.example", e.Message);
        Assert.Contains("docker = true", e.Message);
        Assert.Contains("github_owner = attacker", e.Message);
        Assert.Contains(".pen/Dockerfile", e.Message);
        f.AssertNothingReached();
    }

    [Fact]
    public void AnUncheckableContextIsRefusedEvenWhenTheUserWouldSayYes()
    {
        using var f = new Fixture();
        File.CreateSymbolicLink(Path.Combine(f.Project.PenDir, "loop"), "loop");
        var e = Assert.Throws<PenException>(() => f.Run(Saying("y")));
        Assert.Contains("can't approve .pen/", e.Message);
        f.AssertNothingReached();
        Assert.Throws<PenException>(() => Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: true, NoTerminal));
    }

    [Fact]
    public void ApprovingLetsItAllThrough_AndDoesntAskAgain()
    {
        using var f = new Fixture();
        Assert.Equal(0, f.Run(Saying("y")));
        var context = f.Seen("context-path");
        Assert.Contains(f.Calls, c => c.StartsWith("build ", StringComparison.Ordinal) && c.EndsWith($"-f {context}/Dockerfile {context}", StringComparison.Ordinal));
        Assert.Equal("tok_attacker", f.Seen("token-seen"));
        Assert.Contains("evil.example", f.Seen("allowlist-seen"));

        // No terminal now, so asking would refuse.
        Assert.Equal(0, f.Run(NoTerminal));
    }

    [Fact]
    public void AChangedDockerfileAsksAgain()
    {
        using var f = new Fixture();
        Assert.Equal(0, f.Run(Saying("y")));
        File.WriteAllText(Path.Combine(f.Project.PenDir, "Dockerfile"), "FROM pen:latest\nRUN worse\n");
        File.Delete(f.CallsFile);
        var e = Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Contains(".pen/Dockerfile", e.Message);
        Assert.DoesNotContain("allow evil.example", e.Message); // only what's new
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void ADockerfileThatChangesAfterTheGateIsntBuilt()
    {
        using var f = new Fixture(projectToml: "");
        Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: true, NoTerminal);
        File.WriteAllText(Path.Combine(f.Project.PenDir, "Dockerfile"), "FROM pen:latest\nRUN worse\n");
        var e = Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project));
        Assert.Contains("isn't what was approved", e.Message);
        Assert.Empty(f.Calls);
    }

    private const string SomeTools = "pen-tools:0123456789abcdef", OtherTools = "pen-tools:fedcba9876543210";

    [Fact]
    public void OnAToolsImage_ItsBuiltFromThePrivateCopy_WithTheToolsInPlaceOfPenLatest()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        File.WriteAllText(Path.Combine(f.T.Path, "swap-in"), "FROM pen:latest\nRUN worse\n");
        var callsBefore = f.Calls.Length;

        var name = Image.EnsureProjectLayer(f.Host, f.Project, baseImage: SomeTools);

        Assert.Equal($"{f.Project.Key}:0123456789abcdef", name);
        var build = Assert.Single(f.Calls.Skip(callsBefore));
        Assert.Contains($"-t {name} ", build);
        Assert.Contains("--build-context pen:latest=docker-image://pen-tools:0123456789abcdef ", build);
        Assert.StartsWith(f.Host.RuntimeDir(f.Project.Key), f.Seen("context-path"));
        Assert.Equal(Approved, f.Seen("seen-context/Dockerfile")); // not the swap
    }

    [Fact]
    public void WithoutToolsNothingStandsInForPenLatest()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        var callsBefore = f.Calls.Length;
        Assert.Equal($"{f.Project.Key}:latest", Image.EnsureProjectLayer(f.Host, f.Project));
        Assert.DoesNotContain("--build-context", Assert.Single(f.Calls.Skip(callsBefore)));
    }

    [Fact]
    public void AddingToolsIsNoWayAroundApproval()
    {
        using var f = new Fixture(projectToml: "");
        Assert.Contains("isn't what was approved", Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project, baseImage: SomeTools)).Message);
        Assert.Empty(f.Calls);

        Approve(f);
        File.WriteAllText(Path.Combine(f.Project.PenDir, "Dockerfile"), "FROM pen:latest\nRUN worse\n");
        Assert.Contains("isn't what was approved", Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project, baseImage: SomeTools)).Message);
        Assert.Empty(f.Calls);
    }

    /// <summary>The fake docker says every image exists, so a build skipped because the tag does would show.</summary>
    [Fact]
    public void AnExistingTagIsBuiltAgain_SoANewlyApprovedDockerfileReachesIt()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        Image.EnsureProjectLayer(f.Host, f.Project, baseImage: SomeTools);
        const string next = "FROM pen:latest\nRUN next\n";
        File.WriteAllText(Path.Combine(f.Project.PenDir, "Dockerfile"), next);
        Approve(f);
        var callsBefore = f.Calls.Length;

        Assert.Equal($"{f.Project.Key}:0123456789abcdef", Image.EnsureProjectLayer(f.Host, f.Project, baseImage: SomeTools));

        Assert.Single(f.Calls.Skip(callsBefore), c => c.StartsWith("build ", StringComparison.Ordinal));
        Assert.Equal(next, f.Seen("seen-context/Dockerfile"));
    }

    [Fact]
    public void TwoToolSetsInOneProjectGetTwoImages()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        var one = Image.EnsureProjectLayer(f.Host, f.Project, baseImage: SomeTools);
        var other = Image.EnsureProjectLayer(f.Host, f.Project, baseImage: OtherTools);
        Assert.NotEqual(one, other);
        Assert.Contains(f.Calls, c => c.Contains($"-t {one} ", StringComparison.Ordinal) && c.Contains($"docker-image://{SomeTools} ", StringComparison.Ordinal));
        Assert.Contains(f.Calls, c => c.Contains($"-t {other} ", StringComparison.Ordinal) && c.Contains($"docker-image://{OtherTools} ", StringComparison.Ordinal));
    }

    private static void Approve(Fixture f) =>
        Assert.Equal(0, Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: true, NoTerminal));

    private const string Approved = "FROM pen:latest\nRUN evil\n"; // the Fixture's default

    [Fact]
    public void ASwapAfterTheHashDoesntReachTheBuild()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        File.WriteAllText(Path.Combine(f.T.Path, "swap-in"), "FROM pen:latest\nRUN worse\n");

        Assert.NotNull(Image.EnsureProjectLayer(f.Host, f.Project));

        Assert.Contains("worse", File.ReadAllText(Path.Combine(f.Project.PenDir, "Dockerfile"))); // it did swap
        Assert.Equal(Approved, f.Seen("seen-context/Dockerfile"));
    }

    [Fact]
    public async Task RacingSwapsNeverReachTheBuild_ReplayingAnAgentInTheMainCheckout()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        var dockerfile = Path.Combine(f.Project.PenDir, "Dockerfile");
        var mode = File.GetUnixFileMode(dockerfile);
        var (evil, good, next) = (Path.Combine(f.T.Path, "evil"), Path.Combine(f.T.Path, "good"), Path.Combine(f.T.Path, "next"));
        File.WriteAllText(evil, "FROM pen:latest\nRUN worse\n");
        File.WriteAllText(good, Approved);
        File.SetUnixFileMode(evil, mode);
        File.SetUnixFileMode(good, mode);

        using var stop = new CancellationTokenSource();
        var swapper = Task.Factory.StartNew(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                File.Copy(evil, next, overwrite: true);
                File.Move(next, dockerfile, overwrite: true);
                Thread.Sleep(1);
                File.Copy(good, next, overwrite: true);
                File.Move(next, dockerfile, overwrite: true);
                Thread.Sleep(5);
            }
        }, TaskCreationOptions.LongRunning);

        var built = new List<string>();
        for (int i = 0; i < 60; i++)
        {
            var seen = Path.Combine(f.T.Path, "seen-context");
            if (Directory.Exists(seen)) Directory.Delete(seen, recursive: true);
            try { Image.EnsureProjectLayer(f.Host, f.Project); }
            catch (PenException) { continue; } // refused: fine
            built.Add(f.Seen("seen-context/Dockerfile"));
        }
        stop.Cancel();
        await swapper;
        Assert.NotEmpty(built); // the approved one does get built between swaps
        Assert.All(built, content => Assert.Equal(Approved, content));
    }

    [Fact]
    public void AChangeBeforeTheCopyIsRefused_AndSoIsHostileContent()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        File.CreateSymbolicLink(Path.Combine(f.Project.PenDir, "out"), "/etc/passwd");
        var e = Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project));
        Assert.Contains("can't build from .pen/", e.Message);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void TheContextIsOutOfTheProjectInAPrivateDir_AndGoesAfterTheBuild()
    {
        using var f = new Fixture(projectToml: "# config\n");
        File.WriteAllText(Path.Combine(f.Project.PenDir, ".dockerignore"), "secret\n");
        var script = Path.Combine(f.Project.PenDir, "setup.sh");
        File.WriteAllText(script, "true\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_101_101);
        File.CreateSymbolicLink(Path.Combine(f.Project.PenDir, "link"), "setup.sh");
        Approve(f);

        Assert.NotNull(Image.EnsureProjectLayer(f.Host, f.Project));

        var context = f.Seen("context-path");
        Assert.StartsWith(f.Host.RuntimeDir(f.Project.Key) + "/", context);
        Assert.False(RepoGuard.IsInside(f.Project.Dir, context));
        Assert.EndsWith("/Dockerfile", f.Calls[^1].Split(' ')[^2]);
        Assert.False(Path.Exists(context));
        Assert.False(Path.Exists(Path.GetDirectoryName(context)));

        var seen = Path.Combine(f.T.Path, "seen-context");
        Assert.False(Path.Exists(Path.Combine(seen, "config.toml"))); // it isn't hashed, so no build may read it
        Assert.Equal("secret\n", File.ReadAllText(Path.Combine(seen, ".dockerignore")));
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(Path.Combine(seen, "setup.sh")));
        Assert.Equal("setup.sh", new FileInfo(Path.Combine(seen, "link")).LinkTarget);
    }

    [Fact]
    public void AFailedBuildAlsoCleansUp()
    {
        using var f = new Fixture(projectToml: "", dockerBody: """
            if [ "$1" = build ]; then for a; do ctx=$a; done; printf '%s' "$ctx" >context-path; exit 3; fi
            exit 0

            """);
        Approve(f);
        Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project));
        Assert.False(Path.Exists(f.Seen("context-path")));
    }

    /// <summary>What a pen that died mid-build leaves, read-only directory and all.</summary>
    private static string StaleContext(Fixture f)
    {
        Session.EnsureRuntimeDir(f.Host, f.Project);
        var parent = Path.Combine(f.Host.RuntimeDir(f.Project.Key), "layer-" + Project.Hash(f.Project.Dir));
        var ro = Directory.CreateDirectory(Path.Combine(parent, "context", "ro")).FullName;
        File.WriteAllText(Path.Combine(ro, "stale"), "x");
        File.SetUnixFileMode(ro, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return ro;
    }

    [Fact]
    public void ACopyLeftByADeadPenIsReplaced()
    {
        using var f = new Fixture(projectToml: "");
        StaleContext(f);
        Approve(f);
        Assert.NotNull(Image.EnsureProjectLayer(f.Host, f.Project));
        Assert.False(Path.Exists(Path.Combine(f.T.Path, "seen-context", "ro")));
    }

    [Fact]
    public void PruneRemovesACopyLeftByADeadPen()
    {
        using var f = new Fixture(projectToml: "", dockerfile: "");
        StaleContext(f);
        var runtimeDir = f.Host.RuntimeDir(f.Project.Key);
        Directory.Delete(f.Project.Dir, recursive: true);
        Assert.True(Prune.Remove(f.Host, Assert.Single(Prune.Find(f.Host, [], []), e => e.Key == f.Project.Key)));
        Assert.False(Directory.Exists(runtimeDir));
    }

    [Fact]
    public async Task BuildsInOneDirectoryTakeTurns()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        var lockFile = Path.Combine(f.Host.RuntimeDir(f.Project.Key), "layer-" + Project.Hash(f.Project.Dir) + ".lock");
        var held = FileLock.TryTake(lockFile, exclusive: true)!;
        var build = Task.Factory.StartNew(() => Image.EnsureProjectLayer(f.Host, f.Project), TaskCreationOptions.LongRunning);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.False(build.IsCompleted);
        Assert.Empty(f.Calls);
        held.Dispose();
        await build.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.NotEmpty(f.Calls);
    }

    /// <summary>Answers y, first doing what an agent does while the user reads.</summary>
    private sealed class DoingThenAnswering(Action meanwhile, string answer = "y") : TextReader
    {
        public override string? ReadLine()
        {
            meanwhile();
            return answer;
        }
    }

    private static string CapturingStderr(Action action)
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { action(); }
        finally { Console.SetError(stderr); }
        return captured.ToString();
    }

    private static string[] Reviews(Fixture f) =>
        Directory.GetDirectories(f.Host.RuntimeDir(f.Project.Key), "layer-*-review-*");

    private const string Benign = "FROM pen:latest\nRUN benign\n";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASwapWhileTheUserReadsApprovesOnlyWhatWasShown(bool throughPenTrust)
    {
        using var f = new Fixture(projectToml: "", dockerfile: Benign);
        var dockerfile = Path.Combine(f.Project.PenDir, "Dockerfile");
        string? inTheCopy = null;
        var answers = new Answers(new DoingThenAnswering(() =>
        {
            File.WriteAllText(dockerfile, "FROM pen:latest\nRUN evil-swap\n");
            inTheCopy = File.ReadAllText(Path.Combine(Assert.Single(Reviews(f)), "context", "Dockerfile"));
        }), IsTerminal: true);

        var shown = CapturingStderr(() =>
        {
            if (throughPenTrust) Assert.Equal(0, Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: false, answers));
            else Trust.RequireForSession(f.Host, f.Project, f.User, f.ProjectConfig, answers);
        });

        Assert.Contains("| RUN benign", shown);
        Assert.DoesNotContain("evil-swap", shown);
        Assert.Equal(Benign, inTheCopy);
        Assert.Empty(Reviews(f)); // gone once answered

        // The evil file isn't approved: it's asked about again, and doesn't build.
        Assert.Contains(".pen/Dockerfile", Assert.Throws<PenException>(() => f.Run(NoTerminal)).Message);
        Assert.Contains("isn't what was approved", Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project)).Message);
        Assert.Empty(f.Calls);

        // Swapped back, the approved bytes need no prompt.
        File.WriteAllText(dockerfile, Benign);
        Assert.Equal(0, f.Run(NoTerminal));
    }

    [Fact]
    public void WhatsShownIsWhatsHashed_EvenIfTheLiveFileLooksBenignWhenTheUserLooks()
    {
        using var f = new Fixture(projectToml: "", dockerfile: "FROM pen:latest\nRUN evil-shown\n");
        var dockerfile = Path.Combine(f.Project.PenDir, "Dockerfile");
        var answers = new Answers(new DoingThenAnswering(() => File.WriteAllText(dockerfile, Benign), answer: "n"), IsTerminal: true);
        var shown = CapturingStderr(() => Assert.Throws<PenException>(() => Trust.RequireForSession(f.Host, f.Project, f.User, f.ProjectConfig, answers)));
        Assert.Contains("| RUN evil-shown", shown);
        Assert.Empty(Reviews(f));
        Assert.False(File.Exists(TrustStore.PathFor(f.Host.RuntimeDir(f.Project.Key), f.Project.Dir)));
    }

    [Fact]
    public void ThePromptEscapesAndCutsTheDockerfile_NamingTheCopy()
    {
        var text = "FROM pen:latest\nRUN \x1b[2Jhidden\r\n" + new string('x', 500) + "\n" + string.Concat(Enumerable.Repeat("RUN a\n", 100));
        using var f = new Fixture(projectToml: "", dockerfile: text);
        string? copy = null;
        var answers = new Answers(new DoingThenAnswering(() => copy = Path.Combine(Assert.Single(Reviews(f)), "context"), answer: "n"), IsTerminal: true);
        var shown = CapturingStderr(() => Assert.Throws<PenException>(() => Trust.RequireForSession(f.Host, f.Project, f.User, f.ProjectConfig, answers)));
        Assert.DoesNotContain('\x1b', shown);
        Assert.Contains(@"\x1b[2Jhidden\r", shown);
        Assert.Contains("cut: 300 more characters", shown);
        Assert.Contains($"cut: {103 - Trust.MaxShownLines} more lines", shown);
        Assert.Contains(copy!, shown);
    }

    [Fact]
    public void AReviewAbandonedByAKilledPenGoesOnceOld_AndAFreshOneStays()
    {
        using var f = new Fixture(projectToml: "");
        Session.EnsureRuntimeDir(f.Host, f.Project);
        var prefix = Path.Combine(f.Host.RuntimeDir(f.Project.Key), $"layer-{Project.Hash(f.Project.Dir)}-review-");
        var (old, fresh) = (prefix + "old", prefix + "fresh");
        Directory.CreateDirectory(Path.Combine(old, "context"));
        Directory.CreateDirectory(Path.Combine(fresh, "context"));
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-2));
        using (LayerContext.Review(f.Host, f.Project)) { }
        Assert.False(Directory.Exists(old));
        Assert.True(Directory.Exists(fresh));
    }

    [Fact]
    public void AConfigCommentCantReachABuild_AndDoesntAsk()
    {
        using var f = new Fixture(projectToml: "# one\n");
        Approve(f);
        File.WriteAllText(f.Project.ProjectConfigPath, "# two: run me\n");
        Assert.Equal(0, f.Run(NoTerminal));
        Assert.False(Path.Exists(Path.Combine(f.T.Path, "seen-context", "config.toml")));
    }

    [Fact]
    public void TrustListApprovesNothing_AndShowsEverything()
    {
        var domains = Enumerable.Range(0, 50).Select(i => $"d{i}.example").ToList();
        var toml = "allow = [" + string.Join(',', domains.Select(d => $"\"{d}\"")) + "]\ndocker = true\n";
        using var f = new Fixture(projectToml: toml);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { Assert.Equal(0, Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: false, NoTerminal, list: true)); }
        finally { Console.SetOut(stdout); }

        Assert.All(domains, d => Assert.Contains(d, captured.ToString()));
        Assert.Contains("docker = true", captured.ToString());
        Assert.Contains("| RUN evil", captured.ToString());
        Assert.Empty(Reviews(f));
        Assert.False(File.Exists(TrustStore.PathFor(f.Host.RuntimeDir(f.Project.Key), f.Project.Dir)));
        var after = Trust.Requested(f.Project, f.User, f.ProjectConfig, TrustStore.For(f.Host, f.Project));
        Assert.Equal(50, after.Domains.Count);
        Assert.True(after.Docker);
        Assert.NotNull(after.Layer);
        Assert.Throws<PenException>(() => f.Run(NoTerminal));
    }

    [Fact]
    public void ATamperedStoreAsksAgain()
    {
        using var f = new Fixture();
        Assert.Equal(0, f.Run(Saying("y")));
        File.WriteAllText(TrustStore.PathFor(f.Host.RuntimeDir(f.Project.Key), f.Project.Dir), "garbage\n");
        File.Delete(f.CallsFile);
        Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void PortsAloneDontAsk()
    {
        using var f = new Fixture(projectToml: "ports = [39871]\n", dockerfile: "");
        Assert.Equal(0, new Session(f.Host, f.Project, f.User, f.ProjectConfig).Run(["true"], ports: Port.Merge(f.ProjectConfig.Ports, []), answers: NoTerminal));
    }

    [Fact]
    public void TheOwnersOriginCaseNeedsNoApproval()
    {
        using var f = new Fixture(projectToml: "github_owner = \"ACME\"\n", dockerfile: "");
        Assert.Equal(0, f.Run(NoTerminal));
        Assert.Equal("tok_acme", f.Seen("token-seen"));
    }

    // pen trust

    [Fact]
    public void TrustYesApproves_WithoutATerminal()
    {
        using var f = new Fixture();
        Assert.Equal(0, Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: true, NoTerminal));
        Assert.Equal(0, f.Run(NoTerminal));
        Assert.Equal(0, Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: false, NoTerminal)); // nothing new
    }

    [Fact]
    public void TrustAsks_AndAnsweringNoApprovesNothing()
    {
        using var f = new Fixture();
        Assert.Equal(1, Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: false, Saying("n")));
        Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Equal(0, Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: false, Saying("y")));
        Assert.Equal(0, f.Run(NoTerminal));
    }

    [Fact]
    public void TrustWithoutATerminalOrYesRefuses()
    {
        using var f = new Fixture();
        var e = Assert.Throws<PenException>(() => Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: false, NoTerminal));
        Assert.Contains("--yes", e.Message);
        Assert.False(File.Exists(TrustStore.PathFor(f.Host.RuntimeDir(f.Project.Key), f.Project.Dir)));
    }

    // pen upgrade

    [Fact]
    public void UpgradeSkipsAnUnapprovedLayer()
    {
        using var f = new Fixture();
        Image.UpgradeProjectLayer(f.Host, f.Project, clean: false);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void UpgradeSkipsAnUncheckableLayer()
    {
        using var f = new Fixture();
        File.CreateSymbolicLink(Path.Combine(f.Project.PenDir, "out"), "/etc/passwd");
        Image.UpgradeProjectLayer(f.Host, f.Project, clean: false);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void UpgradeRebuildsAnApprovedLayer_AndSkipsItOnceChanged()
    {
        using var f = new Fixture(projectToml: "");
        Trust.Command(f.Host, f.Project, f.User, f.ProjectConfig, yes: true, NoTerminal);
        Image.UpgradeProjectLayer(f.Host, f.Project, clean: false);
        Assert.Contains(f.Calls, c => c.StartsWith("build ", StringComparison.Ordinal));

        File.Delete(f.CallsFile);
        File.WriteAllText(Path.Combine(f.Project.PenDir, "Dockerfile"), "FROM pen:latest\nRUN worse\n");
        Image.UpgradeProjectLayer(f.Host, f.Project, clean: false);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void UpgradeBuildsFromAPrivateCopyToo()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        File.WriteAllText(Path.Combine(f.T.Path, "swap-in"), "FROM pen:latest\nRUN worse\n");
        Image.UpgradeProjectLayer(f.Host, f.Project, clean: false);
        Assert.Equal(Approved, f.Seen("seen-context/Dockerfile"));
        Assert.StartsWith(f.Host.RuntimeDir(f.Project.Key) + "/", f.Seen("context-path"));
    }

    // pen allow

    /// <summary>A session running in the project, whose allowlist a live update rewrites.</summary>
    private static (SessionDir Session, FileLock Lock) RunningSession(Fixture f)
    {
        Session.EnsureRuntimeDir(f.Host, f.Project);
        var s = new SessionDir(f.Project.Key, f.T.Sub("home", ".local", "state", "pen", f.Project.Key, "sessions", "12345678"));
        File.WriteAllText(s.ComposeFile, "");
        File.WriteAllText(s.EnvFile, $"PROJECT_DIR={f.Project.Dir}\n");
        File.WriteAllText(s.File("allowlist.txt"), "");
        return (s, FileLock.TryTake(s.LockFile, exclusive: true)!);
    }

    [Fact]
    public void GlobalAllowDoesntApplyAProjectsUnapprovedDomainsLive()
    {
        using var f = new Fixture(dockerfile: "");
        var (s, l) = RunningSession(f);
        using var _ = l;
        Assert.Equal(0, AllowCommand.Add(f.Host, ["typed.example"], global: true));
        var list = File.ReadAllText(s.File("allowlist.txt"));
        Assert.Contains("typed.example", list);
        Assert.DoesNotContain("evil.example", list);
    }

    [Fact]
    public void ProjectAllowInAnotherDirDoesntApplyUnapprovedDomainsLive_ButDoesApprovedOnes()
    {
        using var f = new Fixture(projectToml: "allow = [\"evil.example\", \"ok.example\"]\n", dockerfile: "");
        var (s, l) = RunningSession(f);
        using var _ = l;
        TrustStore.For(f.Host, f.Project).ApproveDomains(["ok.example"]);
        Assert.True(AllowCommand.Apply(f.Host, s));
        var list = File.ReadAllText(s.File("allowlist.txt"));
        Assert.Contains("ok.example", list);
        Assert.DoesNotContain("evil.example", list);
    }

    [Fact]
    public void ACorruptStoreAppliesNoProjectDomainsLive()
    {
        using var f = new Fixture(projectToml: "allow = [\"ok.example\"]\n", dockerfile: "");
        var (s, l) = RunningSession(f);
        using var _ = l;
        TrustStore.For(f.Host, f.Project).ApproveDomains(["ok.example"]);
        File.WriteAllText(TrustStore.PathFor(f.Host.RuntimeDir(f.Project.Key), f.Project.Dir), "garbage\n");
        Assert.True(AllowCommand.Apply(f.Host, s));
        Assert.DoesNotContain("ok.example", File.ReadAllText(s.File("allowlist.txt")));
    }

    [Fact]
    public void ADomainAddedWithPenAllowDoesntAskNextSession()
    {
        using var f = new Fixture(projectToml: "", dockerfile: "");
        var cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = f.Project.Dir;
        try { Assert.Equal(0, AllowCommand.Add(f.Host, ["typed.example"], global: false)); }
        finally { Environment.CurrentDirectory = cwd; }
        Assert.Equal(["typed.example"], f.ProjectConfig.Allow);
        Assert.Equal(0, f.Run(NoTerminal));
        Assert.Contains("typed.example", f.Seen("allowlist-seen"));
    }

    [Fact]
    public void TypingAPlantedDomainApprovesIt_OnlyThatOne()
    {
        using var f = new Fixture(projectToml: "allow = [\"planted.example\", \"other.example\"]\n", dockerfile: "");
        var cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = f.Project.Dir;
        try { Assert.Equal(0, AllowCommand.Add(f.Host, ["planted.example"], global: false)); }
        finally { Environment.CurrentDirectory = cwd; }
        var store = TrustStore.For(f.Host, f.Project);
        Assert.True(store.CoversDomain("planted.example"));
        Assert.False(store.CoversDomain("other.example"));
    }

    [Fact]
    public void GlobalAllowApprovesNothingInTheProject()
    {
        using var f = new Fixture(dockerfile: "");
        Assert.Equal(0, AllowCommand.Add(f.Host, ["typed.example"], global: true));
        Assert.False(TrustStore.For(f.Host, f.Project).CoversDomain("typed.example"));
    }

    [Fact]
    public void AllowListingReportsAgainstApprovedDomains()
    {
        using var f = new Fixture(projectToml: "allow = [\"evil.example\", \"ok.example\"]\n", dockerfile: "");
        var (s, l) = RunningSession(f);
        using var _ = l;
        TrustStore.For(f.Host, f.Project).ApproveDomains(["ok.example"]);
        File.WriteAllText(s.ProxyLog,
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT evil.example:443 -\n" +
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT ok.example:443 -\n");
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { AllowCommand.List(f.Host, f.Project, Config.Empty, f.ProjectConfig); }
        finally { Console.SetOut(stdout); }
        Assert.Contains("evil.example", captured.ToString());
        Assert.DoesNotContain("ok.example", captured.ToString());
    }
}
