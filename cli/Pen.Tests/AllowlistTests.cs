namespace Pen.Tests;

public class AllowlistTests
{
    [Fact]
    public void MergeKeepsOrderAndDropsDuplicates() =>
        Assert.Equal(["a.com", "b.com", "c.com"], Allowlist.Merge(["a.com", "b.com"], ["B.com", "c.com"], ["a.com"]));

    [Fact]
    public void MergeDropsEntriesAWildcardCovers()
    {
        var merged = Allowlist.Merge(
            ["github.com", "api.github.com", "codeload.github.com", "nuget.org"],
            [".github.com"],
            [".api.github.com", ".nuget.org", "notgithub.com"]);
        Assert.Equal([".github.com", ".nuget.org", "notgithub.com"], merged);
    }

    [Fact]
    public void ADomainEndingLikeAWildcardWithoutADotIsKept() =>
        Assert.Equal([".hub.com", "github.com"], Allowlist.Merge([".hub.com", "github.com"]));

    [Fact]
    public void TheDefaultListIsValidAndAlreadyMerged()
    {
        var list = Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt")).ToList();
        Assert.NotEmpty(list);
        Assert.All(list, d => Assert.True(Allowlist.IsValidDomain(d), d));
        Assert.Equal(list, Allowlist.Merge(list));
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData(".example.com", true)]
    [InlineData("a-b.c.example.co.uk", true)]
    [InlineData("xn--bcher-kva.example", true)]
    [InlineData("localhost", false)]
    [InlineData(".com", false)]
    [InlineData("-a.com", false)]
    [InlineData("a..com", false)]
    [InlineData("a.com.", false)]
    [InlineData("1.2.3.4", false)]
    [InlineData("example.com:443", false)]
    [InlineData("*.example.com", false)]
    [InlineData("Example.com", false)] // callers lowercase first
    public void IsValidDomain(string d, bool valid) => Assert.Equal(valid, Allowlist.IsValidDomain(d));

    [Fact]
    public void BlockedHostsFromTheProxyLog()
    {
        string[] log =
        [
            "2026-11-06T10:00:00Z 172.20.0.3 TCP_TUNNEL/200 CONNECT github.com:443 140.82.112.3",
            "2026-11-06T10:00:01Z 172.20.0.3 TCP_DENIED/403 CONNECT example.com:443 -",
            "2026-11-06T10:00:02Z 172.20.0.3 TCP_DENIED/403 GET http://foo.io/path?x=1 -",
            "2026-11-06T10:00:03Z 172.20.0.3 TCP_DENIED/403 CONNECT Example.com:443 -",
            "2026-11-06T10:00:04Z 172.20.0.3 TCP_DENIED/403 CONNECT 140.82.112.3:443 -",
            "2026-11-06T10:00:05Z 172.20.0.3 TCP_DENIED/403 CONNECT [2001:db8::1]:443 -",
            "garbage",
            "",
        ];
        Assert.Equal(["example.com", "foo.io", "140.82.112.3", "2001:db8::1"], Allowlist.FindBlockedHosts(log));
    }

    [Fact]
    public void BlockedLine()
    {
        Assert.Null(Allowlist.FormatBlockedLine([]));
        Assert.Equal("blocked: foo.com, bar.io, 1.2.3.4 → pen allow foo.com bar.io (add --global for all projects)",
            Allowlist.FormatBlockedLine(["foo.com", "bar.io", "1.2.3.4"]));
        Assert.Equal("blocked: 1.2.3.4", Allowlist.FormatBlockedLine(["1.2.3.4"]));
    }

    [Fact]
    public void BlockedLine_ShowsTheLatestFew_AndCountsTheRest()
    {
        var hosts = Enumerable.Range(0, 5000).Select(i => $"h{i}.example").ToList();
        var shown = Enumerable.Range(4980, 20).Select(i => $"h{i}.example").ToList();
        Assert.Equal($"blocked: {string.Join(", ", shown)} and 4980 more (pen allow lists the latest) → pen allow {string.Join(' ', shown)} (add --global for all projects)",
            Allowlist.FormatBlockedLine(hosts));
        Assert.DoesNotContain("h4979.example", Allowlist.FormatBlockedLine(hosts));
    }

