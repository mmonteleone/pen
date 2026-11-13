namespace Pen.Tests;

/// <summary>What .pen/ asks for, and the store that records what's been approved.</summary>
[Collection(nameof(LockAndProcessTests))] // they start many processes, which briefly hold others' flocks through fork
public class TrustTests
{
    private const string Hex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed class Fixture : IDisposable
    {
        public readonly TempDir T = new();
        public readonly HostEnv Host;
        public readonly Project Project;

        public Fixture(string? origin = "https://github.com/Acme/repo.git")
        {
            Host = T.Host();
            var repo = Git.Repo(T.Sub("repo"));
            if (origin != null) Git.Run(repo, "remote", "add", "origin", origin);
            Project = Project.Resolve(repo, Host);
            Session.EnsureRuntimeDir(Host, Project);
        }

        public string RuntimeDir => Host.GetRuntimeDir(Project.Key);
        public string StorePath => TrustStore.GetPath(RuntimeDir, Project.Dir);
        public TrustStore Store => TrustStore.For(Host, Project);

        public TrustRequest Requested(string projectToml, string userToml = "") =>
            new TrustGate(new ProjectContext(Host, Project, Config.Parse(userToml, "u", ConfigKind.User), Config.Parse(projectToml, "p", ConfigKind.Project)), new Answers(TextReader.Null, IsTerminal: false), RecipeCatalog.Embedded).FindUnapproved(Store, []);

        public void Dockerfile(string text = "FROM pen:latest\n")
        {
            Directory.CreateDirectory(Project.PenDir);
            File.WriteAllText(Path.Combine(Project.PenDir, "Dockerfile"), text);
        }

        public void Dispose() => T.Dispose();
    }

    [Fact]
    public void AnEmptyProjectAsksForNothing()
    {
        using var f = new Fixture();
        Assert.True(f.Requested("").IsEmpty);
    }

    [Fact]
    public void PortsAreNotAskedAbout()
    {
        using var f = new Fixture();
        Assert.True(f.Requested("ports = [3000]\n").IsEmpty);
    }

    [Fact]
    public void ADomainIsAskedAboutUnlessAlreadyAllowed()
    {
        using var f = new Fixture();
        Assert.Equal(["new.example"], f.Requested("allow = [\"new.example\"]\n").Domains);
        Assert.Empty(f.Requested("allow = [\"new.example\"]\n", "allow = [\"new.example\"]\n").Domains);
        Assert.Empty(f.Requested("allow = [\"a.new.example\"]\n", "allow = [\".new.example\"]\n").Domains);
        Assert.Empty(f.Requested("allow = [\"api.github.com\"]\n").Domains); // a default
        Assert.Empty(f.Requested("allow = [\"x.stackexchange.com\"]\n").Domains); // the defaults have .stackexchange.com
        Assert.Equal([".new.example"], f.Requested("allow = [\".new.example\"]\n", "allow = [\"new.example\"]\n").Domains);
    }

    [Fact]
    public void AnApprovedDomainIsntAskedAgain_ButAWiderOneIs()
    {
        using var f = new Fixture();
        f.Store.Approve(f.Requested("allow = [\"new.example\", \".wild.example\"]\n"));
        Assert.True(f.Requested("allow = [\"new.example\"]\n").IsEmpty);
        Assert.True(f.Requested("allow = [\"a.wild.example\"]\n").IsEmpty);
        Assert.Equal([".new.example"], f.Requested("allow = [\".new.example\"]\n").Domains);
        Assert.Equal(["other.example"], f.Requested("allow = [\"new.example\", \"other.example\"]\n").Domains);
    }

    [Fact]
    public void DockerIsAskedAboutOnlyWhenOn_AndOnce()
    {
        using var f = new Fixture();
        Assert.False(f.Requested("docker = false\n").Docker);
        Assert.True(f.Requested("docker = true\n").Docker);
        f.Store.Approve(f.Requested("docker = true\n"));
        Assert.True(f.Requested("docker = true\n").IsEmpty);
    }

