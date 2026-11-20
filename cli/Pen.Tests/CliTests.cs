namespace Pen.Tests;

public class CliTests
{
    private static CliInvocation P(params string[] args) => CommandParser.Parse(args, () => null, RecipeCatalog.Embedded);

    /// <summary>Parses <paramref name="args"/>, which must ask to run a command or a shell.</summary>
    private static RunInvocation R(params string[] args) => Assert.IsType<RunInvocation>(P(args));

    /// <summary>Parses <paramref name="args"/>, which must ask for help or the version, and renders it.</summary>
    internal static string Render(params string[] args)
    {
        var c = Assert.IsType<BuiltinInvocation>(P(args));
        var output = new StringWriter();
        Assert.Equal(0, CommandTree.InvokeBuiltin(c.ParseResult, output));
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
        Assert.Equal(new TrustInvocation(TrustMode.Review), P("trust"));
        Assert.Equal(new TrustInvocation(TrustMode.ApproveAll), P("trust", "--yes"));
        Assert.Equal(new TrustInvocation(TrustMode.ApproveAll), P("trust", "-y"));
        Assert.Equal(new TrustInvocation(TrustMode.List), P("trust", "--list"));
        Assert.StartsWith("pen trust: --yes and --list don't go together", Refused("trust", "--yes", "--list").Message);
        Assert.StartsWith("pen trust: unexpected argument \"-n\"", Refused("trust", "-n").Message);
        Assert.IsType<RunInvocation>(P("run", "trust"));
        Assert.Contains("trust", Render("--help"));
        Assert.Contains("-y, --yes", Render("trust", "--help"));
    }

    [Fact]
    public void NoArgumentsIsAShell()
    {
        var c = R();
        Assert.Empty(c.Command);
        Assert.Empty(R("--").Command);
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
        var c = R(args);
        Assert.Equal(args, c.Command);
    }

    [Fact]
    public void RunPassesTheRestThrough_EvenASubcommandName()
    {
        var c = R("run", "upgrade", "--clean");
        Assert.Equal(["upgrade", "--clean"], c.Command);
    }

    [Fact]
    public void RunCleanRunsCleanInTheSandbox()
    {
        var c = R("run", "clean");
        Assert.Equal(["clean"], c.Command);
    }

    [Fact]
    public void ACommandNamedLikeAReservedSubcommandNeedsRun()
    {
        Assert.IsType<ToolsInvocation>(P("tools"));
        Assert.IsType<PsInvocation>(P("ls"));
        Assert.Equal(["tools"], R("run", "tools").Command);
        Assert.Equal(["ls", "-la"], R("run", "ls", "-la").Command);
        Assert.IsType<RunInvocation>(P("run", "ls"));
        Assert.Equal(["ps"], R("run", "ps").Command);
    }

    [Fact]
    public void RunAcceptsADoubleDash()
    {
        Assert.Equal(["-x"], R("run", "--", "-x").Command);
        Assert.Equal(["ls"], R("--", "ls").Command);
        var dashed = R("run", "--", "--docker");
        Assert.Equal(["--docker"], dashed.Command);
        Assert.False(dashed.Docker);
        Assert.Equal(["--docker"], R("--", "--docker").Command);
        Assert.Equal(["a", "--", "b"], R("run", "--", "a", "--", "b").Command);
        Assert.Equal(["a", "--", "b"], R("--", "a", "--", "b").Command);
        Assert.Equal(["run", "x"], R("run", "--", "run", "x").Command);
    }

