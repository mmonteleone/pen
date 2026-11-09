namespace Pen;

internal enum CommandKind { Run, Upgrade, Allow, Trust, Sign, Reset, Ps, Prune, Help, Version, ToolList, BuildToolsImage }

/// <summary>
/// A parsed command line. For Run, Args is the command to run in the sandbox
/// (empty: a shell); for Allow, the domains (lowercased); for Sign, the base, if given;
/// for Help, the subcommand to describe, if any.
/// Defaults: pen allow --defaults. Yes, List: pen trust --yes, --list. Ports: pen --port, in order without duplicates.
/// Tools: pen --tool, canonical.
/// </summary>
internal sealed record Command(CommandKind Kind, IReadOnlyList<string> Args, bool Clean = false, bool Global = false, bool Docker = false, bool Defaults = false, bool Yes = false, bool List = false)
{
    public IReadOnlyList<int> Ports { get; init; } = [];
    public IReadOnlyList<ToolSpec> Tools { get; init; } = [];
}

internal static class Cli
{
    /// <summary>
    /// With --tool only: builds those tools' image if it's missing and prints its name.
    /// Undocumented, for the smoke checks, since sessions don't run tools images.
    /// It reads no config, so nothing in .pen/ reaches a build.
    /// </summary>
    private const string BuildToolsImageOption = "--build-tools-image";

    public const string Usage = """
        usage:
          pen [<options>]           interactive shell in this project's sandbox
          pen [<options>] <cmd…>    run a command in the sandbox, e.g. pen claude
          pen [<options>] run <cmd…>
                                    same, for a command whose name matches a subcommand
                                    --docker: with Docker-in-Docker (or docker = true in .pen/config.toml)
                                    --port <n>: forward localhost:<n> to the sandbox (repeatable;
                                    or ports = [<n>, …] in .pen/config.toml)
                                    --tool <spec>: a tool, as name or name:version (repeatable;
                                    or tools = […] in either config.toml). Checked only:
                                    sessions don't install tools yet
          pen --tool list           list the tools pen can install
          pen upgrade [--clean]     newer base image and agents; --clean skips the cache
          pen allow <domain…> [--global]
                                    allow domains, applied to running sessions too
          pen allow                 list recently blocked domains
          pen allow --defaults      list the domains pen allows out of the box
          pen trust [--yes|--list]  review and approve what this project's .pen/ asks for
          pen ps                    list running sessions, in every project
          pen sign [<base>]         on the host: re-sign unpushed commits with your key
          pen reset                 wipe this project's volumes (asks first)
          pen prune                 remove what moved or deleted projects left behind (asks first)
          pen <subcommand> --help   more about one of them
        """;

    /// <summary>pen &lt;subcommand&gt; --help.</summary>
    public static readonly Dictionary<string, string> SubcommandHelp = new(StringComparer.Ordinal)
    {
        ["run"] = """
            usage: pen [--docker] [--port <n>]… [--tool <spec>]… [<cmd…>]
                   pen [--docker] [--port <n>]… [--tool <spec>]… run [--] <cmd…>

            Runs a command in this project's sandbox, or an interactive shell when there's
            none, e.g. pen claude, pen codex, pen run npm ci. Everything after the command's
            name is passed to it unchanged, including options like --help. Use pen run when
            the command's name is also one of pen's subcommands (pen run reset).

            Run it from a repo root. Ctrl-C goes to the command; the session ends when it
            exits, and pen then lists any domains the proxy blocked.

              --docker   give the session its own Docker daemon (one per project at a
                         time); docker = true in .pen/config.toml does this for every session
              --port <n> forward port <n> on the host's 127.0.0.1 to port <n> in the
                         sandbox (repeatable); ports = [<n>, …] in .pen/config.toml does
                         this for every session. A server in the sandbox must listen on
                         0.0.0.0, not only localhost
              --tool <spec>
                         a tool, as name or name:version (repeatable); tools = […] in
                         either config.toml adds to these, and pen --tool list shows what's
                         available. Checked only: sessions don't install tools yet
            """,
        ["upgrade"] = """
            usage: pen upgrade [--clean]

            Updates the base image and reinstalls the agents at their latest versions,
            then pulls newer proxy and Docker-in-Docker images. Run in a project with an
            approved .pen/Dockerfile (see pen trust), it rebuilds that layer too. Logins
            and config are kept.

              --clean    rebuild without the build cache
            """,
        ["allow"] = """
            usage: pen allow
                   pen allow <domain…> [--global]
                   pen allow --defaults

            With no arguments, lists domains the proxy blocked in this project's sessions
            (last 7 days) that still aren't allowed.

            With domains, adds them to allow in .pen/config.toml (creating it if needed),
            approves them for this directory (see pen trust) and applies them to this
            project's running sessions. A leading dot (.example.com) also allows subdomains.

              --global     add them to ~/.config/pen/config.toml instead, for every
                           project, and apply them to all running sessions
              --defaults   list the domains pen allows out of the box
            """,
        ["trust"] = """
            usage: pen trust [--yes | --list]

            .pen/ can be written by an agent, so what it asks for needs your approval first:
            allow domains not already allowed, docker = true, a github_owner that differs
            from origin's, and a .pen/Dockerfile with the rest of .pen/ it builds from.
            pen trust lists what's new in this directory and asks, as a session would.
            Approved values aren't asked about again; a changed .pen/Dockerfile or other
            file there is. Approvals are per directory, so a new worktree starts with none.

              --yes      approve without asking, for scripts
              --list     list everything new, however long, and approve nothing
            """,
        ["ps"] = """
            usage: pen ps

            Lists the running sessions of every project: the project's directory, the
            session's id (its containers are named pen-<dir>-<hash>-<id>-…), docker for
            a session with Docker-in-Docker, how long it has been running, and its command.
            """,
        ["sign"] = """
            usage: pen sign [<base>]

            Re-creates the commits in <base>..HEAD with your own signature, using your
            normal git signing setup, so agent commits (unsigned in the sandbox) can be
            pushed signed. Trees, authors and messages are kept; hashes change, and the
            old commits stay in the reflog. Runs on the host, not in the sandbox.

            <base> defaults to the branch's upstream (or origin/HEAD). pen refuses to
            rewrite commits that are already on a remote branch, and won't run while a
            session is open in this project.
            """,
        ["reset"] = """
            usage: pen reset

            Removes this project's sandbox volumes, after asking: its home (agent logins
            and config, shell history), its NuGet cache and its Docker-in-Docker images.
            The project's files and ~/.config/pen are untouched. Not while a session is
            open in this project.
            """,
        ["prune"] = """
            usage: pen prune

            Finds the volumes, project-layer images and state that projects you have moved
            or deleted left behind, lists them with the projects still in use, and removes
            them after asking. A project whose directory is gone may be on a volume that
            isn't mounted: mount it first. Projects pen hasn't run in since it began
            recording their directories are listed with the commands to remove them by
            hand, never removed. Not while a session is open in the project.
            """,
    };

