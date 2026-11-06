namespace Pen.Tests;

public class AllowCommandTests
{
    private static string Add(string text, params string[] domains) =>
        ConfigEdit.AddAllowed(text, "x.toml", ConfigKind.User, domains, out _);

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
        var text = Embedded.Text("templates/config.toml");
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
    public void KeepsCrLf() =>
        Assert.Equal("allow = [\r\n  \"a.io\",\r\n  \"b.io\",\r\n]\r\n", Add("allow = [\r\n  \"a.io\",\r\n]\r\n", "b.io"));

    [Fact]
    public void SkipsWhatsAllowed_AndRefusesABrokenFile()
    {
        var text = "allow = [\"a.io\"]\n";
        Assert.Same(text, ConfigEdit.AddAllowed(text, "x", ConfigKind.User, ["A.io", "a.io"], out var added));
        Assert.Empty(added);
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
        var text = AllowCommand.Defaults();
        Assert.StartsWith("# --- AI providers", text);
        Assert.DoesNotContain("Squid", text.Split('\n')[0]);
        Assert.Equal(Allowlist.ParseList(Embedded.Text("proxy/allowlist.txt")), Allowlist.ParseList(text));
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
        var hosts = AllowCommand.BlockedAcross([older, newer]).Select(b => b.Host).ToList();
        Assert.Equal(["example.com", "plain.io", "old.io"], hosts);
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("api.github.com", true)]
    [InlineData("x.example.org", true)]
    [InlineData("example.org", true)]
    [InlineData("badexample.org", false)]
    [InlineData("example.com", false)]
    public void IsAllowed(string host, bool allowed) =>
        Assert.Equal(allowed, Allowlist.IsAllowed(["github.com", "api.github.com", ".example.org"], host));
}
