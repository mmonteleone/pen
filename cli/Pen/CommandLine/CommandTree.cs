using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Pen.Platform;
using Pen.Proxy;
using Pen.Sessions;
using Pen.Tools;
using SclCommand = System.CommandLine.Command;

namespace Pen.CommandLine;

/// <summary>Pen's command line as a System.CommandLine tree: its subcommands, options, arguments and help.</summary>
/// <remarks>
/// Built for each parse, since the domain arguments' parsers close over the start directory (null when it can't be
/// read), for the advice on an argument named like a file there (<see cref="ShellGlob"/>). Values are checked as
/// they're parsed, with Pen's own messages. <see cref="CommandBoundary"/> reads its option and subcommand names from
/// here, so the two can't disagree.
/// </remarks>
internal sealed class CommandTree
{
    /// <summary>
    /// The width help is wrapped at when it doesn't go to a terminal, or to one narrower than
    /// <see cref="MinHelpWidth"/>.
    /// </summary>
    private const int DefaultHelpWidth = 80;

    /// <summary>The narrowest terminal whose width help is wrapped at.</summary>
    /// <remarks>
    /// System.CommandLine's wrapping fails or never finishes at a few columns, so a narrower terminal gets
    /// <see cref="DefaultHelpWidth"/>.
    /// </remarks>
    private const int MinHelpWidth = 40;

    /// <summary>The longer help each command has after the sections System.CommandLine writes.</summary>
    private readonly Dictionary<SclCommand, string> _details = [];