    [Fact]
    public void BlockedLine_CutsALongHostShort()
    {
        var line = Allowlist.FormatBlockedLine([new string('a', 5000) + ".example"])!;
        Assert.True(line.Length < 200);
        Assert.Contains("…", line);
        Assert.DoesNotContain("pen allow", line);
    }

    [Theory]
    [InlineData("x.com", "x.com", true)]
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

    [Theory]
    [InlineData("*", true)]
    [InlineData("example.com", true)]
    [InlineData(".example.com", true)]
    [InlineData(".", false)]
    [InlineData(".com", false)]
    [InlineData("*.example.com", false)]
    [InlineData("**", false)]
    [InlineData(" *", false)]
    [InlineData("1.2.3.4", false)]
    public void IsValidAllowEntry(string entry, bool valid) => Assert.Equal(valid, Allowlist.IsValidAllowEntry(entry));

    [Fact]
    public void IsValidDomain_StillRejectsTheStar()
    {
        Assert.False(Allowlist.IsValidDomain("*"));
        Assert.False(Allowlist.IsValidDomain("."));
        Assert.False(Allowlist.IsValidDomain("**"));
    }

    [Fact]
    public void MergeCollapsesToTheStar_WhenAnySourceHasIt()
    {
        Assert.Equal(["*"], Allowlist.Merge(["github.com", ".nuget.org"], ["a.com", "*"], ["b.com"]));
        Assert.Equal(["*"], Allowlist.Merge(["*"]));
        Assert.Equal(["*"], Allowlist.Merge([], [" * "]));
        Assert.Equal(["a.com", ".b.com"], Allowlist.Merge(["a.com", ".b.com", "x.b.com"]));
    }

    [Fact]
    public void MergeDeny_AppendsTheUsersAndTheProjects()
    {
        var user = Config.Parse("deny = [\"gist.github.com\", \".evil.example\"]", "~/.config/pen/config.toml",
            ConfigKind.User);
        var project = Config.Parse("deny = [\"a.evil.example\", \"Pastebin.com\", \"gist.github.com\"]",
            ".pen/config.toml", ConfigKind.Project);
        Assert.Equal(["gist.github.com", ".evil.example", "pastebin.com"], Allowlist.MergeDeny(user, project));
        Assert.Empty(Allowlist.MergeDeny(Config.Empty, Config.Empty));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("a.b.example.org")]
    [InlineData("localhost")]
    [InlineData("*")]
    [InlineData("xn--bcher-kva.example")]
    [InlineData("1.example.com")]
    public void TheStarAllowsAnyName(string host) => Assert.True(Allowlist.IsAllowed(["*"], host));

