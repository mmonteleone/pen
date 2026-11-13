namespace Pen.Tests;

public class ConfigTests
{
    private const string UserFile = "~/.config/pen/config.toml";
    private const string ProjFile = ".pen/config.toml";
    private static Config User(string text) => Config.Parse(text, UserFile, ConfigKind.User);
    private static Config Proj(string text) => Config.Parse(text, ProjFile, ConfigKind.Project);

    private static string Error(Func<object> f) => Assert.Throws<PenException>(() => f()).Message;

    [Fact]
    public void TablesCommentsAndStrings()
    {
        var c = User("""
            # a comment

            [github]
            my-user = "tok1"   # trailing comment
            ACME = 'tok=2#x'

            [env]
            FOO = "bar baz"
            B = '''multi
            line'''
            """);
        Assert.Equal("tok1", c.GitHubTokens["my-user"]);
        Assert.Equal("tok=2#x", c.GitHubTokens["acme"]);
        Assert.Equal([new("FOO", "bar baz"), new("B", "multi\nline")], c.Env);
    }

    [Fact]
    public void DottedKeysAndInlineTablesWorkToo()
    {
        var c = User("github.acme = \"t\"\nenv = { X = \"1\" }\n");
        Assert.Equal("t", c.GitHubTokens["acme"]);
        Assert.Equal([new("X", "1")], c.Env);
    }

    [Fact]
    public void CrLfLineEndings() =>
        Assert.Equal("v", User("[env]\r\nX = \"v\"\r\n").Env.Single().Value);

    [Fact]
    public void TomlErrorsNameFileAndLine()
    {
        Assert.StartsWith($"{UserFile}:4:", Error(() => User("# x\n\n[env]\nX = ")));
        Assert.StartsWith($"{UserFile}:2:", Error(() => User("[env]\nX = unquoted")));
        // TOML forbids defining a key or table twice; Pen doesn't let the last one win.
        Assert.StartsWith($"{UserFile}:3:", Error(() => User("[env]\nX = \"a\"\nX = \"b\"")));
        Assert.StartsWith($"{UserFile}:3:", Error(() => User("[env]\nX = \"a\"\n[env]\nY = \"b\"")));
    }

    [Fact]
    public void UnknownKeysAreErrors()
    {
        Assert.Equal($"{UserFile}: unknown key \"githib\"", Error(() => User("[githib]\nacme = \"t\"")));
        Assert.Equal($"{ProjFile}: unknown key \"dockr\"", Error(() => Proj("dockr = true")));
    }

    [Fact]
    public void AllowIsAListOfDomains_InEitherFile()
    {
        Assert.Equal(["example.com", ".foo.io"], User("allow = [\"Example.COM\", '.foo.io']").Allow);
        Assert.Equal(["a.com", "b.com"], Proj("allow = [\n  \"a.com\",  # comment\n  # \"x.com\",\n  \"b.com\",\n]").Allow);
        Assert.Empty(Proj("allow = []").Allow);
        Assert.StartsWith($"{ProjFile}: allow must be a list", Error(() => Proj("allow = \"a.com\"")));
    }

    [Theory]
    [InlineData("\"not a domain\"")]
    [InlineData("\"1.2.3.4\"")]
    [InlineData("\".com\"")]
    [InlineData("\"https://example.com\"")]
    [InlineData("1")]
    public void InvalidAllowEntries(string entry) =>
        Assert.StartsWith($"{UserFile}: allow: ", Error(() => User($"allow = [\"ok.com\", {entry}]")));

    [Fact]
    public void DockerIsAProjectBoolean()
    {
        Assert.Null(Proj("").Docker);
        Assert.True(Proj("docker = true").Docker);
        Assert.False(Proj("docker = false").Docker);
        Assert.Equal($"{ProjFile}: docker must be true or false (unquoted)", Error(() => Proj("docker = \"true\"")));
        Assert.StartsWith($"{UserFile}: \"docker\" belongs in {ProjFile}", Error(() => User("docker = true")));
    }

    [Fact]
    public void PortsIsAProjectListOfPortNumbers_WithoutDuplicates()
    {
        Assert.Empty(Proj("").Ports);
        Assert.Empty(Proj("ports = []").Ports);
        Assert.Equal([3000, 1, 65535], Proj("ports = [3000, 1, 65535, 3000]").Ports);
        Assert.Equal([5173, 8080], Proj("ports = [\n  5173,  # vite\n  8080,\n]").Ports);
        Assert.Equal([8080], Proj("ports = [0x1F90]").Ports); // TOML integers in any base
    }

    [Theory]
    [InlineData("ports = 3000", "ports must be a list of port numbers")]
    [InlineData("ports = \"3000\"", "ports must be a list of port numbers")]
    [InlineData("ports = [0]", "ports: 0 isn't a port number (1 to 65535, unquoted)")]
    [InlineData("ports = [65536]", "ports: 65536 isn't a port number")]
    [InlineData("ports = [-80]", "ports: -80 isn't a port number")]
    [InlineData("ports = [4294967376]", "ports: 4294967376 isn't a port number")] // 2^32 + 80: no truncation to 80
    [InlineData("ports = [\"3000\"]", "ports: \"3000\" isn't a port number")]
    [InlineData("ports = [80.0]", "ports: 80 isn't a port number")]
    [InlineData("ports = [true]", "ports: True isn't a port number")]
    [InlineData("ports = [[80]]", "ports: ")]
    public void InvalidPorts(string text, string message) =>
        Assert.StartsWith($"{ProjFile}: {message}", Error(() => Proj(text)));

    [Fact]
    public void PortsBelongInTheProjectFile() =>
        Assert.Equal($"{UserFile}: \"ports\" belongs in {ProjFile} (or use pen --port)", Error(() => User("ports = [3000]")));

