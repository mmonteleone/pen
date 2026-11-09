using System.Formats.Tar;
using System.Text;
using System.Text.Json;

namespace Pen.Tests;

[Collection(nameof(LockAndProcessTests))] // FakeDocker changes PATH
public class ToolsImageTests
{
    private const string BaseId = "sha256:aaaa";

    private static RecipeCatalog Catalog(string script = "#!/bin/sh\n", string settings = "default = \"1\"\nversion = '^\\d+$'\nmultiple = true\n") =>
        RecipeCatalog.FromFiles(new Dictionary<string, string>
        {
            ["tools/a/tool.toml"] = settings,
            ["tools/a/install.sh"] = script,
            ["tools/b/tool.toml"] = "default = \"1\"\nversion = '^\\d+$'\nmultiple = true\n",
            ["tools/b/install.sh"] = "#!/bin/sh\necho b\n",
            ["tools/anything/tool.toml"] = "default = \"1\"\nversion = '.*'\n",
            ["tools/anything/install.sh"] = "#!/bin/sh\n",
        });

    private static readonly ToolSpec[] Tools = [new("a", "1"), new("a", "2"), new("b", "3")];

    [Fact]
    public void TheTagIgnoresOrderAndDuplicates()
    {
        var want = ToolsImage.Tag(BaseId, Tools, Catalog());
        Assert.Matches("^pen-tools:[0-9a-f]{16}$", want);
        Assert.Equal(want, ToolsImage.Tag(BaseId, [Tools[2], Tools[1], Tools[0]], Catalog()));
        Assert.Equal(want, ToolsImage.Tag(BaseId, [Tools[1], Tools[2], Tools[0], Tools[1]], Catalog()));
    }

    [Fact]
    public void TheTagChangesWithTheBaseTheToolsAndTheRecipeFiles()
    {
        var want = ToolsImage.Tag(BaseId, Tools, Catalog());
        Assert.NotEqual(want, ToolsImage.Tag("sha256:bbbb", Tools, Catalog()));
        Assert.NotEqual(want, ToolsImage.Tag(BaseId, Tools[..2], Catalog()));
        Assert.NotEqual(want, ToolsImage.Tag(BaseId, [Tools[0], new("a", "9"), Tools[2]], Catalog()));
        Assert.NotEqual(want, ToolsImage.Tag(BaseId, Tools, Catalog(script: "#!/bin/sh\necho changed\n")));
        Assert.NotEqual(want, ToolsImage.Tag(BaseId, Tools, Catalog(settings: "default = \"1\"\nversion = '^\\d+$'\nmultiple = true\nallow = [\"example.org\"]\n")));
    }

    [Fact]
    public void ARecipeNotInUseDoesntAffectTheTag()
    {
        var more = RecipeCatalog.FromFiles(new Dictionary<string, string>
        {
            ["tools/a/tool.toml"] = "default = \"1\"\nversion = '^\\d+$'\nmultiple = true\n",
            ["tools/a/install.sh"] = "#!/bin/sh\n",
            ["tools/b/tool.toml"] = "default = \"1\"\nversion = '^\\d+$'\nmultiple = true\n",
            ["tools/b/install.sh"] = "#!/bin/sh\necho changed\n",
        });
        ToolSpec[] onlyA = [new("a", "1")];
        Assert.Equal(ToolsImage.Tag(BaseId, onlyA, Catalog()), ToolsImage.Tag(BaseId, onlyA, more));
    }

    [Fact]
    public void AVersionReachesTheHashAndTheScriptExactlyAsWritten()
    {
        ToolSpec[] zero = [new("anything", "24.04")], none = [new("anything", "24.4")];
        Assert.NotEqual(ToolsImage.Tag(BaseId, zero, Catalog()), ToolsImage.Tag(BaseId, none, Catalog()));
        var run = ToolsImage.Dockerfile(zero, Catalog()).TrimEnd('\n').Split('\n')[2];
        Assert.Equal(["/pen-recipe/install.sh", "24.04"], Run(run).Argv);
    }

    [Fact]
    public void EachRecipeRunsOnceInCanonicalOrderWithItsVersionsAsArguments()
    {
        var lines = ToolsImage.Dockerfile([Tools[2], Tools[1], Tools[0]], Catalog()).TrimEnd('\n').Split('\n');
        Assert.Equal(["FROM pen:latest", "USER root"], lines[..2]);
        Assert.Equal(4, lines.Length);
        var (argv, mounted) = Run(lines[2]);
        Assert.Equal(["/pen-recipe/install.sh", "1", "2"], argv);
        Assert.Equal("tools/a", mounted);
        (argv, mounted) = Run(lines[3]);
        Assert.Equal(["/pen-recipe/install.sh", "3"], argv);
        Assert.Equal("tools/b", mounted);
    }

    /// <summary>A generated RUN line's arguments as Docker reads them, and its mounted directory.</summary>
    private static (string[] Argv, string Mounted) Run(string line)
    {
        var m = System.Text.RegularExpressions.Regex.Match(line, @"^RUN --mount=type=bind,source=([^,]+),target=/pen-recipe (\[.*\])$");
        Assert.True(m.Success, line);
        return (JsonDocument.Parse(m.Groups[2].Value).RootElement.EnumerateArray().Select(e => e.GetString()!).ToArray(), m.Groups[1].Value);
    }