    [Fact]
    public void AnOwnerIsAskedAboutWhenItDiffersFromOrigin_IgnoringCase()
    {
        using var f = new Fixture();
        Assert.True(f.Requested("github_owner = \"ACME\"\n").IsEmpty);
        var r = f.Requested("github_owner = \"Other\"\n");
        Assert.Equal("Other", r.GitHubOwner);
        Assert.Equal("Acme", r.OriginOwner);
        f.Store.Approve(r);
        Assert.True(f.Requested("github_owner = \"OTHER\"\n").IsEmpty);
        Assert.Equal("third", f.Requested("github_owner = \"third\"\n").GitHubOwner);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://gitlab.com/acme/repo.git")]
    public void AnOwnerIsAskedAboutWhenOriginNamesNone(string? origin)
    {
        using var f = new Fixture(origin);
        Assert.Equal("acme", f.Requested("github_owner = \"acme\"\n").GitHubOwner);
    }

    [Fact]
    public void AnOwnerCountsAsDifferingWhenTheGitConfigCantBeRead()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads everything");
        using var f = new Fixture();
        var config = Path.Combine(f.Project.CommonGitDir!, "config");
        File.SetUnixFileMode(config, UnixFileMode.None);
        try { Assert.Equal("acme", f.Requested("github_owner = \"acme\"\n").GitHubOwner); }
        finally { File.SetUnixFileMode(config, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
    }

    [Fact]
    public void ALayerIsAskedAboutWhenNewOrChanged_ButNotForAConfigEdit()
    {
        using var f = new Fixture();
        Assert.Null(f.Requested("").Layer);

        File.WriteAllText(Directory.CreateDirectory(f.Project.PenDir).FullName + "/setup.sh", "true\n");
        Assert.Null(f.Requested("").Layer); // no Dockerfile: nothing builds from it

        f.Dockerfile();
        var first = f.Requested("").Layer;
        Assert.NotNull(first?.Hash);
        f.Store.Approve(f.Requested(""));
        Assert.True(f.Requested("").IsEmpty);

        File.WriteAllText(f.Project.ProjectConfigPath, "# comment\n");
        Assert.True(f.Requested("").IsEmpty);

        f.Dockerfile("FROM pen:latest\nRUN evil\n");
        var changed = f.Requested("").Layer;
        Assert.NotNull(changed?.Hash);
        Assert.NotEqual(first!.Hash, changed!.Hash);

        // Back to the approved Dockerfile, but with a changed file COPY could read.
        f.Dockerfile();
        Assert.True(f.Requested("").IsEmpty);
        File.WriteAllText(Path.Combine(f.Project.PenDir, "setup.sh"), "evil\n");
        Assert.NotNull(f.Requested("").Layer);
    }

    [Fact]
    public void OnlyTheLastApprovedLayerCounts()
    {
        using var f = new Fixture();
        f.Dockerfile("FROM pen:latest\n");
        f.Store.Approve(f.Requested(""));
        f.Dockerfile("FROM pen:latest\nRUN true\n");
        f.Store.Approve(f.Requested(""));
        f.Dockerfile("FROM pen:latest\n");
        Assert.NotNull(f.Requested("").Layer);
    }

    [Fact]
    public void AHostileContextCantBeApproved()
    {
        using var f = new Fixture();
        f.Dockerfile();
        File.CreateSymbolicLink(Path.Combine(f.Project.PenDir, "loop"), "loop");
        var r = f.Requested("docker = true\n");
        Assert.NotNull(r.Layer?.Problem);
        Assert.Null(r.Layer!.Hash);
        Assert.Throws<PenException>(() => f.Store.Approve(r));
        Assert.False(File.Exists(f.StorePath));
    }

    [Fact]
    public void ADanglingDockerfileSymlinkIsStillADockerfile()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.Project.PenDir);
        File.CreateSymbolicLink(Path.Combine(f.Project.PenDir, "Dockerfile"), "/nonexistent");
        Assert.NotNull(f.Requested("").Layer?.Problem);
    }