    [Theory]
    [InlineData("[github]\nacme = \"t\"")]
    [InlineData("[env]\nFOO = \"x\"")]
    public void UserOnlyTablesAreRefusedInAProjectFile(string text)
    {
        var msg = Error(() => Proj(text));
        Assert.StartsWith($"{ProjFile}:", msg);
        Assert.Contains(UserFile, msg);
    }

    [Fact]
    public void GitHubOwnerIsProjectOnly()
    {
        Assert.Equal("acme", Proj("github_owner = \"acme\"").GitHubOwner);
        Assert.StartsWith($"{UserFile}: \"github_owner\" belongs in", Error(() => User("github_owner = \"acme\"")));
        Assert.StartsWith($"{ProjFile}: github_owner:", Error(() => Proj("github_owner = \"bad_owner\"")));
    }

    [Theory]
    [InlineData("github = \"x\"", "github must be a table")]
    [InlineData("[github]\nacme = 1", "github.acme must be a non-empty string")]
    [InlineData("[github]\nacme = \"\"", "github.acme must be a non-empty string")]
    [InlineData("[github]\nbad_owner = \"t\"", "github.bad_owner:")]
    [InlineData("[github]\nacme = \"a\"\nACME = \"b\"", "github.ACME: a token for \"ACME\" is already set")]
    [InlineData("[env]\n\"1X\" = \"v\"", "env.1X:")]
    [InlineData("[env]\nGH_TOKEN = \"v\"", "env.GH_TOKEN: GH_TOKEN is set by pen")]
    [InlineData("[env]\nPEN_UID = \"v\"", "env.PEN_UID:")]
    [InlineData("[env]\nhttps_proxy = \"v\"", "env.https_proxy:")]
    [InlineData("[env]\nX = true", "env.X must be a non-empty string")]
    public void InvalidUserValues(string text, string message) =>
        Assert.StartsWith($"{UserFile}: {message}", Error(() => User(text)));

    [Fact]
    public void LoadReadsConfigToml_WhichMayBeMissing()
    {
        using var t = new TempDir();
        Assert.Empty(Config.Load(t.Path, ".pen", ConfigKind.Project).Allow);

        File.WriteAllText(Path.Combine(t.Path, "config.toml"), "docker = true\nallow = [\"a.com\"]\nports = [3000]\n");
        var c = Config.Load(t.Path, ".pen", ConfigKind.Project);
        Assert.Equal(["a.com"], c.Allow);
        Assert.True(c.Docker);
        Assert.Equal([3000], c.Ports);

        File.WriteAllText(Path.Combine(t.Path, "config.toml"), "docker = 1\n");
        Assert.StartsWith(".pen/config.toml: docker", Error(() => Config.Load(t.Path, ".pen", ConfigKind.Project)));
    }

    [Fact]
    public void ALegacyConfigFileIsAnError()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "config"), "allow = a.com\n");
        var msg = Error(() => Config.Load(t.Path, ".pen", ConfigKind.Project));
        Assert.StartsWith(".pen/config is pen's old config format", msg);
        Assert.Contains(".pen/config.toml", msg);
    }

    [Fact]
    public void TheTemplatesParseEmpty_WithEveryExampleCommentedOut()
    {
        var text = Embedded.ReadText("templates/config.toml");
        var empty = User(text);
        Assert.Empty(empty.Env);
        Assert.Empty(empty.GitHubTokens);
        Assert.Empty(empty.Allow);
        Assert.Empty(empty.Tools);
        foreach (var example in new[] { "\"example.com\",", "tools = [", "my-user = \"github_pat_", "ANTHROPIC_API_KEY = ", "OPENAI_API_KEY = ", "CLAUDE_CODE_OAUTH_TOKEN = " })
            Assert.Contains("# " + example, text);
        // Uncommented, the examples parse.
        string[] examples = ["  # \"", "# tools = ", "# my-", "# ANTHROPIC", "# OPENAI", "# CLAUDE_CODE"];
        var uncommented = string.Join('\n', text.Split('\n').Select(l => examples.Any(l.StartsWith) ? l.Replace("# ", "") : l));
        var full = User(uncommented);
        Assert.Equal(["example.com", ".example.org"], full.Allow);
        Assert.Equal(["claude", "codex", "go"], full.Tools.Select(t => t.Name));
        Assert.Equal(2, full.GitHubTokens.Count);
        Assert.Equal(3, full.Env.Count);
    }

    [Fact]
    public void FirstRunWritesThePrivateTemplatesOnce()
    {
        using var t = new TempDir();
        var host = t.Host();
        Config.EnsureUserConfig(host, () => ("T", "t@example.invalid"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(host.UserConfigPath));
        Assert.Equal(Embedded.ReadText("templates/config.toml"), File.ReadAllText(host.UserConfigPath));

        File.WriteAllText(host.UserConfigPath, "[env]\nX = \"mine\"\n");
        Config.EnsureUserConfig(host, () => ("T", "t@example.invalid"));
        Assert.Equal("[env]\nX = \"mine\"\n", File.ReadAllText(host.UserConfigPath));

        // A missing config.toml is recreated in an existing dir, but not next to a legacy config.
        File.Delete(host.UserConfigPath);
        Config.EnsureUserConfig(host, () => ("T", "t@example.invalid"));
        Assert.True(File.Exists(host.UserConfigPath));
        File.Delete(host.UserConfigPath);
        File.WriteAllText(Path.Combine(host.ConfigDir, "config"), "");
        Config.EnsureUserConfig(host, () => ("T", "t@example.invalid"));
        Assert.False(File.Exists(host.UserConfigPath));
    }
}
