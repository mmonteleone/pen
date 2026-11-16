namespace Pen.Tests;

public class CliTests
{
    private static Command P(params string[] args) => CommandParser.Parse(args, () => null);

    [Fact]
    public void AnIOExceptionNothingHandledIsOneCleanLine_AndBugsStillThrow()
    {
        Assert.Equal(0, Program.RunGuarded(() => 0));
        Assert.Equal(1, Program.RunGuarded(() => throw new IOException("IO_SharingViolation_NoFileName")));
        Assert.Equal(1, Program.RunGuarded(() => throw new PathTooLongException("IO_PathTooLong")));
        Assert.Equal(3, Program.RunGuarded(() => throw new PenException("refused", 3)));
        Assert.Throws<InvalidOperationException>(() => Program.RunGuarded(() => throw new InvalidOperationException()));

        var message = Program.DescribeFileFailure(new PathTooLongException("IO_PathTooLong"));
        Assert.StartsWith("a file operation failed (PathTooLongException)", message);
        Assert.DoesNotContain("IO_PathTooLong", message);
    }

    [Fact]
    public void TrustTakesOnlyYes()
    {
        Assert.Equal(CommandKind.Trust, P("trust").Kind);
        Assert.False(P("trust").Yes);
        Assert.True(P("trust", "--yes").Yes);
        Assert.True(P("trust", "--list").List);
        Assert.Equal(2, Assert.Throws<PenException>(() => P("trust", "--yes", "--list")).ExitCode);
        Assert.Equal(CommandKind.Help, P("trust", "--help").Kind);
        Assert.Equal(2, Assert.Throws<PenException>(() => P("trust", "-y")).ExitCode);
        Assert.Equal(CommandKind.Run, P("run", "trust").Kind);
        Assert.Contains("pen trust", HelpText.Usage);
        Assert.Contains("--yes", HelpText.Subcommands["trust"]);
    }

