namespace Pen.Tests;

public class RecipeTests
{
    private const string Good = "default = \"10\"\nversion = '^\\d+$'\n";

    private static RecipeCatalog Load(params (string Path, string Text)[] files) =>
        RecipeCatalog.FromFiles(files.Select(f => KeyValuePair.Create(f.Path, f.Text)));

    private static RecipeCatalog LoadToml(string toml) =>
        Load(("tools/x/tool.toml", toml), ("tools/x/install.sh", "#!/bin/sh\n"));

    private static string Error(Func<object> f) => Assert.Throws<PenException>(() => f()).Message;

    [Fact]
    public void EveryEmbeddedRecipeParses()
    {
        var names = Embedded.Under(RecipeCatalog.Root).Select(p => p.Split('/')[1]).Distinct().ToList();
        Assert.NotEmpty(names);
        foreach (var name in names)
        {
            Assert.True(RecipeCatalog.Embedded.TryGet(name, out var recipe), name);
            Assert.StartsWith("#!", Embedded.Text(recipe.InstallPath));
        }
    }

    [Fact]
    public void TheTestRecipeWorksButIsntOffered()
    {
        Assert.True(RecipeCatalog.Embedded.TryGet("pen-test", out var recipe));
        Assert.True(recipe.Hidden);
        Assert.DoesNotContain("pen-test", RecipeCatalog.Embedded.Listing());
        Assert.DoesNotContain("pen-test", RecipeCatalog.Embedded.Available());
    }

    [Fact]
    public void TheDotnetRecipeIsOfferedWithSeveralVersions()
    {
        Assert.True(RecipeCatalog.Embedded.TryGet("dotnet", out var recipe));
        Assert.False(recipe.Hidden);
        Assert.True(recipe.Multiple);
        Assert.Equal("10", recipe.Default);
        Assert.Contains("dotnet", RecipeCatalog.Embedded.Available());
        Assert.Equal(
            [".nuget.org", "builds.dotnet.microsoft.com", "aka.ms", "www.microsoft.com", "crl3.digicert.com", "ocsp.digicert.com"],
            recipe.Allow);
    }

    [Theory]
    [InlineData("6", true)]
    [InlineData("7", true)]
    [InlineData("8", true)]
    [InlineData("9", true)]
    [InlineData("10", true)]
    [InlineData("11", true)]
    [InlineData("5", false)]
    [InlineData("12", false)]
    [InlineData("1", false)]
    [InlineData("08", false)]
    [InlineData("8.0", false)]
    [InlineData("10.0.100", false)]
    [InlineData("100", false)]
    [InlineData("8\n", false)]
    [InlineData("latest", false)]
    [InlineData("lts", false)]
    public void TheDotnetRecipeAcceptsOnlyMajorVersionsThatInstall(string version, bool accepted)
    {
        Assert.True(RecipeCatalog.Embedded.TryGet("dotnet", out var recipe));
        Assert.Equal(accepted, recipe.AcceptsVersion(version));
    }

