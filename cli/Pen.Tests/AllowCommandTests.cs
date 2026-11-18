namespace Pen.Tests;

[Collection(nameof(LockAndProcessTests))]
public class AllowCommandTests
{
    [Fact]
    public void RepeatingSavedDomainsRetriesAFailedLiveUpdateWithoutEditingConfig()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var s = new SessionDir(project.Key, t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678"));
        File.WriteAllText(s.ComposeFile, "");
        File.WriteAllText(s.EnvFile, $"PROJECT_DIR={project.Dir}\n");
        File.WriteAllText(s.GetPath("allowlist.txt"), "");
        using var sessionLock = FileLock.TryTake(s.LockFile, exclusive: true);
        using var fake = new FakeDocker(t, FakeDocker.ServingAclFiles + "if [ ! -f retry ]; then exit 1; fi\n");
        Assert.Equal(1, new AllowCommand(host, new StartDir(null), RecipeCatalog.Embedded, TimeProvider.System).Run(["retry.example"], global: true));
        var config = File.ReadAllText(host.UserConfigPath);
        var written = File.GetLastWriteTimeUtc(host.UserConfigPath);
        File.WriteAllText(Path.Combine(t.Path, "retry"), "");
        Assert.Equal(0, new AllowCommand(host, new StartDir(null), RecipeCatalog.Embedded, TimeProvider.System).Run(["retry.example"], global: true));
        Assert.Equal(config, File.ReadAllText(host.UserConfigPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(host.UserConfigPath));
        Assert.Contains("retry.example", File.ReadAllText(s.GetPath("allowlist.txt")));
        // Each run reads the proxy's view, then reconfigures.
        Assert.Equal(4, File.ReadAllLines(Path.Combine(t.Path, "calls")).Length);
    }

    private static string Add(string text, params string[] domains) =>
        ConfigEdit.AddToList(text, "x.toml", ConfigKind.User, ConfigListKey.Allow, domains, RecipeCatalog.Embedded).Text;

    [Fact]
    public void AddsToAMultiLineArray_KeepingCommentsAndIndent()
    {
        var text = """
            # top
            allow = [
                "a.io",  # why a
                # "off.io",
            ]

            [github]
            acme = "tok"
            """;
        Assert.Equal("""
            # top
            allow = [
                "a.io",  # why a
                # "off.io",
                "b.io",
                ".c.io",
            ]

            [github]
            acme = "tok"
            """, Add(text, "b.io", ".c.io"));
    }

    [Fact]
    public void AddsACommaToALastItemWithout_AndHandlesABracketAfterIt()
    {
        Assert.Equal("allow = [\n  \"a.io\", # c\n  \"b.io\",\n]\n", Add("allow = [\n  \"a.io\" # c\n]\n", "b.io"));
        Assert.Equal("allow = [\"a.io\",\n  \"b.io\",\n  \"c.io\",\n]\n", Add("allow = [\"a.io\",\n  \"b.io\"]\n", "c.io"));
    }

    [Fact]
    public void AddsToTheTemplatesCommentedArray()
    {
        var text = Embedded.ReadText("templates/config.toml");
        var result = Add(text, "b.io");
        Assert.Contains("  # \".example.org\",\n  \"b.io\",\n]", result);
        Assert.Equal(["b.io"], Config.Parse(result, "x", ConfigKind.User, RecipeCatalog.Embedded).Allow);
    }

    [Fact]
    public void ExpandsAOneLineArray()
    {
        Assert.Equal("allow = [\n  \"a.io\",\n  'b.io',\n  \"c.io\",\n] # end\n", Add("allow = [\"a.io\", 'b.io'] # end\n", "c.io"));
        Assert.Equal("allow = [\n  \"c.io\",\n]\n", Add("allow = []\n", "c.io"));
    }

    [Fact]
    public void AddsANewArrayAboveTheFirstTableAndItsComments()
    {
        Assert.Equal("# head\n\nallow = [\n  \"b.io\",\n]\n\n# about github\n[github]\nacme = \"t\"\n",
            Add("# head\n\n# about github\n[github]\nacme = \"t\"\n", "b.io"));
        Assert.Equal("[github]\nacme = \"t\"\n".Insert(0, "allow = [\n  \"b.io\",\n]\n\n"), Add("[github]\nacme = \"t\"\n", "b.io"));
        Assert.Equal("allow = [\n  \"b.io\",\n]\n\n", Add("", "b.io"));
    }

    [Fact]
    public void KeepsAProjectFilesOtherKeys_PortsIncluded()
    {
        var text = "github_owner = \"acme\"\ndocker = true\nports = [3000, 5173]\nallow = [\"a.io\"]\n";
        var result = ConfigEdit.AddToList(text, ".pen/config.toml", ConfigKind.Project, ConfigListKey.Allow, ["b.io"], RecipeCatalog.Embedded).Text;
        Assert.Equal(text.Replace("[\"a.io\"]", "[\n  \"a.io\",\n  \"b.io\",\n]"), result);
        Assert.Equal([3000, 5173], Config.Parse(result, "x", ConfigKind.Project, RecipeCatalog.Embedded).Ports);
    }

    [Fact]
    public void KeepsCrLf() =>
        Assert.Equal("allow = [\r\n  \"a.io\",\r\n  \"b.io\",\r\n]\r\n", Add("allow = [\r\n  \"a.io\",\r\n]\r\n", "b.io"));

    [Fact]
    public void SkipsWhatsAllowed_AndRefusesABrokenFile()
    {
        var text = "allow = [\"a.io\"]\n";
        var edit = ConfigEdit.AddToList(text, "x", ConfigKind.User, ConfigListKey.Allow, ["A.io", "a.io"], RecipeCatalog.Embedded);
        Assert.Same(text, edit.Text);
        Assert.Empty(edit.Added);
        Assert.StartsWith("x.toml: unknown key", Assert.Throws<PenException>(() => Add("allw = 1\n", "b.io")).Message);
    }

    [Fact]
    public void AnAllowThatIsntAListIsReported_NotEdited() =>
        Assert.StartsWith("x.toml: allow must be a list", Assert.Throws<PenException>(() => Add("allow = \"a.io\"\n", "b.io")).Message);

    [Fact]
    public void DefaultsListsEveryDefaultDomain_WithSectionComments_WithoutTheFileHeader()
    {
        var text = AllowCommand.FormatDefaults();
        Assert.StartsWith("# --- GitHub", text);
        Assert.DoesNotContain("Squid", text.Split('\n')[0]);
        Assert.Equal(Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt")), Allowlist.ParseList(text));
        Assert.EndsWith("\n", text);
        Assert.False(text.EndsWith("\n\n"));
    }

    [Fact]
    public void BlockedAcrossLogs_NewestFirst()
    {
        string[] older =
        [
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT old.io:443 -",
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_TUNNEL/200 CONNECT github.com:443 140.82.1.1",
            "2026-11-01T10:00:02Z 172.18.0.3 TCP_DENIED/403 CONNECT example.com:443 -",
        ];
        string[] newer =
        [
            "2026-11-05T09:00:00Z 172.18.0.3 TCP_DENIED/403 GET http://plain.io/x -",
            "2026-11-05T09:30:00Z 172.18.0.3 TCP_DENIED_ABORTED/403 CONNECT example.com:443 -",
        ];
        var hosts = AllowCommand.FindBlockedAcross([older, newer]).Select(b => b.Host).ToList();
        Assert.Equal(["example.com", "plain.io", "old.io"], hosts);
    }

    // Live apply of allow and deny.

    /// <summary>A running session's dir with an allowlist and, unless it's from an older Pen, the other ACL files.</summary>
    private static SessionDir AclSession(TempDir t, bool old)
    {
        var s = new SessionDir("pen-test-12345678", t.Sub("runtime", "sessions", "12345678"));
        File.WriteAllText(s.AllowlistFile, "github.com\n");
        if (!old)
        {
            File.WriteAllText(s.DenylistFile, "");
            File.WriteAllText(s.OpenEgressFile, "");
        }
        return s;
    }

    private static AllowCommand.Egress Egress(string[] allow, string[] deny) =>
        new(Allowlist.Merge(allow), Allowlist.MergeNamed(allow), Allowlist.Merge(deny));

    [Fact]
    public void WriteAclFiles_WritesAllThreeInPlace()
    {
        using var t = new TempDir();
        var s = AclSession(t, old: false);
        var inodes = new[] { s.AllowlistFile, s.DenylistFile, s.OpenEgressFile }.Select(path => Native.Stat(path, follow: false)!.Value.Inode).ToList();
        var written = AllowCommand.WriteAclFiles(s, Egress(["github.com", "*"], ["gist.github.com"]));
        Assert.Null(written.Pending);
        // What the proxy is checked to see before it reloads: all three, deny first.
        Assert.Equal([new("denylist.txt", "gist.github.com\n"), new("allowlist.txt", "github.com\n"),
            new AllowCommand.AclFile("open-egress.txt", ".\n")], written.Files);
        // In open egress too, a domain allowed by name stays in the allowlist: it may resolve privately.
        Assert.Equal("github.com\n", File.ReadAllText(s.AllowlistFile));
        Assert.Equal("gist.github.com\n", File.ReadAllText(s.DenylistFile));
        Assert.Equal(".\n", File.ReadAllText(s.OpenEgressFile));
        Assert.Equal(inodes, new[] { s.AllowlistFile, s.DenylistFile, s.OpenEgressFile }.Select(path => Native.Stat(path, follow: false)!.Value.Inode).ToList());

        Assert.Null(AllowCommand.WriteAclFiles(s, Egress(["github.com"], [])).Pending);
        Assert.Equal("github.com\n", File.ReadAllText(s.AllowlistFile));
        Assert.Equal("", File.ReadAllText(s.DenylistFile));
        Assert.Equal("", File.ReadAllText(s.OpenEgressFile));
    }

    [Fact]
    public void WriteAclFiles_AnOldSessionGetsOnlyItsAllowlist_NeverTheStar()
    {
        using var t = new TempDir();
        var s = AclSession(t, old: true);
        var written = AllowCommand.WriteAclFiles(s, Egress(["github.com", "new.example", "*"], ["gist.github.com"]));
        Assert.Equal("deny and allow = [\"*\"] apply", written.Pending);
        Assert.Equal([new AllowCommand.AclFile("allowlist.txt", "github.com\nnew.example\n")], written.Files);
        Assert.Equal("github.com\nnew.example\n", File.ReadAllText(s.AllowlistFile));
        Assert.False(File.Exists(s.DenylistFile));
        Assert.False(File.Exists(s.OpenEgressFile));

        Assert.Equal("allow = [\"*\"] applies", AllowCommand.WriteAclFiles(s, Egress(["*"], [])).Pending);
        Assert.DoesNotContain("*", File.ReadAllText(s.AllowlistFile));
        Assert.Equal("deny applies", AllowCommand.WriteAclFiles(s, Egress(["github.com"], ["x.example"])).Pending);
        Assert.Null(AllowCommand.WriteAclFiles(s, Egress(["github.com"], [])).Pending);
        Assert.Equal("github.com\n", File.ReadAllText(s.AllowlistFile));
    }

    [Fact]
    public void WriteAclFiles_AMissingFileFailsRatherThanBeingMade()
    {
        using var t = new TempDir();
        var s = AclSession(t, old: false);
        File.Delete(s.OpenEgressFile);
        Assert.ThrowsAny<IOException>(() => AllowCommand.WriteAclFiles(s, Egress(["*"], [])));
        Assert.False(File.Exists(s.OpenEgressFile));
    }

    /// <summary>A running session in <paramref name="project"/>, from this Pen or an older one.</summary>
    private static (SessionDir Session, FileLock Lock) Running(TempDir t, HostEnv host, Project project, bool old,
        string id = "12345678")
    {
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var s = new SessionDir(project.Key, t.Sub("home", ".local", "state", "pen", project.Key, "sessions", id));
        File.WriteAllText(s.ComposeFile, "");
        File.WriteAllText(s.EnvFile, $"PROJECT_DIR={project.Dir}\n");
        File.WriteAllText(s.AllowlistFile, "");
        if (!old)
        {
            File.WriteAllText(s.DenylistFile, "");
            File.WriteAllText(s.OpenEgressFile, "");
        }
        return (s, FileLock.TryTake(s.LockFile, exclusive: true)!);
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

    [Fact]
    public void Apply_GivesANewSessionTheUsersStarAndBothDenyLists_WithOneReconfigure()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(host.UserConfigPath, "allow = [\"*\", \"typed.example\"]\ndeny = [\"pastebin.com\"]\n");
        var project = Project.Resolve(t.Sub("repo"), host);
        Directory.CreateDirectory(project.PenDir);
        File.WriteAllText(project.ProjectConfigPath, "deny = [\"gist.github.com\"]\n");
        var (s, l) = Running(t, host, project, old: false);
        using var _ = l;
        using var fake = new FakeDocker(t, FakeDocker.ServingAclFiles + "exit 0\n");
        var err = CapturingStderr(() => Assert.True(AllowCommand.Apply(host, s, RecipeCatalog.Embedded, TimeProvider.System)));
        Assert.Equal("", err);
        var calls = File.ReadAllLines(Path.Combine(t.Path, "calls"));
        Assert.Equal(2, calls.Length);
        Assert.EndsWith(" sh /etc/squid/denylist.txt /etc/squid/allowlist.txt /etc/squid/open-egress.txt", calls[0]);
        Assert.EndsWith(" exec -T proxy squid -k reconfigure", calls[1]);
        // The domains allowed by name stay in the allowlist, as they'd reach a private address that "*" doesn't.
        Assert.Equal(Allowlist.Render(Allowlist.MergeNamed(
                Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt")), ["typed.example"])),
            File.ReadAllText(s.AllowlistFile));
        Assert.Contains("typed.example\n", File.ReadAllText(s.AllowlistFile));
        Assert.DoesNotContain("*", File.ReadAllText(s.AllowlistFile));
        Assert.Equal("pastebin.com\ngist.github.com\n", File.ReadAllText(s.DenylistFile));
        Assert.Equal(".\n", File.ReadAllText(s.OpenEgressFile));
        Assert.Single(File.ReadAllLines(Path.Combine(t.Path, "calls")), call => call.Contains("squid -k reconfigure"));
    }

    [Fact]
    public void Apply_ToAnOldSession_KeepsTheStarOut_AndSaysItAppliesToNewSessions()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(host.UserConfigPath, "allow = [\"*\", \"typed.example\"]\ndeny = [\"pastebin.com\"]\n");
        var project = Project.Resolve(t.Sub("repo"), host);
        var (s, l) = Running(t, host, project, old: true);
        using var _ = l;
        using var fake = new FakeDocker(t, FakeDocker.ServingAclFiles + "exit 0\n");
        var err = CapturingStderr(() => Assert.True(AllowCommand.Apply(host, s, RecipeCatalog.Embedded, TimeProvider.System)));
        // Its proxy has only the allowlist to check.
        var calls = File.ReadAllLines(Path.Combine(t.Path, "calls"));
        Assert.Equal(2, calls.Length);
        Assert.EndsWith(" exec -T proxy sh -c " + "for f in \"$@\"; do printf '=== %s\\n' \"$f\"; cat -- \"$f\" || exit 1; done" + " sh /etc/squid/allowlist.txt", calls[0]);
        Assert.EndsWith(" exec -T proxy squid -k reconfigure", calls[1]);
        var list = File.ReadAllText(s.AllowlistFile);
        Assert.DoesNotContain("*", list);
        Assert.Contains("typed.example\n", list);
        Assert.Contains("github.com\n", list);
        Assert.False(File.Exists(s.DenylistFile));
        Assert.False(File.Exists(s.OpenEgressFile));
        Assert.Contains($"the running session {s.ComposeProject} was started by an older Pen: deny and allow = [\"*\"] apply only to sessions started from now on", err);
    }

    /// <summary>The fake's proxy reads, in order; the rest of the calls are reconfigures.</summary>
    private static (int Reads, int Reconfigures) CountProxyCalls(TempDir t)
    {
        var calls = File.ReadAllLines(Path.Combine(t.Path, "calls"));
        Assert.All(calls, call => Assert.True(call.Contains(" /etc/squid/") || call.EndsWith(" squid -k reconfigure")));
        return (calls.Count(call => call.Contains(" /etc/squid/")), calls.Count(call => call.EndsWith(" squid -k reconfigure")));
    }

    [Fact]
    public void Apply_WhenTheProxyKeepsSeeingOtherFiles_DoesntReconfigure_AndReportsTheSession()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(host.UserConfigPath, "deny = [\"pastebin.com\"]\n");
        var project = Project.Resolve(t.Sub("repo"), host);
        var (s, l) = Running(t, host, project, old: false);
        using var _ = l;
        // A lagging bind mount: the proxy sees the files as just truncated, so reloading would drop the deny.
        using var fake = new FakeDocker(t, """
            case "$*" in *" /etc/squid/"*)
              printf '=== /etc/squid/denylist.txt\n=== /etc/squid/allowlist.txt\n=== /etc/squid/open-egress.txt\n'
              ;;
            esac
            exit 0

            """);
        var err = CapturingStderr(() => Assert.False(AllowCommand.Apply(host, s, RecipeCatalog.Embedded, TimeProvider.System)));
        Assert.Contains($"couldn't apply it to the running session {s.ComposeProject}: its proxy still sees other ACL files than were written, so Squid wasn't reloaded; run this again", err);
        Assert.Equal((5, 0), CountProxyCalls(t));
        Assert.Equal("pastebin.com\n", File.ReadAllText(s.DenylistFile));
    }

    [Fact]
    public void Apply_WhenTheProxyCatchesUpOnARetry_ReconfiguresOnce()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(host.UserConfigPath, "deny = [\"pastebin.com\"]\n");
        var project = Project.Resolve(t.Sub("repo"), host);
        var (s, l) = Running(t, host, project, old: false);
        using var _ = l;
        using var fake = new FakeDocker(t, """
            case "$*" in *" /etc/squid/"*)
              n=$(cat reads 2>/dev/null || echo 0)
              echo $((n + 1)) >reads
              if [ "$n" -lt 2 ]; then printf '=== /etc/squid/denylist.txt\n'; exit 0; fi
              ;;
            esac

            """ + FakeDocker.ServingAclFiles + "exit 0\n");
        var err = CapturingStderr(() => Assert.True(AllowCommand.Apply(host, s, RecipeCatalog.Embedded, TimeProvider.System)));
        Assert.Equal("", err);
        Assert.Equal((3, 1), CountProxyCalls(t));
        Assert.EndsWith(" squid -k reconfigure", File.ReadAllLines(Path.Combine(t.Path, "calls"))[^1]);
    }

    [Fact]
    public void Apply_WhenTheProxyCantBeRead_DoesntReconfigure()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        var (s, l) = Running(t, host, project, old: true);
        using var _ = l;
        using var fake = new FakeDocker(t, """
            case "$*" in *" /etc/squid/"*) echo 'cat: /etc/squid/allowlist.txt: Permission denied' >&2; exit 1 ;; esac
            exit 0

            """);
        var err = CapturingStderr(() => Assert.False(AllowCommand.Apply(host, s, RecipeCatalog.Embedded, TimeProvider.System)));
        Assert.Contains($"couldn't apply it to the running session {s.ComposeProject}: couldn't read its proxy's ACL files, so Squid wasn't reloaded; run this again: cat: /etc/squid/allowlist.txt: Permission denied", err);
        Assert.Equal((1, 0), CountProxyCalls(t));
    }