    [Fact]
    public void RemovingOrCommentingOutApprovedThingsAsksForNothing()
    {
        using var f = new Fixture();
        f.Dockerfile();
        f.Store.Approve(f.Requested("allow = [\"a.example\"]\ndocker = true\ngithub_owner = \"other\"\n"));
        Assert.True(f.Requested("# allow = [\"a.example\"]\n").IsEmpty);
        Assert.True(f.Requested("allow = []\n# docker = true\n").IsEmpty);
        Assert.True(f.Requested("").IsEmpty);
        File.Delete(Path.Combine(f.Project.PenDir, "Dockerfile"));
        Assert.True(f.Requested("").IsEmpty);
    }

    [Fact]
    public void TheStoreIsPerDirectory_SoAWorktreeStartsWithNone()
    {
        using var f = new Fixture();
        f.Store.Approve(f.Requested("docker = true\n"));
        var wt = Path.Combine(f.Project.Dir, "wt");
        Git.Run(f.Project.Dir, "worktree", "add", "-q", wt, "-b", "wt");
        var worktree = Project.Resolve(wt, f.Host);
        Assert.Equal(f.Project.Key, worktree.Key);
        Assert.False(TrustStore.For(f.Host, worktree).Docker);
        Assert.True(f.Store.Docker);
    }

    [Fact]
    public void AFileForAnotherDirectoryApprovesNothing_ForASharedShortHash()
    {
        using var f = new Fixture();
        f.Store.Approve(f.Requested("docker = true\n"));
        Assert.True(f.Store.Docker);
        var colliding = TrustStore.Load(f.RuntimeDir, f.Project.Dir);
        File.WriteAllText(f.StorePath, File.ReadAllText(f.StorePath).Replace("dir " + f.Project.Dir, "dir /elsewhere"));
        Assert.False(TrustStore.Load(f.RuntimeDir, f.Project.Dir).Docker);
        Assert.True(colliding.Docker); // read before: a loaded store doesn't change
    }

    [Theory]
    [InlineData("evil.example\ndocker")]
    [InlineData("evil.example\n")]
    [InlineData("evil.example\nlayer " + Hex)]
    [InlineData("evil.example\r\ndocker")]
    [InlineData("not a domain")]
    [InlineData("")]
    public void AValueThatIsntADomainIsRefused_AndForgesNothing(string hostile)
    {
        using var f = new Fixture();
        f.Store.ApproveDomains(["fine.example"]);
        var before = File.ReadAllText(f.StorePath);
        Assert.Throws<PenException>(() => f.Store.ApproveDomains([hostile]));
        Assert.Equal(before, File.ReadAllText(f.StorePath));
        Assert.False(f.Store.Docker);
        Assert.Null(f.Store.LayerHash);
        Assert.False(f.Store.CoversDomain("evil.example"));
    }

    [Fact]
    public void AnOwnerOrHashThatIsntOneIsRefused()
    {
        using var f = new Fixture();
        var empty = new List<string>();
        Assert.Throws<PenException>(() => f.Store.Approve(new TrustRequest(empty, false, "evil\ndocker", null, null)));
        Assert.Throws<PenException>(() => f.Store.Approve(new TrustRequest(empty, false, "evil\n", null, null)));
        Assert.Throws<PenException>(() => f.Store.Approve(new TrustRequest(empty, false, null, null, new($"{Hex}\ndocker", null))));
        Assert.False(File.Exists(f.StorePath));
    }

    [Theory]
    [InlineData("allow = [\"evil.example\\n\"]\n")]
    [InlineData("allow = [\"evil.example\\ndocker\"]\n")]
    [InlineData("github_owner = \"evil\\n\"\n")]
    public void ConfigRefusesANewlineInAValue_EvenATrailingOne(string toml) =>
        Assert.Throws<PenException>(() => Config.Parse(toml, "p", ConfigKind.Project));