    [Fact]
    public void AfterRunAnyOtherDashArgumentIsTheCommand()
    {
        Assert.Equal(["-x"], R("run", "-x").Command);
        Assert.Equal(["--version"], R("run", "--version").Command);
        Assert.Equal(["--dockr", "x"], R("run", "--dockr", "x").Command);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "--")]
    [InlineData("run", "--docker")]
    [InlineData("run", "-d", "-p", "80")]
    public void RunNeedsACommand(params string[] args) =>
        Assert.Equal("pen run: missing the command to run\nRun 'pen run --help' for usage.", Refused(args).Message);

    [Theory]
    [InlineData(typeof(ListBlockedInvocation), "allow")]
    [InlineData(typeof(ListDeniedInvocation), "deny")]
    [InlineData(typeof(SignInvocation), "sign")]
    [InlineData(typeof(SignInvocation), "sign", "origin/main")]
    [InlineData(typeof(CleanInvocation), "clean")]
    [InlineData(typeof(PsInvocation), "ps")]
    [InlineData(typeof(PsInvocation), "ls")]
    [InlineData(typeof(ToolsInvocation), "tools")]
    [InlineData(typeof(InitInvocation), "init")]
    public void SubcommandsAreRecognized(Type kind, params string[] args)
    {
        var c = P(args);
        Assert.IsType(kind, c);
        if (c is SignInvocation sign)
            Assert.Equal(args.ElementAtOrDefault(1), sign.Base);
    }

    [Fact]
    public void AllowTakesDomainsAndGlobal_Lowercased_WithoutDuplicates()
    {
        var c = Assert.IsType<AllowDomainsInvocation>(P("allow", "Example.com", "--global", ".foo.io", "example.com"));
        Assert.True(c.Global);
        Assert.Equal(["example.com", ".foo.io"], c.Domains);
        Assert.False(Assert.IsType<AllowDomainsInvocation>(P("allow", "a.io")).Global);
        Assert.True(Assert.IsType<AllowDomainsInvocation>(P("allow", "-g", "a.io")).Global);
    }

    [Fact]
    public void DenyTakesDomainsAndGlobal_Lowercased_WithoutDuplicates()
    {
        var c = Assert.IsType<DenyDomainsInvocation>(P("deny", "Gist.GitHub.com", "--global", ".foo.io", "gist.github.com"));
        Assert.True(c.Global);
        Assert.Equal(["gist.github.com", ".foo.io"], c.Domains);
        Assert.False(Assert.IsType<DenyDomainsInvocation>(P("deny", "a.io")).Global);
        Assert.True(Assert.IsType<DenyDomainsInvocation>(P("deny", "-g", "a.io")).Global);
        Assert.IsType<ListDeniedInvocation>(P("deny"));
    }

    [Fact]
    public void AllowTakesTheStar_DenyRefusesIt()
    {
        Assert.Equal(["*", "a.io"], Assert.IsType<AllowDomainsInvocation>(P("allow", "*", "a.io")).Domains);
        Assert.True(Assert.IsType<AllowDomainsInvocation>(P("allow", "--global", "*")).Global);
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
        var e = Assert.Throws<PenException>(() => CommandParser.Parse([command, arg], () => dir, RecipeCatalog.Embedded));
        Assert.StartsWith($"pen {command}: \"{arg}\" isn't a domain", e.Message);
        Assert.Contains("; " + advice, e.Message);
    }

    [Fact]
    public void ANonDomainNamingNoEntryHere_GetsNoGlobAdvice()
    {
        using var t = new TempDir();
        var dir = t.Sub("cwd");
        File.WriteAllText(Path.Combine(dir, "LICENSE"), "");
        var e = Assert.Throws<PenException>(() => CommandParser.Parse(["allow", "other"], () => dir, RecipeCatalog.Embedded));
        Assert.DoesNotContain("expand", e.Message);
        e = Assert.Throws<PenException>(() => CommandParser.Parse(["allow", "license"], () => null, RecipeCatalog.Embedded));
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
        Assert.Equal(["claude", "--help=1"], R("claude", "--help=1").Command);
        Assert.Equal(["x", "-h:y"], R("run", "x", "-h:y").Command);
        Assert.Equal(["x", "--version:"], R("x", "--version:").Command);
        Assert.Equal(["x", "-h:y"], R("--docker", "run", "x", "--", "-h:y").Command.Where(a => a != "--").ToList());
    }

    [Fact]
    public void AnOnOrOffOptionDoesntTakeTheNextArgument()
    {
        var c = R("-d", "false", "claude");
        Assert.True(c.Docker);
        Assert.Equal(["false", "claude"], c.Command);
        Assert.Equal(["true"], R("--docker", "true").Command);
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
        Assert.Equal(most, R(most).Command);
        Assert.Equal(most, R(["run", .. most]).Command);
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
        Assert.Equal(["claude", "--help"], R("claude", "--help").Command);
        Assert.Equal(["claude", "--help"], R("run", "claude", "--help").Command);
        Assert.Equal(["--help"], R("run", "--", "--help").Command);
        Assert.IsType<RunInvocation>(P("run", "claude", "-h"));
        Assert.Equal(["claude", "-h"], R("-d", "claude", "-h").Command);
    }

    [Fact]
    public void OnlyDashHAndDashDashHelpAskForHelp()
    {
        foreach (var arg in new[] { "-?", "-H", "--Help", "-help" })
            Assert.StartsWith($"unknown option \"{arg}\"", Refused(arg).Message);
        Assert.Equal(["/?"], R("/?").Command);
        Assert.Equal(["/h"], R("/h").Command);
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
        Assert.IsType<ListDefaultDomainsInvocation>(P("allow", "--defaults"));
        Assert.IsType<ListBlockedInvocation>(P("allow"));
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
        Assert.Equal(new UpgradeInvocation(NoCache: false), P("upgrade"));
        Assert.Equal(new UpgradeInvocation(NoCache: true), P("upgrade", "--no-cache"));
        Assert.Throws<PenException>(() => P("upgrade", "--clean"));
        Assert.Throws<PenException>(() => P("upgrade", "--cleen"));
    }

    [Fact]
    public void Clean()
    {
        Assert.IsType<CleanInvocation>(P("clean"));
        Assert.IsType<CleanStaleInvocation>(P("clean", "--stale"));
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
        Assert.False(R("claude").Docker);
        Assert.True(R("run", "--docker", "claude").Docker);
        Assert.Equal(["claude"], R("run", "--docker", "claude").Command);
        Assert.Equal(["claude", "--docker"], R("claude", "--docker").Command);
        Assert.True(R("-d", "claude").Docker);

        var shell = R("--docker");
        Assert.True(shell.Docker);
        Assert.Empty(shell.Command);

        var c = R("--docker", "codex", "exec", "hi");
        Assert.True(c.Docker);
        Assert.Equal(["codex", "exec", "hi"], c.Command);

        var r = R("--docker", "run", "allow");
        Assert.True(r.Docker);
        Assert.Equal(["allow"], r.Command);
        Assert.True(R("--docker", "--docker", "true").Docker);
        Assert.Equal(["ls"], R("--docker", "--", "ls").Command);
        Assert.Equal(["allow", "x.com"], R("-d", "--", "allow", "x.com").Command);
    }

    [Fact]
    public void PortIsRepeatable_InOrder_WithoutDuplicates()
    {
        Assert.Empty(R("claude").Ports);
        Assert.Empty(R("--docker", "claude").Ports);

        var c = R("--port", "3000", "--port=5173", "--port", "3000", "claude", "--port", "9");
        Assert.Equal([3000, 5173], c.Ports);
        Assert.Equal(["claude", "--port", "9"], c.Command); // after the command, it's the command's
        Assert.False(c.Docker);

        var shell = R("--port", "1");
        Assert.Empty(shell.Command);
        Assert.Equal([1], shell.Ports);

        var r = R("--port", "65535", "--docker", "--port", "8080", "run", "--", "ps");
        Assert.True(r.Docker);
        Assert.Equal([65535, 8080], r.Ports);
        Assert.Equal(["ps"], r.Command);
        Assert.Equal([80], R("--docker", "--port", "80", "--docker", "true").Ports);
        Assert.Equal([80], R("run", "--port", "80", "x").Ports);
        Assert.Equal([3000], R("-p", "3000", "claude").Ports);
        var next = R("-p", "80", "81", "claude"); // one value per --port
        Assert.Equal([80], next.Ports);
        Assert.Equal(["81", "claude"], next.Command);
    }

    [Fact]
    public void RunOptionsGoBeforeTheCommandAndAfterRun_AndMerge()
    {
        var c = R("-d", "run", "-p", "80", "claude");
        Assert.True(c.Docker);
        Assert.Equal([80], c.Ports);
        Assert.Equal(["claude"], c.Command);

        var merged = R("-p", "80", "-t", "pen-test", "run", "-p", "81", "-p", "80", "--tool=pen-test:1", "--docker", "x");
        Assert.Equal([80, 81], merged.Ports);
        Assert.Equal([new ToolSpec("pen-test", "1")], merged.Tools);
        Assert.True(merged.Docker);
        Assert.Equal(["x"], merged.Command);

        var dashed = R("run", "-d", "-p", "80", "-t", "pen-test", "--", "--docker");
        Assert.Equal(["--docker"], dashed.Command);
        Assert.True(dashed.Docker);
        Assert.Equal(["allow", "x.com"], R("run", "-d", "allow", "x.com").Command);
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
            var c = R(args);
            Assert.Equal(command, c.Command);
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
        Assert.IsType<BuiltinInvocation>(P("--help"));
        Assert.IsType<BuiltinInvocation>(P("-h"));
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
            var c = R(args);
            Assert.Equal(args.SkipWhile(a => a is "run" or "--" or "-d"), c.Command);
        }
        Assert.StartsWith($"pen allow: \"{at}\" isn't a domain", Refused("allow", at).Message);
    }

    [Fact]
    public void AFirstArgumentNamedLikePenIsntDropped()
    {
        var name = System.CommandLine.RootCommand.ExecutableName;
        Assert.Equal(["pen", "x"], R("pen", "x").Command);
        Assert.Equal(["pen", "pen", "x"], R("pen", "pen", "x").Command);
        Assert.Equal([name, "x"], R(name, "x").Command);
        Assert.Equal([name, name], R("-d", name, name).Command);
        Assert.Equal(["./" + name, "--help"], R("./" + name, "--help").Command);
        Assert.StartsWith($"unknown option \"-x/{name}\"", Refused("-x/" + name, "claude").Message);
    }

    [Fact]
    public void AnUnknownOptionAfterARunOptionIsAUsageError() =>
        Assert.Equal(2, Assert.Throws<PenException>(() => CommandParser.Parse(["--tool", "pen-test", "--nope"], () => null, RecipeCatalog.Embedded)).ExitCode);
}
