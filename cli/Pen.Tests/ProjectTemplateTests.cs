using System.Text.RegularExpressions;

namespace Pen.Tests;

/// <summary>
/// ProjectTemplate renders the embedded project template: every key commented, with detected tools and hide
/// patterns turned on and hints left as comments, always valid as a project config.
/// </summary>
public class ProjectTemplateTests
{
    private const string Display = ".pen/config.toml";

    private static readonly string[] ProjectKeys =
        ["tools", "allow", "deny", "hide", "replace", "ports", "docker", "github_owner"];

    private static Config Parse(string text) => Config.Parse(text, Display, ConfigKind.Project, RecipeCatalog.Embedded);

    private static string[] ActiveLines(string text) =>
        text.Split('\n').Where(line => line.Trim().Length > 0 && !line.TrimStart().StartsWith('#')).ToArray();

    private static ProjectDetection Full() => new()
    {
        Tools =
        [
            new DetectedTool(new ToolSpec("dotnet", "10"), ["cli/global.json"]) { IsPinned = true },
            new DetectedTool(new ToolSpec("go", "1.27"), ["go.mod", "tools/go.mod"]),
            new DetectedTool(new ToolSpec("java", "21"), ["pom.xml"])
            {
                HasUnknownVersion = true,
                UnknownVersions = ["22"],
            },
            new DetectedTool(new ToolSpec("rust", "stable"), ["Cargo.toml"]) { HasUnknownVersion = true },
        ],
        Hide =
        [
            new DetectedPattern(".env", [".env", "web/.env"]),
            new DetectedPattern(".env.local", [".env.local"]),
            new DetectedPattern("/we\"ird #dir/*.pem", ["we\"ird #dir/a.pem"]),
            new DetectedPattern("/clés/*.key", ["clés/a.key", "clés/b.key", "clés/c.key", "clés/d.key", "clés/e.key"]),
            new DetectedPattern("/it's here/*.p12", []),
            new DetectedPattern("secrets/", ["secrets"]),
        ],
        DockerFiles = ["Dockerfile", "compose.yaml", "a/Dockerfile", "b/Dockerfile", "c/Dockerfile"],
        ReplaceHints =
        [
            new ReplaceHint(".env", ".env.example"),
            new ReplaceHint("we\"ird #dir/.env", "we\"ird #dir/.env.example"),
        ],
        HasNode = true,
        HasPython = true,
    };

    [Fact]
    public void None_IsAllCommented_AndParsesAsAnEmptyProjectConfig()
    {
        var text = ProjectTemplate.Render(ProjectDetection.None);
        Assert.DoesNotContain("{{", text);
        Assert.DoesNotContain("Found", text);
        Assert.Empty(ActiveLines(text));
        Assert.EndsWith("\n", text);
        var config = Parse(text);
        Assert.Empty(config.Tools);
        Assert.Empty(config.Hide);
        Assert.Empty(config.Allow);
        Assert.Empty(config.Replace);
    }

    [Fact]
    public void None_NamesEveryProjectKey_InACommentedExample()
    {
        var text = ProjectTemplate.Render(ProjectDetection.None);
        foreach (var key in ProjectKeys)
            Assert.Contains($"\n# {key} = ", text);
    }

    [Fact]
    public void None_ItsExamples_ParseWhenAllUncommented_AsTopLevelKeys()
    {
        var text = ProjectTemplate.Render(ProjectDetection.None);
        var keys = string.Join("|", ProjectKeys);
        var uncommented = Regex.Replace(text, $"(?m)^# (({keys}) = )", "$1");
        var config = Parse(uncommented);
        Assert.Equal(ProjectKeys.Length, ActiveLines(uncommented).Length);
        Assert.NotEmpty(config.Tools);
        Assert.NotEmpty(config.Allow);
        Assert.NotEmpty(config.Deny);
        Assert.NotEmpty(config.Hide);
        Assert.Single(config.Replace);
        Assert.NotEmpty(config.Ports);
        Assert.True(config.Docker);
        Assert.Equal("my-org", config.GitHubOwner);

        // A list pen allow adds at the end stays top-level, after the uncommented replace too.
        var edit = ConfigEdit.AddToList(uncommented.Replace("allow = ", "# allow = "), Display, ConfigKind.Project,
            ConfigListKey.Allow, ["example.com"], RecipeCatalog.Embedded);
        Assert.Equal(["example.com"], Parse(edit.Text).Allow);
        Assert.Single(Parse(edit.Text).Replace);
    }

    [Fact]
    public void None_AddToListAddsAllowAndDeny()
    {
        var text = ProjectTemplate.Render(ProjectDetection.None);
        var allowed = ConfigEdit.AddToList(text, Display, ConfigKind.Project, ConfigListKey.Allow,
            ["example.com", ".example.org"], RecipeCatalog.Embedded);
        Assert.StartsWith(text, allowed.Text);
        var denied = ConfigEdit.AddToList(allowed.Text, Display, ConfigKind.Project, ConfigListKey.Deny,
            ["gist.github.com"], RecipeCatalog.Embedded);
        var config = Parse(denied.Text);
        Assert.Equal(["example.com", ".example.org"], config.Allow);
        Assert.Equal(["gist.github.com"], config.Deny);
        Assert.Empty(config.Tools);
        Assert.Empty(config.Hide);
    }