    private static bool IsHelp(string a) => a is "-h" or "--help";

    private static readonly Dictionary<string, CommandKind> Subcommands = new(StringComparer.Ordinal)
    {
        ["run"] = CommandKind.Run,
        ["upgrade"] = CommandKind.Upgrade,
        ["allow"] = CommandKind.Allow,
        ["trust"] = CommandKind.Trust,
        ["sign"] = CommandKind.Sign,
        ["reset"] = CommandKind.Reset,
        ["ps"] = CommandKind.Ps,
        ["prune"] = CommandKind.Prune,
    };

    /// <summary>
    /// A first argument that's a subcommand selects it; any other first argument
    /// is the command to run. Options before the command are pen's: --docker,
    /// --port and --tool (for running), help and version. Any other one is an error.
    /// </summary>
    public static Command Parse(IReadOnlyList<string> args)
    {
        // The options for running, in any order and repeatable.
        string? runOption = null;
        var docker = false;
        var ports = new List<int>();
        var tools = new List<ToolSpec>();
        var buildToolsImage = false;
        var i = 0;
        while (i < args.Count)
        {
            var a = args[i];
            if (a == "--docker")
            {
                docker = true;
                i++;
            }
            else if (a == "--port" || a.StartsWith("--port=", StringComparison.Ordinal))
            {
                var value = a == "--port" ? (i + 1 < args.Count ? args[i + 1] : null) : a["--port=".Length..];
                if (value == null) throw Error("--port: missing the port number");
                if (!int.TryParse(value, System.Globalization.NumberStyles.None, null, out var port) || !Port.IsValid(port))
                    throw Error($"--port: \"{value}\" isn't a port number (1 to 65535)");
                if (!ports.Contains(port)) ports.Add(port);
                i += a == "--port" ? 2 : 1;
            }
            else if (a == "--tool" || a.StartsWith("--tool=", StringComparison.Ordinal))
            {
                var value = a == "--tool" ? (i + 1 < args.Count ? args[i + 1] : null) : a["--tool=".Length..];
                if (value == null) throw Error("--tool: missing the tool (name or name:version)");
                if (value == "list")
                {
                    if (args.Count != (a == "--tool" ? 2 : 1)) throw Error("--tool list takes nothing else");
                    return new(CommandKind.ToolList, []);
                }
                if (ToolSpec.TryParse(value, RecipeCatalog.Embedded, out var tool) is { } invalid) throw Error($"--tool: {invalid}");
                tools.Add(tool);
                i += a == "--tool" ? 2 : 1;
            }
            else if (a == BuildToolsImageOption)
            {
                buildToolsImage = true;
                i++;
            }
            else break;
            runOption ??= a.StartsWith("--port", StringComparison.Ordinal) ? "--port"
                : a.StartsWith("--tool", StringComparison.Ordinal) ? "--tool"
                : a;
        }
        if (runOption != null)
        {
            var after = args.Skip(i).ToList();
            IReadOnlyList<ToolSpec> Canonical() =>
                ToolSpec.TryCanonicalize(tools, RecipeCatalog.Embedded, out var canonical) is { } conflict ? throw Error($"--tool: {conflict}") : canonical;
            if (buildToolsImage)
            {
                if (docker || ports.Count > 0 || after.Count > 0) throw Error($"{BuildToolsImageOption} takes only --tool");
                return new(CommandKind.BuildToolsImage, []) { Tools = Canonical() };
            }
            var c = Parse(after);
            if (c.Kind != CommandKind.Run)
                throw Error(after.Count > 0 && Subcommands.ContainsKey(after[0])
                    ? $"{runOption} goes only with a command to run, not pen {after[0]}"
                    : $"{runOption} goes only with a command to run");
            return c with { Docker = docker, Ports = ports, Tools = Canonical() };
        }
        if (args.Count == 0) return new(CommandKind.Run, []);
        var first = args[0];
        var rest = args.Skip(1).ToList();

        switch (first)
        {
            case "-h" or "--help":
                return new(CommandKind.Help, []);
            case "--version":
                return new(CommandKind.Version, []);
            case "--":
                return new(CommandKind.Run, rest);
        }

        if (Subcommands.TryGetValue(first, out var kind))
        {
            // run's own help only right after it: later, --help belongs to the command.
            if (kind == CommandKind.Run ? rest.Count > 0 && IsHelp(rest[0]) : rest.Any(IsHelp))
                return new(CommandKind.Help, [first]);
            switch (kind)
            {
                case CommandKind.Run:
                    if (rest.Count > 0 && rest[0] == "--") rest.RemoveAt(0);
                    if (rest.Count == 0) throw Error("pen run: missing the command to run");
                    return new(CommandKind.Run, rest);
                case CommandKind.Upgrade:
                    var clean = false;
                    foreach (var a in rest)
                    {
                        if (a == "--clean") clean = true;
                        else throw Error($"pen upgrade: unexpected argument \"{a}\"");
                    }
                    return new(CommandKind.Upgrade, [], clean);
                case CommandKind.Reset:
                    if (rest.Count > 0) throw Error($"pen reset: unexpected argument \"{rest[0]}\"");
                    return new(CommandKind.Reset, []);
                case CommandKind.Ps:
                    if (rest.Count > 0) throw Error($"pen ps: unexpected argument \"{rest[0]}\"");
                    return new(CommandKind.Ps, []);
                case CommandKind.Prune:
                    if (rest.Count > 0) throw Error($"pen prune: unexpected argument \"{rest[0]}\"");
                    return new(CommandKind.Prune, []);
                case CommandKind.Allow:
                    var global = false;
                    var domains = new List<string>();
                    if (rest.Contains("--defaults"))
                    {
                        if (rest.Count > 1) throw Error("pen allow --defaults takes nothing else");
                        return new(CommandKind.Allow, [], Defaults: true);
                    }
                    foreach (var a in rest)
                    {
                        if (a == "--global") global = true;
                        else if (a.StartsWith('-')) throw Error($"pen allow: unknown option \"{a}\"");
                        else if (!Allowlist.IsValidDomain(a.ToLowerInvariant()))
                            throw Error($"pen allow: \"{a}\" isn't a domain (use example.com, or .example.com to include its subdomains)");
                        else if (!domains.Contains(a.ToLowerInvariant())) domains.Add(a.ToLowerInvariant());
                    }
                    if (global && domains.Count == 0) throw Error("pen allow --global: missing the domains to allow");
                    return new(CommandKind.Allow, domains, Global: global);
                case CommandKind.Trust:
                    var yes = false;
                    var list = false;
                    foreach (var a in rest)
                    {
                        if (a == "--yes") yes = true;
                        else if (a == "--list") list = true;
                        else throw Error($"pen trust: unexpected argument \"{a}\"");
                    }
                    if (yes && list) throw Error("pen trust: --yes and --list don't go together");
                    return new(CommandKind.Trust, [], Yes: yes, List: list);
                case CommandKind.Sign:
                    if (rest.Count > 1) throw Error($"pen sign: unexpected argument \"{rest[1]}\"");
                    if (rest.Count == 1 && rest[0].StartsWith('-')) throw Error($"pen sign: unknown option \"{rest[0]}\"");
                    return new(CommandKind.Sign, rest);
                default:
                    return new(kind, rest);
            }
        }

        if (first.StartsWith('-'))
            throw Error($"unknown option \"{first}\"");

        return new(CommandKind.Run, args.ToList());
    }

    private static PenException Error(string message) => new($"{message}\n{Usage}", exitCode: 2);
}