    [Fact]
    public void TheDotnetRecipeSetsContainerSettingsButNoSingleVersion()
    {
        Assert.True(RecipeCatalog.Embedded.TryGet("dotnet", out var recipe));
        Assert.Equal(
            ["ASPNETCORE_HTTP_PORTS", "DOTNET_RUNNING_IN_CONTAINER", "DOTNET_GENERATE_ASPNET_CERTIFICATE", "DOTNET_USE_POLLING_FILE_WATCHER",
             "NUGET_XMLDOC_MODE", "POWERSHELL_DISTRIBUTION_CHANNEL", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT"],
            recipe.Env.Select(e => e.Key));
        Assert.Contains(new KeyValuePair<string, string>("ASPNETCORE_HTTP_PORTS", "8080"), recipe.Env);
        Assert.Contains(new KeyValuePair<string, string>("DOTNET_CLI_TELEMETRY_OPTOUT", "1"), recipe.Env);
        foreach (var leftOut in new[] { "DOTNET_VERSION", "ASPNET_VERSION", "DOTNET_SDK_VERSION" })
            Assert.DoesNotContain(recipe.Env, e => e.Key == leftOut);
    }

    [Fact]
    public void AllSettingsAreRead()
    {
        Assert.True(LoadToml("""
            default = "10"
            version = '^\d+$'
            multiple = true
            allow = [".nuget.org", "builds.dotnet.microsoft.com"]

            [env]
            DOTNET_NOLOGO = "1"
            B = "2"
            """).TryGet("x", out var recipe));
        Assert.Equal("10", recipe.Default);
        Assert.True(recipe.Multiple);
        Assert.False(recipe.Hidden);
        Assert.Equal([".nuget.org", "builds.dotnet.microsoft.com"], recipe.Allow);
        Assert.Equal([new("DOTNET_NOLOGO", "1"), new("B", "2")], recipe.Env);
        Assert.Equal("tools/x/install.sh", recipe.InstallPath);
        Assert.True(recipe.AcceptsVersion("8"));
        Assert.False(recipe.AcceptsVersion("8.0"));
    }

    [Fact]
    public void OptionalSettingsDefaultToTheCautiousValue()
    {
        Assert.True(LoadToml(Good).TryGet("x", out var recipe));
        Assert.False(recipe.Multiple);
        Assert.False(recipe.Hidden);
        Assert.Empty(recipe.Allow);
        Assert.Empty(recipe.Env);
    }

    [Theory]
    [InlineData("version = '^\\d+$'", "default is missing")]
    [InlineData("default = \"10\"", "version is missing")]
    [InlineData("default = 10\nversion = '^\\d+$'", "default must be")]
    [InlineData("default = \"\"\nversion = '^\\d+$'", "default must be")]
    [InlineData("default = \"10\"\nversion = 1", "version must be")]
    [InlineData("default = \"10\"\nversion = '('", "isn't a valid regular expression")]
    [InlineData("default = \"ten\"\nversion = '^\\d+$'", "default \"ten\"")]
    [InlineData("default = \"010\"\nversion = '^[1-9]\\d*$'", "default \"010\"")]
    [InlineData("default = \"1;id\"\nversion = '.*'", "default \"1;id\"")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\nmultiple = \"yes\"", "multiple must be")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\nhidden = 1", "hidden must be")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\nallow = \"a.io\"", "allow must be")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\nallow = [\"A.io\"]", "allow: A.io")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\nallow = [1]", "allow: 1")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\nenv = 1", "env must be")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\n[env]\n\"A B\" = \"1\"", "isn't a valid environment")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\n[env]\nHTTP_PROXY = \"x\"", "is set by pen")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\n[env]\nPEN_X = \"x\"", "is set by pen")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\n[env]\nX = 1", "env.X must be")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\n[env]\nX = \"a\\nRUN evil\"", "line break")]
    [InlineData("default = \"10\"\nversion = '^\\d+$'\ndefaults = \"1\"", "unknown key")]
    [InlineData("default = \"10\"\ndefault = \"11\"\nversion = '^\\d+$'", "tools/x/tool.toml:2:")]
    [InlineData("default = ", "tools/x/tool.toml:1:")]
    public void AMalformedToolTomlFails(string toml, string expected)
    {
        var message = Error(() => LoadToml(toml));
        Assert.StartsWith("tools/x/tool.toml", message);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void AMalformedRecipeDirectoryFails()
    {
        Assert.Contains("install.sh is missing", Error(() => Load(("tools/x/tool.toml", Good))));
        Assert.Contains("tool.toml is missing", Error(() => Load(("tools/x/install.sh", "#!/bin/sh\n"))));
        Assert.Contains("must start with #!", Error(() => Load(("tools/x/tool.toml", Good), ("tools/x/install.sh", "echo hi\n"))));
        Assert.Contains("has only", Error(() => LoadWith("tools/x/extra.sh")));
        Assert.Contains("not part of a recipe", Error(() => LoadWith("tools/x/sub/install.sh")));
        Assert.Contains("not part of a recipe", Error(() => LoadWith("tools/loose.toml")));
        Assert.Contains("not part of a recipe", Error(() => Load(("tools/X/tool.toml", Good))));
        Assert.Contains("not part of a recipe", Error(() => Load(("tools/a:b/tool.toml", Good))));
        Assert.Contains("not part of a recipe", Error(() => Load(("elsewhere/x/tool.toml", Good))));
    }

    private static RecipeCatalog LoadWith(string extra) =>
        Load(("tools/x/tool.toml", Good), ("tools/x/install.sh", "#!/bin/sh\n"), (extra, ""));

    [Fact]
    public void NoRecipeCanTakeTheNameOfTheListing() =>
        Assert.Contains("not part of a recipe", Error(() => Load(("tools/list/tool.toml", Good), ("tools/list/install.sh", "#!/bin/sh\n"))));

    [Fact]
    public void TheListingSaysWhatEachToolOffers()
    {
        var catalog = Load(
            ("tools/dotnet/tool.toml", "default = \"10\"\nversion = '^\\d+$'\nmultiple = true\n"),
            ("tools/dotnet/install.sh", "#!/bin/sh\n"),
            ("tools/go/tool.toml", "default = \"1.24\"\nversion = '.*'\n"),
            ("tools/go/install.sh", "#!/bin/sh\n"),
            ("tools/hid/tool.toml", "default = \"1\"\nversion = '.*'\nhidden = true\n"),
            ("tools/hid/install.sh", "#!/bin/sh\n"));
        Assert.Equal("dotnet  default 10, several versions can be installed together\ngo      default 1.24\n", catalog.Listing());
        Assert.Equal("available: dotnet, go", catalog.Available());
    }

    [Fact]
    public void WithOnlyHiddenRecipesTheListingSaysSo()
    {
        var catalog = LoadToml(Good + "hidden = true\n");
        Assert.Equal("no tools are available yet\n", catalog.Listing());
        Assert.Equal("none are available yet", catalog.Available());
    }
}