    public static TheoryData<string> Corrupt => new()
    {
        "",
        "garbage\n",
        "pen-trust 1\n",
        "pen-trust 2\ndir {dir}\ndocker\n",
        "pen-trust 1\ndir {dir}\ndocker", // truncated
        "pen-trust 1\ndir {dir}\nunknown\ndocker\n",
        "pen-trust 1\ndir {dir}\ndocker extra\n",
        "pen-trust 1\ndir {dir}\ndocker\n\n",
        "pen-trust 1\ndir {dir}\ndocker\r\n",
        "pen-trust 1\ndir {dir}\nallow not a domain\ndocker\n",
        "pen-trust 1\ndir {dir}\nallow EVIL.example\ndocker\n",
        "pen-trust 1\ndir {dir}\ngithub_owner Upper\ndocker\n",
        "pen-trust 1\ndir {dir}\ngithub_owner -bad\ndocker\n",
        "pen-trust 1\ndir {dir}\nlayer abc\ndocker\n",
        $"pen-trust 1\ndir {{dir}}\nlayer {Hex.ToUpperInvariant()}\ndocker\n",
        $"pen-trust 1\ndir {{dir}}\nlayer {Hex}\nlayer {Hex}\ndocker\n",
    };

    private static void AssertNothingApproved(TrustStore store)
    {
        Assert.False(store.Docker);
        Assert.Null(store.LayerHash);
        Assert.False(store.CoversDomain("fine.example"));
        Assert.False(store.CoversOwner("other"));
    }

    [Theory]
    [MemberData(nameof(Corrupt))]
    public void ACorruptStoreApprovesNothing(string content)
    {
        using var f = new Fixture();
        f.Store.Approve(f.Requested("allow = [\"fine.example\"]\ndocker = true\ngithub_owner = \"other\"\n"));
        File.WriteAllText(f.StorePath, content.Replace("{dir}", f.Project.Dir));
        AssertNothingApproved(f.Store);
    }

    [Fact]
    public void AStoreThatIsntAReadableFileApprovesNothing_WithoutHanging()
    {
        using var f = new Fixture();
        var cases = new Action[]
        {
            () => File.WriteAllBytes(f.StorePath, [0xff, 0xfe, 0x00, 0x80, (byte)'\n']),
            () => { using var s = File.Create(f.StorePath); s.SetLength(1L << 40); }, // sparse, over the size limit
            () => { File.Delete(f.StorePath); MkFifo(f.StorePath); },
            () => { File.Delete(f.StorePath); File.CreateSymbolicLink(f.StorePath, f.StorePath); },
            () => { File.Delete(f.StorePath); Directory.CreateDirectory(f.StorePath); },
        };
        foreach (var plant in cases)
        {
            plant();
            AssertNothingApproved(Within10s(() => TrustStore.Load(f.RuntimeDir, f.Project.Dir)));
        }
    }

