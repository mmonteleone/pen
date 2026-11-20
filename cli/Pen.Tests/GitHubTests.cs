namespace Pen.Tests;

public class GitHubTests
{
    [Theory]
    [InlineData("https://github.com/acme/repo.git", "acme")]
    [InlineData("https://github.com/Acme-Corp/repo", "Acme-Corp")]
    [InlineData("https://user@github.com/acme/repo.git", "acme")]
    [InlineData("https://GitHub.com/acme/repo", "acme")]
    [InlineData("git@github.com:acme/repo.git", "acme")]
    [InlineData("github.com:acme/repo.git", "acme")]
    [InlineData("ssh://git@github.com/acme/repo.git", "acme")]
    [InlineData("ssh://git@github.com:22/acme/repo", "acme")]
    [InlineData("git://github.com/acme/repo", "acme")]
    public void OwnerFromUrl(string url, string owner) => Assert.Equal(owner, GitHub.ParseOwner(url));

    [Theory]
    [InlineData("https://gitlab.com/acme/repo.git")]
    [InlineData("https://github.company.com/acme/repo")]
    [InlineData("https://evilgithub.com/acme/repo")]
    [InlineData("git@bitbucket.org:acme/repo.git")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("https://github.com/")]
    public void NotAGitHubOwner(string url) => Assert.Null(GitHub.ParseOwner(url));

    [Fact]
    public void OriginUrlFromGitConfig()
    {
        const string config = """
            [core]
            	repositoryformatversion = 0
            	bare = false
            [remote "upstream"]
            	url = https://github.com/upstream/repo.git
            [remote "origin"]
            	url = git@github.com:acme/repo.git
            	fetch = +refs/heads/*:refs/remotes/origin/*
            [branch "main"]
            	remote = origin
            """;
        Assert.Equal("git@github.com:acme/repo.git", GitHub.FindOriginUrl(config));
    }

    [Fact]
    public void OriginUrlQuotedWithComment() =>
        Assert.Equal("https://github.com/a b/r", GitHub.FindOriginUrl("[Remote \"origin\"]\n  URL = \"https://github.com/a b/r\" ; note\n"));

    [Fact]
    public void OriginSubsectionIsCaseSensitive() =>
        Assert.Null(GitHub.FindOriginUrl("[remote \"Origin\"]\nurl = https://github.com/a/r\n"));

    [Fact]
    public void MissingOrigin()
    {
        Assert.Null(GitHub.FindOriginUrl("[core]\nbare = false\n"));
        Assert.Null(GitHub.FindOriginUrl(""));
    }

    private static Project RepoWithOrigin(TempDir t, string? url)
    {
        var dir = t.Sub("repo");
        var git = Path.Combine(dir, ".git");
        Directory.CreateDirectory(git);
        File.WriteAllText(Path.Combine(git, "config"), url == null ? "[core]\n" : $"[remote \"origin\"]\n\turl = {url}\n");
        return new Project(dir, "pen-repo-00000000", git, dir);
    }

    [Fact]
    public void ProjectOwner_OverrideWins()
    {
        using var t = new TempDir();
        var p = RepoWithOrigin(t, "https://github.com/acme/r");
        Assert.Equal("acme", GitHub.FindProjectOwner(p, ProjectConfiguration.Empty));
        Assert.Equal("fork", GitHub.FindProjectOwner(p, ConfigParser.ParseProject("github_owner = \"fork\"", "x", RecipeCatalog.Embedded)));
        Assert.Null(GitHub.FindProjectOwner(RepoWithOrigin(new TempDir(), null), ProjectConfiguration.Empty));
    }

    [Fact]
    public void TokenNoticeOncePerProject_AndAgainAfterATokenIsRemoved()
    {
        using var t = new TempDir();
        var host = t.Host();
        var runtime = t.Sub("runtime");
        var marker = Path.Combine(runtime, "token-notice");
        var none = UserConfiguration.Empty;
        var with = ConfigParser.ParseUser("[github]\nACME = \"tok\"", "x", RecipeCatalog.Embedded);

        Assert.Null(GitHub.SelectTokenAndUpdateNotice("acme", none, runtime, host));
        Assert.True(File.Exists(marker));
        Assert.Null(GitHub.SelectTokenAndUpdateNotice("acme", none, runtime, host));

        Assert.Equal("tok", GitHub.SelectTokenAndUpdateNotice("Acme", with, runtime, host)); // owner match is case-insensitive
        Assert.False(File.Exists(marker));

        Assert.Null(GitHub.SelectTokenAndUpdateNotice("acme", none, runtime, host));
        Assert.True(File.Exists(marker));

        Assert.Null(GitHub.SelectTokenAndUpdateNotice(null, with, runtime, host));
    }
}

/// <summary>Which token a session gets, and the notice that's printed once when there's none.</summary>
[Collection(nameof(LockAndProcessTests))] // it replaces the console's stderr
public class GitHubTokenNoticeTests
{
    private static string? Select(string? owner, UserConfiguration user, string runtimeDir, HostEnv host) =>
        GitHub.SelectTokenAndUpdateNotice(owner, user, runtimeDir, host);

    private static (string? Token, string Stderr) Capturing(Func<string?> select)
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { return (select(), captured.ToString()); }
        finally { Console.SetError(stderr); }
    }

    [Fact]
    public void TheTokenIsTheOwnersIgnoringCase_AndTheNoticeIsPrintedOnceUntilATokenClearsIt()
    {
        using var t = new TempDir();
        var host = t.Host();
        var runtime = t.Sub("runtime");
        var marker = Path.Combine(runtime, "token-notice");
        var none = ConfigParser.ParseUser("[github]\nother = \"tok_other\"\n", "u", RecipeCatalog.Embedded);
        var with = ConfigParser.ParseUser("[github]\nAcme = \"tok_acme\"\nother = \"tok_other\"\n", "u", RecipeCatalog.Embedded);
        var notice = $"pen: no GitHub token for 'Acme': add Acme = \"github_pat_…\" under [github] in {host.Display(host.UserConfigPath)}\n";

        // No owner: no token, no notice and no marker, whatever the user has.
        Assert.Equal((null, ""), Capturing(() => Select(null, with, runtime, host)));
        Assert.Equal((null, ""), Capturing(() => Select(null, none, runtime, host)));
        Assert.False(Path.Exists(marker));

        // A token for the owner: no notice, and no marker is made.
        Assert.Equal(("tok_acme", ""), Capturing(() => Select("acme", with, runtime, host)));
        Assert.Equal(("tok_acme", ""), Capturing(() => Select("ACME", with, runtime, host)));
        Assert.False(Path.Exists(marker));

        // None for the owner: the notice once, naming the owner as given, and an empty marker.
        Assert.Equal((null, notice), Capturing(() => Select("Acme", none, runtime, host)));
        Assert.Equal("", File.ReadAllText(marker));
        Assert.Equal((null, ""), Capturing(() => Select("Acme", none, runtime, host)));
        Assert.Equal((null, ""), Capturing(() => Select("acme", none, runtime, host)));
        Assert.True(File.Exists(marker));

        // A token again clears the marker silently, so losing it notices again.
        Assert.Equal(("tok_acme", ""), Capturing(() => Select("Acme", with, runtime, host)));
        Assert.False(Path.Exists(marker));
        Assert.Equal((null, notice), Capturing(() => Select("Acme", none, runtime, host)));
        Assert.True(File.Exists(marker));

        // The marker is per runtime dir.
        var otherRuntime = t.Sub("other-runtime");
        Assert.Equal((null, notice), Capturing(() => Select("Acme", none, otherRuntime, host)));
    }
}