    public CommandTree(Func<string?> startDir, RecipeCatalog catalog)
    {
        Catalog = catalog;
        Root = new RootCommand(RootDescription) { HelpName = "pen" };
        // Never invoked: without an action, System.CommandLine refuses a command line with no subcommand.
        Root.SetAction(_ => 0);
        Root.Directives.Clear();
        _details[Root] = RootDetails;
        foreach (var option in Root.Options.Where(o => o is HelpOption or VersionOption).ToList())
            Root.Options.Remove(option);

        RootOptions = CreateRunOptions(
            catalog,
            "with Docker-in-Docker (or docker = true in .pen/config.toml)",
            "forward localhost:<n> to the sandbox (repeatable; or ports = [<n>, …] in .pen/config.toml)",
            "a tool or agent, as name or name:version (repeatable; or tools = […] in either config.toml); a " +
                "command named after an agent brings it: pen claude");
        RootCommandLine = CreateCommandLine("the command to run and its arguments");
        // The hidden argument would otherwise show in every subcommand's usage.
        RootCommandLine.Hidden = true;
        foreach (var option in RootOptions.All)
            Root.Options.Add(option);
        Root.Arguments.Add(RootCommandLine);

        Help = new HelpOption("--help", "-h") { Description = "show help and usage information" };
        // HelpOption always comes with its HelpAction.
        Help.Action = new DetailedHelpAction((HelpAction)Help.Action!, _details);
        Root.Options.Add(Help);
        Version = new VersionOption { Description = "show pen's version", Action = new VersionAction(Help) };
        Root.Options.Add(Version);

        Run = AddCommand("run", "run a command whose name matches a subcommand, e.g. pen run clean", RunDetails);
        RunOptions = CreateRunOptions(
            catalog,
            "give the session its own Docker daemon (one per project at a time); docker = true in " +
                ".pen/config.toml does this for every session",
            "forward port <n> on the host's 127.0.0.1 to port <n> in the sandbox (repeatable); ports = [<n>, …] " +
                "in .pen/config.toml does this for every session. A server in the sandbox must listen on " +
                "0.0.0.0, not only localhost",
            "a tool or agent, as name or name:version (repeatable); tools = […] in either config.toml adds to " +
                "these, and pen tools shows what's available. The session runs with them installed, and can " +
                "reach the domains they need. A command named after an agent (pen claude, pen run claude) " +
                "brings that agent at its default version unless --tool or tools in either config.toml already " +
                "names it; to have an agent in a shell or behind another command, name it here or in tools");
        RunCommandLine = CreateCommandLine("the command to run and its arguments, passed to it unchanged");
        foreach (var option in RunOptions.All)
            Run.Options.Add(option);
        Run.Arguments.Add(RunCommandLine);
        Run.Validators.Add(result =>
        {
            if (result.GetResult(RunCommandLine) is not { Tokens.Count: > 0 })
                result.AddError("pen run: missing the command to run");
        });

        Tools = AddCommand("tools", "list the agents and tools pen can install", ToolsDetails);

        Upgrade = AddCommand(
            "upgrade", "rebuild the base image, agents and tools; --no-cache skips the cache", UpgradeDetails);
        NoCache = AddFlag(Upgrade, "--no-cache", null, "rebuild without the build cache");

        Allow = AddCommand("allow",
            "allow domains, applied to running sessions too; pen allow '*' (quoted) allows any domain; " +
                "with none, list recently blocked domains", AllowDetails);
        AllowDomains = AddDomains(Allow,
            "a domain to allow; .example.com also allows its subdomains, and '*' (quoted) any domain",
            allowStar: true, startDir);
        AllowGlobal = AddFlag(Allow, "--global", "-g", "add them to ~/.config/pen/config.toml instead, for every " +
            "project, and apply them to all running sessions");
        AllowDefaults = AddFlag(Allow, "--defaults", null, "list the domains pen allows out of the box");
        Allow.Validators.Add(ValidateAllow);

        Deny = AddCommand("deny",
            "deny domains, over every allow, applied to running sessions too; with none, list the denied domains",
            DenyDetails);
        DenyDomains = AddDomains(Deny, "a domain to deny; .example.com also denies its subdomains",
            allowStar: false, startDir);
        DenyGlobal = AddFlag(Deny, "--global", "-g", "add them to ~/.config/pen/config.toml instead, for every " +
            "project, and apply them to all running sessions");
        Deny.Validators.Add(result => ValidateGlobal(result, "deny", DenyGlobal, DenyDomains));

        Init = AddCommand("init", "write a starting .pen/config.toml for this project", InitDetails);

        Trust = AddCommand("trust", "review and approve what this project's .pen/ asks for", TrustDetails);
        TrustYes = AddFlag(Trust, "--yes", "-y", "approve without asking, for scripts");
        TrustList = AddFlag(Trust, "--list", null, "list everything new, however long, and approve nothing");
        Trust.Validators.Add(result =>
        {
            if (result.GetResult(TrustYes) is not null && result.GetResult(TrustList) is not null)
                result.AddError("pen trust: --yes and --list don't go together");
        });

        Ps = AddCommand("ps", "list running sessions, in every project", PsDetails);
        Ps.Aliases.Add("ls");

        Sign = AddCommand("sign", "on the host: re-sign unpushed commits with your key", SignDetails);
        SignBase = new Argument<string?>("base")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = "the commits after it, up to HEAD, are re-signed",
        };
        Sign.Arguments.Add(SignBase);
        Sign.Validators.Add(result =>
        {
            if (result.GetResult(SignBase) is { Tokens: [{ Value: var value }] } && value.StartsWith('-'))
                result.AddError($"pen sign: unknown option \"{value}\"");
        });

