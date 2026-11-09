namespace Pen.Tests;

public class ToolsTests
{
    private static RecipeCatalog Catalog { get; } = RecipeCatalog.FromFiles(new Dictionary<string, string>
    {
        ["tools/dotnet/tool.toml"] = "default = \"10\"\nversion = '^\\d+$'\nmultiple = true\n",
        ["tools/dotnet/install.sh"] = "#!/bin/sh\n",
        ["tools/go/tool.toml"] = "default = \"1.24\"\nversion = '^\\d+\\.\\d+(\\.\\d+)?$'\n",
        ["tools/go/install.sh"] = "#!/bin/sh\n",
        ["tools/secret/tool.toml"] = "default = \"1\"\nversion = '^\\d+$'\nhidden = true\n",
        ["tools/secret/install.sh"] = "#!/bin/sh\n",
    });

    private static ToolSpec Tool(string spec)
    {
        Assert.Null(ToolSpec.TryParse(spec, Catalog, out var tool));
        return tool;
    }

    private static string Bad(string spec) =>
        Assert.IsType<string>(ToolSpec.TryParse(spec, Catalog, out _));

    private static List<string> Canonical(params string[] specs)
    {
        Assert.Null(ToolSpec.TryCanonicalize(specs.Select(Tool), Catalog, out var canonical));
        return canonical.Select(t => t.ToString()).ToList();
    }

    [Fact]
    public void ANameMeansTheDefaultVersion()
    {
        Assert.Equal(new ToolSpec("dotnet", "10"), Tool("dotnet"));
        Assert.Equal(Tool("dotnet"), Tool("dotnet:10"));
    }

    [Theory]
    [InlineData("dotnet:010", "dotnet:10")]
    [InlineData("dotnet:0", "dotnet:0")]
    [InlineData("dotnet:000", "dotnet:0")]
    [InlineData("go:01.022", "go:1.22")]
    [InlineData("go:1.0.003", "go:1.0.3")]
    public void VersionsHaveOneSpelling(string spec, string normal) =>
        Assert.Equal(normal, Tool(spec).ToString());

    [Theory]
    [InlineData("8.0.100-rc.01", "8.0.100-rc.1")]
    [InlineData("1.0-02", "1.0-02")]
    [InlineData("v01", "v01")]
    public void OnlyAllDigitPartsLoseTheirZeros(string version, string normal) =>
        Assert.Equal(normal, ToolSpec.NormalizeVersion(version));

    [Fact]
    public void PermutationsAndDuplicatesGiveTheSameList()
    {
        string[] expected = ["dotnet:10", "dotnet:8", "go:1.24"];
        Assert.Equal(expected, Canonical("dotnet:8", "go", "dotnet"));
        Assert.Equal(expected, Canonical("go", "dotnet", "dotnet:8"));
        Assert.Equal(expected, Canonical("dotnet", "dotnet:10", "go:1.24", "go", "dotnet:08", "dotnet:8"));
    }

    [Fact]
    public void AListSortsOrdinally()
    {
        var catalog = RecipeCatalog.FromFiles(new Dictionary<string, string>
        {
            ["tools/a/tool.toml"] = "default = \"1\"\nversion = '^\\d+$'\n",
            ["tools/a/install.sh"] = "#!/bin/sh\n",
            ["tools/a-b/tool.toml"] = "default = \"1\"\nversion = '^\\d+$'\n",
            ["tools/a-b/install.sh"] = "#!/bin/sh\n",
        });
        Assert.Null(ToolSpec.TryCanonicalize([new("a", "1"), new("a-b", "1")], catalog, out var canonical));
        Assert.Equal(["a-b:1", "a:1"], canonical.Select(t => t.ToString()));
    }

