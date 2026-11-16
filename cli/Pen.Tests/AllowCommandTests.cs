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
        ConfigEdit.AddAllowed(text, "x.toml", ConfigKind.User, domains).Text;

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
        var result = ConfigEdit.AddAllowed(text, ".pen/config.toml", ConfigKind.Project, ["b.io"]).Text;
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
        var edit = ConfigEdit.AddAllowed(text, "x", ConfigKind.User, ["A.io", "a.io"]);
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
        Assert.Equal(["example.com", ".foo.io"], AllowCommand.Append(path, ".pen/config.toml", ConfigKind.Project, ["example.com", ".foo.io"], AllowCommand.ProjectConfigStub));
        var text = File.ReadAllText(path);
        Assert.StartsWith(AllowCommand.ProjectConfigStub, text);
        Assert.Equal(["example.com", ".foo.io"], Config.Parse(text, "x", ConfigKind.Project).Allow);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead, File.GetUnixFileMode(path));

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.Equal(["b.io"], AllowCommand.Append(path, "x", ConfigKind.Project, ["b.io", "example.com"], ""));
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
        Assert.Throws<PenException>(() => AllowCommand.Append(path, ".pen/config.toml", ConfigKind.Project, ["b.io"], AllowCommand.ProjectConfigStub));
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
    public void ListingWithTheStar_ShowsDeniedHostsApart_WithoutPenAllow()
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
            "2026-11-01T10:00:02Z 172.18.0.3 TCP_DENIED/403 CONNECT 1.2.3.4:443 -\n");
        var user = Config.Parse("allow = [\"*\"]\n", "u", ConfigKind.User);
        var projectConfig = Config.Parse("deny = [\"gist.github.com\"]\n", "p", ConfigKind.Project);
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { new AllowCommand(host, RecipeCatalog.Embedded).ListBlocked(new ProjectContext(host, project, user, projectConfig)); }
        finally { Console.SetOut(stdout); }
        var lines = captured.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("blocked in this project's sessions, newest first:", lines[0]);
        Assert.StartsWith("  1.2.3.4 ", lines[1]);
        Assert.Equal("denied by config, blocked in this project's sessions, newest first:", lines[2]);
        Assert.StartsWith("  gist.github.com ", lines[3]);
        Assert.Equal(4, lines.Length);
        Assert.DoesNotContain("pen allow", captured.ToString());
        Assert.DoesNotContain("example.com", captured.ToString());
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
}