    [Fact]
    public void ApplyToRunning_WhenDockerCantStart_ReportsEachSessionAndTriesThemAll()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(host.UserConfigPath, "deny = [\"pastebin.com\"]\n");
        var project = Project.Resolve(t.Sub("repo"), host);
        var (first, firstLock) = Running(t, host, project, old: false, id: "11111111");
        using var _ = firstLock;
        var (second, secondLock) = Running(t, host, project, old: false, id: "22222222");
        using var __ = secondLock;
        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", t.Sub("no-docker"));
        try
        {
            var err = CapturingStderr(() =>
                Assert.False(AllowCommand.ApplyToRunning(host, project, false, RecipeCatalog.Embedded, TimeProvider.System)));
            foreach (var s in new[] { first, second })
            {
                Assert.Contains($"couldn't apply it to the running session {s.ComposeProject}: docker not found: is it installed and on PATH?", err);
                Assert.Equal("pastebin.com\n", File.ReadAllText(s.DenylistFile));
            }
            Assert.DoesNotContain("applied to", err);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }

    [Fact]
    public void Apply_WaitsForAnotherHoldingTheSessionsAclLock()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        var (s, l) = Running(t, host, project, old: false);
        using var _ = l;
        using var fake = new FakeDocker(t, FakeDocker.ServingAclFiles + "exit 0\n");
        var other = FileLock.TryTake(s.AclLockFile, exclusive: true)!;
        using var release = new Timer(_ => other.Dispose(), null, 500, Timeout.Infinite);
        var err = CapturingStderr(() => Assert.True(AllowCommand.Apply(host, s, RecipeCatalog.Embedded, TimeProvider.System)));
        Assert.Contains($"waiting for another pen allow or pen deny applying to the running session {s.ComposeProject}", err);
        Assert.Equal((1, 1), CountProxyCalls(t));
        // Not one of the files whose presence says what the session is.
        Assert.True(s.IsOpen());
        Session.DeleteSessionFiles(s);
        Assert.False(File.Exists(s.AclLockFile));
    }

    [Fact]
    public void Apply_ReadsTheConfigOnlyOnceItHoldsTheSessionsAclLock()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        var (s, l) = Running(t, host, project, old: false);
        using var _ = l;
        using var fake = new FakeDocker(t, FakeDocker.ServingAclFiles + "exit 0\n");
        var other = FileLock.TryTake(s.AclLockFile, exclusive: true)!;
        bool applied = false;
        var apply = new Thread(() => CapturingStderr(() => applied = AllowCommand.Apply(host, s, RecipeCatalog.Embedded, TimeProvider.System)));
        apply.Start();
        Thread.Sleep(500);
        File.WriteAllText(host.UserConfigPath, "deny = [\"pastebin.com\"]\n");
        other.Dispose();
        apply.Join();
        Assert.True(applied);
        Assert.Contains("pastebin.com", File.ReadAllText(s.DenylistFile));
    }

    [Fact]
    public void ListingWithTheStar_ShowsWhatOpenEgressBlockedApart_AsTheEndLinesDo_WithoutPenAllow()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        File.WriteAllText(Path.Combine(dir, "proxy.log"),
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT gist.github.com:443 -\n" +
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT example.com:443 -\n" +
            "2026-11-01T10:00:02Z 172.18.0.3 TCP_DENIED/403 CONNECT 1.2.3.4:443 -\n" +
            "2026-11-01T10:00:03Z 172.18.0.3 TCP_DENIED/403 CONNECT github.com:443 -\n");
        var user = Config.Parse("allow = [\"*\"]\n", "u", ConfigKind.User, RecipeCatalog.Embedded);
        var projectConfig = Config.Parse("deny = [\"gist.github.com\"]\n", "p", ConfigKind.Project, RecipeCatalog.Embedded);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { new AllowCommand(host, new StartDir(null), RecipeCatalog.Embedded, TimeProvider.System).ListBlocked(new ProjectContext(host, project, user, projectConfig)); }
        finally { Console.SetOut(stdout); }
        var lines = captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // example.com resolved privately, say, or was blocked before egress opened: "*" covers it, but it's listed.
        Assert.Equal("blocked in this project's sessions (raw IP, private address, unresolvable name, another port or blocked earlier), newest first:", lines[0]);
        Assert.StartsWith("  1.2.3.4 ", lines[1]);
        Assert.StartsWith("  example.com ", lines[2]);
        Assert.Equal("denied by config, blocked in this project's sessions, newest first:", lines[3]);
        Assert.StartsWith("  gist.github.com ", lines[4]);
        Assert.Equal(5, lines.Length); // github.com is allowed by name, a default
        Assert.DoesNotContain("pen allow", captured.ToString());
    }

    [Fact]
    public void ListingWithDenyButNoStar_ShowsRawIpsApart_AsTheEndLinesDo()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        File.WriteAllText(Path.Combine(dir, "proxy.log"),
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT 1.2.3.4:443 -\n" +
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT example.com:443 -\n");
        var user = Config.Parse("deny = [\"pastebin.com\"]\n", "u", ConfigKind.User, RecipeCatalog.Embedded);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { new AllowCommand(host, new StartDir(null), RecipeCatalog.Embedded, TimeProvider.System).ListBlocked(new ProjectContext(host, project, user, new Config())); }
        finally { Console.SetOut(stdout); }
        Assert.Equal(
            "blocked in this project's sessions, newest first:\n" +
            $"  {"example.com",-40} {new DateTimeOffset(2026, 11, 1, 10, 0, 1, TimeSpan.Zero).ToLocalTime():yyyy-MM-dd HH:mm}\n" +
            "pen allow example.com   (add --global for all projects)\n" +
            "blocked in this project's sessions (raw IP), newest first:\n" +
            $"  {"1.2.3.4",-40} {new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero).ToLocalTime():yyyy-MM-dd HH:mm}\n",
            captured.ToString());
    }

    [Fact]
    public void ListingWithoutTheStar_ShowsDeniedApart_AndOffersPenAllowForTheRest()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        File.WriteAllText(Path.Combine(dir, "proxy.log"),
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT api.github.com:443 -\n" +
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT example.com:443 -\n");
        var user = Config.Parse("deny = [\"api.github.com\"]\n", "u", ConfigKind.User, RecipeCatalog.Embedded);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { new AllowCommand(host, new StartDir(null), RecipeCatalog.Embedded, TimeProvider.System).ListBlocked(new ProjectContext(host, project, user, new Config())); }
        finally { Console.SetOut(stdout); }
        var text = captured.ToString();
        Assert.Contains("pen allow example.com   (add --global for all projects)\n", text);
        Assert.Contains("denied by config, blocked in this project's sessions, newest first:\n  api.github.com ", text);
        Assert.DoesNotContain("pen allow api.github.com", text);
    }

    [Fact]
    public void ListingADeniedHostThatIsntAllowed_ShowsItOnlyUnderDenied_WithoutPenAllow()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        File.WriteAllText(Path.Combine(dir, "proxy.log"),
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT pastebin.com:443 -\n");
        var user = Config.Parse("deny = [\"pastebin.com\"]\n", "u", ConfigKind.User, RecipeCatalog.Embedded);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { new AllowCommand(host, new StartDir(null), RecipeCatalog.Embedded, TimeProvider.System).ListBlocked(new ProjectContext(host, project, user, new Config())); }
        finally { Console.SetOut(stdout); }
        var lines = captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("denied by config, blocked in this project's sessions, newest first:", lines[0]);
        Assert.StartsWith("  pastebin.com ", lines[1]);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("pen allow", captured.ToString());
    }

    // Adding to deny, and what pen allow adds.

    private static string AddDeny(string text, params string[] domains) =>
        ConfigEdit.AddToList(text, "x.toml", ConfigKind.User, ConfigListKey.Deny, domains, RecipeCatalog.Embedded).Text;

    [Fact]
    public void AddToList_CreatesADenyArray_LeavingAllowAlone()
    {
        var text = "allow = [\"a.io\"]\n\n[github]\nacme = \"t\"\n";
        var result = AddDeny(text, "gist.github.com");
        Assert.Equal("allow = [\"a.io\"]\n\ndeny = [\n  \"gist.github.com\",\n]\n\n[github]\nacme = \"t\"\n", result);
        var config = Config.Parse(result, "x", ConfigKind.User, RecipeCatalog.Embedded);
        Assert.Equal(["a.io"], config.Allow);
        Assert.Equal(["gist.github.com"], config.Deny);
    }

    [Fact]
    public void AddToList_AddsToAnExistingDenyArray_SkippingWhatsDenied_NotWhatsAllowed()
    {
        var text = "allow = [\"b.io\"]\ndeny = [\"a.io\"]\n";
        var edit = ConfigEdit.AddToList(text, "x", ConfigKind.Project, ConfigListKey.Deny, ["A.io", "b.io"], RecipeCatalog.Embedded);
        Assert.Equal(["b.io"], edit.Added);
        Assert.Equal("allow = [\"b.io\"]\ndeny = [\n  \"a.io\",\n  \"b.io\",\n]\n", edit.Text);
        Assert.Same(text, ConfigEdit.AddToList(text, "x", ConfigKind.Project, ConfigListKey.Deny, ["a.io"], RecipeCatalog.Embedded).Text);
    }

    [Fact]
    public void AddToList_TakesTheStarInAllow()
    {
        var result = Add("allow = [\"a.io\"]\n", "*");
        Assert.Equal("allow = [\n  \"a.io\",\n  \"*\",\n]\n", result);
        Assert.Equal(["a.io", "*"], Config.Parse(result, "x", ConfigKind.User, RecipeCatalog.Embedded).Allow);
    }

    [Fact]
    public void AddToList_MessagesNameTheKey()
    {
        Assert.StartsWith("x.toml: deny must be a list", Assert.Throws<PenException>(() => AddDeny("deny = \"a.io\"\n", "b.io")).Message);
        // A domain the file can't hold makes the re-parse fail, which the check reports for that key.
        Assert.Equal("x.toml: couldn't add to deny automatically; add \"*\" by hand",
            Assert.Throws<PenException>(() => AddDeny("", "*")).Message);
    }

    [Fact]
    public void DenyWins_NamesEachFileWhoseDenyCoversTheDomain_ButNotForTheStar()
    {
        List<(string Where, List<string> Deny)> denies =
        [
            ("~/.config/pen/config.toml", ["pastebin.com", ".example.org"]),
            (".pen/config.toml", ["pastebin.com"]),
        ];
        Assert.Equal(
            [
                "pastebin.com is also denied by ~/.config/pen/config.toml and .pen/config.toml; deny wins",
                "a.example.org is also denied by ~/.config/pen/config.toml; deny wins",
                ".example.org is also denied by ~/.config/pen/config.toml; deny wins",
                "example.org is also denied by ~/.config/pen/config.toml; deny wins",
            ],
            AllowCommand.FormatDenyWins(denies, ["pastebin.com", "a.example.org", ".example.org", "example.org", ".pastebin.com", "*"]));
    }

    [Fact]
    public void RunWarnsWhenADenyStillCoversTheDomain()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(host.UserConfigPath, "deny = [\".pastebin.com\"]\n");
        var err = CapturingStderr(() => Assert.Equal(0, new AllowCommand(host, new StartDir(null), RecipeCatalog.Embedded, TimeProvider.System).Run(["api.pastebin.com", "ok.example"], global: true)));
        Assert.Contains($"pen: api.pastebin.com is also denied by {host.Display(host.UserConfigPath)}; deny wins\n", err);
        Assert.DoesNotContain("ok.example is also", err);
        Assert.Equal(["api.pastebin.com", "ok.example"], Config.Load(host.ConfigDir, "x", ConfigKind.User, RecipeCatalog.Embedded).Allow);
    }

    [Fact]
    public void AProjectStarIsApprovedLikeAnyDomainPenAllowWrites()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        Assert.False(TrustStore.For(host, project).CoversDomain("*"));
        var addition = AllowCommand.AddAllowed(host, project, global: false, ["*"], RecipeCatalog.Embedded);
        Assert.Equal(".pen/config.toml", addition.Where);
        Assert.Equal(["*"], addition.Added);
        Assert.Equal(["*"], Config.Load(project.PenDir, ".pen", ConfigKind.Project, RecipeCatalog.Embedded).Allow);
        Assert.True(TrustStore.For(host, project).CoversDomain("*"));
    }

    [Fact]
    public void GlobHint_ForArgumentsNamedLikeFilesHere_IgnoringCase()
    {
        using var t = new TempDir();
        var dir = t.Sub("cwd");
        File.WriteAllText(Path.Combine(dir, "README.md"), "");
        Directory.CreateDirectory(Path.Combine(dir, "src.io"));
        Assert.Null(AllowCommand.FormatGlobHint("allow", dir, ["example.com"], ".pen/config.toml"));
        Assert.Equal("readme.md names a file here: did your shell expand *? quote it: pen allow '*'; " +
            "it was added to .pen/config.toml: remove it there if it isn't a domain",
            AllowCommand.FormatGlobHint("allow", dir, ["readme.md", "example.com"], ".pen/config.toml"));
        Assert.Equal("readme.md src.io name files here: did your shell expand *? pen deny takes domains, not '*'; " +
            "they were added to ~/.config/pen/config.toml: remove them there if they aren't domains",
            AllowCommand.FormatGlobHint("deny", dir, ["readme.md", "src.io"], "~/.config/pen/config.toml"));
        Assert.Null(AllowCommand.FormatGlobHint("allow", Path.Combine(dir, "missing"), ["readme.md"], "x"));
    }
}
