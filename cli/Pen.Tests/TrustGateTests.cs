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
    /// Succeeds at everything, and the proxy sees the ACL files as written; compose run records the session's allowlist and
    /// token. A project-layer build keeps a copy of what it was given, and first
    /// puts the file "swap-in", if there is one, over .pen/Dockerfile: a swap
    /// after Pen's last look.
    /// </summary>
    private const string RecordingDocker = FakeDocker.ServingAclFiles + """
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
            find home -name denylist.txt -exec cp {} denylist-seen \;
            find home -name open-egress.txt -exec cp {} open-egress-seen \;
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
            User = Config.Parse("[github]\nattacker = \"tok_attacker\"\nacme = \"tok_acme\"\n", "u", ConfigKind.User, RecipeCatalog.Embedded);
            Directory.CreateDirectory(Host.ConfigDir);
            File.WriteAllText(Host.UserConfigPath, "[github]\nattacker = \"tok_attacker\"\nacme = \"tok_acme\"\n");
            _docker = new FakeDocker(T, dockerBody);
        }

        public Config ProjectConfig => Config.Load(Project.PenDir, ".pen", ConfigKind.Project, RecipeCatalog.Embedded);
        public string CallsFile => Path.Combine(T.Path, "calls");
        public string[] Calls => File.Exists(CallsFile) ? File.ReadAllLines(CallsFile) : [];
        public string Seen(string name) => File.ReadAllText(Path.Combine(T.Path, name));
        public bool HasSeen(string name) => File.Exists(Path.Combine(T.Path, name));

        public int Run(Answers answers, IReadOnlyList<ToolSpec>? flagTools = null, RecipeCatalog? recipes = null, string[]? command = null) =>
            new Session(new ProjectContext(Host, Project, User, ProjectConfig), answers, recipes ?? RecipeCatalog.Embedded).Run(new(command ?? ["true"]) { Docker = ProjectConfig.Docker == true, FlagTools = flagTools ?? [] });

        public void AssertNothingReached()
        {
            Assert.Empty(Calls); // nothing at all ran, so no token or allowlist went anywhere
            Assert.False(HasSeen("token-seen"));
            Assert.False(File.Exists(Path.Combine(Host.GetRuntimeDir(Project.Key), "token-notice")));
            Assert.Empty(Directory.GetDirectories(Path.Combine(Host.GetRuntimeDir(Project.Key), "sessions")));
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
        Assert.False(File.Exists(TrustStore.GetPath(f.Host.GetRuntimeDir(f.Project.Key), f.Project.Dir)));
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
        Assert.Throws<PenException>(() => new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: true, list: false));
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
        new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: true, list: false);
        File.WriteAllText(Path.Combine(f.Project.PenDir, "Dockerfile"), "FROM pen:latest\nRUN worse\n");
        var e = Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name));
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
        Assert.StartsWith(f.Host.GetRuntimeDir(f.Project.Key), f.Seen("context-path"));
        Assert.Equal(Approved, f.Seen("seen-context/Dockerfile")); // not the swap
    }

    [Fact]
    public void WithoutToolsNothingStandsInForPenLatest()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        var callsBefore = f.Calls.Length;
        Assert.Equal($"{f.Project.Key}:latest", Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name));
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
        Assert.Equal(0, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: true, list: false));

    private const string Approved = "FROM pen:latest\nRUN evil\n"; // the Fixture's default

    [Fact]
    public void ASwapAfterTheHashDoesntReachTheBuild()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        File.WriteAllText(Path.Combine(f.T.Path, "swap-in"), "FROM pen:latest\nRUN worse\n");

        Assert.NotNull(Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name));

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
            try { Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name); }
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
        var e = Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name));
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

        Assert.NotNull(Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name));

        var context = f.Seen("context-path");
        Assert.StartsWith(f.Host.GetRuntimeDir(f.Project.Key) + "/", context);
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
        Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name));
        Assert.False(Path.Exists(f.Seen("context-path")));
    }

    /// <summary>What a Pen that died mid-build leaves, read-only directory and all.</summary>
    private static string StaleContext(Fixture f)
    {
        Session.EnsureRuntimeDir(f.Host, f.Project);
        var parent = Path.Combine(f.Host.GetRuntimeDir(f.Project.Key), "layer-" + Project.HashPath(f.Project.Dir));
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
        Assert.NotNull(Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name));
        Assert.False(Path.Exists(Path.Combine(f.T.Path, "seen-context", "ro")));
    }

    [Fact]
    public void CleanStaleRemovesACopyLeftByADeadPen()
    {
        using var f = new Fixture(projectToml: "", dockerfile: "");
        StaleContext(f);
        var runtimeDir = f.Host.GetRuntimeDir(f.Project.Key);
        Directory.Delete(f.Project.Dir, recursive: true);
        Assert.True(CleanStaleCommand.Remove(f.Host, Assert.Single(CleanStaleCommand.Find(f.Host, [], []), e => e.Key == f.Project.Key)));
        Assert.False(Directory.Exists(runtimeDir));
    }

    [Fact]
    public async Task BuildsInOneDirectoryTakeTurns()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        var lockFile = Path.Combine(f.Host.GetRuntimeDir(f.Project.Key), "layer-" + Project.HashPath(f.Project.Dir) + ".lock");
        var held = FileLock.TryTake(lockFile, exclusive: true)!;
        var build = Task.Factory.StartNew(() => Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name), TaskCreationOptions.LongRunning);
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
        Directory.GetDirectories(f.Host.GetRuntimeDir(f.Project.Key), "layer-*-review-*");

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
            if (throughPenTrust) Assert.Equal(0, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), answers, RecipeCatalog.Embedded).Run(yes: false, list: false));
            else new TrustGate(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), answers, RecipeCatalog.Embedded).RequireForSession([]);
        });

        Assert.Contains("| RUN benign", shown);
        Assert.DoesNotContain("evil-swap", shown);
        Assert.Equal(Benign, inTheCopy);
        Assert.Empty(Reviews(f)); // gone once answered

        // The evil file isn't approved: it's asked about again, and doesn't build.
        Assert.Contains(".pen/Dockerfile", Assert.Throws<PenException>(() => f.Run(NoTerminal)).Message);
        Assert.Contains("isn't what was approved", Assert.Throws<PenException>(() => Image.EnsureProjectLayer(f.Host, f.Project, baseImage: Image.Name)).Message);
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
        var shown = CapturingStderr(() => Assert.Throws<PenException>(() => new TrustGate(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), answers, RecipeCatalog.Embedded).RequireForSession([])));
        Assert.Contains("| RUN evil-shown", shown);
        Assert.Empty(Reviews(f));
        Assert.False(File.Exists(TrustStore.GetPath(f.Host.GetRuntimeDir(f.Project.Key), f.Project.Dir)));
    }

    [Fact]
    public void ThePromptEscapesAndCutsTheDockerfile_NamingTheCopy()
    {
        var text = "FROM pen:latest\nRUN \x1b[2Jhidden\r\n" + new string('x', 500) + "\n" + string.Concat(Enumerable.Repeat("RUN a\n", 100));
        using var f = new Fixture(projectToml: "", dockerfile: text);
        string? copy = null;
        var answers = new Answers(new DoingThenAnswering(() => copy = Path.Combine(Assert.Single(Reviews(f)), "context"), answer: "n"), IsTerminal: true);
        var shown = CapturingStderr(() => Assert.Throws<PenException>(() => new TrustGate(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), answers, RecipeCatalog.Embedded).RequireForSession([])));
        Assert.DoesNotContain('\x1b', shown);
        Assert.Contains(@"\x1b[2Jhidden\r", shown);
        Assert.Contains("cut: 300 more characters", shown);
        Assert.Contains($"cut: {103 - TrustGate.MaxShownLines} more lines", shown);
        Assert.Contains(copy!, shown);
    }

    [Fact]
    public void AReviewAbandonedByAKilledPenGoesOnceOld_AndAFreshOneStays()
    {
        using var f = new Fixture(projectToml: "");
        Session.EnsureRuntimeDir(f.Host, f.Project);
        var prefix = Path.Combine(f.Host.GetRuntimeDir(f.Project.Key), $"layer-{Project.HashPath(f.Project.Dir)}-review-");
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
        try { Assert.Equal(0, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: false, list: true)); }
        finally { Console.SetOut(stdout); }

        Assert.All(domains, d => Assert.Contains(d, captured.ToString()));
        Assert.Contains("docker = true", captured.ToString());
        Assert.Contains("| RUN evil", captured.ToString());
        Assert.Empty(Reviews(f));
        Assert.False(File.Exists(TrustStore.GetPath(f.Host.GetRuntimeDir(f.Project.Key), f.Project.Dir)));
        var after = new TrustGate(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).FindUnapproved(TrustStore.For(f.Host, f.Project), []);
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
        File.WriteAllText(TrustStore.GetPath(f.Host.GetRuntimeDir(f.Project.Key), f.Project.Dir), "garbage\n");
        File.Delete(f.CallsFile);
        Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void PortsAloneDontAsk()
    {
        using var f = new Fixture(projectToml: "ports = [39871]\n", dockerfile: "");
        Assert.Equal(0, new Session(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(new(["true"]) { Ports = Port.Merge(f.ProjectConfig.Ports, []) }));
    }

    [Fact]
    public void TheOwnersOriginCaseNeedsNoApproval()
    {
        using var f = new Fixture(projectToml: "github_owner = \"ACME\"\n", dockerfile: "");
        Assert.Equal(0, f.Run(NoTerminal));
        Assert.Equal("tok_acme", f.Seen("token-seen"));
    }

    // allow = ["*"]

    private const string StarToml = "allow = [\"*\"]\n";

    private static string CaptureStderr(Func<int> run, out int exit)
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { exit = run(); }
        finally { Console.SetError(stderr); }
        return captured.ToString();
    }

    [Fact]
    public void AProjectStarIsRefusedWithoutATerminal_SayingItOpensEgress_BesideItsOtherDomains()
    {
        using var f = new Fixture(projectToml: "allow = [\"*\", \"extra.example\"]\n", dockerfile: "");
        var e = Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Contains(TrustRequest.AnyDomainLine, e.Message);
        Assert.Contains("ANY domain for this project's sessions", e.Message);
        Assert.Contains("allow extra.example: the sandbox can reach it", e.Message);
        Assert.DoesNotContain("allow *: the sandbox can reach it", e.Message);
        f.AssertNothingReached();
    }

    [Fact]
    public void PenTrustWithoutATerminalRefusesAProjectStar_ShowingItsOpenEgressLine()
    {
        using var f = new Fixture(projectToml: StarToml, dockerfile: "");
        var command = new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded);
        var e = Assert.Throws<PenException>(() => command.Run(yes: false, list: false));
        Assert.Contains(TrustRequest.AnyDomainLine, e.Message);
        Assert.False(TrustStore.For(f.Host, f.Project).CoversDomain("*"));
    }

    [Fact]
    public void ApprovingAProjectStarAtThePromptRecordsIt_AndOpensEgress_WithoutAskingAgain()
    {
        using var f = new Fixture(projectToml: StarToml, dockerfile: "");
        var stderr = CaptureStderr(() => f.Run(Saying("y")), out var exit);
        Assert.Equal(0, exit);
        Assert.Contains(TrustRequest.AnyDomainLine, stderr);
        Assert.Contains("approve these?", stderr);
        Assert.Contains("\nallow *\n", File.ReadAllText(TrustStore.GetPath(f.Host.GetRuntimeDir(f.Project.Key), f.Project.Dir)));
        Assert.Equal(".\n", f.Seen("open-egress-seen"));
        Assert.DoesNotContain("*", f.Seen("allowlist-seen"));
        Assert.Equal(0, f.Run(NoTerminal));
    }

    [Fact]
    public void DecliningAProjectStarApprovesNothing()
    {
        using var f = new Fixture(projectToml: StarToml, dockerfile: "");
        Assert.Throws<PenException>(() => f.Run(Saying("n")));
        f.AssertNothingReached();
        Assert.False(TrustStore.For(f.Host, f.Project).CoversDomain("*"));
    }

    [Fact]
    public void AnApprovedStarDoesntApproveADomainNamedBesideIt()
    {
        using var f = new Fixture(projectToml: StarToml, dockerfile: "");
        Assert.Equal(0, f.Run(Saying("y")));
        File.Delete(f.CallsFile);
        File.WriteAllText(f.Project.ProjectConfigPath, "allow = [\"*\", \"internal.example\"]\n");
        var e = Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Contains("allow internal.example: the sandbox can reach it", e.Message);
        Assert.DoesNotContain(TrustRequest.AnyDomainLine, e.Message);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void AnApprovedStarAppliesLive_WithoutTheProjectsUnapprovedNamedDomains()
    {
        using var f = new Fixture(projectToml: "allow = [\"*\", \"internal.example\"]\n", dockerfile: "");
        var (s, l) = RunningSession(f);
        using var _ = l;
        File.WriteAllText(s.DenylistFile, "");
        File.WriteAllText(s.OpenEgressFile, "");
        TrustStore.For(f.Host, f.Project).ApproveDomains(["*"]);
        Assert.True(AllowCommand.Apply(f.Host, s, RecipeCatalog.Embedded));
        Assert.Equal(".\n", File.ReadAllText(s.OpenEgressFile));
        var list = File.ReadAllText(s.AllowlistFile);
        Assert.DoesNotContain("internal.example", list);
        Assert.DoesNotContain("*", list);
    }

    private static int RunAs(Fixture f, string userToml, Answers answers)
    {
        File.WriteAllText(f.Host.UserConfigPath, userToml); // what pen allow reads live
        var user = Config.Parse(userToml, "u", ConfigKind.User, RecipeCatalog.Embedded);
        return new Session(new ProjectContext(f.Host, f.Project, user, f.ProjectConfig), answers, RecipeCatalog.Embedded).Run(new(["true"]));
    }

    [Fact]
    public void AUsersStarDoesntLetAProjectsNamedDomainIn_ItsAskedFor()
    {
        using var f = new Fixture(projectToml: "allow = [\"internal.example\"]\n", dockerfile: "");
        // Allowed by name, it would reach private addresses, which the user's "*" doesn't.
        var e = Assert.Throws<PenException>(() => RunAs(f, StarToml, NoTerminal));
        Assert.Contains("allow internal.example: the sandbox can reach it", e.Message);
        f.AssertNothingReached();
        Assert.Throws<PenException>(() => RunAs(f, StarToml, Saying("n")));
        f.AssertNothingReached();

        var stderr = CaptureStderr(() => RunAs(f, StarToml, Saying("y")), out var exit);
        Assert.Equal(0, exit);
        Assert.Contains("allow internal.example: the sandbox can reach it", stderr);
        Assert.Contains("internal.example", f.Seen("allowlist-seen").Split('\n'));
        Assert.Equal(".\n", f.Seen("open-egress-seen"));
    }

    [Fact]
    public void AUsersStarAppliesLive_WithoutTheProjectsUnapprovedNamedDomains()
    {
        using var f = new Fixture(projectToml: "allow = [\"*\", \"internal.example\"]\n", dockerfile: "");
        File.WriteAllText(f.Host.UserConfigPath, StarToml);
        var (s, l) = RunningSession(f);
        using var _ = l;
        File.WriteAllText(s.DenylistFile, "");
        File.WriteAllText(s.OpenEgressFile, "");
        Assert.True(AllowCommand.Apply(f.Host, s, RecipeCatalog.Embedded));
        Assert.Equal(".\n", File.ReadAllText(s.OpenEgressFile));
        Assert.DoesNotContain("internal.example", File.ReadAllText(s.AllowlistFile));
    }

    [Theory]
    [InlineData("allow = [\"*\", \".corp.example\"]\n", "allow = [\"*\", \"a.corp.example\", \"api.github.com\", \"approved.example\", \"zz.example\"]\ndeny = [\"gist.github.com\"]\n")]
    [InlineData("allow = [\"*\"]\n", "allow = [\"approved.example\", \"api.github.com\"]\n")]
    [InlineData("allow = [\".corp.example\", \"u.example\"]\n", "allow = [\"a.corp.example\", \"api.github.com\", \"approved.example\", \".wild.example\"]\n")]
    [InlineData("", "allow = [\"approved.example\", \".wild.example\", \"x.wild.example\"]\n")]
    public void SessionStartAndLiveApplyGiveTheSameAclFiles(string userToml, string projectToml)
    {
        using var f = new Fixture(projectToml: projectToml, dockerfile: "");
        Session.EnsureRuntimeDir(f.Host, f.Project);
        TrustStore.For(f.Host, f.Project).ApproveDomains(["approved.example", "zz.example", ".wild.example"]);
        Assert.Equal(0, RunAs(f, userToml, NoTerminal));
        var started = (Allow: f.Seen("allowlist-seen"), Deny: f.Seen("denylist-seen"), Open: f.Seen("open-egress-seen"));
        Assert.Contains("approved.example", started.Allow.Split('\n'));

        var (s, l) = RunningSession(f);
        using var _ = l;
        File.WriteAllText(s.DenylistFile, "");
        File.WriteAllText(s.OpenEgressFile, "");
        Assert.True(AllowCommand.Apply(f.Host, s, RecipeCatalog.Embedded));
        Assert.Equal(started, (File.ReadAllText(s.AllowlistFile), File.ReadAllText(s.DenylistFile), File.ReadAllText(s.OpenEgressFile)));
    }

    [Fact]
    public void AUsersStarMeansTheProjectsStarDoesntAsk_AndApprovesNothing()
    {
        using var f = new Fixture(projectToml: StarToml, dockerfile: "");
        var user = Config.Parse("allow = [\"*\"]\n", "u", ConfigKind.User, RecipeCatalog.Embedded);
        Assert.Equal(0, new Session(new ProjectContext(f.Host, f.Project, user, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(new(["true"])));
        Assert.Equal(".\n", f.Seen("open-egress-seen"));
        Assert.False(TrustStore.For(f.Host, f.Project).CoversDomain("*"));
    }

    [Fact]
    public void TrustListShowsTheStarsWarning_AndTrustYesApprovesIt()
    {
        using var f = new Fixture(projectToml: "allow = [\"*\", \"extra.example\"]\n", dockerfile: "");
        var context = new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { Assert.Equal(0, new TrustCommand(context, NoTerminal, RecipeCatalog.Embedded).Run(yes: false, list: true)); }
        finally { Console.SetOut(stdout); }
        Assert.Contains(TrustRequest.AnyDomainLine + "\n", captured.ToString());
        Assert.Contains("allow extra.example", captured.ToString());
        Assert.False(TrustStore.For(f.Host, f.Project).CoversDomain("*"));

        var stderr = CaptureStderr(() => new TrustCommand(context, NoTerminal, RecipeCatalog.Embedded).Run(yes: true, list: false), out var exit);
        Assert.Equal(0, exit);
        Assert.Contains(TrustRequest.AnyDomainLine, stderr);
        var store = TrustStore.For(f.Host, f.Project);
        Assert.True(store.CoversDomain("*"));
        Assert.True(store.CoversDomain("extra.example"));
        Assert.Equal(0, f.Run(NoTerminal));
    }

    // pen trust

    [Fact]
    public void TrustYesApproves_WithoutATerminal()
    {
        using var f = new Fixture();
        Assert.Equal(0, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: true, list: false));
        Assert.Equal(0, f.Run(NoTerminal));
        Assert.Equal(0, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: false, list: false)); // nothing new
    }

    [Fact]
    public void TrustAsks_AndAnsweringNoApprovesNothing()
    {
        using var f = new Fixture();
        Assert.Equal(1, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), Saying("n"), RecipeCatalog.Embedded).Run(yes: false, list: false));
        Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Equal(0, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), Saying("y"), RecipeCatalog.Embedded).Run(yes: false, list: false));
        Assert.Equal(0, f.Run(NoTerminal));
    }

    [Fact]
    public void TrustWithoutATerminalOrYesRefuses()
    {
        using var f = new Fixture();
        var e = Assert.Throws<PenException>(() => new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: false, list: false));
        Assert.Contains("--yes", e.Message);
        Assert.False(File.Exists(TrustStore.GetPath(f.Host.GetRuntimeDir(f.Project.Key), f.Project.Dir)));
    }

    // pen upgrade

    [Fact]
    public void UpgradeSkipsAnUnapprovedLayer()
    {
        using var f = new Fixture();
        new UpgradeCommand(f.Host, Config.Empty, RecipeCatalog.Embedded).RebuildProjectLayer(f.Project, noCache: false, Image.Name);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void UpgradeSkipsAnUncheckableLayer()
    {
        using var f = new Fixture();
        File.CreateSymbolicLink(Path.Combine(f.Project.PenDir, "out"), "/etc/passwd");
        new UpgradeCommand(f.Host, Config.Empty, RecipeCatalog.Embedded).RebuildProjectLayer(f.Project, noCache: false, Image.Name);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void UpgradeRebuildsAnApprovedLayer_AndSkipsItOnceChanged()
    {
        using var f = new Fixture(projectToml: "");
        new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: true, list: false);
        new UpgradeCommand(f.Host, Config.Empty, RecipeCatalog.Embedded).RebuildProjectLayer(f.Project, noCache: false, Image.Name);
        Assert.Contains(f.Calls, c => c.StartsWith("build ", StringComparison.Ordinal));

        File.Delete(f.CallsFile);
        File.WriteAllText(Path.Combine(f.Project.PenDir, "Dockerfile"), "FROM pen:latest\nRUN worse\n");
        new UpgradeCommand(f.Host, Config.Empty, RecipeCatalog.Embedded).RebuildProjectLayer(f.Project, noCache: false, Image.Name);
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void UpgradeBuildsFromAPrivateCopyToo()
    {
        using var f = new Fixture(projectToml: "");
        Approve(f);
        File.WriteAllText(Path.Combine(f.T.Path, "swap-in"), "FROM pen:latest\nRUN worse\n");
        new UpgradeCommand(f.Host, Config.Empty, RecipeCatalog.Embedded).RebuildProjectLayer(f.Project, noCache: false, Image.Name);
        Assert.Equal(Approved, f.Seen("seen-context/Dockerfile"));
        Assert.StartsWith(f.Host.GetRuntimeDir(f.Project.Key) + "/", f.Seen("context-path"));
    }

    // pen allow

    /// <summary>A session running in the project, whose allowlist a live update rewrites.</summary>
    private static (SessionDir Session, FileLock Lock) RunningSession(Fixture f)
    {
        Session.EnsureRuntimeDir(f.Host, f.Project);
        var s = new SessionDir(f.Project.Key, f.T.Sub("home", ".local", "state", "pen", f.Project.Key, "sessions", "12345678"));
        File.WriteAllText(s.ComposeFile, "");
        File.WriteAllText(s.EnvFile, $"PROJECT_DIR={f.Project.Dir}\n");
        File.WriteAllText(s.GetPath("allowlist.txt"), "");
        return (s, FileLock.TryTake(s.LockFile, exclusive: true)!);
    }

    [Fact]
    public void GlobalAllowDoesntApplyAProjectsUnapprovedDomainsLive()
    {
        using var f = new Fixture(dockerfile: "");
        var (s, l) = RunningSession(f);
        using var _ = l;
        Assert.Equal(0, new AllowCommand(f.Host, RecipeCatalog.Embedded).Run(["typed.example"], global: true));
        var list = File.ReadAllText(s.GetPath("allowlist.txt"));
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
        Assert.True(AllowCommand.Apply(f.Host, s, RecipeCatalog.Embedded));
        var list = File.ReadAllText(s.GetPath("allowlist.txt"));
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
        File.WriteAllText(TrustStore.GetPath(f.Host.GetRuntimeDir(f.Project.Key), f.Project.Dir), "garbage\n");
        Assert.True(AllowCommand.Apply(f.Host, s, RecipeCatalog.Embedded));
        Assert.DoesNotContain("ok.example", File.ReadAllText(s.GetPath("allowlist.txt")));
    }

    [Fact]
    public void ADomainAddedWithPenAllowDoesntAskNextSession()
    {
        using var f = new Fixture(projectToml: "", dockerfile: "");
        var cwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = f.Project.Dir;
        try { Assert.Equal(0, new AllowCommand(f.Host, RecipeCatalog.Embedded).Run(["typed.example"], global: false)); }
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
        try { Assert.Equal(0, new AllowCommand(f.Host, RecipeCatalog.Embedded).Run(["planted.example"], global: false)); }
        finally { Environment.CurrentDirectory = cwd; }
        var store = TrustStore.For(f.Host, f.Project);
        Assert.True(store.CoversDomain("planted.example"));
        Assert.False(store.CoversDomain("other.example"));
    }

    [Fact]
    public void GlobalAllowApprovesNothingInTheProject()
    {
        using var f = new Fixture(dockerfile: "");
        Assert.Equal(0, new AllowCommand(f.Host, RecipeCatalog.Embedded).Run(["typed.example"], global: true));
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
        try { new AllowCommand(f.Host, RecipeCatalog.Embedded).ListBlocked(new ProjectContext(f.Host, f.Project, Config.Empty, f.ProjectConfig)); }
        finally { Console.SetOut(stdout); }
        Assert.Contains("evil.example", captured.ToString());
        Assert.DoesNotContain("ok.example", captured.ToString());
    }

    // Tools

    /// <summary>
    /// As <see cref="RecordingDocker"/>, with a base image to read the ID of, tools images
    /// that exist only once built, and what compose run was told to run.
    /// </summary>
    private const string ToolsDocker = """
        case "$*" in
          "image inspect --format {{.Id}} pen:latest") echo sha256:base; exit 0 ;;
          "image inspect --format {{.Id}} pen-tools:"*) [ -f tools-built ] || exit 1; exit 0 ;;
          "build --progress plain -t pen-tools:"*) : >tools-built; echo "$*" >>tools-builds ;;
        esac
        case " $* " in
          *" run --rm "*) printf '%s' "$PEN_IMAGE" >image-seen; find home -name tool-domains -exec cp {} tool-domains-seen \; ;;
        esac

        """ + RecordingDocker;

    private const string ProjectTool = "tools = [\"pen-test:1\"]\n";
    private static readonly ToolSpec PenTest = new("pen-test", "1");

    private static Fixture ToolsFixture(string projectToml = ProjectTool, string dockerfile = "") =>
        new(projectToml: projectToml, dockerfile: dockerfile, dockerBody: ToolsDocker);

    [Fact]
    public void AnUnapprovedProjectToolDoesNothingWithoutATerminal_AndTheMessageShowsItsDomains()
    {
        using var f = ToolsFixture();
        var e = Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Contains("tool pen-test:1", e.Message);
        Assert.Contains("example.org", e.Message);
        Assert.Contains("pen trust", e.Message);
        f.AssertNothingReached();
    }

    [Fact]
    public void DecliningAProjectToolDoesNothing()
    {
        using var f = ToolsFixture();
        Assert.Throws<PenException>(() => f.Run(Saying("n")));
        f.AssertNothingReached();
        Assert.False(TrustStore.For(f.Host, f.Project).CoversTool(PenTest));
    }

    [Fact]
    public void AnApprovedProjectToolBuildsRunsAndAddsItsDomains_AndDoesntAskAgain()
    {
        using var f = ToolsFixture();
        Assert.Equal(0, f.Run(Saying("y")));
        Assert.Matches("^pen-tools:[0-9a-f]{16}$", f.Seen("image-seen"));
        Assert.Contains("example.org", f.Seen("allowlist-seen").Split('\n'));
        Assert.Equal(0, f.Run(NoTerminal));
    }

    [Fact]
    public void ChangingAnApprovedToolsVersionAsksAgain_AndBuildsNothing()
    {
        using var f = ToolsFixture();
        Assert.Equal(0, f.Run(Saying("y")));
        File.Delete(f.CallsFile);
        File.WriteAllText(f.Project.ProjectConfigPath, "tools = [\"pen-test:2\"]\n");
        Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void AProjectDenyNeedsNoApproval_ReachesTheProxy_AndWarnsWhenItTakesWhatAToolNeeds()
    {
        using var f = ToolsFixture(projectToml: "deny = [\"example.org\", \"gist.github.com\"]\n");
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { Assert.Equal(0, f.Run(NoTerminal, flagTools: [PenTest])); }
        finally { Console.SetError(stderr); }
        Assert.Contains("pen: deny covers example.org, which pen-test needs\n", captured.ToString());
        Assert.DoesNotContain("egress is open", captured.ToString());
        Assert.Equal("example.org\ngist.github.com\n", f.Seen("denylist-seen"));
        Assert.Equal("", f.Seen("open-egress-seen"));
        // Deny beats allow in squid; the allowlist still holds what allow merged.
        Assert.Contains("example.org", f.Seen("allowlist-seen").Split('\n'));
    }

    [Fact]
    public void AToolFromAFlagDoesntAsk_AddsItsDomains_AndDoesntApproveTheProjectsEntry()
    {
        using var f = ToolsFixture();
        Assert.Equal(0, f.Run(NoTerminal, flagTools: [PenTest]));
        Assert.Matches("^pen-tools:[0-9a-f]{16}$", f.Seen("image-seen"));
        Assert.Contains("example.org", f.Seen("allowlist-seen").Split('\n'));
        Assert.False(TrustStore.For(f.Host, f.Project).CoversTool(PenTest));

        File.Delete(f.CallsFile);
        Assert.Throws<PenException>(() => f.Run(NoTerminal));
        Assert.Empty(f.Calls);
    }

    [Fact]
    public void AToolFromUserConfigDoesntAsk_AndDoesntApproveTheProjectsEntry()
    {
        using var f = ToolsFixture();
        var user = Config.Parse(ProjectTool, "u", ConfigKind.User, RecipeCatalog.Embedded);
        Assert.Equal(0, new Session(new ProjectContext(f.Host, f.Project, user, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(new(["true"])));
        Assert.Contains("example.org", f.Seen("allowlist-seen").Split('\n'));
        Assert.False(TrustStore.For(f.Host, f.Project).CoversTool(PenTest));
    }

    [Fact]
    public void AnUnapprovedToolDoesntSlipInBesideAFlag()
    {
        using var f = ToolsFixture(projectToml: "tools = [\"pen-test-b:1\"]\n");
        var e = Assert.Throws<PenException>(() => f.Run(NoTerminal, flagTools: [PenTest]));
        Assert.Contains("tool pen-test-b:1", e.Message);
        f.AssertNothingReached();
    }

    [Fact]
    public void ToolsThatCantGoTogetherStopTheSessionBeforeDocker()
    {
        using var f = ToolsFixture();
        var e = Assert.Throws<PenException>(() => f.Run(Saying("y"), flagTools: [new("pen-test", "2")]));
        Assert.Contains("can't be used together", e.Message);
        f.AssertNothingReached();
    }

    [Fact]
    public void AProjectLayerStacksOnTheSessionsTools_AndTheSessionRunsIt()
    {
        using var f = ToolsFixture(dockerfile: "FROM pen:latest\n");
        Assert.Equal(0, f.Run(Saying("y")));
        var tools = Assert.Single(File.ReadAllLines(Path.Combine(f.T.Path, "tools-builds")));
        var tag = tools.Split(' ')[4]["pen-tools:".Length..];
        Assert.Equal($"{f.Project.Key}:{tag}", f.Seen("image-seen"));
        Assert.Contains(f.Calls, c => c.Contains($"--build-context pen:latest=docker-image://pen-tools:{tag}"));
    }

    // The embedded pen-test recipe's name, as a visible agent that brings example.org.
    private static readonly RecipeCatalog AgentCatalog = RecipeCatalog.FromFiles(new Dictionary<string, string>
    {
        ["tools/pen-test/tool.toml"] = "default = \"1\"\nversion = '^[12]$'\nkind = \"agent\"\nallow = [\"example.org\"]\n",
        ["tools/pen-test/install.sh"] = "#!/bin/sh\n",
    });

    [Fact]
    public void ACommandNamingAnAgentImpliesItWithoutAsking_AndItsDomainsArrive()
    {
        using var f = ToolsFixture(projectToml: "");
        Assert.Equal(0, f.Run(NoTerminal, recipes: AgentCatalog, command: ["pen-test"]));
        Assert.Matches("^pen-tools:[0-9a-f]{16}$", f.Seen("image-seen"));
        Assert.Contains("example.org", f.Seen("allowlist-seen").Split('\n'));
        Assert.Equal("example.org\n", f.Seen("tool-domains-seen"));
        Assert.False(TrustStore.For(f.Host, f.Project).CoversTool(PenTest));
    }

    [Theory]
    [InlineData("pen-test:1")]
    [InlineData("pen-test:2")]
    public void AProjectEntryForTheImpliedAgentStillNeedsApproval(string entry)
    {
        using var f = ToolsFixture(projectToml: $"tools = [\"{entry}\"]\n");
        var e = Assert.Throws<PenException>(() => f.Run(NoTerminal, recipes: AgentCatalog, command: ["pen-test"]));
        Assert.Contains($"tool {entry}", e.Message);
        f.AssertNothingReached();
    }

    [Fact]
    public void ACommandThatIsntAnAgentImpliesNothing()
    {
        using var f = ToolsFixture(projectToml: "");
        Assert.Equal(0, f.Run(NoTerminal, recipes: AgentCatalog, command: ["bash"]));
        Assert.DoesNotContain("example.org", f.Seen("allowlist-seen").Split('\n'));
        Assert.Equal("pen:latest", f.Seen("image-seen"));
    }

    [Fact]
    public void ALiveUpdateKeepsARecordedToolsDomains()
    {
        using var f = new Fixture(projectToml: "", dockerfile: "");
        var (s, l) = RunningSession(f);
        using var _ = l;
        s.WriteTools([PenTest], ["example.org"]);
        Assert.Equal(0, new AllowCommand(f.Host, RecipeCatalog.Embedded).Run(["typed.example"], global: true));
        var list = File.ReadAllText(s.GetPath("allowlist.txt")).Split('\n');
        Assert.Contains("typed.example", list);
        Assert.Contains("example.org", list);
    }

    [Fact]
    public void ALiveUpdateAddsNoDomainsFromAToolPlantedInTheProject_EvenAnApprovedOne()
    {
        using var f = ToolsFixture();
        var (s, l) = RunningSession(f);
        using var _ = l;
        Assert.True(AllowCommand.Apply(f.Host, s, RecipeCatalog.Embedded));
        Assert.DoesNotContain("example.org", File.ReadAllText(s.GetPath("allowlist.txt")));

        // Approved after this session started: it's for sessions that start with the tool.
        new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: true, list: false);
        Assert.True(AllowCommand.Apply(f.Host, s, RecipeCatalog.Embedded));
        Assert.DoesNotContain("example.org", File.ReadAllText(s.GetPath("allowlist.txt")));
    }

    [Fact]
    public void TrustYesApprovesAToolAndTrustListDoesnt()
    {
        using var f = ToolsFixture();
        Assert.Equal(0, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: false, list: true));
        Assert.False(TrustStore.For(f.Host, f.Project).CoversTool(PenTest));
        Assert.Equal(1, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), Saying("n"), RecipeCatalog.Embedded).Run(yes: false, list: false));
        Assert.False(TrustStore.For(f.Host, f.Project).CoversTool(PenTest));
        Assert.Equal(0, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: true, list: false));
        Assert.True(TrustStore.For(f.Host, f.Project).CoversTool(PenTest));
    }

    [Fact]
    public void AllowListingCountsToolsOnlyWhenApproved()
    {
        using var f = ToolsFixture();
        var (s, l) = RunningSession(f);
        using var _ = l;
        File.WriteAllText(s.ProxyLog, "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT example.org:443 -\n");
        string Listed()
        {
            var stdout = Console.Out;
            var captured = new StringWriter();
            Console.SetOut(captured);
            try { new AllowCommand(f.Host, RecipeCatalog.Embedded).ListBlocked(new ProjectContext(f.Host, f.Project, Config.Empty, f.ProjectConfig)); }
            finally { Console.SetOut(stdout); }
            return captured.ToString();
        }
        Assert.Contains("example.org", Listed());
        new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, RecipeCatalog.Embedded).Run(yes: true, list: false);
        Assert.DoesNotContain("example.org", Listed());
    }

    // pen upgrade's tools

    [Fact]
    public void UpgradeTakesTheUsersAndApprovedProjectTools_AndSkipsTheRest()
    {
        using var f = ToolsFixture();
        Session.EnsureRuntimeDir(f.Host, f.Project);
        var user = Config.Parse("tools = [\"pen-test-b:1\"]\n", "u", ConfigKind.User, RecipeCatalog.Embedded);
        ToolSpec userTool = new("pen-test-b", "1");
        Assert.Equal([userTool], new UpgradeCommand(f.Host, user, RecipeCatalog.Embedded).SelectTools(f.Project));
        Assert.Equal([userTool], new UpgradeCommand(f.Host, user, RecipeCatalog.Embedded).SelectTools(null));

        TrustStore.For(f.Host, f.Project).Approve(new TrustRequest([], false, null, null, null) { Tools = [new(PenTest, true, ["example.org"])] });
        Assert.Equal([userTool, PenTest], new UpgradeCommand(f.Host, user, RecipeCatalog.Embedded).SelectTools(f.Project));
        Assert.Empty(f.Calls); // nothing's built from an unapproved entry on the way
    }

    [Fact]
    public void UpgradeStillRunsWhenTheProjectsToolsCantBeUsed()
    {
        using var f = ToolsFixture();
        Session.EnsureRuntimeDir(f.Host, f.Project);
        var user = Config.Parse("tools = [\"pen-test:2\"]\n", "u", ConfigKind.User, RecipeCatalog.Embedded);
        TrustStore.For(f.Host, f.Project).Approve(new TrustRequest([], false, null, null, null) { Tools = [new(PenTest, true, ["example.org"])] });
        Assert.Equal([new ToolSpec("pen-test", "2")], new UpgradeCommand(f.Host, user, RecipeCatalog.Embedded).SelectTools(f.Project));

        File.WriteAllText(f.Project.ProjectConfigPath, "nonsense = 1\n");
        Assert.Equal([new ToolSpec("pen-test", "2")], new UpgradeCommand(f.Host, user, RecipeCatalog.Embedded).SelectTools(f.Project));
    }

    [Fact]
    public void UpgradeStacksAnApprovedLayerOnTheToolsItBuilt()
    {
        using var f = ToolsFixture(projectToml: "", dockerfile: "FROM pen:latest\n");
        Approve(f);
        new UpgradeCommand(f.Host, Config.Empty, RecipeCatalog.Embedded).RebuildProjectLayer(f.Project, noCache: true, baseImage: "pen-tools:0123456789abcdef");
        var build = Assert.Single(f.Calls, c => c.StartsWith("build ", StringComparison.Ordinal));
        Assert.Contains($"-t {f.Project.Key}:0123456789abcdef", build);
        Assert.Contains("--no-cache", build);
        Assert.Contains("--build-context pen:latest=docker-image://pen-tools:0123456789abcdef", build);
    }

    /// <summary>pen-test as a later Pen's recipe might have it.</summary>
    private static RecipeCatalog Recipe(params string[] domains) => RecipeCatalog.FromFiles(new Dictionary<string, string>
    {
        ["tools/pen-test/tool.toml"] = $"default = \"1\"\nversion = '^\\d+$'\nallow = [{string.Join(", ", domains.Select(d => $"\"{d}\""))}]\n",
        ["tools/pen-test/install.sh"] = "#!/bin/sh\n",
    });

    // Approve a tool, then a Pen whose recipe brings one more domain, with no terminal to ask on.
    [Fact]
    public void ADomainARecipeAddsAfterApprovalIsRefusedWithoutATerminal_AndNothingOfTheEntryTakesEffect()
    {
        using var f = ToolsFixture(projectToml: "tools = [\"pen-test:77\"]\n");
        Assert.Equal(0, new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, Recipe("example.org")).Run(yes: true, list: false));
        Assert.Equal(0, f.Run(NoTerminal, recipes: Recipe("example.org")));
        Assert.DoesNotContain("example.net", f.Seen("allowlist-seen"));

        File.Delete(f.CallsFile);
        File.Delete(Path.Combine(f.T.Path, "allowlist-seen"));
        File.Delete(Path.Combine(f.T.Path, "tools-built"));
        var later = Recipe("example.org", "example.net");
        var e = Assert.Throws<PenException>(() => f.Run(NoTerminal, recipes: later));
        Assert.Contains("pen-test:77 now brings more domains", e.Message);
        Assert.Contains("example.net", e.Message);
        Assert.Empty(f.Calls);
        Assert.False(f.HasSeen("allowlist-seen"));

        Assert.Throws<PenException>(() => f.Run(Saying("n"), recipes: later));
        Assert.Empty(f.Calls);

        Assert.Equal(0, f.Run(Saying("y"), recipes: later));
        Assert.Contains("example.net", f.Seen("allowlist-seen").Split('\n'));
        Assert.Equal(0, f.Run(NoTerminal, recipes: later));
    }

    [Fact]
    public void AnAllowDomainPlantedInTheProjectDoesntApproveTheToolsNewDomain()
    {
        using var f = ToolsFixture(projectToml: "tools = [\"pen-test:77\"]\n");
        new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, Recipe("example.org")).Run(yes: true, list: false);
        TrustStore.For(f.Host, f.Project).ApproveDomains(["example.net"]);
        File.WriteAllText(f.Project.ProjectConfigPath, "tools = [\"pen-test:77\"]\nallow = [\"example.net\"]\n");
        Assert.Contains("now brings more domains", Assert.Throws<PenException>(() => f.Run(NoTerminal, recipes: Recipe("example.org", "example.net"))).Message);
    }

    [Fact]
    public void ADroppedDomainDoesntAsk_AndLeavesTheAllowlist()
    {
        using var f = ToolsFixture(projectToml: "tools = [\"pen-test:77\"]\n");
        new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, Recipe("example.org", "example.net")).Run(yes: true, list: false);
        Assert.Equal(0, f.Run(NoTerminal, recipes: Recipe("example.org")));
        Assert.DoesNotContain("example.net", f.Seen("allowlist-seen"));
    }

    [Fact]
    public void AFlagForTheToolStillDoesntAskOrApprove_EvenWithANewDomain()
    {
        using var f = ToolsFixture(projectToml: "tools = [\"pen-test:77\"]\n");
        new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, Recipe("example.org")).Run(yes: true, list: false);
        var later = Recipe("example.org", "example.net");
        Assert.Equal(0, f.Run(NoTerminal, flagTools: [new("pen-test", "77")], recipes: later));
        Assert.False(TrustStore.For(f.Host, f.Project).CoversToolDomain(new("pen-test", "77"), "example.net"));
        Assert.Throws<PenException>(() => f.Run(NoTerminal, recipes: later));
    }

    [Fact]
    public void UpgradeAndTheAllowListingSkipAToolWithAnUnapprovedDomain()
    {
        using var f = ToolsFixture(projectToml: "tools = [\"pen-test:77\"]\n");
        new TrustCommand(new ProjectContext(f.Host, f.Project, f.User, f.ProjectConfig), NoTerminal, Recipe("example.org")).Run(yes: true, list: false);
        ToolSpec tool = new("pen-test", "77");
        Assert.Equal([tool], new UpgradeCommand(f.Host, Config.Empty, Recipe("example.org")).SelectTools(f.Project));
        Assert.Empty(new UpgradeCommand(f.Host, Config.Empty, Recipe("example.org", "example.net")).SelectTools(f.Project));
        Assert.Equal([tool], TrustStore.For(f.Host, f.Project).SelectApprovedTools([tool], Recipe("example.org")));
        Assert.Empty(TrustStore.For(f.Host, f.Project).SelectApprovedTools([tool], Recipe("example.org", "example.net")));
    }
}
