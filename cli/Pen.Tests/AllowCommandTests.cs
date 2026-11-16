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
        Session.EnsureRuntimeDir(host, project);
        var s = new SessionDir(project.Key, t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678"));
        File.WriteAllText(s.ComposeFile, "");
        File.WriteAllText(s.EnvFile, $"PROJECT_DIR={project.Dir}\n");
        File.WriteAllText(s.GetPath("allowlist.txt"), "");
        using var sessionLock = FileLock.TryTake(s.LockFile, exclusive: true);
        using var fake = new FakeDocker(t, "if [ ! -f retry ]; then exit 1; fi\n");
        Assert.Equal(1, new AllowCommand(host, RecipeCatalog.Embedded).Run(["retry.example"], global: true));
        var config = File.ReadAllText(host.UserConfigPath);
        var written = File.GetLastWriteTimeUtc(host.UserConfigPath);
        File.WriteAllText(Path.Combine(t.Path, "retry"), "");
        Assert.Equal(0, new AllowCommand(host, RecipeCatalog.Embedded).Run(["retry.example"], global: true));
        Assert.Equal(config, File.ReadAllText(host.UserConfigPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(host.UserConfigPath));
        Assert.Contains("retry.example", File.ReadAllText(s.GetPath("allowlist.txt")));
        Assert.Equal(2, File.ReadAllLines(Path.Combine(t.Path, "calls")).Length);
    }

    private static string Add(string text, params string[] domains) =>
        ConfigEdit.AddToList(text, "x.toml", ConfigKind.User, "allow", domains).Text;

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
        Assert.Equal(["b.io"], Config.Parse(result, "x", ConfigKind.User).Allow);
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
        var result = ConfigEdit.AddToList(text, ".pen/config.toml", ConfigKind.Project, "allow", ["b.io"]).Text;
        Assert.Equal(text.Replace("[\"a.io\"]", "[\n  \"a.io\",\n  \"b.io\",\n]"), result);
        Assert.Equal([3000, 5173], Config.Parse(result, "x", ConfigKind.Project).Ports);
    }

    [Fact]
    public void KeepsCrLf() =>
        Assert.Equal("allow = [\r\n  \"a.io\",\r\n  \"b.io\",\r\n]\r\n", Add("allow = [\r\n  \"a.io\",\r\n]\r\n", "b.io"));

    [Fact]
    public void SkipsWhatsAllowed_AndRefusesABrokenFile()
    {
        var text = "allow = [\"a.io\"]\n";
        var edit = ConfigEdit.AddToList(text, "x", ConfigKind.User, "allow", ["A.io", "a.io"]);
        Assert.Same(text, edit.Text);
        Assert.Empty(edit.Added);
        Assert.StartsWith("x.toml: unknown key", Assert.Throws<PenException>(() => Add("allw = 1\n", "b.io")).Message);
    }

    [Fact]
    public void AnAllowThatIsntAListIsReported_NotEdited() =>
        Assert.StartsWith("x.toml: allow must be a list", Assert.Throws<PenException>(() => Add("allow = \"a.io\"\n", "b.io")).Message);

    [Fact]
    public void AppendCreatesTheProjectStub_AndKeepsAnExistingFilesMode()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "config.toml");
        Assert.Equal(["example.com", ".foo.io"], AllowCommand.Append(path, ".pen/config.toml", ConfigKind.Project, "allow", ["example.com", ".foo.io"], AllowCommand.ProjectConfigStub));
        var text = File.ReadAllText(path);
        Assert.StartsWith(AllowCommand.ProjectConfigStub, text);
        Assert.Equal(["example.com", ".foo.io"], Config.Parse(text, "x", ConfigKind.Project).Allow);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(path));

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(["b.io"], AllowCommand.Append(path, "x", ConfigKind.Project, "allow", ["b.io", "example.com"], ""));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(["example.com", ".foo.io", "b.io"], Config.Load(t.Path, ".pen", ConfigKind.Project).Allow);
        Assert.Single(Directory.GetFiles(t.Path)); // no temp file left
    }

    [Fact]
    public void AppendLeavesABrokenFileAlone()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "config.toml");
        File.WriteAllText(path, "allw = 1\n");
        Assert.Throws<PenException>(() => AllowCommand.Append(path, ".pen/config.toml", ConfigKind.Project, "allow", ["b.io"], AllowCommand.ProjectConfigStub));
        Assert.Equal("allw = 1\n", File.ReadAllText(path));
    }

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
        Assert.Null(AllowCommand.WriteAclFiles(s, Egress(["github.com", "*"], ["gist.github.com"])));
        // In open egress too, a domain allowed by name stays in the allowlist: it may resolve privately.
        Assert.Equal("github.com\n", File.ReadAllText(s.AllowlistFile));
        Assert.Equal("gist.github.com\n", File.ReadAllText(s.DenylistFile));
        Assert.Equal(".\n", File.ReadAllText(s.OpenEgressFile));
        Assert.Equal(inodes, new[] { s.AllowlistFile, s.DenylistFile, s.OpenEgressFile }.Select(path => Native.Stat(path, follow: false)!.Value.Inode).ToList());

        Assert.Null(AllowCommand.WriteAclFiles(s, Egress(["github.com"], [])));
        Assert.Equal("github.com\n", File.ReadAllText(s.AllowlistFile));
        Assert.Equal("", File.ReadAllText(s.DenylistFile));
        Assert.Equal("", File.ReadAllText(s.OpenEgressFile));
    }

    [Fact]
    public void WriteAclFiles_AnOldSessionGetsOnlyItsAllowlist_NeverTheStar()
    {
        using var t = new TempDir();
        var s = AclSession(t, old: true);
        Assert.Equal("deny and allow = [\"*\"] apply",
            AllowCommand.WriteAclFiles(s, Egress(["github.com", "new.example", "*"], ["gist.github.com"])));
        Assert.Equal("github.com\nnew.example\n", File.ReadAllText(s.AllowlistFile));
        Assert.False(File.Exists(s.DenylistFile));
        Assert.False(File.Exists(s.OpenEgressFile));

        Assert.Equal("allow = [\"*\"] applies", AllowCommand.WriteAclFiles(s, Egress(["*"], [])));
        Assert.DoesNotContain("*", File.ReadAllText(s.AllowlistFile));
        Assert.Equal("deny applies", AllowCommand.WriteAclFiles(s, Egress(["github.com"], ["x.example"])));
        Assert.Null(AllowCommand.WriteAclFiles(s, Egress(["github.com"], [])));
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
    private static (SessionDir Session, FileLock Lock) Running(TempDir t, HostEnv host, Project project, bool old)
    {
        Session.EnsureRuntimeDir(host, project);
        var s = new SessionDir(project.Key, t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678"));
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
        using var fake = new FakeDocker(t, "exit 0\n");
        var err = CapturingStderr(() => Assert.True(AllowCommand.Apply(host, s)));
        Assert.Equal("", err);
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
        using var fake = new FakeDocker(t, "exit 0\n");
        var err = CapturingStderr(() => Assert.True(AllowCommand.Apply(host, s)));
        var list = File.ReadAllText(s.AllowlistFile);
        Assert.DoesNotContain("*", list);
        Assert.Contains("typed.example\n", list);
        Assert.Contains("github.com\n", list);
        Assert.False(File.Exists(s.DenylistFile));
        Assert.False(File.Exists(s.OpenEgressFile));
        Assert.Contains($"the running session {s.ComposeProject} was started by an older Pen: deny and allow = [\"*\"] apply only to sessions started from now on", err);
    }

    [Fact]
    public void ListingWithTheStar_ShowsWhatOpenEgressBlockedApart_AsTheEndLinesDo_WithoutPenAllow()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        Session.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        File.WriteAllText(Path.Combine(dir, "proxy.log"),
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT gist.github.com:443 -\n" +
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT example.com:443 -\n" +
            "2026-11-01T10:00:02Z 172.18.0.3 TCP_DENIED/403 CONNECT 1.2.3.4:443 -\n" +
            "2026-11-01T10:00:03Z 172.18.0.3 TCP_DENIED/403 CONNECT github.com:443 -\n");
        var user = Config.Parse("allow = [\"*\"]\n", "u", ConfigKind.User);
        var projectConfig = Config.Parse("deny = [\"gist.github.com\"]\n", "p", ConfigKind.Project);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { new AllowCommand(host, RecipeCatalog.Embedded).ListBlocked(new ProjectContext(host, project, user, projectConfig)); }
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
        Session.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        File.WriteAllText(Path.Combine(dir, "proxy.log"),
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT 1.2.3.4:443 -\n" +
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT example.com:443 -\n");
        var user = Config.Parse("deny = [\"pastebin.com\"]\n", "u", ConfigKind.User);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { new AllowCommand(host, RecipeCatalog.Embedded).ListBlocked(new ProjectContext(host, project, user, new Config())); }
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
        Session.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        File.WriteAllText(Path.Combine(dir, "proxy.log"),
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT api.github.com:443 -\n" +
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT example.com:443 -\n");
        var user = Config.Parse("deny = [\"api.github.com\"]\n", "u", ConfigKind.User);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { new AllowCommand(host, RecipeCatalog.Embedded).ListBlocked(new ProjectContext(host, project, user, new Config())); }
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
        Session.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        File.WriteAllText(Path.Combine(dir, "proxy.log"),
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT pastebin.com:443 -\n");
        var user = Config.Parse("deny = [\"pastebin.com\"]\n", "u", ConfigKind.User);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { new AllowCommand(host, RecipeCatalog.Embedded).ListBlocked(new ProjectContext(host, project, user, new Config())); }
        finally { Console.SetOut(stdout); }
        var lines = captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("denied by config, blocked in this project's sessions, newest first:", lines[0]);
        Assert.StartsWith("  pastebin.com ", lines[1]);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain("pen allow", captured.ToString());
    }

    // Adding to deny, and what pen allow adds.

    private static string AddDeny(string text, params string[] domains) =>
        ConfigEdit.AddToList(text, "x.toml", ConfigKind.User, "deny", domains).Text;

    [Fact]
    public void AddToList_CreatesADenyArray_LeavingAllowAlone()
    {
        var text = "allow = [\"a.io\"]\n\n[github]\nacme = \"t\"\n";
        var result = AddDeny(text, "gist.github.com");
        Assert.Equal("allow = [\"a.io\"]\n\ndeny = [\n  \"gist.github.com\",\n]\n\n[github]\nacme = \"t\"\n", result);
        var config = Config.Parse(result, "x", ConfigKind.User);
        Assert.Equal(["a.io"], config.Allow);
        Assert.Equal(["gist.github.com"], config.Deny);
    }

    [Fact]
    public void AddToList_AddsToAnExistingDenyArray_SkippingWhatsDenied_NotWhatsAllowed()
    {
        var text = "allow = [\"b.io\"]\ndeny = [\"a.io\"]\n";
        var edit = ConfigEdit.AddToList(text, "x", ConfigKind.Project, "deny", ["A.io", "b.io"]);
        Assert.Equal(["b.io"], edit.Added);
        Assert.Equal("allow = [\"b.io\"]\ndeny = [\n  \"a.io\",\n  \"b.io\",\n]\n", edit.Text);
        Assert.Same(text, ConfigEdit.AddToList(text, "x", ConfigKind.Project, "deny", ["a.io"]).Text);
    }

    [Fact]
    public void AddToList_TakesTheStarInAllow()
    {
        var result = Add("allow = [\"a.io\"]\n", "*");
        Assert.Equal("allow = [\n  \"a.io\",\n  \"*\",\n]\n", result);
        Assert.Equal(["a.io", "*"], Config.Parse(result, "x", ConfigKind.User).Allow);
    }

    [Fact]
    public void AddToList_MessagesNameTheKey()
    {
        Assert.StartsWith("x.toml: deny must be a list", Assert.Throws<PenException>(() => AddDeny("deny = \"a.io\"\n", "b.io")).Message);
        // A domain the file can't hold makes the re-parse fail, which the check reports for that key.
        Assert.Equal("x.toml: couldn't add to deny automatically; add \"*\" by hand",
            Assert.Throws<PenException>(() => AddDeny("", "*")).Message);
        Assert.Throws<ArgumentException>(() => ConfigEdit.AddToList("", "x", ConfigKind.User, "tools", ["a.io"]));
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
        var err = CapturingStderr(() => Assert.Equal(0, new AllowCommand(host, RecipeCatalog.Embedded).Run(["api.pastebin.com", "ok.example"], global: true)));
        Assert.Contains($"pen: api.pastebin.com is also denied by {host.Display(host.UserConfigPath)}; deny wins\n", err);
        Assert.DoesNotContain("ok.example is also", err);
        Assert.Equal(["api.pastebin.com", "ok.example"], Config.Load(host.ConfigDir, "x", ConfigKind.User).Allow);
    }

    [Fact]
    public void AProjectStarIsApprovedLikeAnyDomainPenAllowWrites()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        Session.EnsureRuntimeDir(host, project);
        Assert.False(TrustStore.For(host, project).CoversDomain("*"));
        var addition = AllowCommand.AddAllowed(host, project, global: false, ["*"]);
        Assert.Equal(".pen/config.toml", addition.Where);
        Assert.Equal(["*"], addition.Added);
        Assert.Equal(["*"], Config.Load(project.PenDir, ".pen", ConfigKind.Project).Allow);
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
