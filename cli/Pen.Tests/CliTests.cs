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
    [InlineData("Allow", "allow", "example.com", "--global")]
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
    public void Upgrade()
    {
        Assert.False(P("upgrade").Clean);
        Assert.True(P("upgrade", "--clean").Clean);
        Assert.Throws<PenException>(() => P("upgrade", "--cleen"));
        Assert.Throws<PenException>(() => P("reset", "now"));
    }

    [Theory]
    [InlineData("--docker")]
    [InlineData("-v")]
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
