namespace Pen.Tests;

public class AllowCommandTests
{
    [Fact]
    public void AppendCreatesTheStub_AndParses()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "allowlist.txt");
        var added = AllowCommand.Append(path, ".pen/allowlist.txt", ["example.com", ".foo.io"], AllowCommand.ProjectAllowlistStub);
        Assert.Equal(["example.com", ".foo.io"], added);

        var text = File.ReadAllText(path);
        Assert.StartsWith(AllowCommand.ProjectAllowlistStub, text);
        Assert.EndsWith("\nexample.com\n.foo.io\n", text);
        Assert.Equal(["example.com", ".foo.io"], Config.ParseAllowlist(text, "x"));
    }

    [Fact]
    public void AppendSkipsWhatsListed_KeepsCommentsAndMode_AndFixesAMissingNewline()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "allowlist.txt");
        File.WriteAllText(path, "# mine\na.io");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var added = AllowCommand.Append(path, "~/.config/pen/allowlist.txt", ["a.io", "b.io"], stub: null);
        Assert.Equal(["b.io"], added);
        Assert.Equal("# mine\na.io\nb.io\n", File.ReadAllText(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

        Assert.Empty(AllowCommand.Append(path, "x", ["b.io", "a.io"], stub: null));
        Assert.Equal("# mine\na.io\nb.io\n", File.ReadAllText(path));
    }

    [Fact]
    public void AppendRefusesABrokenFile_WithoutTouchingIt()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "allowlist.txt");
        File.WriteAllText(path, "not a domain\n");
        var e = Assert.Throws<PenException>(() => AllowCommand.Append(path, ".pen/allowlist.txt", ["b.io"], AllowCommand.ProjectAllowlistStub));
        Assert.Contains(".pen/allowlist.txt:1", e.Message);
        Assert.Equal("not a domain\n", File.ReadAllText(path));
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