    [Fact]
    public void Full_TurnsOnToolsAndHide_AndNothingElse()
    {
        var text = ProjectTemplate.Render(Full());
        Assert.DoesNotContain("{{", text);
        var config = Parse(text);
        Assert.Equal(["dotnet:10", "go:1.27", "java:21", "rust:stable"], config.Tools.Select(tool => tool.ToString()));
        Assert.Equal(
            [".env", ".env.local", "/we\"ird #dir/*.pem", "/clés/*.key", "/it's here/*.p12", "secrets/"],
            config.Hide.Select(pattern => pattern.Text));
        Assert.Empty(config.Allow);
        Assert.Empty(config.Deny);
        Assert.Empty(config.Replace);
        Assert.Empty(config.Ports);
        Assert.Null(config.Docker);
        Assert.Null(config.GitHubOwner);

        var active = ActiveLines(text);
        Assert.Equal("tools = [\"dotnet:10\", \"go\", \"java\", \"rust\"]", active[0]);
        Assert.Equal("hide = [", active[1]);
        Assert.Equal("]", active[^1]);
        Assert.Equal(9, active.Length);
    }

    [Fact]
    public void Full_SaysWhatEachWasFoundFrom_CappingLongLists()
    {
        var text = ProjectTemplate.Render(Full());
        Assert.Contains("# dotnet:10, from cli/global.json\n", text);
        Assert.Contains("# go, from go.mod, tools/go.mod\n", text);
        Assert.Contains("# java, from pom.xml (22 isn't a version its recipe knows, so it's at the recipe's default)\n",
            text);
        Assert.Contains("# rust, from Cargo.toml (a version found isn't one its recipe knows", text);
        Assert.Contains("  \".env\", # .env, web/.env\n", text);
        Assert.Contains("  \"/we\\\"ird #dir/*.pem\", # we\"ird #dir/a.pem\n", text);
        Assert.Contains("  \"/clés/*.key\", # clés/a.key, clés/b.key, clés/c.key and 2 more\n", text);
        Assert.Contains("  \"/it's here/*.p12\",\n", text);
        Assert.Contains("# Found Dockerfile, compose.yaml, a/Dockerfile and 2 more: ", text);
        Assert.Contains("# Found package.json: Node", text);
        Assert.Contains("# Found pyproject.toml or requirements.txt: Python", text);
    }

    [Fact]
    public void Full_TheDockerNote_IsJustAboveTheCommentedDockerSetting()
    {
        var lines = ProjectTemplate.Render(Full()).Split('\n');
        var docker = Array.IndexOf(lines, "# docker = true");
        Assert.Equal("# Found Dockerfile, compose.yaml, a/Dockerfile and 2 more: if sessions need Docker, uncomment this.",
            lines[docker - 1]);
    }

    [Fact]
    public void Full_TheReplaceHint_IsACommentedExampleThatParsesWhenUncommented()
    {
        var text = ProjectTemplate.Render(Full());
        var hint = Assert.Single(text.Split('\n'), line => line.StartsWith("# replace = { \".env\" = \".env.example\""));
        Assert.Equal("# replace = { \".env\" = \".env.example\", " +
            "\"we\\\"ird #dir/.env\" = \"we\\\"ird #dir/.env.example\" }", hint);
        var config = Parse(text.Replace(hint, hint[2..]));
        Assert.Equal(
            [KeyValuePair.Create(".env", ".env.example"), KeyValuePair.Create("we\"ird #dir/.env", "we\"ird #dir/.env.example")],
            config.Replace);
    }

    [Fact]
    public void ManyReplaceHints_AreCapped()
    {
        var detection = new ProjectDetection
        {
            ReplaceHints = [.. Enumerable.Range(0, 5).Select(i => new ReplaceHint($"d{i}/.env", $"d{i}/.env.example"))],
        };
        var text = ProjectTemplate.Render(detection);
        Assert.Contains("\"d2/.env\" = \"d2/.env.example\" }\n# (and 2 more .env files", text);
        Assert.DoesNotContain("d3/", text);
        Parse(text);
    }

    [Fact]
    public void AddToList_AddsToAFullRender()
    {
        var text = ProjectTemplate.Render(Full());
        var edit = ConfigEdit.AddToList(text, Display, ConfigKind.Project, ConfigListKey.Deny, ["pastebin.com"], RecipeCatalog.Embedded);
        var config = Parse(edit.Text);
        Assert.Equal(["pastebin.com"], config.Deny);
        Assert.Equal(6, config.Hide.Count);
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a\"b\\c", "\"a\\\"b\\\\c\"")]
    [InlineData("a\nb\tc\u0001\u007f", "\"a\\nb\\tc\\u0001\\u007F\"")]
    [InlineData("clés #1 'x'", "\"clés #1 'x'\"")]
    public void Quote_WritesATomlBasicString(string value, string expected)
    {
        Assert.Equal(expected, ProjectTemplate.Quote(value));
        Assert.Equal(value, Config.ParseToml($"k = {expected}\n", "x")["k"]);
    }

    [Fact]
    public void Quote_RefusesALoneSurrogate()
    {
        foreach (var lone in new[] { "a\ud800b", "a\udc00b", "\ud800", "\udc00\ud800" })
            Assert.Throws<ArgumentException>(() => ProjectTemplate.Quote(lone));
    }

    [Fact]
    public void Quote_KeepsAValidSurrogatePair()
    {
        const string value = "a\U0001F600b";
        var quoted = ProjectTemplate.Quote(value);
        Assert.Equal("\"" + value + "\"", quoted);
        Assert.Equal(value, Config.ParseToml($"k = {quoted}\n", "x")["k"]);
    }
}
