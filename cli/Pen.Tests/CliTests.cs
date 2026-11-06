namespace Pen.Tests;

public class CliTests
{
    private static Command P(params string[] args) => Cli.Parse(args);

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
    [InlineData("Sign", "sign")]
    [InlineData("Sign", "sign", "origin/main")]
    [InlineData("Reset", "reset")]
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

    [Theory]
    [InlineData("run", "run", "--help")]
    [InlineData("run", "run", "-h")]
    [InlineData("upgrade", "upgrade", "--help")]
    [InlineData("upgrade", "upgrade", "--clean", "-h")]
    [InlineData("allow", "allow", "--help")]
    [InlineData("allow", "allow", "example.com", "--help")]
    [InlineData("allow", "allow", "--defaults", "--help")]
    [InlineData("sign", "sign", "--help")]
    [InlineData("reset", "reset", "-h")]
    public void SubcommandHelp(string topic, params string[] args)
    {
        var c = P(args);
        Assert.Equal(CommandKind.Help, c.Kind);
        Assert.Equal([topic], c.Args);
        Assert.StartsWith("usage: pen", Cli.SubcommandHelp[topic]);
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
        foreach (var name in new[] { "run", "upgrade", "allow", "sign", "reset" })
            Assert.True(Cli.SubcommandHelp.ContainsKey(name), name);
        Assert.Contains("pen <subcommand> --help", Cli.Usage);
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
        Assert.False(P("upgrade").Clean);
        Assert.True(P("upgrade", "--clean").Clean);
        Assert.Throws<PenException>(() => P("upgrade", "--cleen"));
        Assert.Throws<PenException>(() => P("reset", "now"));
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

    [Theory]
    [InlineData("upgrade")]
    [InlineData("allow")]
    [InlineData("sign")]
    [InlineData("reset")]
    [InlineData("--help")]
    public void DockerWithAnythingButRunningIsAnError(string what)
    {
        var e = Assert.Throws<PenException>(() => P("--docker", what));
        Assert.Contains("--docker goes only with a command to run", e.Message);
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
}
