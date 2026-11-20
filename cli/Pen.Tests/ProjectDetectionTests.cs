using System.Diagnostics;
using Pen.Masking;

namespace Pen.Tests;

/// <summary>
/// ProjectDetection walks a project read-only, to depth 3, without following symlinks or entering build and
/// dependency dirs, and suggests recipe-valid tools, hide patterns and commented hints, keeping unsafe names out.
/// </summary>
public class ProjectDetectionTests
{
    private static ProjectDetection Detect(TempDir t) => ProjectDetection.Detect(t.Path, RecipeCatalog.Embedded);

    private static string Write(TempDir t, string relative, string content = "")
    {
        var path = Path.Combine(t.Path, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string[] Specs(ProjectDetection detection) =>
        detection.ToolSpecs.Select(tool => tool.ToString()).ToArray();

    private static DetectedTool Tool(ProjectDetection detection, string spec) =>
        Assert.Single(detection.Tools, tool => tool.Tool.ToString() == spec);

    private static string[] Patterns(ProjectDetection detection) =>
        detection.Hide.Select(pattern => pattern.Pattern).ToArray();

    private static DetectedPattern Pattern(ProjectDetection detection, string pattern) =>
        Assert.Single(detection.Hide, found => found.Pattern == pattern);

    // ---- nothing ----

    [Fact]
    public void None_HasNothing()
    {
        Assert.Empty(ProjectDetection.None.Tools);
        Assert.Empty(ProjectDetection.None.Hide);
        Assert.Empty(ProjectDetection.None.DockerFiles);
        Assert.Empty(ProjectDetection.None.ReplaceHints);
        Assert.False(ProjectDetection.None.HasNode);
        Assert.False(ProjectDetection.None.HasPython);
    }

    [Fact]
    public void AnEmptyProject_DetectsNothing()
    {
        using var t = new TempDir();
        var detection = Detect(t);
        Assert.Empty(detection.Tools);
        Assert.Empty(detection.Hide);
    }

    [Fact]
    public void AMissingProjectDir_DetectsNothing()
    {
        using var t = new TempDir();
        var detection = ProjectDetection.Detect(Path.Combine(t.Path, "missing"), RecipeCatalog.Embedded);
        Assert.Empty(detection.Tools);
    }

    // ---- dotnet ----

    [Fact]
    public void GlobalJson_GivesItsMajor()
    {
        using var t = new TempDir();
        Write(t, "cli/global.json", """{ "sdk": { "version": "8.0.100", "rollForward": "latestFeature" } }""");
        Write(t, "cli/Pen/Pen.csproj");
        var detection = Detect(t);
        Assert.Equal(["dotnet:8"], Specs(detection));
        var tool = Tool(detection, "dotnet:8");
        Assert.Equal(["cli/global.json"], tool.Sources);
        Assert.False(tool.HasUnknownVersion);
        Assert.True(tool.IsPinned);
    }

    [Fact]
    public void EachGlobalJson_AddsItsMajor()
    {
        using var t = new TempDir();
        Write(t, "global.json", """{ "sdk": { "version": "10.0.100" } }""");
        Write(t, "old/global.json", """
            // comments and trailing commas are allowed in global.json
            { "sdk": { "version": "8.0.400", }, }
            """);
        Assert.Equal(["dotnet:10", "dotnet:8"], Specs(Detect(t)));
    }

    [Fact]
    public void AnUnknownSdkVersion_FallsBackToTheDefault_WithANote()
    {
        using var t = new TempDir();
        Write(t, "global.json", """{ "sdk": { "version": "42.0.100" } }""");
        var detection = Detect(t);
        var tool = Assert.Single(detection.Tools);
        Assert.Equal("dotnet", tool.Tool.Name);
        Assert.True(RecipeCatalog.Embedded.TryGet("dotnet", out var recipe));
        Assert.Equal(recipe.Default, tool.Tool.Version);
        Assert.True(tool.HasUnknownVersion);
        Assert.Equal(["42.0.100"], tool.UnknownVersions);
        Assert.Equal(["global.json"], tool.Sources);
    }

    [Fact]
    public void AnUnknownVersionThatIsntAVersion_IsNotedButNotKept()
    {
        using var t = new TempDir();
        Write(t, "global.json", """{ "sdk": { "version": "$(rm -rf /)" } }""");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.True(tool.HasUnknownVersion);
        Assert.Empty(tool.UnknownVersions);
    }

    [Fact]
    public void AProjectFileWithoutGlobalJson_GivesDotnetAtItsDefault()
    {
        using var t = new TempDir();
        Write(t, "src/App/App.csproj");
        Write(t, "App.slnx");
        var detection = Detect(t);
        var tool = Assert.Single(detection.Tools);
        Assert.True(RecipeCatalog.Embedded.TryGet("dotnet", out var recipe));
        Assert.Equal(new ToolSpec("dotnet", recipe.Default), tool.Tool);
        Assert.False(tool.HasUnknownVersion);
        Assert.False(tool.IsPinned);
        Assert.Equal(["App.slnx", "src/App/App.csproj"], tool.Sources);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "sdk": { "version": 10 } }""")]
    [InlineData("""["sdk"]""")]
    [InlineData("")]
    public void AGarbageGlobalJson_DoesntThrow_AndGivesTheDefault(string content)
    {
        using var t = new TempDir();
        Write(t, "global.json", content);
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal("dotnet", tool.Tool.Name);
        Assert.False(tool.HasUnknownVersion);
    }

    [Fact]
    public void AnUnreadableGlobalJson_DoesntThrow()
    {
        using var t = new TempDir();
        var path = Write(t, "global.json", """{ "sdk": { "version": "8.0.100" } }""");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var tool = Assert.Single(Detect(t).Tools);
            Assert.Equal("dotnet", tool.Tool.Name);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void AnUnlistableDirectory_IsSkipped()
    {
        using var t = new TempDir();
        Write(t, "go.mod");
        Write(t, "locked/Cargo.toml");
        var locked = Path.Combine(t.Path, "locked");
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            Assert.Equal(["go"], Detect(t).ToolSpecs.Select(tool => tool.Name));
        }
        finally
        {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void AVersionFromGlobalJson_IsPinned_EvenAtTheDefault()
    {
        using var t = new TempDir();
        Assert.True(RecipeCatalog.Embedded.TryGet("dotnet", out var recipe));
        Write(t, "global.json", $$"""{ "sdk": { "version": "{{recipe.Default}}.0.100" } }""");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal(new ToolSpec("dotnet", recipe.Default), tool.Tool);
        Assert.True(tool.IsPinned);
    }

    [Fact]
    public void ADefaultFromAProjectFile_IsntPinned()
    {
        using var t = new TempDir();
        Write(t, "App.csproj");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal("dotnet", tool.Tool.Name);
        Assert.False(tool.IsPinned);
    }

    [Fact]
    public void AnUnknownVersion_IsntPinned()
    {
        using var t = new TempDir();
        Write(t, "global.json", """{ "sdk": { "version": "42.0.100" } }""");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.True(tool.HasUnknownVersion);
        Assert.False(tool.IsPinned);
    }

    // ---- go, rust, java ----

    [Fact]
    public void GoMod_GivesGoAtItsDefault()
    {
        using var t = new TempDir();
        Write(t, "go.mod", "module example.com/x\n\ngo 1.99\n");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.True(RecipeCatalog.Embedded.TryGet("go", out var recipe));
        Assert.Equal(new ToolSpec("go", recipe.Default), tool.Tool);
        Assert.Equal(["go.mod"], tool.Sources);
        Assert.False(tool.HasUnknownVersion);
        Assert.False(tool.IsPinned);
    }

    [Fact]
    public void SeveralGoMods_GiveOneGo()
    {
        using var t = new TempDir();
        Write(t, "a/go.mod");
        Write(t, "b/go.mod");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal(["a/go.mod", "b/go.mod"], tool.Sources);
    }

    [Fact]
    public void CargoToml_GivesRustStable()
    {
        using var t = new TempDir();
        Write(t, "Cargo.toml");
        Assert.Equal(["rust:stable"], Specs(Detect(t)));
    }

    [Fact]
    public void RustToolchainToml_GivesItsChannel()
    {
        using var t = new TempDir();
        Write(t, "Cargo.toml");
        Write(t, "rust-toolchain.toml", "[toolchain]\nchannel = \"1.85\"\ncomponents = [\"clippy\"]\n");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal("rust:1.85", tool.Tool.ToString());
        Assert.Equal(["Cargo.toml", "rust-toolchain.toml"], tool.Sources);
    }

    [Fact]
    public void APlainRustToolchain_GivesItsChannel()
    {
        using var t = new TempDir();
        Write(t, "crates/x/Cargo.toml");
        Write(t, "rust-toolchain", "1.84.1\n");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal("rust:1.84.1", tool.Tool.ToString());
        Assert.Equal(["crates/x/Cargo.toml", "rust-toolchain"], tool.Sources);
    }

    [Fact]
    public void ANightlyChannel_FallsBackToStable_WithANote()
    {
        using var t = new TempDir();
        Write(t, "Cargo.toml");
        Write(t, "rust-toolchain.toml", "[toolchain]\nchannel = \"nightly-2025-01-01\"\n");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal("rust:stable", tool.Tool.ToString());
        Assert.True(tool.HasUnknownVersion);
        Assert.Equal(["nightly-2025-01-01"], tool.UnknownVersions);
    }

    [Fact]
    public void BadToml_InRustToolchainToml_GivesTheDefault()
    {
        using var t = new TempDir();
        Write(t, "Cargo.toml");
        Write(t, "rust-toolchain.toml", "[toolchain\nchannel = = 1");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal("rust:stable", tool.Tool.ToString());
        Assert.False(tool.HasUnknownVersion);
    }

    [Fact]
    public void RustStable_IsPinnedOnlyWhenAFileSaysSo()
    {
        using var t = new TempDir();
        Write(t, "Cargo.toml");
        Assert.False(Assert.Single(Detect(t).Tools).IsPinned);
        Write(t, "rust-toolchain", "stable\n");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal("rust:stable", tool.Tool.ToString());
        Assert.True(tool.IsPinned);
    }

    [Fact]
    public void APinAndADefault_AtTheSameVersion_ArePinned()
    {
        using var t = new TempDir();
        Write(t, "a/Cargo.toml");
        Write(t, "b/Cargo.toml");
        Write(t, "b/rust-toolchain.toml", "[toolchain]\nchannel = \"stable\"\n");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal("rust:stable", tool.Tool.ToString());
        Assert.True(tool.IsPinned);
        Assert.Equal(["a/Cargo.toml", "b/Cargo.toml", "b/rust-toolchain.toml"], tool.Sources);
    }

    [Fact]
    public void ARustToolchainWithoutCargoToml_AddsNothing()
    {
        using var t = new TempDir();
        Write(t, "rust-toolchain.toml", "[toolchain]\nchannel = \"1.85\"\n");
        Assert.Empty(Detect(t).Tools);
    }

    [Fact]
    public void JavaVersion_GivesItsMajor()
    {
        using var t = new TempDir();
        Write(t, "pom.xml");
        Write(t, ".java-version", "17.0.2\nignored\n");
        var tool = Assert.Single(Detect(t).Tools);
        Assert.Equal("java:17", tool.Tool.ToString());
        Assert.Equal([".java-version", "pom.xml"], tool.Sources);
    }

    [Fact]
    public void JavaVersion_OneDotEight_IsEight()
    {
        using var t = new TempDir();
        Write(t, "gradlew");
        Write(t, ".java-version", "1.8\n");
        Assert.Equal(["java:8"], Specs(Detect(t)));
    }

    [Fact]
    public void JavaMarkers_WithoutAVersionFile_GiveTheDefault()
    {
        using var t = new TempDir();
        Write(t, "app/build.gradle.kts");
        Assert.Equal(["java:21"], Specs(Detect(t)));
    }

    [Fact]
    public void TheNearestVersionFile_AboveEachMarker_Counts()
    {
        using var t = new TempDir();
        Write(t, ".java-version", "21\n");
        Write(t, "a/pom.xml");
        Write(t, "b/pom.xml");
        Write(t, "b/.java-version", "11\n");
        Write(t, "c/.java-version", "17\n"); // no marker at or below it
        var detection = Detect(t);
        Assert.Equal(["java:11", "java:21"], Specs(detection));
        Assert.Equal([".java-version", "a/pom.xml"], Tool(detection, "java:21").Sources);
        Assert.Equal(["b/.java-version", "b/pom.xml"], Tool(detection, "java:11").Sources);
    }

    [Fact]
    public void NodeAndPython_AddNoTool_ButAreSeen()
    {
        using var t = new TempDir();
        Write(t, "web/package.json", "{}");
        Write(t, "requirements.txt");
        var detection = Detect(t);
        Assert.Empty(detection.Tools);
        Assert.True(detection.HasNode);
        Assert.True(detection.HasPython);
    }

    [Fact]
    public void Tools_AreCanonical()
    {
        using var t = new TempDir();
        Write(t, "go.mod");
        Write(t, "Cargo.toml");
        Write(t, "global.json", """{ "sdk": { "version": "10.0.100" } }""");
        Write(t, "pom.xml");
        var specs = Specs(Detect(t));
        Assert.Equal(specs.Order(StringComparer.Ordinal).ToArray(), specs);
        Assert.Equal(["dotnet", "go", "java", "rust"], Detect(t).ToolSpecs.Select(tool => tool.Name));
    }

    // ---- the walk ----

    [Fact]
    public void TheWalk_ListsThreeLevelsDown_AndNoFurther()
    {
        using var t = new TempDir();
        Write(t, "a/b/c/go.mod");
        Write(t, "a/b/c/d/Cargo.toml");
        Write(t, "a/b/c/d/.env");
        Assert.Equal(["go"], Detect(t).ToolSpecs.Select(tool => tool.Name));
        Assert.Empty(Detect(t).Hide);
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".pen")]
    [InlineData("node_modules")]
    [InlineData("vendor")]
    [InlineData("target")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("dist")]
    [InlineData("build")]
    [InlineData(".venv")]
    [InlineData("sub/node_modules")]
    public void SkippedDirs_AreNotEntered(string dir)
    {
        using var t = new TempDir();
        Write(t, $"{dir}/go.mod");
        Write(t, $"{dir}/.env");
        Write(t, $"{dir}/Dockerfile");
        var detection = Detect(t);
        Assert.Empty(detection.Tools);
        Assert.Empty(detection.Hide);
        Assert.Empty(detection.DockerFiles);
    }

    [Fact]
    public void ASymlinkedDirectory_IsNotFollowed()
    {
        using var t = new TempDir();
        var outside = t.Sub("outside");
        File.WriteAllText(Path.Combine(outside, "go.mod"), "");
        File.WriteAllText(Path.Combine(outside, ".env"), "");
        var project = t.Sub("project");
        Directory.CreateSymbolicLink(Path.Combine(project, "linked"), outside);
        var detection = ProjectDetection.Detect(project, RecipeCatalog.Embedded);
        Assert.Empty(detection.Tools);
        Assert.Empty(detection.Hide);
    }

    [Fact]
    public void ASymlinkedMarker_IsNotAToolchain_ButASymlinkedSecretIsHidden()
    {
        using var t = new TempDir();
        var outside = t.Sub("outside");
        File.WriteAllText(Path.Combine(outside, "global.json"), """{ "sdk": { "version": "8.0.100" } }""");
        File.WriteAllText(Path.Combine(outside, "real.env"), "");
        var project = t.Sub("project");
        File.CreateSymbolicLink(Path.Combine(project, "global.json"), Path.Combine(outside, "global.json"));
        File.CreateSymbolicLink(Path.Combine(project, ".env"), Path.Combine(outside, "real.env"));
        var detection = ProjectDetection.Detect(project, RecipeCatalog.Embedded);
        Assert.Empty(detection.Tools);
        Assert.Equal([".env"], Pattern(detection, ".env").Sources);
    }

    [Fact]
    public void TheEntryCap_StopsTheWalk()
    {
        using var t = new TempDir();
        var crowded = t.Sub("a");
        for (var i = 0; i < ProjectDetection.MaxEntries; i++)
            File.Create(Path.Combine(crowded, $"f{i}")).Dispose();
        Write(t, "z/y/go.mod"); // depth 2: listed only after every depth-1 dir, crowded one included
        Assert.Empty(Detect(t).Tools);
    }

    // ---- safe names ----

    [Fact]
    public void AControlCharacterName_IsKeptOutOfTheSources_ButStillCounts()
    {
        using var t = new TempDir();
        Write(t, "bad\ndir/go.mod");
        Write(t, "ok/go.mod");
        Write(t, "bad\u001b[31m/.env");
        Write(t, "x\ny.pem");
        var detection = Detect(t);
        Assert.Equal(["ok/go.mod"], Assert.Single(detection.Tools).Sources);
        Assert.Empty(Pattern(detection, ".env").Sources);
        Assert.Empty(Pattern(detection, "/*.pem").Sources);
        Assert.All(detection.Tools.SelectMany(tool => tool.Sources), source => Assert.True(ProjectDetection.IsSafeName(source)));
    }

    [Fact]
    public void AControlCharacterDir_GivesNoHints()
    {
        using var t = new TempDir();
        Write(t, "bad\ndir/Dockerfile");
        Write(t, "bad\ndir/.env");
        Write(t, "bad\ndir/.env.example");
        var detection = Detect(t);
        Assert.Empty(detection.DockerFiles);
        Assert.Empty(detection.ReplaceHints);
    }

    [Fact]
    public void AnEnvNameWithAControlCharacter_IsntAPattern()
    {
        using var t = new TempDir();
        Write(t, ".env.lo\ncal");
        Assert.Empty(Detect(t).Hide);
    }

    [Fact]
    public void AnUndecodableName_IsKeptOutOfTheSources()
    {
        using var t = new TempDir();
        // A file, not a directory: .NET lists an undecodable name lossily (U+FFFD), and a lossy directory name
        // doesn't lead back to the directory, so nothing inside it could be found.
        var r = Process.RunAndCaptureText(new ProcessStartInfo("sh", ["-c", ": > \"$(printf 'bad\\377.pem')\""])
        {
            WorkingDirectory = t.Path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.SkipWhen(r.ExitStatus.ExitCode != 0 && r.StandardError.Contains("llegal byte sequence", StringComparison.Ordinal), "the file system refuses names that aren't valid UTF-8");
        Assert.True(r.ExitStatus.ExitCode == 0, r.StandardError);
        var pattern = Assert.Single(Detect(t).Hide);
        Assert.Equal("/*.pem", pattern.Pattern);
        Assert.Empty(pattern.Sources);
    }

    // ---- hide ----

    [Fact]
    public void DotEnv_IsHidden()
    {
        using var t = new TempDir();
        Write(t, ".env");
        Write(t, "api/.env");
        var detection = Detect(t);
        Assert.Equal([".env"], Patterns(detection));
        Assert.Equal([".env", "api/.env"], Pattern(detection, ".env").Sources);
    }

    [Fact]
    public void EnvExamples_AreNotHidden_WhileEnvLocalIs()
    {
        using var t = new TempDir();
        Write(t, ".env.example");
        Write(t, ".env.sample");
        Write(t, ".env.template");
        Write(t, ".env.dist");
        Write(t, ".env.defaults");
        Write(t, ".env.EXAMPLE");
        Write(t, ".env.local.example");
        Write(t, ".env.development.sample");
        Write(t, ".env.local");
        Write(t, "web/.env.production");
        Write(t, ".env.has space");
        var detection = Detect(t);
        Assert.Equal([".env.local", ".env.production"], Patterns(detection));
        Assert.Equal(["web/.env.production"], Pattern(detection, ".env.production").Sources);
    }

    [Fact]
    public void ADirectoryNamedDotEnv_IsntHidden()
    {
        using var t = new TempDir();
        t.Sub(".env", "lib");
        Assert.Empty(Detect(t).Hide);
    }

    [Fact]
    public void KeyFiles_AndSecretsDirs_AreHidden_InOrder()
    {
        using var t = new TempDir();
        Write(t, "certs/dev.pem");
        Write(t, "certs/prod.PEM");
        Write(t, "a/b/c/signing.p12");
        Write(t, "tls.key");
        Write(t, "store.pfx");
        Write(t, ".pem"); // a name that's only the extension isn't a key file
        t.Sub("config", "secrets");
        Write(t, ".env");
        var detection = Detect(t);
        Assert.Equal([".env", "/certs/*.pem", "/*.key", "/a/b/c/*.p12", "/*.pfx", "secrets/"], Patterns(detection));
        Assert.Equal(["certs/dev.pem", "certs/prod.PEM"], Pattern(detection, "/certs/*.pem").Sources);
        Assert.Equal(["tls.key"], Pattern(detection, "/*.key").Sources);
        Assert.Equal(["config/secrets"], Pattern(detection, "secrets/").Sources);
        Assert.All(detection.Hide, found => Assert.True(Masking.HidePattern.TryParse(found.Pattern, out _, out _)));
    }

    [Fact]
    public void AKeyFile_IsHiddenInItsOwnDirectory()
    {
        using var t = new TempDir();
        Write(t, "certs/dev.pem");
        var pattern = Assert.Single(Detect(t).Hide);
        Assert.Equal("/certs/*.pem", pattern.Pattern);
        Assert.Equal(["certs/dev.pem"], pattern.Sources);
    }

    [Fact]
    public void AKeyFileAtTheRoot_IsHiddenAtTheRoot()
    {
        using var t = new TempDir();
        Write(t, "server.key");
        var pattern = Assert.Single(Detect(t).Hide);
        Assert.Equal("/*.key", pattern.Pattern);
        Assert.Equal(["server.key"], pattern.Sources);
    }

    [Fact]
    public void KeyFilesInTwoDirectories_GiveTwoPatterns_InOrder()
    {
        using var t = new TempDir();
        Write(t, "web/tls/site.pem");
        Write(t, "certs/dev.pem");
        Write(t, "certs/ca.pem");
        Write(t, "web/tls/site.key");
        var detection = Detect(t);
        Assert.Equal(["/certs/*.pem", "/web/tls/*.pem", "/web/tls/*.key"], Patterns(detection));
        Assert.Equal(["certs/ca.pem", "certs/dev.pem"], Pattern(detection, "/certs/*.pem").Sources);
        Assert.Equal(["web/tls/site.pem"], Pattern(detection, "/web/tls/*.pem").Sources);
    }

    [Theory]
    [InlineData("we*ird/x.pem", "/*/*.pem")]
    [InlineData("a/b?c/x.key", "/a/*/*.key")]
    [InlineData("a/[x]/x.pfx", "/a/*/*.pfx")]
    [InlineData("a/b]/x.pfx", "/a/*/*.pfx")]
    [InlineData("a/back\\slash/c/x.p12", "/a/*/c/*.p12")]
    [InlineData("a/**/x.pem", "/a/*/*.pem")]
    [InlineData("app/[slug]/x.pem", "/app/*/*.pem")]
    [InlineData("a/b?c/d/x.p12", "/a/*/d/*.p12")]
    public void ADirectoryNameWithPatternCharacters_FallsBackToAStarAtItsDepth(string file, string expected)
    {
        using var t = new TempDir();
        Write(t, file);
        var pattern = Assert.Single(Detect(t).Hide);
        Assert.Equal(expected, pattern.Pattern);
        Assert.Equal([file], pattern.Sources);
        Assert.True(HidePattern.TryParse(pattern.Pattern, out var parsed, out _));
        Assert.False(parsed.IsMatch(".venv/lib/certifi/cacert.pem", isDirectory: false));
        Assert.False(parsed.IsMatch("node_modules/x/y.pem", isDirectory: false));
    }

    [Fact]
    public void AnUnsafeDirectoryName_FallsBackToAStarAtItsDepth_WithoutASource()
    {
        using var t = new TempDir();
        Write(t, "a/bad\ndir/x.pem");
        var pattern = Assert.Single(Detect(t).Hide);
        Assert.Equal("/a/*/*.pem", pattern.Pattern);
        Assert.Empty(pattern.Sources);
    }

    [Theory]
    [InlineData("!neg/x.pem", "/!neg/*.pem")]
    [InlineData("#hash/x.pem", "/#hash/*.pem")]
    [InlineData(" lead/trail /x.pem", "/ lead/trail /*.pem")]
    [InlineData("a b/x.pem", "/a b/*.pem")]
    public void ADirectoryNameThatOnlyLooksSpecial_IsWrittenLiterally(string file, string expected)
    {
        using var t = new TempDir();
        Write(t, file);
        Assert.Equal([expected], Patterns(Detect(t)));
    }

    [Theory]
    [InlineData("server.key")]
    [InlineData("certs/dev.pem")]
    [InlineData("a/b/c/signing.P12")]
    [InlineData("a/we*ird/c/x.pfx")]
    [InlineData("!neg/ sp /x.pem")]
    public void EachKeyPattern_ParsesAndMatchesItsFiles(string file)
    {
        using var t = new TempDir();
        Write(t, file);
        var found = Assert.Single(Detect(t).Hide);
        Assert.True(Masking.HidePattern.TryParse(found.Pattern, out var pattern, out _));
        Assert.True(pattern.IsMatch(file, isDirectory: false), found.Pattern);
        Assert.True(pattern.IsAnchored);
    }

    [Fact]
    public void AnchoredKeyPatterns_DontReachOtherDirectories()
    {
        using var t = new TempDir();
        Write(t, "certs/dev.pem");
        var found = Assert.Single(Detect(t).Hide);
        Assert.True(Masking.HidePattern.TryParse(found.Pattern, out var pattern, out _));
        Assert.False(pattern.IsMatch(".venv/lib/certifi/cacert.pem", isDirectory: false));
        Assert.False(pattern.IsMatch("node_modules/x/test/certs/dev.pem", isDirectory: false));
        Assert.False(pattern.IsMatch("certs/sub/dev.pem", isDirectory: false));
    }

    [Theory]
    [InlineData(".venv/lib/python3.12/site-packages/certifi/cacert.pem")]
    [InlineData(".venv/server.key")]
    [InlineData("node_modules/x/test/fixtures/dev.pem")]
    [InlineData("sub/node_modules/x/agent.key")]
    [InlineData("sub/.venv/cacert.pem")]
    public void KeyFilesInSkippedDirs_GiveNoPattern(string file)
    {
        using var t = new TempDir();
        Write(t, file);
        Assert.Empty(Detect(t).Hide);
    }

    [Fact]
    public void AFileNamedSecrets_IsntADirectoryPattern()
    {
        using var t = new TempDir();
        Write(t, "secrets");
        Assert.Empty(Detect(t).Hide);
    }

    // ---- hints ----

    [Fact]
    public void DockerFiles_AreHints()
    {
        using var t = new TempDir();
        Write(t, "Dockerfile");
        Write(t, "deploy/compose.yaml");
        Write(t, "docker-compose.yml");
        Write(t, "dockerfile.txt");
        Assert.Equal(["Dockerfile", "deploy/compose.yaml", "docker-compose.yml"], Detect(t).DockerFiles);
    }

    [Fact]
    public void EnvBesideEnvExample_IsAReplaceHint()
    {
        using var t = new TempDir();
        Write(t, ".env");
        Write(t, ".env.example");
        Write(t, "api/.env");
        Write(t, "api/.env.example");
        Write(t, "web/.env");
        Write(t, "docs/.env.example");
        var detection = Detect(t);
        Assert.Equal(
            [new ReplaceHint(".env", ".env.example"), new ReplaceHint("api/.env", "api/.env.example")],
            detection.ReplaceHints);
    }
}
