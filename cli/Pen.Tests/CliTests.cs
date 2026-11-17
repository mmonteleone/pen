namespace Pen.Tests;

public class CliTests
{
    private static Command P(params string[] args) => CommandParser.Parse(args, () => null);

    /// <summary>Parses <paramref name="args"/>, which must ask for help or the version, and renders it.</summary>
    internal static string Render(params string[] args)
    {
        var c = P(args);
        Assert.Equal(CommandKind.Builtin, c.Kind);
        var output = new StringWriter();
        Assert.Equal(0, CommandTree.InvokeBuiltin(c.ParseResult!, output));
        return output.ToString();
    }

    /// <summary>Parses <paramref name="args"/>, which must be a usage error.</summary>
    internal static PenException Refused(params string[] args)
    {
        var e = Assert.Throws<PenException>(() => P(args));
        Assert.Equal(2, e.ExitCode);
        return e;
    }

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
    public void TrustTakesYesOrList()
    {
        Assert.Equal(CommandKind.Trust, P("trust").Kind);
        Assert.False(P("trust").Yes);
        Assert.True(P("trust", "--yes").Yes);
        Assert.True(P("trust", "-y").Yes);
        Assert.True(P("trust", "--list").List);
        Assert.StartsWith("pen trust: --yes and --list don't go together", Refused("trust", "--yes", "--list").Message);
        Assert.StartsWith("pen trust: unexpected argument \"-n\"", Refused("trust", "-n").Message);
        Assert.Equal(CommandKind.Run, P("run", "trust").Kind);
        Assert.Contains("trust", Render("--help"));
        Assert.Contains("-y, --yes", Render("trust", "--help"));
    }

    [Fact]
    public void NoArgumentsIsAShell()
    {
        var c = P();
        Assert.Equal(CommandKind.Run, c.Kind);
        Assert.Empty(c.Args);
        Assert.Empty(P("--").Args);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex", "exec", "hi")]
    [InlineData("make", "-k")]
    [InlineData("bash", "-c", "echo run")]
    [InlineData("RUN", "x")]
    [InlineData("LS")]
    [InlineData("")]
    [InlineData("claude", "allow", "x.com")]
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
    public void ACommandNamedLikeAReservedSubcommandNeedsRun()
    {
        Assert.Equal(CommandKind.Tools, P("tools").Kind);
        Assert.Equal(CommandKind.Ps, P("ls").Kind);
        Assert.Equal(["tools"], P("run", "tools").Args);
        Assert.Equal(["ls", "-la"], P("run", "ls", "-la").Args);
        Assert.Equal(CommandKind.Run, P("run", "ls").Kind);
        Assert.Equal(["ps"], P("run", "ps").Args);
    }

    [Fact]
    public void RunAcceptsADoubleDash()
    {
        Assert.Equal(["-x"], P("run", "--", "-x").Args);
        Assert.Equal(["ls"], P("--", "ls").Args);
        var dashed = P("run", "--", "--docker");
        Assert.Equal(["--docker"], dashed.Args);
        Assert.False(dashed.Docker);
        Assert.Equal(["--docker"], P("--", "--docker").Args);
        Assert.Equal(["a", "--", "b"], P("run", "--", "a", "--", "b").Args);
        Assert.Equal(["a", "--", "b"], P("--", "a", "--", "b").Args);
        Assert.Equal(["run", "x"], P("run", "--", "run", "x").Args);
    }

    [Fact]
    public void AfterRunAnyOtherDashArgumentIsTheCommand()
    {
        Assert.Equal(["-x"], P("run", "-x").Args);
        Assert.Equal(["--version"], P("run", "--version").Args);
        Assert.Equal(["--dockr", "x"], P("run", "--dockr", "x").Args);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "--")]
    [InlineData("run", "--docker")]
    [InlineData("run", "-d", "-p", "80")]
    public void RunNeedsACommand(params string[] args) =>
        Assert.Equal("pen run: missing the command to run\nRun 'pen run --help' for usage.", Refused(args).Message);

