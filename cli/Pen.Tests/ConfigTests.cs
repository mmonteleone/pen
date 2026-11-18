namespace Pen.Tests;

public class ConfigTests
{
    private const string UserFile = "~/.config/pen/config.toml";
    private const string ProjFile = ".pen/config.toml";
    private static Config User(string text) => ConfigParser.ParseUser(text, UserFile, RecipeCatalog.Embedded);
    private static Config Proj(string text) => ConfigParser.ParseProject(text, ProjFile, RecipeCatalog.Embedded);

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
    public void ABareTldIsRejectedInProjectAllow() =>
        Assert.StartsWith($"{ProjFile}: allow: ", Error(() => Proj("allow = [\"ok.com\", \".com\"]")));

    [Fact]
    public void DenyIsAListOfDomains_InEitherFile()
    {
        Assert.Equal(["gist.github.com", ".foo.io"], User("deny = [\"Gist.GitHub.com\", '.foo.io']").Deny);
        Assert.Equal(["a.com", "b.com"], Proj("deny = [\n  \"a.com\",  # comment\n  \"b.com\",\n]").Deny);
        Assert.Empty(Proj("deny = []").Deny);
        Assert.Empty(Proj("deny = [\"a.com\"]").Allow);
        Assert.Empty(Proj("allow = [\"a.com\"]").Deny);
        var both = User("allow = [\".github.com\"]\ndeny = [\"gist.github.com\"]");
        Assert.Equal([".github.com"], both.Allow);
        Assert.Equal(["gist.github.com"], both.Deny);
        Assert.Equal($"{ProjFile}: deny must be a list of domains, e.g. deny = [\"example.com\"]",
            Error(() => Proj("deny = \"a.com\"")));
    }

    [Theory]
    [InlineData("\"not a domain\"")]
    [InlineData("\"1.2.3.4\"")]
    [InlineData("\".com\"")]
    [InlineData("\".\"")]
    [InlineData("\"*.example.com\"")]
    [InlineData("\"**\"")]
    [InlineData("\"https://example.com\"")]
    [InlineData("1")]
    public void InvalidDenyEntries(string entry)
    {
        Assert.StartsWith($"{UserFile}: deny: ", Error(() => User($"deny = [\"ok.com\", {entry}]")));
        Assert.StartsWith($"{ProjFile}: deny: ", Error(() => Proj($"deny = [\"ok.com\", {entry}]")));
    }

    [Fact]
    public void DenyRefusesTheStar()
    {
        Assert.Equal($"{UserFile}: deny = [\"*\"] would block everything; remove the allow entries instead",
            Error(() => User("deny = [\"*\"]")));
        Assert.Equal($"{ProjFile}: deny = [\"*\"] would block everything; remove the allow entries instead",
            Error(() => Proj("deny = [\"a.com\", '*']")));
    }

    [Fact]
    public void AllowTakesTheStar_InEitherFile()
    {
        Assert.Equal(["*"], User("allow = [\"*\"]").Allow);
        Assert.Equal(["a.com", "*"], Proj("allow = [\"a.com\", '*']").Allow);
    }

    [Theory]
    [InlineData("\".\"")]
    [InlineData("\"*.example.com\"")]
    [InlineData("\"**\"")]
    [InlineData("\" *\"")]
    [InlineData("\"*.\"")]
    [InlineData("\".*\"")]
    public void AllowTakesTheStarOnlyAsTheWholeEntry(string entry)
    {
        Assert.StartsWith($"{UserFile}: allow: ", Error(() => User($"allow = [{entry}]")));
        Assert.StartsWith($"{ProjFile}: allow: ", Error(() => Proj($"allow = [{entry}]")));
    }

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

    [Fact]
    public void HideIsAListOfPatterns_InEitherFile()
    {
        Assert.Empty(Proj("").Hide);
        Assert.Empty(User("hide = []").Hide);
        Assert.Equal([".env", "*.ps1", "secrets/"],
            User("hide = [\".env\", '*.ps1', \"secrets/\"]").Hide.Select(p => p.Text));
        var project = Proj("hide = [\n  \"/apps/web/.env\",  # comment\n  \".env.*\",\n]").Hide;
        Assert.Equal(["/apps/web/.env", ".env.*"], project.Select(p => p.Text));
        Assert.True(project[0].IsLiteral);
        Assert.True(project[1].IsMatch("a/.env.local", false));
    }