    [Theory]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4.")]
    [InlineData("127.1")]
    [InlineData("2130706433")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:1.2.3.4")]
    [InlineData("fe80::1%eth0")]
    [InlineData("[2001:db8::1]")]
    [InlineData("[::1]")]
    [InlineData("[2001:db8::1")]
    [InlineData("")]
    public void TheStarNeverAllowsAnIp(string host)
    {
        Assert.True(Allowlist.IsIpLiteral(host), host);
        Assert.False(Allowlist.IsAllowed(["*"], host));
        Assert.False(Allowlist.IsAllowed(["*", "example.com", ".example.org"], host));
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("1.example.com")]
    [InlineData("a1.2.3.4")]
    [InlineData("*")]
    public void NamesArentIpLiterals(string host) => Assert.False(Allowlist.IsIpLiteral(host));

    [Fact]
    public void WithoutTheStar_ANameIsntAllowedJustForBeingOne()
    {
        Assert.False(Allowlist.IsAllowed(["example.com"], "*"));
        Assert.False(Allowlist.IsAllowed(["example.com"], "other.com"));
        Assert.False(Allowlist.IsDenied(["example.com"], "*"));
    }

    [Fact]
    public void AnExactDenyUnderAWildcardAllow()
    {
        var allow = Allowlist.Merge([".github.com"]);
        var deny = Allowlist.Merge(["gist.github.com"]);
        Assert.True(Allowlist.IsAllowed(allow, "gist.github.com"));
        Assert.True(Allowlist.IsDenied(deny, "gist.github.com"));
        Assert.False(Allowlist.IsDenied(deny, "api.github.com"));
        Assert.False(Allowlist.IsDenied(deny, "a.gist.github.com"));
    }

    [Theory]
    [InlineData("x.com", "x.com", true)]
    [InlineData(".x.com", "x.com", true)]
    [InlineData(".x.com", "a.b.x.com", true)]
    [InlineData(".x.com", ".a.x.com", true)]
    [InlineData(".x.com", "badx.com", false)]
    [InlineData("x.com", "a.x.com", false)]
    [InlineData("a.x.com", "x.com", false)]
    [InlineData(".x.com", "1.2.3.4", false)]
    public void DenyCoverageByADomainOrWildcard(string entry, string host, bool denied) =>
        Assert.Equal(denied, Allowlist.IsDenied([entry], host));

    [Fact]
    public void TheDenyListMatchesUnderTheStarToo()
    {
        var allow = Allowlist.Merge(["*"]);
        var deny = Allowlist.Merge([".evil.example", "gist.github.com"]);
        Assert.True(Allowlist.IsAllowed(allow, "a.evil.example"));
        Assert.True(Allowlist.IsDenied(deny, "a.evil.example"));
        Assert.True(Allowlist.IsDenied(deny, "evil.example"));
        Assert.True(Allowlist.IsDenied(deny, "gist.github.com"));
        Assert.False(Allowlist.IsDenied(deny, "github.com"));
    }

    [Fact]
    public void RenderFiles_WithoutTheStar()
    {
        var files = Allowlist.RenderFiles(Allowlist.Merge(["github.com", ".nuget.org"]), []);
        Assert.Equal("github.com\n.nuget.org\n", files.AllowlistText);
        Assert.Equal("", files.DenylistText);
        Assert.Equal("", files.OpenEgressText);
        // The allowlist is rendered as it always was.
        Assert.Equal(Allowlist.Render(["github.com", ".nuget.org"]), files.AllowlistText);
    }

    [Fact]
    public void RenderFiles_WithDenyEntries()
    {
        var files = Allowlist.RenderFiles(Allowlist.Merge([".github.com"]),
            Allowlist.Merge(["gist.github.com"], ["Pastebin.com", "gist.github.com"]));
        Assert.Equal(".github.com\n", files.AllowlistText);
        Assert.Equal("gist.github.com\npastebin.com\n", files.DenylistText);
        Assert.Equal("", files.OpenEgressText);
    }

    [Fact]
    public void RenderFiles_RejectsTheStarInDeny() =>
        Assert.Throws<ArgumentException>(() => Allowlist.RenderFiles(["github.com"], ["*"]));

    [Fact]
    public void RenderFiles_WithTheStar_OpensEgressAndEmptiesTheAllowlist()
    {
        var files = Allowlist.RenderFiles(Allowlist.Merge(["github.com"], ["*"]), Allowlist.Merge(["gist.github.com"]));
        Assert.Equal("", files.AllowlistText);
        Assert.Equal("gist.github.com\n", files.DenylistText);
        Assert.Equal(".\n", files.OpenEgressText);
        Assert.DoesNotContain("*", files.AllowlistText + files.DenylistText + files.OpenEgressText);
    }

    [Fact]
    public void RenderFiles_WithTheStarUnmerged_StillKeepsItOutOfTheAllowlist()
    {
        var files = Allowlist.RenderFiles(["github.com", "*"], []);
        Assert.Equal("", files.AllowlistText);
        Assert.Equal(".\n", files.OpenEgressText);
    }
}