    [Fact]
    public void AnUnreadableStoreApprovesNothing()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads everything");
        using var f = new Fixture();
        f.Store.ApproveDomains(["fine.example"]);
        File.SetUnixFileMode(f.StorePath, UnixFileMode.None);
        AssertNothingApproved(f.Store);
    }

    /// <summary>Fails instead of hanging the suite if reading the store blocks.</summary>
    private static T Within10s<T>(Func<T> read)
    {
        var task = Task.Factory.StartNew(read, TaskCreationOptions.LongRunning);
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "reading the store hung");
        return task.Result;
    }

    private static void MkFifo(string path)
    {
        var r = System.Diagnostics.Process.RunAndCaptureText(new System.Diagnostics.ProcessStartInfo("mkfifo", [path]) { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, r.ExitStatus.ExitCode);
    }

    [Fact]
    public void ApprovingOverACorruptStoreStartsAgain()
    {
        using var f = new Fixture();
        f.Store.Approve(f.Requested("docker = true\n"));
        File.WriteAllText(f.StorePath, "garbage\n");
        f.Store.ApproveDomains(["fine.example"]);
        Assert.False(f.Store.Docker);
        Assert.True(f.Store.CoversDomain("fine.example"));
    }

    [Fact]
    public void WritingReplacesTheFileByRename_LeavingOnlyAPrivateStore()
    {
        using var f = new Fixture();
        f.Store.ApproveDomains(["a.example"]);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(f.StorePath));

        // A reader holding the old file still sees it whole.
        using var old = new FileStream(f.StorePath, FileMode.Open, FileAccess.Read);
        var before = File.ReadAllText(f.StorePath);
        f.Store.ApproveDomains(["b.example"]);
        using (var reader = new StreamReader(old))
            Assert.Equal(before, reader.ReadToEnd());

        Assert.True(f.Store.CoversDomain("a.example"));
        Assert.True(f.Store.CoversDomain("b.example"));
        Assert.Empty(Directory.GetFiles(f.RuntimeDir, "*.tmp"));
    }

    [Fact]
    public void AFailedWriteLeavesTheStoreAsItWas()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root writes everywhere");
        using var f = new Fixture();
        f.Store.ApproveDomains(["a.example"]);
        var before = File.ReadAllText(f.StorePath);
        var mode = File.GetUnixFileMode(f.RuntimeDir);
        File.SetUnixFileMode(f.RuntimeDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var e = Record.Exception(() => f.Store.ApproveDomains(["b.example"]));
            Assert.True(e is IOException or UnauthorizedAccessException);
        }
        finally { File.SetUnixFileMode(f.RuntimeDir, mode); }
        Assert.Equal(before, File.ReadAllText(f.StorePath));
    }

    [Fact]
    public void ApprovingAddsToWhatAnotherPenRecordedSince()
    {
        using var f = new Fixture();
        var stale = f.Store;
        f.Store.ApproveDomains(["other.example"]);
        stale.ApproveDomains(["mine.example"]);
        Assert.True(f.Store.CoversDomain("other.example"));
        Assert.True(f.Store.CoversDomain("mine.example"));
    }

    [Fact]
    public void ApprovedOfKeepsOnlyApprovedDomains()
    {
        using var f = new Fixture();
        f.Store.ApproveDomains(["a.example", ".wild.example"]);
        Assert.Equal(["a.example", "x.wild.example"], f.Store.SelectApprovedDomains(["a.example", "b.example", "x.wild.example"]));
    }

    [Fact]
    public void TheListEscapesWhatItShows()
    {
        var r = new TrustRequest(["a.example\x1b[2J\nb"], true, "own\x1b]er", "ori\ngin", new(Hex, null));
        var text = string.Join('\n', r.FormatLines());
        Assert.DoesNotContain('\x1b', text);
        Assert.Equal(4, r.FormatLines().Count());
        Assert.Equal(4, text.Split('\n').Length);
        Assert.Contains(@"\x1b", text);
    }

    [Fact]
    public void EachItemSaysWhatItGrants()
    {
        var text = string.Join('\n', new TrustRequest(["a.example"], true, "other", "acme", new(Hex, null)).FormatLines());
        Assert.Contains("privileged", text);
        Assert.Contains("past the proxy", text);
        Assert.Contains("pen trust shows it", text);
        Assert.Contains("everything in .pen/", text);
        Assert.Contains("other", text);
        Assert.Contains("GitHub token", text);
        Assert.Contains("origin's owner is acme", text);
        Assert.Contains("can reach", text);
    }

    private static List<string> ManyDomains(int n) => Enumerable.Range(0, n).Select(i => $"d{i}.example").ToList();

    [Fact]
    public void AFloodOfDomainsIsCapped_AndCantPushOtherItemsOutOfSight()
    {
        var r = new TrustRequest(ManyDomains(30_000), true, "other", "acme", new(Hex, null));
        var lines = r.FormatLines().ToList();
        Assert.Equal(3 + TrustRequest.MaxListedDomains + 1, lines.Count);
        Assert.Contains(".pen/Dockerfile", lines[0]);
        Assert.Contains("docker = true", lines[1]);
        Assert.Contains("github_owner", lines[2]);
        Assert.Contains("29980 more domains", lines[^1]);
        Assert.Contains("pen trust --list", lines[^1]);
        Assert.Contains("29980 domains not listed", r.Question);

        Assert.Equal(3 + 30_000, r.FormatAllLines().Count());
        Assert.Equal("approve these?", new TrustRequest(ManyDomains(20), false, null, null, null).Question);
        Assert.Equal(20, new TrustRequest(ManyDomains(20), false, null, null, null).FormatLines().Count());
    }

    [Fact]
    public void AFloodOfDomainsTakesTimeInProportion()
    {
        using var f = new Fixture();
        var toml = Config.Parse("allow = [" + string.Join(',', ManyDomains(30_000).Select(d => $"\"{d}\"")) + "]\n", "p", ConfigKind.Project);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var request = new TrustGate(new ProjectContext(f.Host, f.Project, Config.Empty, toml), new Answers(TextReader.Null, IsTerminal: false), RecipeCatalog.Embedded).FindUnapproved(f.Store, []);
        Assert.Equal(30_000, request.Domains.Count);
        f.Store.Approve(request);
        var store = f.Store;
        Assert.True(new TrustGate(new ProjectContext(f.Host, f.Project, Config.Empty, toml), new Answers(TextReader.Null, IsTerminal: false), RecipeCatalog.Embedded).FindUnapproved(store, []).IsEmpty);
        Assert.Equal(30_000, store.SelectApprovedDomains(toml.Allow).Count);
        Assert.Equal(30_000 + 1, Allowlist.Merge(toml.Allow, [".wild.example"]).Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"took {watch.Elapsed}");
    }

    [Fact]
    public void AHugeProxyLogTakesTimeInProportion_KeepingFirstSeenOrder()
    {
        const int hosts = 300_000; // a quadratic scan takes minutes on this many
        var lines = Enumerable.Range(0, hosts).Select(i => $"2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT h{i}.example:443 -");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var blocked = Allowlist.BlockedHosts(lines.Concat(lines));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        Assert.Equal(hosts, blocked.Count);
        Assert.Equal("h0.example", blocked[0]);
        Assert.Equal("h299999.example", blocked[^1]);
    }

    [Theory]
    [InlineData(".x.com", "x.com", true)]
    [InlineData(".x.com", ".x.com", true)]
    [InlineData(".x.com", "a.b.x.com", true)]
    [InlineData(".x.com", ".a.x.com", true)]
    [InlineData(".x.com", "badx.com", false)]
    [InlineData("x.com", ".x.com", false)]
    [InlineData("x.com", "a.x.com", false)]
    [InlineData("a.x.com", "x.com", false)]
    public void CoverageByADomainOrWildcard(string entry, string host, bool allowed) =>
        Assert.Equal(allowed, Allowlist.IsAllowed([entry], host));

    [Fact]
    public void MergingDropsOnlyWhatAWildcardCovers() =>
        Assert.Equal([".x.com", ".y.com", "z.com"], Allowlist.Merge(["a.x.com", ".x.com", ".b.x.com", "x.com", "y.com", ".y.com", ".x.com", "z.com"]));

    [Fact]
    public void PruneRemovesTheStoreWithTheRuntimeDir()
    {
        using var t = new TempDir();
        var host = t.Host();
        var dir = t.Sub("gone");
        var project = Project.Resolve(dir, host);
        Session.EnsureRuntimeDir(host, project);
        TrustStore.For(host, project).ApproveDomains(["a.example"]);
        var path = TrustStore.GetPath(host.GetRuntimeDir(project.Key), dir);
        Assert.True(File.Exists(path));
        Directory.Delete(dir);
        Assert.True(Prune.Remove(host, Assert.Single(Prune.Find(host, [], []), e => e.Key == project.Key)));
        Assert.False(File.Exists(path));
    }

    // Tools

    private static readonly ToolSpec Tool1 = new("pen-test", "1");

    [Fact]
    public void AProjectToolAsksUntilApproved_AndAddingOneOrChangingAVersionAsksAgain()
    {
        using var f = new Fixture();
        const string toml = "tools = [\"pen-test:1\"]\n";
        var r = f.Requested(toml);
        Assert.False(r.IsEmpty);
        Assert.Equal(Tool1, Assert.Single(r.Tools).Tool);

        f.Store.Approve(r);
        Assert.True(f.Store.CoversTool(Tool1));
        Assert.True(f.Requested(toml).IsEmpty);
        Assert.True(f.Requested("tools = [\"pen-test\"]\n").IsEmpty); // its default is the same version

        Assert.Equal(new ToolSpec("pen-test", "2"), Assert.Single(f.Requested("tools = [\"pen-test:2\"]\n").Tools).Tool);
        Assert.Equal(new ToolSpec("pen-test-b", "1"), Assert.Single(f.Requested("tools = [\"pen-test:1\", \"pen-test-b\"]\n").Tools).Tool);
    }

    [Fact]
    public void ApprovingAToolDoesntApproveItsDomainsForAllow()
    {
        using var f = new Fixture();
        f.Store.Approve(f.Requested("tools = [\"pen-test:1\"]\n"));
        Assert.Equal(["example.org"], f.Requested("allow = [\"example.org\"]\n").Domains);
    }

    [Fact]
    public void ToolsFromTheUserOrAFlagDontAsk_AndDontApproveTheEntryInTheProject()
    {
        using var f = new Fixture();
        const string toml = "tools = [\"pen-test:1\"]\n";
        Assert.True(f.Requested(toml, toml).IsEmpty);
        Assert.True(f.Requested("", toml).IsEmpty);
        var project = Config.Parse(toml, "p", ConfigKind.Project);
        Assert.True(new TrustGate(new ProjectContext(f.Host, f.Project, Config.Empty, project), new Answers(TextReader.Null, IsTerminal: false), RecipeCatalog.Embedded).FindUnapproved(f.Store, [Tool1]).IsEmpty);

        Assert.False(f.Store.CoversTool(Tool1));
        Assert.Equal(Tool1, Assert.Single(f.Requested(toml).Tools).Tool);
    }

    [Fact]
    public void ThePromptShowsTheToolAndTheDomainsItBrings()
    {
        var text = string.Join('\n', new TrustRequest([], false, null, null, null) { Tools = [new(Tool1, true, ["example.org"]), new(new("pen-test-b", "1"), true, [])] }.FormatLines());
        Assert.Contains("tool pen-test:1: sessions run with it installed, and the sandbox can reach example.org", text);
        Assert.Contains("tool pen-test-b:1: sessions run with it installed\n", text + "\n");
    }

    [Fact]
    public void ALongListOfToolsCantHideTheDomains()
    {
        var tools = Enumerable.Range(1, 500).Select(i => new ToolRequest(new("pen-test-b", i.ToString(System.Globalization.CultureInfo.InvariantCulture)), true, [])).ToList();
        var r = new TrustRequest(["a.example"], true, null, null, null) { Tools = tools };
        var lines = r.FormatLines().ToList();
        Assert.Equal(1 + TrustRequest.MaxListedTools + 1 + 1, lines.Count);
        Assert.Contains("480 more tools", lines[^2]);
        Assert.Contains("allow a.example", lines[^1]);
        Assert.Contains("480 tools not listed", r.Question);
        Assert.Equal(502, r.FormatAllLines().Count());
    }

    [Theory]
    [InlineData("tool pen-test")]
    [InlineData("tool :1")]
    [InlineData("tool pen-test:1;id")]
    [InlineData("tool PEN:1")]
    [InlineData("tool")]
    [InlineData("tool-allow pen-test:1")]
    [InlineData("tool-allow pen-test:1 example.org extra")]
    [InlineData("tool-allow pen-test:1 not_a_domain")]
    [InlineData("tool-allow pen-test example.org")]
    [InlineData("tool-allow  pen-test:1 example.org")]
    public void AToolLineThatIsntOneMakesTheStoreCorrupt(string line)
    {
        using var f = new Fixture();
        f.Store.ApproveDomains(["fine.example"]);
        File.AppendAllText(f.StorePath, line + "\n");
        Assert.False(f.Store.CoversDomain("fine.example"));
        Assert.False(f.Store.CoversTool(Tool1));
    }

    [Fact]
    public void AnApprovedToolIsStoredAsWritten_AndAHostileOneIsRefused()
    {
        using var f = new Fixture();
        f.Store.Approve(new TrustRequest([], false, null, null, null) { Tools = [new(Tool1, true, ["example.org"])] });
        Assert.Contains("tool pen-test:1\n", File.ReadAllText(f.StorePath));
        Assert.Contains("tool-allow pen-test:1 example.org\n", File.ReadAllText(f.StorePath));
        Assert.Throws<PenException>(() => f.Store.Approve(new TrustRequest([], false, null, null, null) { Tools = [new(new("pen-test", "1\ndocker"), true, [])] }));
        Assert.Throws<PenException>(() => f.Store.Approve(new TrustRequest([], false, null, null, null) { Tools = [new(Tool1, true, ["evil.example\ndocker"])] }));
        Assert.False(f.Store.Docker);
        Assert.False(f.Store.CoversToolDomain(Tool1, "evil.example"));
    }

    /// <summary>The embedded pen-test tool, as a Pen whose recipe brings these domains would have it.</summary>
    private static RecipeCatalog Bringing(params string[] domains) => RecipeCatalog.FromFiles(new Dictionary<string, string>
    {
        ["tools/pen-test/tool.toml"] = $"default = \"1\"\nversion = '^\\d+$'\nallow = [{string.Join(", ", domains.Select(d => $"\"{d}\""))}]\n",
        ["tools/pen-test/install.sh"] = "#!/bin/sh\n",
    });

    private TrustRequest RequestedWith(Fixture f, string projectToml, RecipeCatalog catalog) =>
        new TrustGate(new ProjectContext(f.Host, f.Project, Config.Empty, Config.Parse(projectToml, "p", ConfigKind.Project)), new Answers(TextReader.Null, IsTerminal: false), catalog).FindUnapproved(f.Store, []);

    private const string ToolToml = "tools = [\"pen-test:1\"]\n";

    [Fact]
    public void ARecipeThatAddsADomainAsksForOnlyThatOne()
    {
        using var f = new Fixture();
        f.Store.Approve(RequestedWith(f, ToolToml, Bringing("example.org")));
        Assert.True(RequestedWith(f, ToolToml, Bringing("example.org")).IsEmpty);

        var r = RequestedWith(f, ToolToml, Bringing("example.org", "example.net"));
        var tool = Assert.Single(r.Tools);
        Assert.False(tool.IsNew);
        Assert.Equal(["example.net"], tool.Domains);
        Assert.Contains(r.FormatLines(), l => l.Contains("pen-test:1 now brings more domains") && l.Contains("example.net") && !l.Contains("example.org"));
        Assert.Contains(r.FormatAllLines(), l => l.Contains("example.net"));

        f.Store.Approve(r);
        Assert.True(RequestedWith(f, ToolToml, Bringing("example.org", "example.net")).IsEmpty);
    }

    [Fact]
    public void ARecipeThatDropsADomainDoesntAsk()
    {
        using var f = new Fixture();
        f.Store.Approve(RequestedWith(f, ToolToml, Bringing("example.org", "example.net")));
        Assert.True(RequestedWith(f, ToolToml, Bringing("example.org")).IsEmpty);
    }

    [Fact]
    public void AnApprovedAllowDomainDoesntApproveItForATool()
    {
        using var f = new Fixture();
        f.Store.Approve(RequestedWith(f, ToolToml, Bringing("example.org")));
        f.Store.ApproveDomains(["example.net", ".wild.example"]);
        Assert.Equal(["example.net", "a.wild.example"], Assert.Single(RequestedWith(f, ToolToml, Bringing("example.org", "example.net", "a.wild.example")).Tools).Domains);
    }

    [Fact]
    public void AToolsApprovedDomainDoesntApproveItForAllow_OrForAnotherVersion()
    {
        using var f = new Fixture();
        f.Store.Approve(RequestedWith(f, ToolToml, Bringing("example.net")));
        Assert.False(f.Store.CoversDomain("example.net"));
        Assert.Equal(["example.net"], RequestedWith(f, "allow = [\"example.net\"]\n", Bringing("example.net")).Domains);
        Assert.False(f.Store.CoversToolDomain(new("pen-test", "2"), "example.net"));
    }
}