    [Fact]
    public void NoArgumentsIsAShell()
    {
        var c = P();
        Assert.Equal(CommandKind.Run, c.Kind);
        Assert.Empty(c.Args);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex", "exec", "hi")]
    [InlineData("ls", "-la")]
    [InlineData("bash", "-c", "echo run")]
    public void AnythingElseIsTheCommandToRun(params string[] args)
    {
        var c = P(args);
        Assert.Equal(CommandKind.Run, c.Kind);
        Assert.Equal(args, c.Args);
    }

    [Fact]
    public void RunPassesTheRestThrough_EvenASubcommandName()
    {
        var c = P("run", "upgrade", "--clean");
        Assert.Equal(CommandKind.Run, c.Kind);
        Assert.Equal(["upgrade", "--clean"], c.Args);
    }

    [Fact]
    public void RunCleanRunsCleanInTheSandbox()
    {
        var c = P("run", "clean");
        Assert.Equal(CommandKind.Run, c.Kind);
        Assert.Equal(["clean"], c.Args);
    }

    [Fact]
    public void RunAcceptsADoubleDash()
    {
        Assert.Equal(["-x"], P("run", "--", "-x").Args);
        Assert.Equal(["ls"], P("--", "ls").Args);
    }

    [Fact]
    public void RunNeedsACommand()
    {
        var e = Assert.Throws<PenException>(() => P("run"));
        Assert.Equal(2, e.ExitCode);
    }

    [Theory]
    [InlineData("Allow", "allow")]
    [InlineData("Deny", "deny")]
    [InlineData("Sign", "sign")]
    [InlineData("Sign", "sign", "origin/main")]
    [InlineData("Clean", "clean")]
    [InlineData("Ps", "ps")]
    [InlineData("Init", "init")]
    public void SubcommandsAreRecognized(string kind, params string[] args)
    {
        var c = P(args);
        Assert.Equal(kind, c.Kind.ToString());
        Assert.Equal(args.Skip(1), c.Args);
    }

    [Fact]
    public void AllowTakesDomainsAndGlobal_Lowercased_WithoutDuplicates()
    {
        var c = P("allow", "Example.com", "--global", ".foo.io", "example.com");
        Assert.Equal(CommandKind.Allow, c.Kind);
        Assert.True(c.Global);
        Assert.Equal(["example.com", ".foo.io"], c.Args);
        Assert.False(P("allow", "a.io").Global);
    }

    [Fact]
    public void DenyTakesDomainsAndGlobal_Lowercased_WithoutDuplicates()
    {
        var c = P("deny", "Gist.GitHub.com", "--global", ".foo.io", "gist.github.com");
        Assert.Equal(CommandKind.Deny, c.Kind);
        Assert.True(c.Global);
        Assert.Equal(["gist.github.com", ".foo.io"], c.Args);
        Assert.False(P("deny", "a.io").Global);
        Assert.Empty(P("deny").Args);
    }

    [Fact]
    public void AllowTakesTheStar_DenyRefusesIt()
    {
        Assert.Equal(["*", "a.io"], P("allow", "*", "a.io").Args);
        Assert.True(P("allow", "--global", "*").Global);
        var e = Assert.Throws<PenException>(() => P("deny", "a.io", "*"));
        Assert.Equal(2, e.ExitCode);
        Assert.StartsWith("pen deny: \"*\" would block everything; remove the allow entries instead", e.Message);
    }

    [Theory]
    [InlineData("allow", "**")]
    [InlineData("allow", "*.example.com")]
    [InlineData("allow", ".")]
    [InlineData("deny", ".com")]
    [InlineData("deny", "*.example.com")]
    [InlineData("deny", "1.2.3.4")]
    public void OnlyTheExactStarIsntADomain(string command, string arg)
    {
        var e = Assert.Throws<PenException>(() => P(command, arg));
        Assert.StartsWith($"pen {command}: \"{arg}\" isn't a domain", e.Message);
    }

    [Theory]
    [InlineData("allow", "LICENSE", "license", "did your shell expand *? quote it: pen allow '*'")]
    [InlineData("deny", "Makefile", "makefile", "did your shell expand *? pen deny takes domains, not '*'")]
    public void ANonDomainNamingAnEntryHere_GetsTheGlobAdvice(string command, string entry, string arg, string advice)
    {
        using var t = new TempDir();
        var dir = t.Sub("cwd");
        File.WriteAllText(Path.Combine(dir, entry), "");
        var e = Assert.Throws<PenException>(() => CommandParser.Parse([command, arg], () => dir));
        Assert.StartsWith($"pen {command}: \"{arg}\" isn't a domain", e.Message);
        Assert.Contains("; " + advice, e.Message);
    }

    [Fact]
    public void ANonDomainNamingNoEntryHere_GetsNoGlobAdvice()
    {
        using var t = new TempDir();
        var dir = t.Sub("cwd");
        File.WriteAllText(Path.Combine(dir, "LICENSE"), "");
        var e = Assert.Throws<PenException>(() => CommandParser.Parse(["allow", "other"], () => dir));
        Assert.DoesNotContain("expand", e.Message);
        e = Assert.Throws<PenException>(() => CommandParser.Parse(["allow", "license"], () => null));
        Assert.DoesNotContain("expand", e.Message);
    }

    [Theory]
    [InlineData("deny", "--global")]
    [InlineData("deny", "--defaults")]
    [InlineData("deny", "--globl", "a.io")]
    public void BadDenyArgumentsAreUsageErrors_WordedForDeny(params string[] args)
    {
        var e = Assert.Throws<PenException>(() => P(args));
        Assert.Equal(2, e.ExitCode);
        Assert.StartsWith("pen deny", e.Message);
    }

    [Theory]
    [InlineData("run", "run", "--help")]
    [InlineData("run", "run", "-h")]
    [InlineData("upgrade", "upgrade", "--help")]
    [InlineData("upgrade", "upgrade", "--no-cache", "-h")]
    [InlineData("allow", "allow", "--help")]
    [InlineData("allow", "allow", "example.com", "--help")]
    [InlineData("allow", "allow", "--defaults", "--help")]
    [InlineData("deny", "deny", "--help")]
    [InlineData("deny", "deny", "a.io", "-h")]
    [InlineData("sign", "sign", "--help")]
    [InlineData("clean", "clean", "-h")]
    [InlineData("ps", "ps", "--help")]
    [InlineData("init", "init", "-h")]
    [InlineData("clean", "clean", "--stale", "--help")]
    public void SubcommandHelp(string topic, params string[] args)
    {
        var c = P(args);
        Assert.Equal(CommandKind.Help, c.Kind);
        Assert.Equal([topic], c.Args);
        Assert.StartsWith("usage: pen", HelpText.Subcommands[topic]);
    }

    [Fact]
    public void HelpAfterTheCommandBelongsToTheCommand()
    {
        Assert.Equal(["claude", "--help"], P("claude", "--help").Args);
        Assert.Equal(["claude", "--help"], P("run", "claude", "--help").Args);
        Assert.Equal(["--help"], P("run", "--", "--help").Args);
        Assert.Equal(CommandKind.Run, P("run", "claude", "-h").Kind);
        Assert.Empty(P("--help").Args); // the general usage
    }

    [Fact]
    public void EverySubcommandHasHelp_AndTheUsageMentionsIt()
    {
        foreach (var name in new[] { "run", "upgrade", "allow", "deny", "trust", "init", "sign", "clean", "ps" })
            Assert.True(HelpText.Subcommands.ContainsKey(name), name);
        Assert.Contains("pen <subcommand> --help", HelpText.Usage);
    }

    [Fact]
    public void AllowDefaults()
    {
        var c = P("allow", "--defaults");
        Assert.Equal(CommandKind.Allow, c.Kind);
        Assert.True(c.Defaults);
        Assert.Empty(c.Args);
        Assert.False(P("allow").Defaults);
    }

    [Theory]
    [InlineData("allow", "--defaults", "a.io")]
    [InlineData("allow", "--defaults", "--global")]
    [InlineData("allow", "http://example.com")]
    [InlineData("allow", "1.2.3.4")]
    [InlineData("allow", "localhost")]
    [InlineData("allow", "--globl", "a.io")]
    [InlineData("allow", "--global")]
    [InlineData("sign", "--force")]
    [InlineData("sign", "a", "b")]
    public void BadAllowAndSignArgumentsAreUsageErrors(params string[] args)
    {
        var e = Assert.Throws<PenException>(() => P(args));
        Assert.Equal(2, e.ExitCode);
    }

    [Fact]
    public void Upgrade()
    {
        Assert.False(P("upgrade").NoCache);
        Assert.True(P("upgrade", "--no-cache").NoCache);
        Assert.Throws<PenException>(() => P("upgrade", "--clean"));
        Assert.Throws<PenException>(() => P("upgrade", "--cleen"));
    }

    [Fact]
    public void Clean()
    {
        Assert.False(P("clean").Stale);
        var stale = P("clean", "--stale");
        Assert.Equal(CommandKind.Clean, stale.Kind);
        Assert.True(stale.Stale);
        Assert.Empty(stale.Args);
        Assert.Throws<PenException>(() => P("clean", "now"));
    }

    [Theory]
    [InlineData("ps", "-a")]
    [InlineData("ps", "all")]
    [InlineData("init", "x")]
    [InlineData("clean", "--yes")]
    [InlineData("clean", "--all")]
    [InlineData("clean", "--stale", "all")]
    public void PsAndCleanRejectOtherArguments(params string[] args)
    {
        var e = Assert.Throws<PenException>(() => P(args));
        Assert.Equal(2, e.ExitCode);
    }

    [Fact]
    public void DockerGoesWithACommandToRun()
    {
        Assert.False(P("claude").Docker);
        Assert.False(P("run", "--docker").Docker);
        Assert.Equal(["--docker"], P("run", "--docker").Args);
        Assert.Equal(["claude", "--docker"], P("claude", "--docker").Args);

        var shell = P("--docker");
        Assert.Equal(CommandKind.Run, shell.Kind);
        Assert.True(shell.Docker);
        Assert.Empty(shell.Args);

        var c = P("--docker", "codex", "exec", "hi");
        Assert.True(c.Docker);
        Assert.Equal(["codex", "exec", "hi"], c.Args);

        var r = P("--docker", "run", "allow");
        Assert.True(r.Docker);
        Assert.Equal(["allow"], r.Args);
        Assert.True(P("--docker", "--docker", "ls").Docker);
        Assert.Equal(["ls"], P("--docker", "--", "ls").Args);
    }

    [Fact]
    public void PortIsRepeatable_InOrder_WithoutDuplicates()
    {
        Assert.Empty(P("claude").Ports);
        Assert.Empty(P("--docker", "claude").Ports);

        var c = P("--port", "3000", "--port=5173", "--port", "3000", "claude", "--port", "9");
        Assert.Equal(CommandKind.Run, c.Kind);
        Assert.Equal([3000, 5173], c.Ports);
        Assert.Equal(["claude", "--port", "9"], c.Args); // after the command, it's the command's
        Assert.False(c.Docker);

        var shell = P("--port", "1");
        Assert.Equal(CommandKind.Run, shell.Kind);
        Assert.Empty(shell.Args);
        Assert.Equal([1], shell.Ports);

        var r = P("--port", "65535", "--docker", "--port", "8080", "run", "--", "ps");
        Assert.True(r.Docker);
        Assert.Equal([65535, 8080], r.Ports);
        Assert.Equal(["ps"], r.Args);
        Assert.Equal([80], P("--docker", "--port", "80", "--docker", "ls").Ports);
        Assert.Equal(["--port", "80"], P("run", "--port", "80").Args);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--port=")]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--port", "-1")]
    [InlineData("--port", "+80")]
    [InlineData("--port", " 80")]
    [InlineData("--port", "80x")]
    [InlineData("--port", "0x50")]
    [InlineData("--port", "99999999999")]
    [InlineData("--port", "claude")]
    [InlineData("--port=8o", "ls")]
    [InlineData("--docker", "--port", "ls")]
    public void BadPortsAreUsageErrors(params string[] args)
    {
        var e = Assert.Throws<PenException>(() => P(args));
        Assert.Equal(2, e.ExitCode);
        Assert.StartsWith("--port: ", e.Message);
    }