    // A ToolSpec is only meant to hold what TryParse accepts. If a hostile version ever got
    // around that, it must not become Dockerfile text: not a new instruction, a broken JSON
    // array that Docker would run through a shell, or an argument with shell syntax.
    [Theory]
    [InlineData("1\nRUN touch /pwned")]
    [InlineData("1\",\"2")]
    [InlineData("1\"]; touch /pwned; [\"")]
    [InlineData("$(touch /pwned)")]
    [InlineData("--option")]
    [InlineData("1\\")]
    public void AHostileVersionNeverReachesTheDockerfile(string version)
    {
        // This recipe's own pattern accepts anything, so only pen's version syntax stops these.
        ToolSpec[] tools = [new("anything", version)];
        Assert.Contains("isn't a valid version", Assert.Throws<PenException>(() => ToolsImage.Dockerfile(tools, Catalog())).Message);
        Assert.Throws<PenException>(() => ToolsImage.Tag(BaseId, tools, Catalog()));
        Assert.Throws<PenException>(() => ToolsImage.WriteContext(Stream.Null, tools, Catalog()));
    }

    [Fact]
    public void AVersionItsRecipeDoesntAcceptIsRefused()
    {
        Assert.Throws<PenException>(() => ToolsImage.Dockerfile([new("a", "x")], Catalog()));
        Assert.Throws<PenException>(() => ToolsImage.Dockerfile([new("nope", "1")], Catalog()));
    }

    [Fact]
    public void TheContextHasTheDockerfileAndOnlyTheScriptsInUse()
    {
        using var tar = new MemoryStream();
        ToolsImage.WriteContext(tar, [new("b", "3")], Catalog());
        var entries = ReadTar(tar);
        Assert.Equal(["Dockerfile", "tools/b/install.sh"], entries.Keys);
        Assert.Equal(ToolsImage.Dockerfile([new("b", "3")], Catalog()), entries["Dockerfile"].Text);
        Assert.Equal("#!/bin/sh\necho b\n", entries["tools/b/install.sh"].Text);
        Assert.True(entries["tools/b/install.sh"].Mode.HasFlag(UnixFileMode.UserExecute));
        Assert.False(entries["Dockerfile"].Mode.HasFlag(UnixFileMode.UserExecute));
    }

    private static SortedDictionary<string, (string Text, UnixFileMode Mode)> ReadTar(Stream stream)
    {
        stream.Position = 0;
        var entries = new SortedDictionary<string, (string, UnixFileMode)>(StringComparer.Ordinal);
        using var reader = new TarReader(stream);
        while (reader.GetNextEntry() is { } entry)
        {
            using var text = new StreamReader(entry.DataStream!, Encoding.UTF8);
            entries[entry.Name] = (text.ReadToEnd(), entry.Mode);
        }
        return entries;
    }

    [Fact]
    public void NoToolsMeansTheBaseImageAndNoDocker()
    {
        using var t = new TempDir();
        using var fake = new FakeDocker(t, "exit 1\n");
        Assert.Equal(Image.Name, ToolsImage.Ensure([], Catalog()));
        Assert.False(File.Exists(Path.Combine(t.Path, "calls")));
    }

    [Fact]
    public void AnExistingTagIsNotRebuilt()
    {
        using var t = new TempDir();
        using var fake = new FakeDocker(t, "if [ \"$1\" = build ]; then exit 9; fi\necho sha256:aaaa\n");
        Assert.Equal(ToolsImage.Tag(BaseId, Tools, Catalog()), ToolsImage.Ensure(Tools, Catalog()));
        Assert.DoesNotContain("build", File.ReadAllText(Path.Combine(t.Path, "calls")));
    }

    [Fact]
    public void AMissingTagIsBuiltFromTheTarOnStdinWithTheBaseLabel()
    {
        using var t = new TempDir();
        using var fake = new FakeDocker(t, """
            case "$1 $*" in
              build*) cat >context.tar ;;
              *"pen:latest"*) echo sha256:aaaa ;;
              *) exit 1 ;;
            esac

            """);
        var name = ToolsImage.Ensure(Tools, Catalog());
        Assert.Equal(ToolsImage.Tag(BaseId, Tools, Catalog()), name);
        Assert.Contains($"build --progress plain -t {name} --label pen.base-id={BaseId} -", File.ReadAllText(Path.Combine(t.Path, "calls")));
        using var tar = File.OpenRead(Path.Combine(t.Path, "context.tar"));
        Assert.Contains("Dockerfile", ReadTar(tar).Keys);
    }

    [Fact]
    public void AFailedBuildIsAnError()
    {
        using var t = new TempDir();
        using var fake = new FakeDocker(t, """
            case "$1 $*" in
              build*) exit 7 ;;
              *"pen:latest"*) echo sha256:aaaa ;;
              *) exit 1 ;;
            esac

            """);
        Assert.Contains("docker build exited 7", Assert.Throws<PenException>(() => ToolsImage.Ensure(Tools, Catalog())).Message);
    }

    [Fact]
    public void WithoutABaseImageNothingIsBuilt()
    {
        using var t = new TempDir();
        using var fake = new FakeDocker(t, "exit 1\n");
        Assert.Throws<PenException>(() => ToolsImage.Ensure(Tools, Catalog()));
        Assert.DoesNotContain("build", File.ReadAllText(Path.Combine(t.Path, "calls")));
    }
}
