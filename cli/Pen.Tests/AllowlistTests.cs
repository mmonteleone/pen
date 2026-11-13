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
}