    [Theory]
    [InlineData("upgrade")]
    [InlineData("allow")]
    [InlineData("deny")]
    [InlineData("sign")]
    [InlineData("clean")]
    [InlineData("clean", "--stale")]
    [InlineData("ps")]
    [InlineData("init")]
    [InlineData("--help")]
    [InlineData("--version")]
    public void PortWithAnythingButRunningIsAnError(params string[] what)
    {
        var e = Assert.Throws<PenException>(() => P(["--port", "3000", .. what]));
        Assert.StartsWith("--port goes only with a command to run", e.Message);
        var d = Assert.Throws<PenException>(() => P(["--docker", "--port", "3000", .. what]));
        Assert.StartsWith("--docker goes only", d.Message);
    }

    [Fact]
    public void FlagPortsAppendToConfigPorts_WithoutDuplicates_FlagWinning()
    {
        Assert.Empty(Port.Merge([], []));
        Assert.Equal([new(3000, false), new(5173, false)], Port.Merge([3000, 5173], []));
        Assert.Equal([new(3000, false), new(5173, true), new(8080, true)], Port.Merge([3000, 5173], [8080, 5173]));
        Assert.Equal([new(9, true)], Port.Merge([], [9]));
    }

    [Theory]
    [InlineData("-v")]
    [InlineData("--dockr")]
    public void UnknownOptionsAreErrors(string opt)
    {
        var e = Assert.Throws<PenException>(() => P(opt, "claude"));
        Assert.Contains(opt, e.Message);
    }

    [Fact]
    public void HelpAndVersion()
    {
        Assert.Equal(CommandKind.Help, P("--help").Kind);
        Assert.Equal(CommandKind.Help, P("-h").Kind);
        Assert.Equal(CommandKind.Version, P("--version").Kind);
    }

    [Fact]
    public void AnUnknownOptionAfterARunOptionIsAUsageError() =>
        Assert.Equal(2, Assert.Throws<PenException>(() => CommandParser.Parse(["--tool", "pen-test", "--nope"], () => null)).ExitCode);
}