    [Theory]
    [InlineData("Allow", "allow")]
    [InlineData("Deny", "deny")]
    [InlineData("Sign", "sign")]
    [InlineData("Sign", "sign", "origin/main")]
    [InlineData("Clean", "clean")]
    [InlineData("Ps", "ps")]
    [InlineData("Ps", "ls")]
    [InlineData("Tools", "tools")]
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
        Assert.True(P("allow", "-g", "a.io").Global);
    }

    [Fact]
    public void DenyTakesDomainsAndGlobal_Lowercased_WithoutDuplicates()
    {
        var c = P("deny", "Gist.GitHub.com", "--global", ".foo.io", "gist.github.com");
        Assert.Equal(CommandKind.Deny, c.Kind);
        Assert.True(c.Global);
        Assert.Equal(["gist.github.com", ".foo.io"], c.Args);
        Assert.False(P("deny", "a.io").Global);
        Assert.True(P("deny", "-g", "a.io").Global);
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
        var e = Refused(args);
        Assert.StartsWith("pen deny", e.Message);
        Assert.EndsWith("\nRun 'pen deny --help' for usage.", e.Message);
    }

    [Fact]
    public void GlobalNeedsDomains_AndDefaultsTakesNothingElse()
    {
        Assert.StartsWith("pen allow --global: missing the domains to allow", Refused("allow", "--global").Message);
        Assert.StartsWith("pen deny --global: missing the domains to deny", Refused("deny", "-g").Message);
        Assert.StartsWith("pen allow --defaults takes nothing else", Refused("allow", "--defaults", "--global").Message);
        Assert.StartsWith("pen allow --defaults takes nothing else",
            Refused("allow", "--defaults", "--defaults").Message);
        Assert.StartsWith("pen allow: unknown option \"--globl\"", Refused("allow", "--globl", "a.io").Message);
        Assert.StartsWith("pen allow: unknown option \"--docker\"", Refused("allow", "a.io", "--docker").Message);
        Assert.StartsWith("pen allow: unknown option \"--global\"", Refused("allow", "--", "--global").Message);
    }

    [Theory]
    [InlineData("pen tools", "tools", "tools", "x")]
    [InlineData("pen ls", "ps", "ls", "x")]
    [InlineData("pen ps", "ps", "ps", "-a")]
    [InlineData("pen ps", "ps", "ps", "all")]
    [InlineData("pen init", "init", "init", "--force")]
    [InlineData("pen upgrade", "upgrade", "upgrade", "--clean")]
    [InlineData("pen upgrade", "upgrade", "upgrade", "--no-cache", "--cleen")]
    [InlineData("pen clean", "clean", "clean", "now")]
    [InlineData("pen clean", "clean", "clean", "--stale", "all")]
    [InlineData("pen sign", "sign", "sign", "a", "b")]
    [InlineData("pen trust", "trust", "trust", "--yes", "--list", "x")]
    public void AnUnexpectedArgumentIsNamed_WithTheSubcommandsHelp(string name, string help, params string[] args) =>
        Assert.Equal($"{name}: unexpected argument \"{args[^1]}\"\nRun 'pen {help} --help' for usage.",
            Refused(args).Message);

    [Theory]
    [InlineData("pen sign: unknown option \"--force\"", "sign", "--force")]
    [InlineData("pen sign: unknown option \"-x\"", "sign", "--", "-x")]
    public void SignsBaseCantLookLikeAnOption(string message, params string[] args) =>
        Assert.StartsWith(message, Refused(args).Message);

    [Theory]
    [InlineData("--docker", "--docker=false", "claude")]
    [InlineData("--docker", "--docker=", "claude")]
    [InlineData("--docker", "-d:x", "claude")]
    [InlineData("--docker", "run", "--docker=1", "claude")]
    [InlineData("--global", "allow", "--global=x", "a.com")]
    [InlineData("--global", "allow", "-g:x", "a.com")]
    [InlineData("--global", "deny", "-g=x", "a.com")]
    [InlineData("--defaults", "allow", "--defaults=1")]
    [InlineData("--yes", "trust", "--yes=no")]
    [InlineData("--list", "trust", "--list:all")]
    [InlineData("--no-cache", "upgrade", "--no-cache=1")]
    [InlineData("--stale", "clean", "--stale=1")]
    public void AnOnOrOffOptionTakesNoValue(string name, params string[] args) =>
        Assert.StartsWith($"{name} takes no value\n", Refused(args).Message);

    [Theory]
    [InlineData("--help", "--help=1")]
    [InlineData("--help", "-h:x")]
    [InlineData("--help", "-h:-x")]
    [InlineData("--help", "--help:")]
    [InlineData("--help", "allow", "--help=1")]
    [InlineData("--help", "--docker", "-h=")]
    [InlineData("--help", "allow", "bad", "--help:x")]
    [InlineData("--version", "--version:")]
    [InlineData("--version", "--version=1")]
    [InlineData("--version", "-d", "--version:x")]
    public void HelpAndVersionTakeNoValue(string name, params string[] args) =>
        Assert.StartsWith($"{name} takes no value\n", Refused(args).Message);

    [Fact]
    public void HelpAndVersionValuesAfterTheCommandBoundaryAreTheCommands()
    {
        Assert.Equal(["claude", "--help=1"], P("claude", "--help=1").Args);
        Assert.Equal(["x", "-h:y"], P("run", "x", "-h:y").Args);
        Assert.Equal(["x", "--version:"], P("x", "--version:").Args);
        Assert.Equal(["x", "-h:y"], P("--docker", "run", "x", "--", "-h:y").Args.Where(a => a != "--").ToList());
    }

    [Fact]
    public void AnOnOrOffOptionDoesntTakeTheNextArgument()
    {
        var c = P("-d", "false", "claude");
        Assert.True(c.Docker);
        Assert.Equal(["false", "claude"], c.Args);
        Assert.Equal(["true"], P("--docker", "true").Args);
        Assert.StartsWith("pen allow: \"true\" isn't a domain", Refused("allow", "-g", "true").Message);
    }

    [Theory]
    [InlineData("run", "run", "--help")]
    [InlineData("run", "run", "-h")]
    [InlineData("upgrade", "upgrade", "--help")]
    [InlineData("upgrade", "upgrade", "--no-cache", "-h")]
    [InlineData("allow", "allow", "--help")]
    [InlineData("allow", "allow", "example.com", "--help")]
    [InlineData("allow", "allow", "--defaults", "--help")]
    [InlineData("allow", "allow", "bad", "--help")]
    [InlineData("deny", "deny", "--help")]
    [InlineData("deny", "deny", "a.io", "-h")]
    [InlineData("sign", "sign", "--help")]
    [InlineData("clean", "clean", "-h")]
    [InlineData("ps", "ps", "--help")]
    [InlineData("ps", "ls", "--help")]
    [InlineData("init", "init", "-h")]
    [InlineData("clean", "clean", "--stale", "--help")]
    [InlineData("trust", "trust", "--yes", "--list", "--help")]
    [InlineData("tools", "tools", "-h")]
    [InlineData("run", "-d", "run", "--help")]
    [InlineData("run", "run", "-d", "-p", "80", "-h")]
    public void SubcommandHelp(string topic, params string[] args) =>
        Assert.Contains($"Usage:\n  pen {topic} ", Render(args));

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("--help", "claude")]
    [InlineData("--docker", "--help")]
    [InlineData("--port", "0", "--help")]
    [InlineData("-d", "--help")]
    [InlineData("--help", "--version")]
    [InlineData("-h", "--version")]
    [InlineData("--version", "--help")]
    [InlineData("--version", "-h")]
    [InlineData("--help", "--", "-x")]
    public void HelpBeforeTheCommandIsPens_EvenWithABadValueOrVersion(params string[] args) =>
        Assert.Contains("Usage:\n  pen [command] [options]", Render(args));

    [Theory]
    [InlineData("--dockr", "--dockr", "--help")]
    [InlineData("--dockr", "--dockr", "claude", "--help")]
    [InlineData("-x/pen", "-x/pen", "claude", "--help")]
    [InlineData("--dockr", "-d", "--dockr", "-h")]
    [InlineData("--dockr", "--dockr", "allow", "--help")]
    [InlineData("--dockr", "--dockr", "--version", "--help")]
    [InlineData("-", "-", "--help")]
    public void AnUnknownOptionBeforeTheCommandIsRefused_EvenWithHelp(string opt, params string[] args) =>
        Assert.Equal($"unknown option \"{opt}\"\nRun 'pen --help' for usage.", Refused(args).Message);

    [Fact]
    public void TheCommandToRunTakesUpToTheParsersMostArguments()
    {
        var most = Enumerable.Repeat("x", 100_000).ToArray();
        Assert.Equal(most, P(most).Args);
        Assert.Equal(most, P(["run", .. most]).Args);
        Assert.Equal("too many arguments (at most 100,000)\nRun 'pen --help' for usage.",
            Refused([.. most, "x"]).Message);
        Assert.Equal("pen run: too many arguments (at most 100,000)\nRun 'pen run --help' for usage.",
            Refused(["run", .. most, "x"]).Message);
        Assert.StartsWith("too many arguments", Refused(["-d", .. most, "x", "y"]).Message);
    }

    [Theory]
    [InlineData(null, 80)]
    [InlineData(0, 80)]
    [InlineData(1, 80)]
    [InlineData(5, 80)]
    [InlineData(39, 80)]
    [InlineData(40, 40)]
    [InlineData(120, 120)]
    public void HelpIsWrappedAtTheTerminalsWidth_UnlessItsNarrowerThanForty(int? terminal, int width) =>
        Assert.Equal(width, CommandTree.ChooseHelpWidth(terminal));

    [Fact]
    public void HelpAfterTheCommandBelongsToTheCommand()
    {
        Assert.Equal(["claude", "--help"], P("claude", "--help").Args);
        Assert.Equal(["claude", "--help"], P("run", "claude", "--help").Args);
        Assert.Equal(["--help"], P("run", "--", "--help").Args);
        Assert.Equal(CommandKind.Run, P("run", "claude", "-h").Kind);
        Assert.Equal(["claude", "-h"], P("-d", "claude", "-h").Args);
    }

    [Fact]
    public void OnlyDashHAndDashDashHelpAskForHelp()
    {
        foreach (var arg in new[] { "-?", "-H", "--Help", "-help" })
            Assert.StartsWith($"unknown option \"{arg}\"", Refused(arg).Message);
        Assert.Equal(["/?"], P("/?").Args);
        Assert.Equal(["/h"], P("/h").Args);
    }

    [Fact]
    public void TheHelpListsEverySubcommand_WithItsDescription_AndEachHasItsOwn()
    {
        var root = Render("--help");
        var subcommands = new Dictionary<string, string>
        {
            ["run"] = "Ctrl-C goes to the command",
            ["tools"] = "each with its default version",
            ["upgrade"] = "Logins and config are kept",
            ["allow"] = "(last 7 days) that still aren't allowed",
            ["deny"] = "Deny wins over every allow",
            ["init"] = "It never overwrites",
            ["trust"] = ".pen/ can be written by an agent",
            ["ps"] = "Lists the running sessions of every project",
            ["sign"] = "Re-creates the commits in <base>..HEAD",
            ["clean"] = "projects still in use are listed and left alone",
        };
        foreach (var (name, prose) in subcommands)
        {
            Assert.Matches($"\n  (ls, )?{name}( <[a-z]+>)? +[a-z]", root);
            var help = Render(name, "--help");
            Assert.Contains($"Usage:\n  pen {name} ", help);
            Assert.Contains(prose, help);
        }
        Assert.Contains("ls, ps", root);
        Assert.Contains("pen <subcommand> --help", root);
        Assert.Contains("-d, --docker", root);
        Assert.Contains("-p, --port <n>", root);
        Assert.Contains("-t, --tool <spec>", root);
        Assert.Contains("pen claude --help is Claude's help", root);
        Assert.Contains("pen allow --defaults lists the domains", root);
        Assert.Contains("The options go before the command", Render("run", "--help"));
        Assert.Contains("so the two don't go\ntogether", Render("trust", "--help"));
        Assert.DoesNotContain("<cmd>", root.Split("Commands:")[0]);
        Assert.Contains("ports = [<n>, …]", Render("run", "--help"));
        Assert.Contains("-g, --global", Render("allow", "--help"));
        Assert.Contains("-g, --global", Render("deny", "--help"));
    }

    [Fact]
    public void RenderedHelpFitsEightyColumns()
    {
        string[] names = ["run", "tools", "upgrade", "allow", "deny", "init", "trust", "ps", "sign", "clean"];
        foreach (var args in names.Select(name => new[] { name, "--help" }).Append(["--help"]))
        {
            foreach (var line in Render(args).Split('\n'))
                Assert.True(line.TrimEnd().Length <= 80, line);
        }
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
    [InlineData("ls", "-a")]
    [InlineData("init", "x")]
    [InlineData("tools", "x")]
    [InlineData("tools", "--all")]
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
        Assert.True(P("run", "--docker", "claude").Docker);
        Assert.Equal(["claude"], P("run", "--docker", "claude").Args);
        Assert.Equal(["claude", "--docker"], P("claude", "--docker").Args);
        Assert.True(P("-d", "claude").Docker);

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
        Assert.True(P("--docker", "--docker", "true").Docker);
        Assert.Equal(["ls"], P("--docker", "--", "ls").Args);
        Assert.Equal(["allow", "x.com"], P("-d", "--", "allow", "x.com").Args);
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
        Assert.Equal([80], P("--docker", "--port", "80", "--docker", "true").Ports);
        Assert.Equal([80], P("run", "--port", "80", "x").Ports);
        Assert.Equal([3000], P("-p", "3000", "claude").Ports);
        var next = P("-p", "80", "81", "claude"); // one value per --port
        Assert.Equal([80], next.Ports);
        Assert.Equal(["81", "claude"], next.Args);
    }

    [Fact]
    public void RunOptionsGoBeforeTheCommandAndAfterRun_AndMerge()
    {
        var c = P("-d", "run", "-p", "80", "claude");
        Assert.Equal(CommandKind.Run, c.Kind);
        Assert.True(c.Docker);
        Assert.Equal([80], c.Ports);
        Assert.Equal(["claude"], c.Args);

        var merged = P("-p", "80", "-t", "pen-test", "run", "-p", "81", "-p", "80", "--tool=pen-test:1", "--docker", "x");
        Assert.Equal([80, 81], merged.Ports);
        Assert.Equal([new ToolSpec("pen-test", "1")], merged.Tools);
        Assert.True(merged.Docker);
        Assert.Equal(["x"], merged.Args);

        var dashed = P("run", "-d", "-p", "80", "-t", "pen-test", "--", "--docker");
        Assert.Equal(["--docker"], dashed.Args);
        Assert.True(dashed.Docker);
        Assert.Equal(["allow", "x.com"], P("run", "-d", "allow", "x.com").Args);
        Assert.StartsWith("--tool: pen-test:1 and pen-test:2 can't be used together",
            Refused("-t", "pen-test", "run", "-t", "pen-test:2", "claude").Message);
        Assert.Equal("--port: \"0\" isn't a port number (1 to 65535)\nRun 'pen run --help' for usage.",
            Refused("run", "-p", "0", "claude").Message);
    }

    [Theory]
    [InlineData("--port", "80")]
    [InlineData("--port=80")]
    [InlineData("--port:80")]
    [InlineData("-p", "80")]
    [InlineData("-p=80")]
    [InlineData("-p:80")]
    [InlineData("--tool", "pen-test")]
    [InlineData("--tool=pen-test")]
    [InlineData("--tool:pen-test")]
    [InlineData("-t", "pen-test")]
    [InlineData("-t=pen-test")]
    [InlineData("-t:pen-test")]
    [InlineData("--docker")]
    [InlineData("-d")]
    public void EveryFormOfARunOptionLeavesTheCommandsArgumentsAlone(params string[] option)
    {
        string[] command = ["claude", "--help", "-p", "1", "--tool=x", "-d", "--", "-h", "--version"];
        string[][] lines = [[.. option, .. command], ["run", .. option, .. command], [.. option, "run", .. command]];
        foreach (var args in lines)
        {
            var c = P(args);
            Assert.Equal(CommandKind.Run, c.Kind);
            Assert.Equal(command, c.Args);
            Assert.True(c.Docker || c.Ports.SequenceEqual([80]) || c.Tools.SequenceEqual([new ToolSpec("pen-test", "1")]));
        }
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
    [InlineData("--port", "--", "claude")]
    [InlineData("--port", "--help")]
    [InlineData("-p", "0", "claude")]
    [InlineData("-p", "0", "run", "claude")]
    [InlineData("-p", "0", "allow", "x.com")]
    [InlineData("--port", "0", "allow", "x.com")]
    [InlineData("--port", "x", "tools")]
    [InlineData("-p", "80", "-p")]
    [InlineData("-p", "80", "-p", "--", "x")]
    [InlineData("run", "-p")]
    [InlineData("run", "-p", "x", "claude")]
    public void BadPortsAreUsageErrors(params string[] args)
    {
        var e = Assert.Throws<PenException>(() => P(args));
        Assert.Equal(2, e.ExitCode);
        Assert.StartsWith("--port: ", e.Message);
    }

    [Fact]
    public void AMissingValueIsSaidSo()
    {
        Assert.StartsWith("--port: missing the port number\n", Refused("--port").Message);
        Assert.StartsWith("--port: missing the port number\n", Refused("-p", "80", "-p", "--", "x").Message);
        Assert.StartsWith("--tool: missing the tool (name or name:version)\n", Refused("--tool=").Message);
        Assert.StartsWith("--port: missing the port number\n", Refused("--port=", "80", "claude").Message);
        Assert.StartsWith("--port: missing the port number\n", Refused("-p:", "80", "claude").Message);
        Assert.StartsWith("--tool: missing the tool (name or name:version)\n",
            Refused("--tool=", "pen-test", "claude").Message);
        Assert.StartsWith("--tool: missing the tool (name or name:version)\nRun 'pen run --help'",
            Refused("run", "-t=", "pen-test", "claude").Message);
        Assert.StartsWith("--tool: missing the tool (name or name:version)\n", Refused("run", "-t").Message);
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
    [InlineData("tools")]
    [InlineData("ls")]
    [InlineData("trust", "--yes")]
    public void PortWithAnythingButRunningIsAnError(params string[] what)
    {
        Assert.Equal($"--port goes only with a command to run, not pen {what[0]}\nRun 'pen --help' for usage.",
            Refused(["--port", "3000", .. what]).Message);
        Assert.StartsWith("--docker goes only", Refused(["--docker", "--port", "3000", .. what]).Message);
        Assert.StartsWith("--tool goes only", Refused(["-t", "pen-test", "-d", .. what]).Message);
    }

    [Fact]
    public void ASubcommandsOwnErrorComesBeforeTheRunOptionsPlace() =>
        Assert.StartsWith("pen allow: \"bad\" isn't a domain", Refused("--port", "3000", "allow", "bad").Message);

    [Fact]
    public void ARunOptionWithHelpIsHelp_AndWithVersionAnError()
    {
        Assert.Contains("Usage:", Render("--port", "3000", "--help"));
        Assert.Equal("--version option cannot be combined with other arguments.\nRun 'pen --help' for usage.",
            Refused("--port", "3000", "--version").Message);
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
    [InlineData("--DOCKER")]
    [InlineData("-?")]
    [InlineData("-dp")]
    [InlineData("-p80")]
    [InlineData("-tgo")]
    [InlineData("-")]
    public void UnknownOptionsAreErrors(string opt) =>
        Assert.Equal($"unknown option \"{opt}\"\nRun 'pen --help' for usage.", Refused(opt, "claude").Message);

    [Theory]
    [InlineData("-dp", "-dp", "3000")]
    [InlineData("-dp", "-dp", "3000", "claude")]
    [InlineData("--dockr", "--dockr", "allow", "x.com")]
    [InlineData("--dockr", "-d", "--dockr", "run", "claude")]
    [InlineData("-x", "-d", "-x", "claude")]
    [InlineData("--dockr", "--dockr", "--", "claude")]
    [InlineData("-x/pen", "-x/pen", "claude")]
    public void AnUnknownOptionIsRefused_WhereverItIsBeforeTheCommand(string opt, params string[] args) =>
        Assert.StartsWith($"unknown option \"{opt}\"\n", Refused(args).Message);

    [Fact]
    public void HelpAndVersion()
    {
        Assert.Equal(CommandKind.Builtin, P("--help").Kind);
        Assert.Equal(CommandKind.Builtin, P("-h").Kind);
        Assert.Matches("^pen [0-9]+\\.[0-9]+\\.[0-9]+.*\n$", Render("--version"));
        Assert.StartsWith("--version option cannot be combined with other arguments.", Refused("--version", "x").Message);
        Assert.StartsWith("--version option cannot be combined", Refused("--version", "--docker").Message);
    }

    [Fact]
    public void ResponseFilesAndDirectivesArePassedLiterally()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Sub("cwd"), "args");
        File.WriteAllText(file, "--docker\nallow\n");
        var at = "@" + file;
        string[][] lines = [[at], ["claude", at], ["run", at], ["--", at], ["-d", at], ["[diagram]"],
            ["[diagram]", "claude"], ["claude", "[diagram]"], ["[suggest]"], ["-d", "[diagram]", "x"],
            ["run", "[diagram]"], ["[x]", "--help"], ["run", "-d", "[diagram]"]];
        foreach (var args in lines)
        {
            var c = P(args);
            Assert.Equal(CommandKind.Run, c.Kind);
            Assert.Equal(args.SkipWhile(a => a is "run" or "--" or "-d"), c.Args);
        }
        Assert.StartsWith($"pen allow: \"{at}\" isn't a domain", Refused("allow", at).Message);
    }

    [Fact]
    public void AFirstArgumentNamedLikePenIsntDropped()
    {
        var name = System.CommandLine.RootCommand.ExecutableName;
        Assert.Equal(["pen", "x"], P("pen", "x").Args);
        Assert.Equal(["pen", "pen", "x"], P("pen", "pen", "x").Args);
        Assert.Equal([name, "x"], P(name, "x").Args);
        Assert.Equal([name, name], P("-d", name, name).Args);
        Assert.Equal(["./" + name, "--help"], P("./" + name, "--help").Args);
        Assert.StartsWith($"unknown option \"-x/{name}\"", Refused("-x/" + name, "claude").Message);
    }

    [Fact]
    public void AnUnknownOptionAfterARunOptionIsAUsageError() =>
        Assert.Equal(2, Assert.Throws<PenException>(() => CommandParser.Parse(["--tool", "pen-test", "--nope"], () => null)).ExitCode);
}
