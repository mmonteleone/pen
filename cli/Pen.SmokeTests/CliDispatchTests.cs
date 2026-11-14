using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The installed pen's command line: version, help, usage errors and where --port and --tool go.</summary>
public sealed class CliDispatchTests(SmokeFixture fixture) : IClassFixture<SmokeFixture>
{
    private PenCli Pen => fixture.Pen;

    [Fact(DisplayName = "pen --version")]
    public void PrintsItsVersion()
    {
        var result = Pen.Run("--version");

        Assert.Matches(new Regex("^pen [0-9]", RegexOptions.Multiline), result.Stdout);
    }

    [Fact(DisplayName = "pen runs from fish")]
    public void RunsFromFish()
    {
        var fish = SmokeEnvironment.FindOnPath("fish");
        Assert.SkipWhen(fish == null, "fish not installed: skipped the fish check");

        var result = Pen.Wrap(fish, "-c", $"{ShellQuote.Quote(SmokeEnvironment.PenPath)} --version").Run();

        Assert.True(result.ExitCode == 0, result.Describe());
    }

    [Fact(DisplayName = "pen --help lists the subcommands")]
    public void HelpListsTheSubcommands()
    {
        var help = Pen.Run("--help").Stdout;

        Assert.Multiple(
            () => Assert.Contains("pen allow", help, StringComparison.Ordinal),
            () => Assert.Contains("pen upgrade", help, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "an unknown option is a usage error (exit 2)")]
    public void UnknownOptionIsAUsageError()
    {
        var result = Pen.Run("--bogus");

        Assert.True(result.ExitCode == 2, result.Describe());
    }

    [Fact(DisplayName = "pen run without a command is a usage error (exit 2)")]
    public void RunWithoutACommandIsAUsageError()
    {
        var result = Pen.Run("run");

        Assert.True(result.ExitCode == 2, result.Describe());
    }

    [Fact(DisplayName = "pen --port 0 is a usage error (exit 2), before any session")]
    public void PortZeroIsAUsageError() => AssertBadPort("--port", "0");

    [Fact(DisplayName = "pen --port 65536 is a usage error (exit 2), before any session")]
    public void PortPast65535IsAUsageError() => AssertBadPort("--port", "65536");

    [Fact(DisplayName = "pen --port http is a usage error (exit 2), before any session")]
    public void PortNameIsAUsageError() => AssertBadPort("--port", "http");

    [Fact(DisplayName = "pen --port=80x ls is a usage error (exit 2), before any session")]
    public void PortWithTrailingTextIsAUsageError() => AssertBadPort("--port=80x", "ls");

    [Fact(DisplayName = "pen --port is a usage error (exit 2), before any session")]
    public void PortWithoutAValueIsAUsageError() => AssertBadPort("--port");

    [Fact(DisplayName = "--port with a subcommand is a usage error")]
    public void PortWithASubcommandIsAUsageError()
    {
        var result = Pen.Run("--port", "8765", "allow");

        Assert.True(result.IsUsageError("--port goes only with a command to run, not pen allow"), result.Describe());
    }

    [Fact(DisplayName = "pen --help lists pen trust")]
    public void HelpListsTrust() => Assert.Contains("pen trust", Pen.Run("--help").Stdout, StringComparison.Ordinal);

    [Fact(DisplayName = "pen trust --help documents --yes")]
    public void TrustHelpDocumentsYes() =>
        Assert.Contains("--yes", Pen.Run("trust", "--help").Stdout, StringComparison.Ordinal);

    [Fact(DisplayName = "pen --help documents --port")]
    public void HelpDocumentsPort() =>
        Assert.Contains("--port <n>", Pen.Run("--help").Stdout, StringComparison.Ordinal);

    [Fact(DisplayName = "pen run --help documents --port and ports")]
    public void RunHelpDocumentsPortAndPorts()
    {
        var help = Pen.Run("run", "--help").Stdout;

        Assert.Multiple(
            () => Assert.Contains("--port <n>", help, StringComparison.Ordinal),
            () => Assert.Contains("ports = [", help, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "pen --port 8765 --port=8766 starts a session")]
    public void PortsStartASession()
    {
        var result = Pen.Run("--port", "8765", "--port=8766", "run", "true");

        Assert.True(result.ExitCode == 0, result.Describe());
    }

    [Fact(DisplayName = "...and --port after the command is the command's")]
    public void PortAfterTheCommandIsTheCommands()
    {
        var result = Pen.Run("run", "sh", "-c", "echo \"[$*]\"", "_", "--port", "8765");

        Assert.Contains("[--port 8765]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "pen --help documents --tool")]
    public void HelpDocumentsTool() =>
        Assert.Contains("--tool <spec>", Pen.Run("--help").Stdout, StringComparison.Ordinal);

    [Fact(DisplayName = "pen --tool list works outside a session")]
    public void ToolListWorksOutsideASession()
    {
        var result = Pen.Run("--tool", "list");

        Assert.True(result.ExitCode == 0, result.Describe());
    }

    [Fact(DisplayName = "...and doesn't offer pen's test recipe")]
    public void ToolListHidesTheTestRecipe() =>
        Assert.DoesNotContain("pen-test", Pen.Run("--tool", "list").Stdout, StringComparison.Ordinal);

    [Fact(DisplayName = "pen --tool nope is a usage error (exit 2), before any session")]
    public void UnknownToolIsAUsageError() => AssertBadTool("--tool", "nope");

    [Fact(DisplayName = "pen --tool=nope:1 ls is a usage error (exit 2), before any session")]
    public void UnknownToolVersionIsAUsageError() => AssertBadTool("--tool=nope:1", "ls");

    [Fact(DisplayName = "pen --tool is a usage error (exit 2), before any session")]
    public void ToolWithoutASpecIsAUsageError() => AssertBadTool("--tool");

    [Fact(DisplayName = "pen --tool list ls is a usage error (exit 2), before any session")]
    public void ToolListWithACommandIsAUsageError() => AssertBadTool("--tool", "list", "ls");

    [Fact(DisplayName = "a shell-like tool version is refused by the recipe's regex in the AOT build")]
    public void ShellLikeToolVersionIsRefused()
    {
        var result = Pen.Run("--tool", "pen-test:$(touch /tmp/x)");

        Assert.True(result.IsUsageError("isn't a valid version of pen-test"), result.Describe());
    }

    [Fact(DisplayName = "--tool with a subcommand is a usage error")]
    public void ToolWithASubcommandIsAUsageError()
    {
        var result = Pen.Run("--tool", "pen-test:1", "allow");

        Assert.True(result.IsUsageError("--tool goes only with a command to run, not pen allow"), result.Describe());
    }

    [Fact(DisplayName = "pen --tool runs the session with the tool installed")]
    public void ToolIsInstalledInTheSession()
    {
        var result = Pen.Run("--tool", "pen-test", "run", "pen-test");

        Assert.True(result.CapturedStdout == "1", result.Describe());
    }

    private void AssertBadPort(params string[] args)
    {
        var result = Pen.Run(args);

        Assert.True(result.ExitCode == 2 && result.HasLineStartingWith("pen: --port: "), result.Describe());
    }

    private void AssertBadTool(params string[] args)
    {
        var result = Pen.Run(args);

        Assert.True(result.ExitCode == 2 && result.HasLineStartingWith("pen: --tool"), result.Describe());
    }
}
