namespace Pen.Tests;

public class ConfigTests
{
    private static Config User(string text) => Config.Parse(text, "~/.config/pen/config", ConfigKind.User);
    private static Config Proj(string text) => Config.Parse(text, ".pen/config", ConfigKind.Project);

    private static string Error(Func<Config> f) => Assert.Throws<PenException>(() => f()).Message;

    [Fact]
    public void CommentsBlankLinesAndWhitespace()
    {
        var c = User("""
            # a comment
               # an indented comment

              env.FOO   =   bar baz
            allow=Example.COM
            """);
        Assert.Equal([new("FOO", "bar baz")], c.Env);
        Assert.Equal(["example.com"], c.Allow);
    }

    [Fact]
    public void SplitsAtTheFirstEquals()
    {
        var c = User("env.TOKEN = a=b==c\ngithub.Acme = ghp_x=y");
        Assert.Equal("a=b==c", c.Env.Single().Value);
        Assert.Equal("ghp_x=y", c.GitHubTokens["acme"]);
    }

    [Fact]
    public void HashInsideAValueIsKept() =>
        Assert.Equal("abc#def", User("env.X = abc#def").Env.Single().Value);

    [Fact]
    public void CrLfLineEndings() =>
        Assert.Equal("v", User("env.X = v\r\nallow = a.com\r\n").Env.Single().Value);

    [Fact]
    public void UnknownKeyNamesFileAndLine()
    {
        Assert.Equal("~/.config/pen/config:3: unknown key \"githib.acme\"", Error(() => User("# x\n\ngithib.acme = t")));
        Assert.Equal(".pen/config:1: unknown key \"docker\"", Error(() => Proj("docker = true"))); // not implemented yet
    }

    [Theory]
    [InlineData("github.acme = t")]
    [InlineData("env.FOO = x")]
    public void UserOnlyKeysAreRefusedInAProjectFile(string line)
    {
        var msg = Error(() => Proj("allow = a.com\n" + line));
        Assert.StartsWith(".pen/config:2:", msg);
        Assert.Contains("~/.config/pen/config", msg);
    }

    [Fact]
    public void GitHubOwnerIsProjectOnly()
    {
        Assert.Equal("acme", Proj("github_owner = acme").GitHubOwner);
        Assert.StartsWith("~/.config/pen/config:1:", Error(() => User("github_owner = acme")));
    }

    [Theory]
    [InlineData("noequals")]
    [InlineData("= value")]
    [InlineData("env.X =")]
    [InlineData("env.1X = v")]
    [InlineData("env.GH_TOKEN = v")]
    [InlineData("env.PEN_UID = v")]
    [InlineData("env.https_proxy = v")]
    [InlineData("github.bad_owner = t")]
    [InlineData("allow = not a domain")]
    [InlineData("allow = 1.2.3.4")]
    [InlineData("allow = .com")]
    [InlineData("allow = https://example.com")]
    public void InvalidLines(string line) => Assert.StartsWith("~/.config/pen/config:1:", Error(() => User(line)));

    [Fact]
    public void DuplicatesAreErrorsExceptAllow()
    {
        Assert.Contains(":2:", Error(() => User("github.acme = a\ngithub.ACME = b")));
        Assert.Contains(":2:", Error(() => User("env.X = a\nenv.X = b")));
        Assert.Contains(":2:", Error(() => Proj("github_owner = a\ngithub_owner = b")));
        Assert.Equal(2, User("allow = a.com\nallow = a.com").Allow.Count);
    }

    [Fact]
    public void TheTemplateHasEveryKeyCommentedOut()
    {
        var text = Embedded.Text("templates/config");
        Assert.All(text.Split('\n').Where(l => l.Trim().Length > 0), l => Assert.StartsWith("#", l.Trim()));
        var empty = User(text);
        Assert.Empty(empty.Env);
        Assert.Empty(empty.Allow);
        Assert.Empty(empty.GitHubTokens);
        // Uncommented, each example line parses (with a real-looking value).
        foreach (var key in new[] { "github.<owner>", "env.ANTHROPIC_API_KEY", "env.OPENAI_API_KEY", "env.CLAUDE_CODE_OAUTH_TOKEN", "allow = example.com", "allow = .example.org" })
            Assert.Contains("# " + key, text);
    }

    [Fact]
    public void FirstRunWritesThePrivateTemplateOnce()
    {
        using var t = new TempDir();
        var host = t.Host();
        Config.EnsureUserConfig(host);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(host.UserConfigPath));
        Assert.Equal(Embedded.Text("templates/config"), File.ReadAllText(host.UserConfigPath));

        File.WriteAllText(host.UserConfigPath, "env.X = mine\n");
        Config.EnsureUserConfig(host);
        Assert.Equal("env.X = mine\n", File.ReadAllText(host.UserConfigPath));
    }

    [Fact]
    public void MissingFileIsEmpty()
    {
        using var t = new TempDir();
        Assert.Empty(Config.Load(Path.Combine(t.Path, "nope"), "x", ConfigKind.Project).Allow);
    }
}
