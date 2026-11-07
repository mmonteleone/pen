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
    public void OwnerFromUrl(string url, string owner) => Assert.Equal(owner, GitHub.OwnerFromUrl(url));

    [Theory]
    [InlineData("https://gitlab.com/acme/repo.git")]
    [InlineData("https://github.company.com/acme/repo")]
    [InlineData("https://evilgithub.com/acme/repo")]
    [InlineData("git@bitbucket.org:acme/repo.git")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("https://github.com/")]
    public void NotAGitHubOwner(string url) => Assert.Null(GitHub.OwnerFromUrl(url));

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
        Assert.Equal("git@github.com:acme/repo.git", GitHub.OriginUrl(config));
    }

    [Fact]
    public void OriginUrlQuotedWithComment() =>
        Assert.Equal("https://github.com/a b/r", GitHub.OriginUrl("[Remote \"origin\"]\n  URL = \"https://github.com/a b/r\" ; note\n"));

    [Fact]
    public void OriginSubsectionIsCaseSensitive() =>
        Assert.Null(GitHub.OriginUrl("[remote \"Origin\"]\nurl = https://github.com/a/r\n"));

    [Fact]
    public void MissingOrigin()
    {
        Assert.Null(GitHub.OriginUrl("[core]\nbare = false\n"));
        Assert.Null(GitHub.OriginUrl(""));
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
        Assert.Equal("acme", GitHub.ProjectOwner(p, Config.Empty));
        Assert.Equal("fork", GitHub.ProjectOwner(p, Config.Parse("github_owner = \"fork\"", "x", ConfigKind.Project)));
        Assert.Null(GitHub.ProjectOwner(RepoWithOrigin(new TempDir(), null), Config.Empty));
    }

    [Fact]
    public void TokenNoticeOncePerProject_AndAgainAfterATokenIsRemoved()
    {
        using var t = new TempDir();
        var host = t.Host();
        var runtime = t.Sub("runtime");
        var marker = Path.Combine(runtime, "token-notice");
        var none = Config.Empty;
        var with = Config.Parse("[github]\nACME = \"tok\"", "x", ConfigKind.User);

        Assert.Null(GitHub.TokenFor("acme", none, runtime, host));
        Assert.True(File.Exists(marker));
        Assert.Null(GitHub.TokenFor("acme", none, runtime, host));

        Assert.Equal("tok", GitHub.TokenFor("Acme", with, runtime, host)); // owner match is case-insensitive
        Assert.False(File.Exists(marker));

        Assert.Null(GitHub.TokenFor("acme", none, runtime, host));
        Assert.True(File.Exists(marker));

        Assert.Null(GitHub.TokenFor(null, with, runtime, host));
    }
}
