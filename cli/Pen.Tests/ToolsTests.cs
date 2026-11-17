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
        ["tools/helper/tool.toml"] = "default = \"latest\"\nversion = '^(latest|\\d+\\.\\d+\\.\\d+)$'\nkind = \"agent\"\n",
        ["tools/helper/install.sh"] = "#!/bin/sh\n",
        ["tools/ghost/tool.toml"] = "default = \"latest\"\nversion = '.*'\nkind = \"agent\"\nhidden = true\n",
        ["tools/ghost/install.sh"] = "#!/bin/sh\n",
    });

    private static ToolSpec Tool(string spec)
    {
        Assert.True(ToolSpec.TryParse(spec, Catalog, out var tool, out var error), error);
        Assert.Null(error);
        return tool;
    }

    private static string Bad(string spec)
    {
        Assert.False(ToolSpec.TryParse(spec, Catalog, out var tool, out var error));
        Assert.Equal(default, tool);
        return error;
    }

    private static string Conflict(RecipeCatalog catalog, params ToolSpec[] tools)
    {
        Assert.False(ToolSpec.TryCanonicalize(tools, catalog, out var canonical, out var error));
        Assert.Empty(canonical);
        return error;
    }

    private static List<string> Canonical(params string[] specs)
    {
        Assert.True(ToolSpec.TryCanonicalize(specs.Select(Tool), Catalog, out var canonical, out var error), error);
        return canonical.Select(t => t.ToString()).ToList();
    }

    private static string? Implied(string[] command, params string[] named) =>
        ToolSpec.FindImpliedAgent(command, Catalog, named.Select(Tool))?.ToString();

    [Fact]
    public void ACommandNamingAnAgentImpliesItsDefault() =>
        Assert.Equal("helper:latest", Implied(["helper", "--flag"]));

    [Theory]
    [InlineData("helper")]
    [InlineData("helper:1.2.3")]
    public void AnAgentAlreadyNamedAtAnyVersionImpliesNothing(string named)
    {
        Assert.Null(Implied(["helper"], named));
        Assert.Null(Implied(["helper"], "go", named));
    }

    [Fact]
    public void OtherToolsDontStopTheImplication() =>
        Assert.Equal("helper:latest", Implied(["helper"], "go", "dotnet:10"));

    [Theory]
    [InlineData("bash")]
    [InlineData("run")]
    [InlineData("go")]
    [InlineData("ghost")]
    [InlineData("unknown")]
    [InlineData("helper2")]
    [InlineData("Helper")]
    public void NothingElseImpliesAnAgent(string first) =>
        Assert.Null(Implied([first]));

    [Fact]
    public void AnEmptyCommandImpliesNothing() => Assert.Null(Implied([]));

    [Fact]
    public void OnlyTheFirstWordCounts() => Assert.Null(Implied(["bash", "helper"]));

    /// <summary>What a pen command line implies, as Session.Run sees its command.</summary>
    private static string? ImpliedBy(params string[] args)
    {
        var command = CommandParser.Parse(args, () => null, RecipeCatalog.Embedded);
        Assert.Equal(CommandKind.Run, command.Kind);
        return ToolSpec.FindImpliedAgent(command.Args, RecipeCatalog.Embedded, command.Tools)?.ToString();
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("claude", "--version")]
    [InlineData("run", "claude", "--version")]
    [InlineData("run", "--", "claude")]
    [InlineData("--", "claude")]
    [InlineData("--tool", "go", "claude")]
    [InlineData("--tool", "go", "run", "claude", "--version")]
    public void PenClaudeAndPenRunClaudeImplyTheClaudeAgent(params string[] args) =>
        Assert.Equal("claude:latest", ImpliedBy(args));

    [Theory]
    [InlineData("run", "bash", "-c", "command -v claude")]
    [InlineData("run", "timeout", "30", "claude")]
    [InlineData("--tool", "claude:2.1.300", "claude")]
    public void ClaudeOnlyLaterInTheCommand_OrAlreadyNamed_ImpliesNothing(params string[] args) =>
        Assert.Null(ImpliedBy(args));

    [Fact]
    public void ANameMeansTheDefaultVersion()
    {
        Assert.Equal(new ToolSpec("dotnet", "10"), Tool("dotnet"));
        Assert.Equal(Tool("dotnet"), Tool("dotnet:10"));
    }

    [Theory]
    [InlineData("go:24.04")]
    [InlineData("go:1.01")]
    [InlineData("go:01.022")]
    [InlineData("dotnet:010")]
    [InlineData("dotnet:000")]
    public void AVersionStaysAsWritten(string spec) =>
        Assert.Equal(spec, Tool(spec).ToString());

    [Fact]
    public void VersionsThatDifferOnlyInZerosAreDifferentVersions()
    {
        Assert.Equal(["dotnet:010", "dotnet:10"], Canonical("dotnet:10", "dotnet:010", "dotnet:10"));
        Assert.Contains("go:1.01 and go:1.1 can't be used together",
            Conflict(Catalog, Tool("go:1.1"), Tool("go:1.01")));
    }

    [Fact]
    public void ARecipesPatternDecidesWhetherALeadingZeroIsAllowed()
    {
        var catalog = RecipeCatalog.FromFiles(new Dictionary<string, string>
        {
            ["tools/x/tool.toml"] = "default = \"8\"\nversion = '^[1-9]\\d*$'\n",
            ["tools/x/install.sh"] = "#!/bin/sh\n",
        });
        Assert.True(ToolSpec.TryParse("x:8", catalog, out _, out _));
        Assert.False(ToolSpec.TryParse("x:08", catalog, out _, out var error));
        Assert.Contains("\"08\" isn't a valid version of x", error);
    }

    [Fact]
    public void PermutationsAndDuplicatesGiveTheSameList()
    {
        string[] expected = ["dotnet:10", "dotnet:8", "go:1.24"];
        Assert.Equal(expected, Canonical("dotnet:8", "go", "dotnet"));
        Assert.Equal(expected, Canonical("go", "dotnet", "dotnet:8"));
        Assert.Equal(expected, Canonical("dotnet", "dotnet:10", "go:1.24", "go", "dotnet:8"));
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
        Assert.True(ToolSpec.TryCanonicalize([new("a", "1"), new("a-b", "1")], catalog, out var canonical, out _));
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
        Assert.True(ToolSpec.TryParse("dotnet:" + new string('1', 64), Catalog, out _, out _));
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
        Assert.True(ToolSpec.TryParse("loose:12", catalog, out _, out _));
        Assert.False(ToolSpec.TryParse("loose:12-x", catalog, out _, out _));
        Assert.False(ToolSpec.TryParse("loose:x-12", catalog, out _, out _));
    }

    [Fact]
    public void ARecipeRegexAnchoredWithDollar_StillRefusesAFinalNewline()
    {
        var catalog = RecipeCatalog.FromFiles(new Dictionary<string, string>
        {
            ["tools/anchored/tool.toml"] = "default = \"1\"\nversion = '^\\d+$'\n",
            ["tools/anchored/install.sh"] = "#!/bin/sh\n",
        });
        Assert.True(catalog.TryGet("anchored", out var recipe));
        Assert.True(recipe.AcceptsVersion("12"));
        Assert.False(recipe.AcceptsVersion("12\n"));
    }

    [Fact]
    public void TwoVersionsThatCantCoexistAreRefused()
    {
        var message = Conflict(Catalog, Tool("go:1.24"), Tool("go:1.25"));
        Assert.Equal("go:1.24 and go:1.25 can't be used together: only one version of go can be installed", message);
        Assert.True(ToolSpec.TryCanonicalize([Tool("go:1.24"), Tool("go")], Catalog, out _, out _));
        Assert.True(ToolSpec.TryCanonicalize([Tool("dotnet:8"), Tool("dotnet:10")], Catalog, out _, out _));
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
        Assert.Throws<PenException>(() => Config.Parse(text, "f.toml", kind, RecipeCatalog.Embedded));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ToolsAreValidInBothConfigFiles(bool user)
    {
        var kind = user ? ConfigKind.User : ConfigKind.Project;
        var c = Config.Parse("tools = [\"pen-test\", \"pen-test:1\"]\n", "f.toml", kind, RecipeCatalog.Embedded);
        Assert.Equal([new ToolSpec("pen-test", "1")], c.Tools);
        Assert.Empty(Config.Parse("", "f.toml", kind, RecipeCatalog.Embedded).Tools);

        Assert.StartsWith("f.toml: tools: unknown tool \"nope\"", ConfigError("tools = [\"nope\"]", kind).Message);
        Assert.Equal("f.toml: tools: \"01\" isn't a valid version of pen-test", ConfigError("tools = [\"pen-test:01\"]", kind).Message);
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
        var c = CommandParser.Parse(["--tool", "pen-test:1", "--tool=pen-test", "--docker", "--port", "80", "claude", "--tool", "x"], () => null, RecipeCatalog.Embedded);
        Assert.Equal(CommandKind.Run, c.Kind);
        Assert.Equal([new ToolSpec("pen-test", "1")], c.Tools);
        Assert.True(c.Docker);
        Assert.Equal([80], c.Ports);
        Assert.Equal(["claude", "--tool", "x"], c.Args);
        Assert.Empty(CommandParser.Parse(["claude"], () => null, RecipeCatalog.Embedded).Tools);
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
        Assert.Equal(2, Assert.Throws<PenException>(() => CommandParser.Parse(args, () => null, RecipeCatalog.Embedded)).ExitCode);

    [Fact]
    public void TheListingIsPenTools_AndToolListIsJustAnUnknownTool()
    {
        Assert.Equal(CommandKind.Tools, CommandParser.Parse(["tools"], () => null, RecipeCatalog.Embedded).Kind);
        var list = Assert.Throws<PenException>(() => CommandParser.Parse(["--tool", "list"], () => null, RecipeCatalog.Embedded)).Message;
        Assert.StartsWith("--tool: unknown tool \"list\"", list);
        Assert.Contains("; pen tools lists them", list);
        Assert.Contains("\n  tools  ", CliTests.Render("--help"));
        Assert.Contains("-t, --tool <spec>", CliTests.Render("run", "--help"));
        Assert.Contains("tools shows what's available", CliTests.Render("run", "--help"));
    }
}
