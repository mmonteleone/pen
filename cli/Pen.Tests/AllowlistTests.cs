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
            ConfigKind.User, RecipeCatalog.Embedded);
        var project = Config.Parse("deny = [\"a.evil.example\", \"Pastebin.com\", \"gist.github.com\"]",
            ".pen/config.toml", ConfigKind.Project, RecipeCatalog.Embedded);
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
        var files = Allowlist.RenderFiles(Allowlist.Merge(["github.com", ".nuget.org"]), open: false, []);
        Assert.Equal("github.com\n.nuget.org\n", files.AllowlistText);
        Assert.Equal("", files.DenylistText);
        Assert.Equal("", files.OpenEgressText);
        // The allowlist is rendered as it always was.
        Assert.Equal(Allowlist.Render(["github.com", ".nuget.org"]), files.AllowlistText);
    }

    [Fact]
    public void RenderFiles_WithDenyEntries()
    {
        var files = Allowlist.RenderFiles(Allowlist.Merge([".github.com"]), open: false,
            Allowlist.Merge(["gist.github.com"], ["Pastebin.com", "gist.github.com"]));
        Assert.Equal(".github.com\n", files.AllowlistText);
        Assert.Equal("gist.github.com\npastebin.com\n", files.DenylistText);
        Assert.Equal("", files.OpenEgressText);
    }

    [Fact]
    public void RenderFiles_RejectsTheStarInDeny() =>
        Assert.Throws<ArgumentException>(() => Allowlist.RenderFiles(["github.com"], open: false, ["*"]));

    [Fact]
    public void RenderFiles_RejectsTheStarInTheNamedList()
    {
        // A collapsed merge passed by mistake would otherwise drop every named domain, or write "*" as one.
        Assert.Throws<ArgumentException>(() => Allowlist.RenderFiles(Allowlist.Merge(["github.com", "*"]), true, []));
        Assert.Throws<ArgumentException>(() => Allowlist.RenderFiles(["github.com", " * "], true, []));
    }

    [Fact]
    public void MergeNamed_LeavesOutTheStar_AndKeepsWhatItWouldHaveSwallowed()
    {
        IEnumerable<string>[] sources = [["github.com", ".nuget.org"], ["a.com", " * "], ["api.nuget.org", "B.com"]];
        Assert.Equal(["*"], Allowlist.Merge(sources));
        Assert.Equal(["github.com", ".nuget.org", "a.com", "b.com"], Allowlist.MergeNamed(sources));
        Assert.Empty(Allowlist.MergeNamed(["*"]));
        Assert.Equal(Allowlist.Merge(["x.com", ".y.com", "a.y.com"]), Allowlist.MergeNamed(["x.com", ".y.com", "a.y.com"]));
    }

    [Fact]
    public void RenderFiles_WithTheStar_OpensEgressAndKeepsTheNamedDomains()
    {
        // A domain allowed by name reaches a private address that open egress doesn't, so it stays listed.
        IEnumerable<string>[] sources = [["github.com", "127.0.0.1.nip.io"], ["*"]];
        var files = Allowlist.RenderFiles(Allowlist.MergeNamed(sources), Allowlist.Merge(sources).Contains("*"),
            Allowlist.Merge(["gist.github.com"]));
        Assert.Equal("github.com\n127.0.0.1.nip.io\n", files.AllowlistText);
        Assert.Equal("gist.github.com\n", files.DenylistText);
        Assert.Equal(".\n", files.OpenEgressText);
        Assert.DoesNotContain("*", files.AllowlistText + files.DenylistText + files.OpenEgressText);
    }

    [Fact]
    public void RenderFiles_WithOnlyTheStar_HasAnEmptyAllowlist()
    {
        var files = Allowlist.RenderFiles(Allowlist.MergeNamed(["*"]), open: true, []);
        Assert.Equal("", files.AllowlistText);
        Assert.Equal(".\n", files.OpenEgressText);
    }

    [Fact]
    public void OpenEgress_IsReadBackFromWhatRenderFilesWrites()
    {
        Assert.True(Allowlist.IsOpenEgress(Allowlist.RenderFiles([], open: true, []).OpenEgressText));
        Assert.False(Allowlist.IsOpenEgress(Allowlist.RenderFiles(["github.com"], open: false, []).OpenEgressText));
        Assert.False(Allowlist.IsOpenEgress(""));
        Assert.False(Allowlist.IsOpenEgress("a^\n"));
    }

    private static HashSet<string> Set(params string[] entries) => entries.ToHashSet(StringComparer.Ordinal);

    [Fact]
    public void BlockedLines_WithoutDenyOrTheStar_AreTheBlockedLineForWhatsNotAllowedNow()
    {
        string[] hosts = ["foo.com", "github.com", "1.2.3.4", "bar.io"];
        Assert.Equal([Allowlist.FormatBlockedLine(["foo.com", "1.2.3.4", "bar.io"])!],
            Allowlist.FormatBlockedLines(hosts, Set("github.com"), Set()));
        Assert.Equal(["blocked: foo.com, 1.2.3.4, bar.io → pen allow foo.com bar.io (add --global for all projects)"],
            Allowlist.FormatBlockedLines(hosts, Set("github.com"), Set()));
        Assert.Empty(Allowlist.FormatBlockedLines(["github.com"], Set("github.com"), Set()));
        Assert.Empty(Allowlist.FormatBlockedLines([], Set(), Set()));
    }

    [Fact]
    public void BlockedLines_WithDeny_ListDeniedAndRawIpsApart_AndOfferPenAllowOnlyForTheRest()
    {
        var lines = Allowlist.FormatBlockedLines(
            ["gist.github.com", "foo.com", "1.2.3.4", "api.github.com", "2001:db8::1", "x.pastebin.com"],
            Set(".github.com"), Set("gist.github.com", ".pastebin.com"));
        Assert.Equal(
        [
            "denied by config: gist.github.com, x.pastebin.com",
            "blocked (raw IP): 1.2.3.4, 2001:db8::1",
            "blocked: foo.com → pen allow foo.com (add --global for all projects)",
        ], lines);
    }

    [Fact]
    public void BlockedLines_DenyBeatsAnAllowForTheSameHost()
    {
        Assert.Equal(["denied by config: gist.github.com"],
            Allowlist.FormatBlockedLines(["gist.github.com"], Set("gist.github.com"), Set("gist.github.com")));
    }

    [Fact]
    public void BlockedLines_InOpenEgress_ABlockedNameIsGroupedWithRawIps_AndNothingGetsPenAllow()
    {
        var lines = Allowlist.FormatBlockedLines(
            ["host.docker.internal", "gist.github.com", "140.82.112.4", "[::1]", "nx.invalid"],
            Set(Allowlist.AnyDomain), Set("gist.github.com"));
        Assert.Equal(
        [
            "denied by config: gist.github.com",
            "blocked (raw IP, private address, unresolvable name, another port or blocked earlier): host.docker.internal, "
                + "140.82.112.4, [::1], nx.invalid",
        ], lines);
        Assert.DoesNotContain(lines, line => line.Contains("pen allow", StringComparison.Ordinal));
        Assert.Equal(["blocked (raw IP, private address, unresolvable name, another port or blocked earlier): localhost"],
            Allowlist.FormatBlockedLines(["localhost"], Set(Allowlist.AnyDomain), Set()));
    }

    [Fact]
    public void BlockedLines_InOpenEgress_LeaveOutWhatsAllowedByNameNow()
    {
        // Allowed by name, it gets through now (a private address too); deny still beats it.
        var lines = Allowlist.FormatBlockedLines(
            ["127.0.0.1.nip.io", "a.internal.example", "localhost", "gist.github.com"],
            Set(Allowlist.AnyDomain, "127.0.0.1.nip.io", ".internal.example", "gist.github.com"),
            Set("gist.github.com"));
        Assert.Equal(
        [
            "denied by config: gist.github.com",
            "blocked (raw IP, private address, unresolvable name, another port or blocked earlier): localhost",
        ], lines);
        Assert.Empty(Allowlist.FormatBlockedLines(["127.0.0.1.nip.io"],
            Set(Allowlist.AnyDomain, "127.0.0.1.nip.io"), Set()));
    }

    [Fact]
    public void BlockedLines_EachGroupShowsTheLatestFew()
    {
        var hosts = Enumerable.Range(0, 30).Select(i => $"h{i}.denied.example").ToList();
        var line = Assert.Single(Allowlist.FormatBlockedLines(hosts, Set(), Set(".denied.example")));
        Assert.Equal("denied by config: " + string.Join(", ", Enumerable.Range(10, 20).Select(i => $"h{i}.denied.example"))
            + " and 10 more (pen allow lists the latest)", line);
    }

    [Fact]
    public void BlockedLines_SanitizeHosts()
    {
        var lines = Allowlist.FormatBlockedLines(["evil\u001b[2J.denied.example"], Set(), Set(".denied.example"));
        Assert.DoesNotContain('\u001b', Assert.Single(lines));
    }

    // The session-start warnings about egress.

    private static List<string> EgressWarnings(string user, string project, string[] deny,
        params (string Tool, IReadOnlyList<string> Domains)[] tools) =>
        Session.FormatEgressWarnings(Config.Parse(user, "u", ConfigKind.User, RecipeCatalog.Embedded), "~/.config/pen/config.toml",
            Config.Parse(project, "p", ConfigKind.Project, RecipeCatalog.Embedded), deny, tools);

    [Fact]
    public void EgressWarnings_OpenEgressNamesTheFilesWithTheStar()
    {
        Assert.Equal(["egress is open: any domain is allowed (allow = [\"*\"] in ~/.config/pen/config.toml)"],
            EgressWarnings("allow = [\"*\"]\n", "", []));
        Assert.Equal(["egress is open: any domain is allowed (allow = [\"*\"] in .pen/config.toml)"],
            EgressWarnings("", "allow = [\"github.com\", \"*\"]\n", []));
        Assert.Equal(["egress is open: any domain is allowed (allow = [\"*\"] in ~/.config/pen/config.toml and .pen/config.toml)"],
            EgressWarnings("allow = [\"*\"]\n", "allow = [\"*\"]\n", []));
        Assert.Empty(EgressWarnings("allow = [\".github.com\"]\n", "", ["gist.github.com"]));
    }

    [Fact]
    public void EgressWarnings_DenyUnderAToolsWildcardNamesTheDeniedEntry()
    {
        Assert.Equal(["deny covers api.anthropic.com, which claude needs"],
            EgressWarnings("", "", ["api.anthropic.com", "gist.github.com"],
                ("claude", [".anthropic.com", ".claude.ai"]), ("rust", ["index.crates.io"])));
    }

    [Fact]
    public void EgressWarnings_DenyOverAToolsDomainNamesTheToolsDomain_AndEveryToolThatNeedsIt()
    {
        Assert.Equal(["deny covers .anthropic.com, which claude and pi need", "deny covers index.crates.io, which rust needs"],
            EgressWarnings("", "", [".anthropic.com", ".crates.io"],
                ("claude", [".anthropic.com"]), ("pi", [".anthropic.com", ".openai.com"]), ("rust", ["index.crates.io"])));
        Assert.Equal(["deny covers .x.example, which a, b and c need"],
            EgressWarnings("", "", [".x.example"], ("a", [".x.example"]), ("b", [".x.example"]), ("c", [".x.example"])));
    }

    [Fact]
    public void EgressWarnings_NothingForADenyBesideAToolsDomain()
    {
        Assert.Empty(EgressWarnings("", "", ["anthropic.com.evil", "xapi.anthropic.com"], ("claude", ["api.anthropic.com"])));
    }
}
