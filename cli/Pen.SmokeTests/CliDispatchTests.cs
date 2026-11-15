using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The installed pen's command line: version, help, usage errors and where --port and --tool go.</summary>
public sealed class CliDispatchTests(SmokeFixture fixture) : IClassFixture<SmokeFixture>
{
    private PenCli Cli => fixture.Cli;

    [Fact(DisplayName = "pen --version")]
    public void PrintsItsVersion()
    {
        var result = Cli.Run("--version");

        Assert.Matches(new Regex("^pen [0-9]", RegexOptions.Multiline), result.Stdout);
    }

    [Fact(DisplayName = "pen runs from fish")]
    public void RunsFromFish()
    {
        var fish = SmokeEnvironment.FindOnPath("fish");
        Assert.SkipWhen(fish == null, "fish not installed: skipped the fish check");

        var result = Cli.Wrap(fish, "-c", $"{ShellQuote.Quote(SmokeEnvironment.PenPath)} --version").Run();

        Assert.True(result.ExitCode == 0, result.Describe());
    }

    [Fact(DisplayName = "pen --help lists the subcommands")]
    public void HelpListsTheSubcommands()
    {
        var help = Cli.Run("--help").Stdout;

        Assert.Multiple(
            () => Assert.Contains("pen allow", help, StringComparison.Ordinal),
            () => Assert.Contains("pen upgrade", help, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "pen --port 0 is a usage error (exit 2), before any session")]
    public void PortZeroIsAUsageError() => AssertBadPort("--port", "0");

    [Fact(DisplayName = "--port with a subcommand is a usage error")]
    public void PortWithASubcommandIsAUsageError()
    {
        var result = Cli.Run("--port", "8765", "allow");

        Assert.True(result.IsUsageError("--port goes only with a command to run, not pen allow"), result.Describe());
    }

    [Fact(DisplayName = "pen --help documents --port")]
    public void HelpDocumentsPort() =>
        Assert.Contains("--port <n>", Cli.Run("--help").Stdout, StringComparison.Ordinal);

    [Fact(DisplayName = "pen run --help documents --port and ports")]
    public void RunHelpDocumentsPortAndPorts()
    {
        var help = Cli.Run("run", "--help").Stdout;

        Assert.Multiple(
            () => Assert.Contains("--port <n>", help, StringComparison.Ordinal),
            () => Assert.Contains("ports = [", help, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "pen --port 8765 --port=8766 starts a session")]
    public void PortsStartASession()
    {
        var result = Cli.Run("--port", "8765", "--port=8766", "run", "true");

        Assert.True(result.ExitCode == 0, result.Describe());
    }

    [Fact(DisplayName = "...and --port after the command is the command's")]
    public void PortAfterTheCommandIsTheCommands()
    {
        var result = Cli.Run("run", "sh", "-c", "echo \"[$*]\"", "_", "--port", "8765");

        Assert.Contains("[--port 8765]", result.Stdout, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "pen --help documents --tool")]
    public void HelpDocumentsTool() =>
        Assert.Contains("--tool <spec>", Cli.Run("--help").Stdout, StringComparison.Ordinal);

    [Fact(DisplayName = "pen --tool list works outside a session")]
    public void ToolListWorksOutsideASession()
    {
        var result = Cli.Run("--tool", "list");

        Assert.True(result.ExitCode == 0, result.Describe());
    }

    [Fact(DisplayName = "pen --tool nope is a usage error (exit 2), before any session")]
    public void UnknownToolIsAUsageError() => AssertBadTool("--tool", "nope");

    [Fact(DisplayName = "a shell-like tool version is refused by the recipe's regex in the AOT build")]
    public void ShellLikeToolVersionIsRefused()
    {
        var result = Cli.Run("--tool", "pen-test:$(touch /tmp/x)");

        Assert.True(result.IsUsageError("isn't a valid version of pen-test"), result.Describe());
    }

    [Fact(DisplayName = "pen --tool runs the session with the tool installed")]
    public void ToolIsInstalledInTheSession()
    {
        var result = Cli.Run("--tool", "pen-test", "run", "pen-test");

        Assert.True(result.CapturedStdout == "1", result.Describe());
    }

    private void AssertBadPort(params string[] args)
    {
        var result = Cli.Run(args);

        Assert.True(result.ExitCode == 2 && result.HasLineStartingWith("pen: --port: "), result.Describe());
    }

    private void AssertBadTool(params string[] args)
    {
        var result = Cli.Run(args);

        Assert.True(result.ExitCode == 2 && result.HasLineStartingWith("pen: --tool"), result.Describe());
    }
}
