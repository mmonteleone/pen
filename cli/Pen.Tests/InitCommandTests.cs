using System.Reflection;
using System.Runtime.InteropServices;

namespace Pen.Tests;

/// <summary>
/// pen init writes a starting .pen/config.toml with what detection found turned on, never overwriting, and
/// summarizes it.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public class InitCommandTests
{
    private static (HostEnv Host, Project Project) Setup(TempDir t)
    {
        var host = t.Host();
        return (host, Project.Resolve(t.Sub("repo"), host));
    }

    private static int Init(HostEnv host, Project project) =>
        new InitCommand(host, project, RecipeCatalog.Embedded).Run();

    private static string CapturingStderr(Action action)
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { action(); }
        finally { Console.SetError(stderr); }
        return captured.ToString();
    }

    [Fact]
    public void ParsesInit_ItsHelp_AndRefusesArguments()
    {
        Assert.IsType<InitInvocation>(CommandParser.Parse(["init"], () => null, RecipeCatalog.Embedded));

        var help = CliTests.Render("init", "--help");
        Assert.Contains("Usage:\n  pen init [options]", help);
        Assert.Contains("It never overwrites", help);
        Assert.Contains("\n  init  ", CliTests.Render("--help"));

        Assert.Equal(2, Assert.Throws<PenException>(() => CommandParser.Parse(["init", "x"], () => null, RecipeCatalog.Embedded)).ExitCode);
        Assert.Equal(2, Assert.Throws<PenException>(() => CommandParser.Parse(["init", "--force"], () => null, RecipeCatalog.Embedded)).ExitCode);
        var tool = Assert.Throws<PenException>(() => CommandParser.Parse(["--tool", "go", "init"], () => null, RecipeCatalog.Embedded));
        Assert.StartsWith("--tool goes only with a command to run, not pen init", tool.Message);
        Assert.Equal(["init"], Assert.IsType<RunInvocation>(CommandParser.Parse(["run", "init"], () => null, RecipeCatalog.Embedded)).Command);
    }

    [Fact]
    public void WritesAParsingConfig_WithTheDetectedToolsAndHide_InMode644()
    {
        using var t = new TempDir();
        var (host, project) = Setup(t);
        File.WriteAllText(Path.Combine(project.Dir, "go.mod"), "module example.com/x\n");
        File.WriteAllText(Path.Combine(project.Dir, ".env"), "SECRET=1\n");

        var err = CapturingStderr(() => Assert.Equal(0, Init(host, project)));

        var text = File.ReadAllText(project.ProjectConfigPath);
        var config = ConfigParser.ParseProject(text, ".pen/config.toml", RecipeCatalog.Embedded);
        Assert.Equal(["go"], config.Tools.Select(tool => tool.Name));
        Assert.Equal([".env"], config.Hide.Select(pattern => pattern.Text));
        Assert.Null(config.Docker);
        Assert.Empty(config.Allow);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(project.ProjectConfigPath));
        Assert.Equal(
            [
                "pen: created .pen/config.toml",
                "  tools: go (go.mod)",
                "  hide: .env (found .env)",
                "pen: review it, then commit it; the first session asks you to approve its tools (or run pen trust)",
            ],
            err.TrimEnd('\n').Split('\n'));
        // It records the project, as every command that resolves one does.
        Assert.True(File.Exists(Path.Combine(host.GetRuntimeDir(project.Key), "project-dir")));
    }

    [Fact]
    public void WithNothingFound_WritesTheCommentedTemplate_AndAsksNoApproval()
    {
        using var t = new TempDir();
        var (host, project) = Setup(t);

        var err = CapturingStderr(() => Assert.Equal(0, Init(host, project)));

        Assert.Equal(ProjectTemplate.Render(ProjectDetection.None), File.ReadAllText(project.ProjectConfigPath));
        Assert.Equal(
            ["pen: created .pen/config.toml", "pen: review it, then commit it"],
            err.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void RefusesAnExistingConfig_LeavingItAlone()
    {
        using var t = new TempDir();
        var (host, project) = Setup(t);
        Directory.CreateDirectory(project.PenDir);
        // Broken on purpose: it's never read.
        File.WriteAllText(project.ProjectConfigPath, "tools = [\n");
        File.WriteAllText(Path.Combine(project.Dir, "go.mod"), "module x\n");

        var e = Assert.Throws<PenException>(() => Init(host, project));

        Assert.Equal(1, e.ExitCode);
        Assert.Equal(".pen/config.toml already exists: edit it (docs/usage.md#project-settings)", e.Message);
        Assert.Equal("tools = [\n", File.ReadAllText(project.ProjectConfigPath));
    }

    [Fact]
    public void RefusesADirectoryNamedConfigToml()
    {
        using var t = new TempDir();
        var (host, project) = Setup(t);
        Directory.CreateDirectory(project.ProjectConfigPath);

        var e = Assert.Throws<PenException>(() => Init(host, project));

        Assert.StartsWith(".pen/config.toml already exists", e.Message);
    }

    [Fact]
    public void RefusesALegacyConfig_WritingNothing()
    {
        using var t = new TempDir();
        var (host, project) = Setup(t);
        Directory.CreateDirectory(project.PenDir);
        File.WriteAllText(Path.Combine(project.PenDir, "config"), "ALLOW=example.com\n");

        var e = Assert.Throws<PenException>(() => Init(host, project));

        Assert.Equal(1, e.ExitCode);
        Assert.StartsWith(".pen/config, pen's old config format, already exists: move its settings to " +
            ".pen/config.toml", e.Message);
        Assert.False(File.Exists(project.ProjectConfigPath));
    }

    [Fact]
    public void RefusesASymlinkedPenDir()
    {
        using var t = new TempDir();
        var (host, project) = Setup(t);
        var elsewhere = t.Sub("elsewhere");
        Directory.CreateSymbolicLink(project.PenDir, elsewhere);

        var e = Assert.Throws<PenException>(() => Init(host, project));

        Assert.Contains("is a symlink", e.Message);
        Assert.Empty(Directory.GetFileSystemEntries(elsewhere));
    }

    [Fact]
    public void RefusesASymlinkedConfig_DanglingOrNot()
    {
        using var t = new TempDir();
        var (host, project) = Setup(t);
        Directory.CreateDirectory(project.PenDir);
        var target = Path.Combine(t.Path, "target.toml");
        File.CreateSymbolicLink(project.ProjectConfigPath, target);

        var dangling = Assert.Throws<PenException>(() => Init(host, project));
        Assert.Contains("is a symlink", dangling.Message);
        Assert.False(File.Exists(target));

        File.WriteAllText(target, "");
        var existing = Assert.Throws<PenException>(() => Init(host, project));
        Assert.Contains("is a symlink", existing.Message);
        Assert.Equal("", File.ReadAllText(target));
    }

    [Fact]
    public void ACreateFailingInAReadOnlyPenDir_IsntReportedAsAlreadyExisting()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root writes everything");
        using var t = new TempDir();
        var (host, project) = Setup(t);
        Directory.CreateDirectory(project.PenDir);
        File.SetUnixFileMode(project.PenDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var e = Assert.Throws<UnauthorizedAccessException>(() => Init(host, project));

            Assert.Equal($"Access to the path '{project.ProjectConfigPath}' is denied.", e.Message);
        }
        finally
        {
            File.SetUnixFileMode(project.PenDir,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.False(File.Exists(project.ProjectConfigPath));
    }

    /// <summary>
    /// Only something already at the path is reported as existing: another I/O error, here a missing directory (which
    /// pen init makes first, so it's reached only by calling CreateNew directly), is passed on as it is.
    /// </summary>
    [Fact]
    public void CreateNew_PassesOnAnIOErrorOtherThanExisting()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "missing", "config.toml");
        var createNew = typeof(InitCommand).GetMethod("CreateNew", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("InitCommand.CreateNew is gone: update this test to follow it");

        var e = Assert.Throws<TargetInvocationException>(() => createNew.Invoke(null, [path]));

        var inner = Assert.IsType<DirectoryNotFoundException>(e.InnerException);
        Assert.Equal($"Could not find a part of the path '{path}'.", inner.Message);
        Assert.False(Path.Exists(path));
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);

    /// <summary>The file is 644 however narrow the umask. The collection runs alone, so no other test sees it.</summary>
    [Fact]
    public void WritesTheConfigInMode644_UnderARestrictiveUmask()
    {
        using var t = new TempDir();
        var (host, project) = Setup(t);
        var saved = Umask(0b000_111_111) & 0b111_111_111;
        try
        {
            CapturingStderr(() => Assert.Equal(0, Init(host, project)));
        }
        finally
        {
            Umask(saved);
        }

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead,
            File.GetUnixFileMode(project.ProjectConfigPath));
    }

    [Fact]
    public void FormatFound_ShowsToolsAsWritten_CapsSources_AndEscapesRepoText()
    {
        var detection = new ProjectDetection
        {
            Tools =
            [
                new DetectedTool(new ToolSpec("dotnet", "10"), ["cli/global.json"]) { IsPinned = true },
                new DetectedTool(new ToolSpec("go", "1.27"), ["a/go.mod", "b/go.mod", "c/go.mod", "d/go.mod"]),
                new DetectedTool(new ToolSpec("rust", "stable"), []),
            ],
            Hide =
            [
                new DetectedPattern(".env", [".env", "web/.env"]),
                new DetectedPattern("/certs/*.pem", ["certs/dev.pem"]),
                new DetectedPattern("/a\u202e/*.key", ["a\u202e/x.key"]),
                new DetectedPattern("secrets/", ["secrets"]),
            ],
        };

        Assert.Equal(
            [
                "  tools: dotnet:10 (cli/global.json), go (a/go.mod, b/go.mod, c/go.mod and 1 more), rust",
                "  hide: .env, /certs/*.pem, /a\\u202e/*.key and 1 more (found .env, web/.env, certs/dev.pem and 2 more)",
            ],
            InitCommand.FormatFound(detection));
        Assert.Contains("approve its tools", InitCommand.FormatNextStep(detection));
    }

    [Fact]
    public void FormatFound_LeavesOutEmptyLines_AndTheApprovalWithoutTools()
    {
        var hideOnly = new ProjectDetection { Hide = [new DetectedPattern(".env", [".env"])] };

        Assert.Equal(["  hide: .env (found .env)"], InitCommand.FormatFound(hideOnly));
        Assert.Equal("review it, then commit it", InitCommand.FormatNextStep(hideOnly));
        Assert.Empty(InitCommand.FormatFound(ProjectDetection.None));
    }
}