    [Theory]
    [InlineData("hide = \".env\"", "hide must be a list of patterns, e.g. hide = [\".env\", \"secrets/\"]")]
    [InlineData("[hide]\nx = \"y\"", "hide must be a list of patterns")]
    [InlineData("hide = [3]", "hide: 3 isn't a pattern (put it in quotes)")]
    [InlineData("hide = [\".env\", true]", "hide: True isn't a pattern")]
    [InlineData("hide = [[\".env\"]]", "hide: ")]
    [InlineData("hide = [\"\"]", "hide: \"\": a pattern can't be empty")]
    [InlineData("hide = [\"ok\", \"[ab]\"]", "hide: \"[ab]\": [, ] and \\ aren't supported in patterns (use * or ?)")]
    [InlineData("hide = [\"a/../b\"]", "hide: \"a/../b\": a pattern can't have . or .. segments")]
    [InlineData("hide = [\"sub/.git/config\"]", "hide: \"sub/.git/config\": a pattern can't reach into .git")]
    [InlineData("hide = [\".pen/\"]", "hide: \".pen/\": a pattern can't start with .pen, Pen's own directory")]
    [InlineData("hide = [\"**\"]", "hide: \"**\": this pattern would hide the whole project")]
    public void InvalidHide(string text, string message)
    {
        Assert.StartsWith($"{UserFile}: {message}", Error(() => User(text)));
        Assert.StartsWith($"{ProjFile}: {message}", Error(() => Proj(text)));
    }

    [Fact]
    public void ReplaceIsAProjectTableOfPaths_InFileOrder()
    {
        Assert.Empty(Proj("").Replace);
        var replace = Proj("""
            [replace]
            ".env" = ".pen/agent.env"
            "apps/web/.env.local" = "fixtures/web.env"
            "README" = '.pen/readme'
            """).Replace;
        Assert.Equal([
            new(".env", ".pen/agent.env"),
            new("apps/web/.env.local", "fixtures/web.env"),
            new("README", ".pen/readme"),
        ], replace);
        Assert.Equal([new("a.env", "b")], Proj("replace = { \"a.env\" = \"b\" }").Replace);
    }

    [Fact]
    public void ReplaceBelongsInTheProjectFile() =>
        Assert.Equal($"{UserFile}: \"replace\" belongs in {ProjFile}",
            Error(() => User("[replace]\n\".env\" = \".pen/agent.env\"")));

    [Theory]
    // An unquoted dotted key makes a subtable.
    [InlineData("[replace]\napps.env = \"x\"", "replace: \"apps\" must be set to a path in quotes")]
    [InlineData("[replace]\n\".env\" = 1", "replace: \".env\" must be set to a path in quotes, " +
        "and a target path goes in quotes as one key, e.g. \".env\" = \".pen/agent.env\"")]
    [InlineData("[replace]\n\".env\" = [\"x\"]", "replace: \".env\" must be set to a path in quotes")]
    [InlineData("replace = \".env\"", "replace must be a table ([replace])")]
    [InlineData("replace = [\".env\"]", "replace must be a table ([replace])")]
    public void ReplaceValuesMustBePaths(string text, string message) =>
        Assert.StartsWith($"{ProjFile}: {message}", Error(() => Proj(text)));

    [Fact]
    public void AReplacePathOfAtMost1024CharactersIsAcceptedAndALongerOneRefused()
    {
        Assert.Single(Proj($"[replace]\n\"{new string('a', 1024)}\" = \"{new string('b', 1024)}\"").Replace);
        var problem = "can't be longer than 1024 characters";
        Assert.Equal($"{ProjFile}: replace: target \"{new string('a', 1025)}\" {problem}",
            Error(() => Proj($"[replace]\n\"{new string('a', 1025)}\" = \"src\"")));
        Assert.Equal($"{ProjFile}: replace: source \"{new string('b', 1025)}\" (for \"t\") {problem}",
            Error(() => Proj($"[replace]\n\"t\" = \"{new string('b', 1025)}\"")));
    }

