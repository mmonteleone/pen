using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>The installed pen's command line: version, help, usage errors, pen init, pen tools, pen ls and where
/// --port and --tool go.</summary>
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

    [Fact(DisplayName = "pen --version runs from a removed directory")]
    public void VersionRunsFromARemovedDirectory()
    {
        var result = RunFromRemovedDirectory("--version");

        Assert.True(
            result.ExitCode == 0 && Regex.IsMatch(result.Stdout, "^pen [0-9]", RegexOptions.Multiline),
            result.Describe());
    }

    [Fact(DisplayName = "...and pen init there says it can't read the current directory (exit 1)")]
    public void InitFromARemovedDirectorySaysWhy()
    {
        var result = RunFromRemovedDirectory("init");

        Assert.True(
            result.IsRefusal("pen: pen can't read the current directory: it was removed, or its permissions or a "
                + "parent's were changed. Fix that (chmod u+rwx . if it's only this directory's) or cd to the "
                + "project, then rerun\n") && result.ExitCode == 1,
            result.Describe());
    }

    /// <summary>Runs pen with <paramref name="args"/> from a directory that's removed before it starts.</summary>
    private CommandResult RunFromRemovedDirectory(params string[] args)
    {
        var dir = Path.Combine(fixture.Scratch, "removed-" + Guid.NewGuid().ToString("N"));
        const string script = "mkdir \"$1\" && cd \"$1\" && rmdir \"$1\" && shift && exec \"$@\"";
        return Cli.Wrap("/bin/sh", ["-c", script, "sh", dir, SmokeEnvironment.PenPath, .. args]).Run();
    }

    [Fact(DisplayName = "pen --help lists the subcommands")]
    public void HelpListsTheSubcommands()
    {
        var help = Cli.Run("--help").Stdout;

        Assert.Multiple(
            () => Assert.Contains("Usage:\n  pen ", help, StringComparison.Ordinal),
            () => Assert.Contains("\n  allow <domain>", help, StringComparison.Ordinal),
            () => Assert.Contains("\n  deny <domain>", help, StringComparison.Ordinal),
            () => Assert.Contains("\n  upgrade ", help, StringComparison.Ordinal),
            () => Assert.Contains("\n  tools ", help, StringComparison.Ordinal),
            () => Assert.Contains("\n  ls, ps ", help, StringComparison.Ordinal));
    }

    [Fact(DisplayName = "pen deny --help describes it, and pen deny '*' is a usage error")]
    public void DenyHasHelp_AndRefusesTheStar()
    {
        var help = Cli.Run("deny", "--help");
        var star = Cli.Run("deny", "*");

        Assert.Multiple(
            () => Assert.True(
                help.ExitCode == 0 && help.Stdout.Contains("Usage:\n  pen deny ", StringComparison.Ordinal),
                help.Describe()),
            () => Assert.True(star.IsUsageError("pen deny: \"*\" would block everything"), star.Describe()));
    }

    [Fact(DisplayName = "pen init writes .pen/config.toml with the go tool and .env hidden, and says so")]
    public void InitWritesTheDetectedSettings()
    {
        var project = fixture.CreateProject();
        File.WriteAllText(Path.Combine(project.Dir, "go.mod"), "module example.com/smoke\n");
        File.WriteAllText(Path.Combine(project.Dir, ".env"), "SECRET=smoke\n");
        var cli = Cli.WithWorkingDirectory(project.Dir);

        var first = cli.Run("init");
        var second = cli.Run("init");

        var path = Path.Combine(project.Dir, ".pen", "config.toml");
        var active = File.Exists(path) ? ReadActiveLines(path) : [];
        Assert.Multiple(
            () => Assert.True(
                first.ExitCode == 0 && first.HasLineStartingWith("pen: created .pen/config.toml")
                    && first.HasLineStartingWith("  tools: go (go.mod)")
                    && first.HasLineStartingWith("  hide: .env (found .env)"),
                first.Describe()),
            () => Assert.Equal(["tools = [\"go\"]", "hide = [", "\".env\",", "]"], active),
            () => Assert.True(
                second.ExitCode == 1 && second.Output.Contains(
                    ".pen/config.toml already exists: edit it", StringComparison.Ordinal),
                second.Describe()));
    }

    [Fact(DisplayName = "pen init --help describes it")]
    public void InitHasHelp()
    {
        var help = Cli.Run("init", "--help");

        Assert.True(
            help.ExitCode == 0 && help.Stdout.Contains("Usage:\n  pen init ", StringComparison.Ordinal),
            help.Describe());
    }

    [Fact(DisplayName = "pen --port 0 is a usage error (exit 2), before any session")]
    public void PortZeroIsAUsageError() => AssertBadPort("--port", "0");

    [Fact(DisplayName = "pen -p 0 claude is a usage error (exit 2), before any session")]
    public void ShortPortZeroIsAUsageError() => AssertBadPort("-p", "0", "claude");

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

    [Fact(DisplayName = "pen --port=8765 -p 8766 starts a session")]
    public void PortsStartASession()
    {
        var result = Cli.Run("--port=8765", "-p", "8766", "run", "true");

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

    [Fact(DisplayName = "pen tools lists the agents outside a session")]
    public void ToolsWorksOutsideASession()
    {
        var result = Cli.Run("tools");

        Assert.True(
            result.ExitCode == 0 && result.Stdout.Contains("claude", StringComparison.Ordinal), result.Describe());
    }

    [Fact(DisplayName = "pen ls lists the running sessions, as pen ps does")]
    public void LsIsPs()
    {
        var ls = Cli.Run("ls");
        var ps = Cli.Run("ps");

        Assert.True(ls.ExitCode == 0 && ps.ExitCode == 0, ls.Describe() + ps.Describe());
        Assert.Equal(ps.Stderr, ls.Stderr);
        // Another project's session may start or age between the two calls, so the age column is left out: with
        // no sessions the table is absent and both are empty.
        Assert.Equal(WithoutAge(ps.Stdout), WithoutAge(ls.Stdout));
    }

    /// <summary>Drops the AGE column from pen ps's table, whose lines pad each cell to the column's width.</summary>
    private static string[] WithoutAge(string table)
    {
        var lines = table.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return [];
        var start = lines[0].IndexOf("AGE", StringComparison.Ordinal);
        var end = lines[0].IndexOf("COMMAND", StringComparison.Ordinal);
        return [.. lines.Select(line => line.Length > end ? line[..start] + line[end..] : line)];
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

    /// <summary>Reads a config file's lines that aren't blank or comments, trimmed and without trailing
    /// comments.</summary>
    private static List<string> ReadActiveLines(string path) =>
        File.ReadAllLines(path)
            .Select(line => line.Split(" #", 2)[0].Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();

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