        Clean = AddCommand("clean",
            "start this project's sandbox fresh: logins, caches (asks first); with --stale, remove what moved or " +
                "deleted projects and old upgrades left behind, in every project (asks first)", CleanDetails);
        CleanStale = AddFlag(Clean, "--stale", null, "instead, across every project, remove what projects you " +
            "have moved or deleted left behind (sandbox, project-layer images, approvals and logs) and tools " +
            "images built on an old base image");
    }

    /// <summary>Gets the root command: running a command or a shell, and the subcommands under it.</summary>
    public RootCommand Root { get; }

    /// <summary>Gets the recipes --tool values are parsed against.</summary>
    public RecipeCatalog Catalog { get; }

    /// <summary>Gets the options for running given before the command (pen --docker claude).</summary>
    public RunOptionSet RootOptions { get; }

    /// <summary>Gets the command to run, everything from its name on.</summary>
    public Argument<string[]> RootCommandLine { get; }

    /// <summary>Gets the help option, -h or --help, in every command.</summary>
    public HelpOption Help { get; }

    public VersionOption Version { get; }

    /// <summary>Gets pen run: running a command whose name is also a subcommand's.</summary>
    public SclCommand Run { get; }

    /// <summary>Gets the options for running given after run (pen run --docker claude).</summary>
    public RunOptionSet RunOptions { get; }

    /// <summary>Gets pen run's command to run, everything from its name on.</summary>
    public Argument<string[]> RunCommandLine { get; }

    public SclCommand Tools { get; }

    public SclCommand Upgrade { get; }

    public Option<bool> NoCache { get; }

    public SclCommand Allow { get; }

    /// <summary>Gets pen allow's domains, lowercased and without duplicates.</summary>
    public Argument<string[]> AllowDomains { get; }

    public Option<bool> AllowGlobal { get; }

    public Option<bool> AllowDefaults { get; }

    public SclCommand Deny { get; }

    /// <summary>Gets pen deny's domains, lowercased and without duplicates.</summary>
    public Argument<string[]> DenyDomains { get; }

    public Option<bool> DenyGlobal { get; }

    public SclCommand Init { get; }

    public SclCommand Trust { get; }

    public Option<bool> TrustYes { get; }

    public Option<bool> TrustList { get; }

    /// <summary>Gets pen ps, also called pen ls.</summary>
    public SclCommand Ps { get; }

    public SclCommand Sign { get; }

    /// <summary>Gets pen sign's base: none or one.</summary>
    public Argument<string?> SignBase { get; }

    public SclCommand Clean { get; }

    public Option<bool> CleanStale { get; }

    /// <summary>Writes what a help or version request asks for to <paramref name="output"/>.</summary>
    /// <returns>The exit code: 0.</returns>
    /// <remarks>
    /// Nothing else is invoked through System.CommandLine. Synchronous invocation registers no signal handlers,
    /// and its exception handler is off, so Pen's own handling applies.
    /// </remarks>
    public static int InvokeBuiltin(ParseResult result, TextWriter output) =>
        result.Invoke(new InvocationConfiguration
        {
            Output = output,
            EnableDefaultExceptionHandler = false,
            ProcessTerminationTimeout = null,
        });

    /// <summary>Chooses the width help is wrapped at.</summary>
    /// <returns>
    /// The terminal's width when it's at least <see cref="MinHelpWidth"/>, otherwise <see cref="DefaultHelpWidth"/>.
    /// </returns>
    public static int ChooseHelpWidth(int? terminalWidth) =>
        terminalWidth >= MinHelpWidth ? terminalWidth.Value : DefaultHelpWidth;

    /// <summary>Checks whether <paramref name="result"/> shows help: -h or --help given before the command.</summary>
    /// <remarks>
    /// The parser's action is the last of help and version given, so help with version is help either way round.
    /// </remarks>
    public bool AsksForHelp(ParseResult result) =>
        result.Action is DetailedHelpAction || (IsVersion(result.Action) && result.GetResult(Help) is not null);

    /// <summary>Checks whether <paramref name="action"/> shows Pen's version.</summary>
    public static bool IsVersion(CommandLineAction? action) => action is VersionAction;

    private SclCommand AddCommand(string name, string description, string details)
    {
        var command = new SclCommand(name, description);
        Root.Subcommands.Add(command);
        _details[command] = details;
        return command;
    }

    /// <summary>Creates --docker, --port and --tool for one place they can go.</summary>
    private static RunOptionSet CreateRunOptions(RecipeCatalog catalog, string docker, string port, string tool) => new(
        CreateFlag("--docker", "-d", docker),
        new Option<List<int>>("--port", "-p") { Description = port, HelpName = "n", CustomParser = ParsePorts },
        new Option<List<ToolSpec>>("--tool", "-t")
        {
            Description = tool,
            HelpName = "spec",
            CustomParser = result => ParseTools(result, catalog),
        },
        catalog);

    /// <summary>Creates the argument holding the command to run.</summary>
    /// <remarks>
    /// It takes at most 100,000 values, the most System.CommandLine allows (<see cref="ArgumentArity.ZeroOrMore"/>);
    /// the parser leaves any more unmatched.
    /// </remarks>
    private static Argument<string[]> CreateCommandLine(string description) => new("cmd")
    {
        Arity = ArgumentArity.ZeroOrMore,
        Description = description,
    };

    /// <summary>Creates an option that's on or off, given without a value.</summary>
    /// <remarks>
    /// Arity zero, since the default would take a following true or false as its value (pen -d false claude runs
    /// false claude).
    /// </remarks>
    private static Option<bool> CreateFlag(string name, string? alias, string description)
    {
        var flag = alias is null ? new Option<bool>(name) : new Option<bool>(name, alias);
        flag.Arity = ArgumentArity.Zero;
        flag.Description = description;
        return flag;
    }

    private static Option<bool> AddFlag(SclCommand command, string name, string? alias, string description)
    {
        var flag = CreateFlag(name, alias, description);
        command.Options.Add(flag);
        return flag;
    }

    /// <summary>Adds the domains argument of pen allow or pen deny (<paramref name="command"/>).</summary>
    private static Argument<string[]> AddDomains(
        SclCommand command,
        string description,
        bool allowStar,
        Func<string?> startDir)
    {
        var domains = new Argument<string[]>("domain")
        {
            Arity = ArgumentArity.ZeroOrMore,
            Description = description,
            CustomParser = result => ParseDomains(result, command.Name, allowStar, startDir),
        };
        command.Arguments.Add(domains);
        return domains;
    }

    /// <summary>
    /// Parses --port values into <paramref name="ports"/>, in order without duplicates, or says in
    /// <paramref name="error"/> what's wrong with the first value that isn't a port.
    /// </summary>
    public static bool TryParsePorts(
        IEnumerable<string> values,
        out IReadOnlyList<int> ports,
        [NotNullWhen(false)] out string? error)
    {
        var parsed = new List<int>();
        foreach (var value in values)
        {
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                || !Port.IsValid(port))
            {
                ports = [];
                error = $"--port: \"{value}\" isn't a port number (1 to 65535)";
                return false;
            }
            if (!parsed.Contains(port))
                parsed.Add(port);
        }
        ports = parsed;
        error = null;
        return true;
    }

    /// <summary>
    /// Parses --tool values into <paramref name="tools"/>, as given, or says in <paramref name="error"/> what's wrong
    /// with the first value that isn't a tool.
    /// </summary>
    public static bool TryParseTools(
        IEnumerable<string> values,
        RecipeCatalog catalog,
        out IReadOnlyList<ToolSpec> tools,
        [NotNullWhen(false)] out string? error)
    {
        var parsed = new List<ToolSpec>();
        foreach (var value in values)
        {
            if (!ToolSpec.TryParse(value, catalog, out var tool, out var invalid))
            {
                tools = [];
                error = $"--tool: {invalid}";
                return false;
            }
            parsed.Add(tool);
        }
        tools = parsed;
        error = null;
        return true;
    }

    private static List<int> ParsePorts(ArgumentResult result)
    {
        if (!TryParsePorts(result.Tokens.Select(token => token.Value), out var ports, out var error))
            result.AddError(error);
        return [.. ports];
    }

    private static List<ToolSpec> ParseTools(ArgumentResult result, RecipeCatalog catalog)
    {
        if (!TryParseTools(result.Tokens.Select(token => token.Value), catalog, out var tools, out var error))
            result.AddError(error);
        return [.. tools];
    }

    /// <summary>Parses the domain arguments of pen allow or pen deny (<paramref name="name"/>).</summary>
    private static string[] ParseDomains(ArgumentResult result, string name, bool allowStar, Func<string?> startDir)
    {
        var domains = new List<string>();
        foreach (var token in result.Tokens)
        {
            if (!TryReadDomain(name, token.Value, allowStar, startDir, out var domain, out var error))
            {
                result.AddError(error);
                return [];
            }
            if (!domains.Contains(domain))
                domains.Add(domain);
        }
        return [.. domains];
    }

    /// <summary>
    /// Reads one domain argument of pen allow or pen deny (<paramref name="name"/>) into <paramref name="domain"/>,
    /// lowercased, or says in <paramref name="error"/> why it's refused (an unknown option, or not a domain).
    /// </summary>
    /// <remarks>
    /// <see cref="Allowlist.AnyDomain"/> is valid only with <paramref name="allowStar"/>: pen allow's, not pen deny's.
    /// An argument that isn't a domain but names an entry of the start directory gets the advice for a shell that
    /// expanded an unquoted *.
    /// </remarks>
    private static bool TryReadDomain(
        string name,
        string arg,
        bool allowStar,
        Func<string?> startDir,
        out string domain,
        [NotNullWhen(false)] out string? error)
    {
        domain = "";
        error = null;
        if (arg.StartsWith('-'))
        {
            error = $"pen {name}: unknown option \"{arg}\"";
            return false;
        }
        if (arg == Allowlist.AnyDomain)
        {
            if (!allowStar)
            {
                error = $"pen {name}: \"*\" would block everything; remove the allow entries instead";
                return false;
            }
            domain = arg;
            return true;
        }

        var lowered = arg.ToLowerInvariant();
        if (Allowlist.IsValidDomain(lowered))
        {
            domain = lowered;
            return true;
        }
        error = $"pen {name}: \"{arg}\" isn't a domain (use " +
            "example.com, or .example.com to include its subdomains)";
        if (startDir() is { } dir && ShellGlob.NamesEntry(dir, arg))
            error += "; " + ShellGlob.FormatAdvice(name);
        return false;
    }

    /// <summary>Checks pen allow's options: --defaults alone, or --global with domains.</summary>
    private void ValidateAllow(CommandResult result)
    {
        if (result.GetResult(AllowDefaults) is { } defaults)
        {
            var alone = defaults.IdentifierTokenCount == 1 && result.GetResult(AllowGlobal) is null
                && result.GetResult(AllowDomains) is not { Tokens.Count: > 0 };
            if (!alone)
                result.AddError("pen allow --defaults takes nothing else");
            return;
        }
        ValidateGlobal(result, "allow", AllowGlobal, AllowDomains);
    }

    /// <summary>Checks that --global of pen allow or pen deny (<paramref name="name"/>) comes with domains.</summary>
    private static void ValidateGlobal(
        CommandResult result,
        string name,
        Option<bool> global,
        Argument<string[]> domains)
    {
        if (result.GetResult(global) is not null && result.GetResult(domains) is not { Tokens.Count: > 0 })
            result.AddError($"pen {name} --global: missing the domains to {name}");
    }

    /// <summary>Writes System.CommandLine's help for the command, then the command's longer help.</summary>
    /// <remarks>
    /// The width is the terminal's when the help goes to one (<see cref="ChooseHelpWidth"/>), otherwise
    /// <see cref="DefaultHelpWidth"/>, so help that's redirected or captured always looks the same. The longer help
    /// is wrapped at <see cref="DefaultHelpWidth"/> already.
    /// </remarks>
    private sealed class DetailedHelpAction(HelpAction help, IReadOnlyDictionary<SclCommand, string> details)
        : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            var output = parseResult.InvocationConfiguration.Output;
            help.MaxWidth = ChooseHelpWidth(Out.ReadTerminalWidth(output));
            help.Invoke(parseResult);
            if (details.TryGetValue(parseResult.CommandResult.Command, out var text))
                output.Write(text);
            return 0;
        }
    }

    /// <summary>Writes "pen" and Pen's version, or help when <paramref name="help"/> was given too.</summary>
    /// <remarks>
    /// A version request with anything else but help is a usage error: the parser checks that before invoking this.
    /// </remarks>
    private sealed class VersionAction(HelpOption help) : SynchronousCommandLineAction
    {
        public override bool ClearsParseErrors => true;

        public override int Invoke(ParseResult parseResult)
        {
            // CommandTree's constructor set the help option's action to a DetailedHelpAction, which is synchronous.
            if (parseResult.GetResult(help) is not null)
                return ((SynchronousCommandLineAction)help.Action!).Invoke(parseResult);
            var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            parseResult.InvocationConfiguration.Output.WriteLine("pen " + (version ?? "?"));
            return 0;
        }
    }

    private const string RootDescription = """
        pen [<options>]               interactive shell in this project's sandbox
                                      (your $SHELL if bash, zsh or fish, else bash)
        pen [<options>] <cmd…>        run a command in the sandbox, e.g. pen claude
        pen [<options>] run <cmd…>    same, for a command whose name matches a
                                      subcommand

        The options go before the command: everything from the command's name on is
        passed to it unchanged, so pen claude --help is Claude's help.
        """;

    private const string RootDetails = """
        pen <subcommand> --help shows more about one of them.
        pen allow --defaults lists the domains pen allows out of the box.

        """;

    private const string RunDetails = """
        pen [<options>] [<cmd…>]
        pen [<options>] run [<options>] [--] <cmd…>

        Runs a command in this project's sandbox, or an interactive shell when there's
        none (your $SHELL when it's bash, zsh or fish, otherwise bash), e.g. pen claude,
        pen codex, pen run npm ci. The options go before the command: everything from
        the command's name on is passed to it unchanged, so pen claude --help is
        Claude's help. Use pen run when the command's name is also one of pen's
        subcommands (pen run clean), and pen run -- for one whose name starts with a
        dash.

        Run it from a repo root. Ctrl-C goes to the command; the session ends when it
        exits, and pen then lists any domains the proxy blocked.

        """;

    private const string ToolsDetails = """
        Lists the agents and tools that --tool and tools = […] in either config.toml can
        name, each with its default version.

        """;

    private const string UpgradeDetails = """
        Rebuilds the base image, always as a new image, then pulls newer proxy and
        Docker-in-Docker images. Agents and tools are built on the base, so all of them
        are rebuilt: the tools and agents in your config right away, and run in a
        project, the approved tools and .pen/Dockerfile there (see pen trust); any other
        set of tools, such as an agent a command brings, on its next use. A rebuild
        installs an agent or tool at latest at its newest release, and a pinned one at
        the version given. Logins and config are kept.

        """;

    private const string AllowDetails = """
        pen allow
        pen allow <domain…> [--global]
        pen allow --defaults

        With no arguments, lists domains the proxy blocked in this project's sessions
        (last 7 days) that still aren't allowed. With deny or open egress, it lists
        apart the raw IPs blocked (in open egress, any name blocked anyway, such as one
        that resolves to a private address) and the blocked hosts deny covers now.

        With domains, adds them to allow in .pen/config.toml (creating it if needed),
        approves them for this directory (see pen trust) and applies them to this
        project's running sessions. A leading dot (.example.com) also allows subdomains.
        pen allow '*', quoted so the shell doesn't expand it, allows any domain name
        (never a raw IP, nor a name that resolves to a private address); deny still
        wins over it (see pen deny).

        """;

    private const string DenyDetails = """
        pen deny
        pen deny <domain…> [--global]

        With no arguments, lists the denied domains, the config file each is in, and
        which of pen's defaults or the agents' and tools' domains each takes.

        With domains, adds them to deny in .pen/config.toml (creating it if needed) and
        applies them to this project's running sessions. Deny wins over every allow:
        the defaults, either config.toml, agents' and tools' domains, and '*'. A leading
        dot (.example.com) also denies subdomains; gist.github.com denies one host under
        an allowed .github.com.

        """;

    private const string InitDetails = """
        Writes .pen/config.toml for this project: a commented template of every project
        setting. Toolchains found in the project's files (global.json, go.mod,
        Cargo.toml, pom.xml, build.gradle and the like) are turned on in tools, and
        secret files found (.env files, keys and certificates, a secrets directory) in
        hide, each with a comment saying what it was found from; everything else stays
        commented. It looks three directories deep at most, and skips dependency and
        build directories such as node_modules. .pen/ is per worktree, so a linked
        worktree gets its own.

        It never overwrites: if .pen/config.toml exists already, edit it instead. Review
        what it wrote, then commit it. The tools it turns on need your approval like any
        other change to .pen/: the next session asks, or run pen trust.

        """;

    private const string TrustDetails = """
        .pen/ can be written by an agent, so what it asks for needs your approval first:
        allow domains not already allowed, tools with the domains they bring,
        docker = true, a github_owner that differs from origin's, and a .pen/Dockerfile
        with the rest of .pen/ it builds from.
        pen trust lists what's new in this directory and asks, as a session would.
        --yes approves it without asking and --list only lists it, so the two don't go
        together. Approved values aren't asked about again; a changed .pen/Dockerfile
        or other file there is. Approvals are per directory, so a new worktree starts
        with none.

        """;

    private const string PsDetails = """
        Lists the running sessions of every project: the project's directory, the
        session's id (its containers are named pen-<dir>-<hash>-<id>-…), docker for
        a session with Docker-in-Docker, open for one whose egress is open
        (allow = ["*"]), its ports and tools, how long it has been running, and its
        command.

        """;

    private const string SignDetails = """
        Re-creates the commits in <base>..HEAD with your own signature, using your
        normal git signing setup, so agent commits (unsigned in the sandbox) can be
        pushed signed. Trees, authors and messages are kept; hashes change, and the
        old commits stay in the reflog. Runs on the host, not in the sandbox.

        <base> defaults to the branch's upstream (or origin/HEAD). pen refuses to
        rewrite commits that are already on a remote branch, and won't run while a
        session is open in this directory or pen clean runs. It doesn't refuse for a
        session in another worktree of the repo, but that session's agent can change
        what pen sign reads, so wait for it to end too.

        """;

    private const string CleanDetails = """
        pen clean
        pen clean --stale

        With no arguments, starts this project's sandbox fresh, after asking: removes
        its home (agent logins and config, shell history), its NuGet cache and its
        Docker-in-Docker images. The project's files, ~/.config/pen, and the project's
        approvals (pen trust) and blocked-domain logs (pen allow) are kept. A repo's
        linked worktrees share the sandbox, so it refuses while a session or pen sign
        runs in the main checkout or any of its worktrees.

        With --stale, projects still in use are listed and left alone. A project whose
        directory is gone may be on a volume that isn't mounted: mount it first.
        Projects pen hasn't run in since it began recording their directories are
        listed with the commands to remove them by hand, never removed. A project with
        an open session is skipped.

        """;
}