    [Theory]
    [InlineData("", "is empty")]
    [InlineData("/etc/passwd", "must be relative to the project, not absolute")]
    [InlineData("apps/", "must name a file, so it can't end in /")]
    [InlineData("*.env", "can't have *, ?, [, ] or \\ in it")]
    [InlineData("a?.env", "can't have *, ?, [, ] or \\ in it")]
    [InlineData("[a].env", "can't have *, ?, [, ] or \\ in it")]
    [InlineData("a\\\\b", "can't have *, ?, [, ] or \\ in it")]
    [InlineData("a//b", "can't have an empty segment (//)")]
    [InlineData("./.env", "can't have . or .. segments")]
    [InlineData("../.env", "can't have . or .. segments")]
    [InlineData("a/../.env", "can't have . or .. segments")]
    [InlineData(".git/config", "can't be in .git")]
    [InlineData("sub/.GIT/config", "can't be in .git")]
    [InlineData(".git", "can't be in .git")]
    public void InvalidReplacePaths(string path, string problem)
    {
        Assert.Equal($"{ProjFile}: replace: target \"{path.Replace("\\\\", "\\")}\" {problem}",
            Error(() => Proj($"[replace]\n\"{path}\" = \"src\"")));
        Assert.Equal($"{ProjFile}: replace: source \"{path.Replace("\\\\", "\\")}\" (for \"t\") {problem}",
            Error(() => Proj($"[replace]\n\"t\" = \"{path}\"")));
    }

    [Fact]
    public void AReplacePathWithANulSaysSo()
    {
        Assert.Equal($"{ProjFile}: replace: target \"a\\x00b\" contains a NUL character",
            Error(() => Proj("[replace]\n\"a\\u0000b\" = \"src\"")));
        Assert.Equal($"{ProjFile}: replace: source \"a\\x00b\" (for \"t\") contains a NUL character",
            Error(() => Proj("[replace]\n\"t\" = \"a\\u0000b\"")));
    }

    [Theory]
    [InlineData(".pen/config.toml")]
    [InlineData(".PEN/x")]
    public void AReplaceTargetCantBeInPen_ButASourceCan(string path)
    {
        Assert.Equal($"{ProjFile}: replace: target \"{path}\" can't be in .pen, Pen's own directory",
            Error(() => Proj($"[replace]\n\"{path}\" = \"src\"")));
        Assert.Equal([new("t", path)], Proj($"[replace]\n\"t\" = \"{path}\"").Replace);
    }

    [Fact]
    public void ReplaceTargetsThatDifferOnlyInCaseAreRefused() =>
        Assert.Equal($"{ProjFile}: replace: \".env\" and \".ENV\" are the same file where the filesystem ignores case",
            Error(() => Proj("[replace]\n\".env\" = \"a\"\n\".ENV\" = \"b\"")));

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
        Assert.Empty(ConfigLoader.LoadProject(t.Path, ".pen", RecipeCatalog.Embedded).Allow);

        File.WriteAllText(Path.Combine(t.Path, "config.toml"), "docker = true\nallow = [\"a.com\"]\nports = [3000]\n");
        var c = ConfigLoader.LoadProject(t.Path, ".pen", RecipeCatalog.Embedded);
        Assert.Equal(["a.com"], c.Allow);
        Assert.True(c.Docker);
        Assert.Equal([3000], c.Ports);

        File.WriteAllText(Path.Combine(t.Path, "config.toml"), "docker = 1\n");
        Assert.StartsWith(".pen/config.toml: docker", Error(() => ConfigLoader.LoadProject(t.Path, ".pen", RecipeCatalog.Embedded)));
    }

    [Fact]
    public void ALegacyConfigFileIsAnError()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "config"), "allow = a.com\n");
        var msg = Error(() => ConfigLoader.LoadProject(t.Path, ".pen", RecipeCatalog.Embedded));
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
        UserConfigInitializer.EnsureExists(host, () => new GitIdentity("T", "t@example.invalid"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(host.UserConfigPath));
        Assert.Equal(Embedded.ReadText("templates/config.toml"), File.ReadAllText(host.UserConfigPath));

        File.WriteAllText(host.UserConfigPath, "[env]\nX = \"mine\"\n");
        UserConfigInitializer.EnsureExists(host, () => new GitIdentity("T", "t@example.invalid"));
        Assert.Equal("[env]\nX = \"mine\"\n", File.ReadAllText(host.UserConfigPath));

        // A missing config.toml is recreated in an existing dir, but not next to a legacy config.
        File.Delete(host.UserConfigPath);
        UserConfigInitializer.EnsureExists(host, () => new GitIdentity("T", "t@example.invalid"));
        Assert.True(File.Exists(host.UserConfigPath));
        File.Delete(host.UserConfigPath);
        File.WriteAllText(Path.Combine(host.ConfigDir, "config"), "");
        UserConfigInitializer.EnsureExists(host, () => new GitIdentity("T", "t@example.invalid"));
        Assert.False(File.Exists(host.UserConfigPath));
    }
}