    [Fact]
    public void AnUnknownToolNamesThoseAvailable_ButNotHiddenOnes()
    {
        var message = Bad("nope:1");
        Assert.StartsWith("unknown tool \"nope\"", message);
        Assert.Contains("available: dotnet, go", message);
        Assert.DoesNotContain("secret", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(":10")]
    [InlineData("DOTNET")]
    [InlineData(" dotnet")]
    [InlineData("dotnet ")]
    [InlineData("../dotnet")]
    public void NamesMustMatchExactly(string spec) =>
        Assert.StartsWith("unknown tool", Bad(spec));

    [Theory]
    [InlineData("dotnet:")]
    [InlineData("dotnet:abc")]
    [InlineData("dotnet:10:11")]
    [InlineData("dotnet:1.5")]
    // Each could reach install.sh's arguments if only an unanchored or careless regex guarded it.
    [InlineData("dotnet:10;id")]
    [InlineData("dotnet:$(id)")]
    [InlineData("dotnet:`id`")]
    [InlineData("dotnet:10 11")]
    [InlineData("dotnet:10\n")]
    [InlineData("dotnet:\n10")]
    [InlineData("dotnet:-10")]
    [InlineData("dotnet:--help")]
    [InlineData("dotnet:../10")]
    [InlineData("dotnet:10/")]
    [InlineData("dotnet:١٠")] // \d in .NET matches non-ASCII digits
    public void InvalidVersionsAreRefused(string spec) =>
        Assert.Contains("isn't a valid version of dotnet", Bad(spec));

    [Fact]
    public void AVersionTooLongIsRefused_WhateverTheRecipeAllows()
    {
        Assert.Null(ToolSpec.TryParse("dotnet:" + new string('1', 64), Catalog, out _));
        Assert.Contains("isn't a valid version", Bad("dotnet:" + new string('1', 65)));
    }

    [Fact]
    public void ARefusalCantForgeALineOrSendControlCodes()
    {
        Assert.DoesNotContain('\n', Bad("dotnet:1\npen: all clear"));
        Assert.DoesNotContain('\x1b', Bad("dotnet:\x1b[2J"));
        Assert.DoesNotContain('\n', Bad("no\npen: all clear:1"));
        Assert.DoesNotContain('\x1b', Bad("\x1b[2J"));
    }

    [Fact]
    public void ARecipeRegexMustMatchTheWholeVersion()
    {
        var catalog = RecipeCatalog.FromFiles(new Dictionary<string, string>
        {
            ["tools/loose/tool.toml"] = "default = \"1\"\nversion = '\\d+'\n",
            ["tools/loose/install.sh"] = "#!/bin/sh\n",
        });
        Assert.Null(ToolSpec.TryParse("loose:12", catalog, out _));
        Assert.NotNull(ToolSpec.TryParse("loose:12-x", catalog, out _));
        Assert.NotNull(ToolSpec.TryParse("loose:x-12", catalog, out _));
    }

    [Fact]
    public void TwoVersionsThatCantCoexistAreRefused()
    {
        var message = ToolSpec.TryCanonicalize([Tool("go:1.24"), Tool("go:1.25")], Catalog, out _);
        Assert.Equal("go:1.24 and go:1.25 can't be used together: only one version of go can be installed", message);
        Assert.Null(ToolSpec.TryCanonicalize([Tool("go:1.24"), Tool("go")], Catalog, out _));
        Assert.Null(ToolSpec.TryCanonicalize([Tool("dotnet:8"), Tool("dotnet:10")], Catalog, out _));
    }

    [Fact]
    public void MergeAppendsLists_RefusingAConflictBetweenThem()
    {
        Assert.Equal(["dotnet:8", "go:1.24"], ToolSpec.Merge(Catalog, [Tool("go")], [Tool("dotnet:8")], [Tool("go:1.24")]).Select(t => t.ToString()));
        Assert.Empty(ToolSpec.Merge(Catalog, [], [], []));
        var e = Assert.Throws<PenException>(() => ToolSpec.Merge(Catalog, [Tool("go:1.24")], [], [Tool("go:1.25")]));
        Assert.Contains("can't be used together", e.Message);
    }

    // The embedded test recipe: multiple = false, default 1.
    private static PenException ConfigError(string text, ConfigKind kind) =>
        Assert.Throws<PenException>(() => Config.Parse(text, "f.toml", kind));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToolsAreValidInBothConfigFiles(bool user)
    {
        var kind = user ? ConfigKind.User : ConfigKind.Project;
        var c = Config.Parse("tools = [\"pen-test\", \"pen-test:01\"]\n", "f.toml", kind);
        Assert.Equal([new ToolSpec("pen-test", "1")], c.Tools);
        Assert.Empty(Config.Parse("", "f.toml", kind).Tools);

        Assert.StartsWith("f.toml: tools: unknown tool \"nope\"", ConfigError("tools = [\"nope\"]", kind).Message);
        Assert.Equal("f.toml: tools: \"1;id\" isn't a valid version of pen-test", ConfigError("tools = [\"pen-test:1;id\"]", kind).Message);
        Assert.Contains("can't be used together", ConfigError("tools = [\"pen-test:1\", \"pen-test:2\"]", kind).Message);
        Assert.StartsWith("f.toml: tools must be a list", ConfigError("tools = \"pen-test\"", kind).Message);
        Assert.StartsWith("f.toml: tools: 1 isn't a tool", ConfigError("tools = [1]", kind).Message);
    }

    [Fact]
    public void AConfigToolsRefusalCantForgeALine() =>
        Assert.DoesNotContain('\n', ConfigError("tools = [\"pen-test:1\\npen: all clear\"]", ConfigKind.Project).Message);

    [Fact]
    public void TheFlagAcceptsWhatTheConfigDoes()
    {
        var c = Cli.Parse(["--tool", "pen-test:01", "--tool=pen-test", "--docker", "--port", "80", "claude", "--tool", "x"]);
        Assert.Equal(CommandKind.Run, c.Kind);
        Assert.Equal([new ToolSpec("pen-test", "1")], c.Tools);
        Assert.True(c.Docker);
        Assert.Equal([80], c.Ports);
        Assert.Equal(["claude", "--tool", "x"], c.Args);
        Assert.Empty(Cli.Parse(["claude"]).Tools);
    }

    [Theory]
    [InlineData("--tool")]
    [InlineData("--tool", "nope")]
    [InlineData("--tool=")]
    [InlineData("--tool", "--docker")]
    [InlineData("--tool", "pen-test:x")]
    [InlineData("--tool", "pen-test:1;id", "ls")]
    [InlineData("--tool", "pen-test:1", "--tool", "pen-test:2")]
    [InlineData("--tool", "pen-test", "allow")]
    [InlineData("--tool", "pen-test", "ps")]
    [InlineData("--tool", "list", "ls")]
    [InlineData("--docker", "--tool", "list")]
    [InlineData("--tool=list", "ls")]
    [InlineData("--toolx", "pen-test")]
    public void BadFlagsAreUsageErrors(params string[] args) =>
        Assert.Equal(2, Assert.Throws<PenException>(() => Cli.Parse(args)).ExitCode);

    [Fact]
    public void ToolListIsItsOwnCommand()
    {
        Assert.Equal(CommandKind.ToolList, Cli.Parse(["--tool", "list"]).Kind);
        Assert.Equal(CommandKind.ToolList, Cli.Parse(["--tool=list"]).Kind);
        Assert.Contains("--tool list", Cli.Usage);
        Assert.Contains("--tool <spec>", Cli.SubcommandHelp["run"]);
    }
}