/// <summary>--docker, --port and --tool, the options for running, at one place they can go.</summary>
/// <param name="Docker">--docker or -d.</param>
/// <param name="Port">--port or -p, repeatable; parsed in order without duplicates.</param>
/// <param name="Tool">--tool or -t, repeatable.</param>
/// <param name="Catalog">The recipes --tool values are parsed against.</param>
internal sealed record RunOptionSet(
    Option<bool> Docker,
    Option<List<int>> Port,
    Option<List<ToolSpec>> Tool,
    RecipeCatalog Catalog)
{
    public IEnumerable<Option> All => [Docker, Port, Tool];

    /// <summary>Gets the options that take a value, with how their values are checked.</summary>
    public IEnumerable<ValuedOption> Valued =>
    [
        new(Port, "--port: missing the port number",
            values => CommandTree.TryParsePorts(values, out _, out var error) ? null : error),
        new(Tool, "--tool: missing the tool (name or name:version)",
            values => CommandTree.TryParseTools(values, Catalog, out _, out var error) ? null : error),
    ];
}

/// <summary>An option for running that takes a value.</summary>
/// <param name="Option">The option.</param>
/// <param name="Missing">What's wrong when it's given without a value.</param>
/// <param name="FindError">Finds what's wrong with the values given, or returns null.</param>
internal sealed record ValuedOption(Option Option, string Missing, Func<IEnumerable<string>, string?> FindError);
