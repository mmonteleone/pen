using System.Globalization;
using Pen.Proxy;
using Pen.Sessions;
using Pen.Tools;

namespace Pen.CommandLine;

/// <summary>Parses Pen's command line into a <see cref="Command"/>.</summary>
internal static class CommandParser
{
    private static readonly Dictionary<string, CommandKind> Subcommands = new(StringComparer.Ordinal)
    {
        ["run"] = CommandKind.Run,
        ["upgrade"] = CommandKind.Upgrade,
        ["allow"] = CommandKind.Allow,
        ["trust"] = CommandKind.Trust,
        ["sign"] = CommandKind.Sign,
        ["clean"] = CommandKind.Clean,
        ["ps"] = CommandKind.Ps,
    };

    /// <summary>Parses the arguments Pen was started with.</summary>
    /// <returns>The command they ask for.</returns>
    /// <exception cref="PenException">The arguments aren't valid; exit code 2, with the usage.</exception>
    /// <remarks>
    /// A first argument that's a subcommand selects it; any other first argument
    /// is the command to run. Options before the command are Pen's: --docker,
    /// --port and --tool (for running), help and version. Any other one is an error.
    /// </remarks>
    public static Command Parse(IReadOnlyList<string> args)
    {
        // The options for running, in any order and repeatable.
        var options = new RunOptions();
        var index = 0;
        while (index < args.Count && !options.IsToolList)
        {
            var consumed = options.ReadAt(args, index);
            if (consumed == 0)
                break;
            index += consumed;
        }

        if (options.IsToolList)
            return new(CommandKind.ToolList, []);
        if (options.FirstName == null)
            return ParseCommand(args);
        return ParseWithRunOptions(args.Skip(index).ToList(), options);
    }

    /// <summary>Parses the command that follows Pen's options for running, and adds those options to it.</summary>
    private static Command ParseWithRunOptions(List<string> after, RunOptions options)
    {
        var command = ParseCommand(after);
        if (command.Kind != CommandKind.Run)
        {
            throw CreateUsageError(after.Count > 0 && Subcommands.ContainsKey(after[0])
                ? $"{options.FirstName} goes only with a command to run, not pen {after[0]}"
                : $"{options.FirstName} goes only with a command to run");
        }

        if (ToolSpec.TryCanonicalize(options.Tools, RecipeCatalog.Embedded, out var canonical) is { } conflict)
            throw CreateUsageError($"--tool: {conflict}");
        return command with { Docker = options.Docker, Ports = options.Ports, Tools = canonical };
    }

    /// <summary>Parses a command line that doesn't start with an option for running.</summary>
    private static Command ParseCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return new(CommandKind.Run, []);
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
            return ParseSubcommand(first, kind, rest);
        if (first.StartsWith('-'))
            throw CreateUsageError($"unknown option \"{first}\"");

        return new(CommandKind.Run, args.ToList());
    }

    /// <summary>Parses the arguments after a subcommand's name.</summary>
    private static Command ParseSubcommand(string name, CommandKind kind, List<string> rest)
    {
        if (IsHelpRequest(kind, rest))
            return new(CommandKind.Help, [name]);
        return kind switch
        {
            CommandKind.Run => ParseRun(rest),
            CommandKind.Upgrade => ParseUpgrade(rest),
            CommandKind.Clean => ParseClean(rest),
            CommandKind.Ps => ParseWithoutArguments(name, kind, rest),
            CommandKind.Allow => ParseAllow(rest),
            CommandKind.Trust => ParseTrust(rest),
            CommandKind.Sign => ParseSign(rest),
            _ => new(kind, rest),
        };
    }

    /// <summary>Checks whether the arguments after a subcommand ask for its help.</summary>
    /// <remarks>run's own help only right after it: later, --help belongs to the command.</remarks>
    private static bool IsHelpRequest(CommandKind kind, List<string> rest)
    {
        if (kind == CommandKind.Run)
            return rest.Count > 0 && IsHelp(rest[0]);
        return rest.Any(IsHelp);
    }

    private static bool IsHelp(string arg) => arg is "-h" or "--help";

    /// <summary>Parses pen run's arguments: an optional --, then the command to run.</summary>
    private static Command ParseRun(List<string> rest)
    {
        if (rest.Count > 0 && rest[0] == "--")
            rest.RemoveAt(0);
        if (rest.Count == 0)
            throw CreateUsageError("pen run: missing the command to run");
        return new(CommandKind.Run, rest);
    }

    /// <summary>Parses pen upgrade's arguments: only --no-cache.</summary>
    private static Command ParseUpgrade(List<string> rest)
    {
        var noCache = false;
        foreach (var arg in rest)
        {
            if (arg == "--no-cache")
                noCache = true;
            else
                throw CreateUsageError($"pen upgrade: unexpected argument \"{arg}\"");
        }
        return new(CommandKind.Upgrade, []) { NoCache = noCache };
    }

    /// <summary>Parses pen clean's arguments: only --stale.</summary>
    private static Command ParseClean(List<string> rest)
    {
        var stale = false;
        foreach (var arg in rest)
        {
            if (arg == "--stale")
                stale = true;
            else
                throw CreateUsageError($"pen clean: unexpected argument \"{arg}\"");
        }
        return new(CommandKind.Clean, []) { Stale = stale };
    }

    /// <summary>Parses the arguments of a subcommand that takes none (pen ps).</summary>
    private static Command ParseWithoutArguments(string name, CommandKind kind, List<string> rest)
    {
        if (rest.Count > 0)
            throw CreateUsageError($"pen {name}: unexpected argument \"{rest[0]}\"");
        return new(kind, []);
    }

    /// <summary>Parses pen allow's arguments: --defaults alone, or domains and --global.</summary>
    private static Command ParseAllow(List<string> rest)
    {
        if (rest.Contains("--defaults"))
        {
            if (rest.Count > 1)
                throw CreateUsageError("pen allow --defaults takes nothing else");
            return new(CommandKind.Allow, []) { Defaults = true };
        }

        var global = false;
        var domains = new List<string>();
        foreach (var arg in rest)
        {
            if (arg == "--global")
            {
                global = true;
                continue;
            }

            var domain = ReadDomain(arg);
            if (!domains.Contains(domain))
                domains.Add(domain);
        }
        if (global && domains.Count == 0)
            throw CreateUsageError("pen allow --global: missing the domains to allow");
        return new(CommandKind.Allow, domains) { Global = global };
    }

    /// <summary>Reads one domain argument of pen allow.</summary>
    /// <returns>The domain, lowercased.</returns>
    /// <exception cref="PenException">The argument is an unknown option or isn't a domain.</exception>
    private static string ReadDomain(string arg)
    {
        if (arg.StartsWith('-'))
            throw CreateUsageError($"pen allow: unknown option \"{arg}\"");
        var domain = arg.ToLowerInvariant();
        if (!Allowlist.IsValidDomain(domain))
        {
            throw CreateUsageError($"pen allow: \"{arg}\" isn't a domain (use " +
                "example.com, or .example.com to include its subdomains)");
        }

        return domain;
    }

    /// <summary>Parses pen trust's arguments: --yes or --list.</summary>
    private static Command ParseTrust(List<string> rest)
    {
        var yes = false;
        var list = false;
        foreach (var arg in rest)
        {
            if (arg == "--yes")
                yes = true;
            else if (arg == "--list")
                list = true;
            else
                throw CreateUsageError($"pen trust: unexpected argument \"{arg}\"");
        }
        if (yes && list)
            throw CreateUsageError("pen trust: --yes and --list don't go together");
        return new(CommandKind.Trust, []) { Yes = yes, List = list };
    }

    /// <summary>Parses pen sign's arguments: at most a base, which can't look like an option.</summary>
    private static Command ParseSign(List<string> rest)
    {
        if (rest.Count > 1)
            throw CreateUsageError($"pen sign: unexpected argument \"{rest[1]}\"");
        if (rest.Count == 1 && rest[0].StartsWith('-'))
            throw CreateUsageError($"pen sign: unknown option \"{rest[0]}\"");
        return new(CommandKind.Sign, rest);
    }

    /// <summary>Creates the error for an invalid command line: the message, then the usage, with exit code 2.</summary>
    private static PenException CreateUsageError(string message) =>
        new($"{message}\n{HelpText.Usage}", exitCode: 2);

    /// <summary>The value of an option given as <c>--name value</c> or <c>--name=value</c>.</summary>
    /// <param name="Value">The value, or null when <c>--name</c> is the last argument.</param>
    /// <param name="Width">How many arguments the option and its value take up: 2, or 1 with =.</param>
    private readonly record struct OptionValue(string? Value, int Width)
    {
        /// <summary>
        /// Matches the argument at <paramref name="index"/> against the option <paramref name="name"/>.
        /// </summary>
        /// <returns>The option's value, or null if the argument is a different one.</returns>
        public static OptionValue? Match(IReadOnlyList<string> args, int index, string name)
        {
            var arg = args[index];
            if (arg == name)
                return new(index + 1 < args.Count ? args[index + 1] : null, 2);
            if (arg.StartsWith(name + "=", StringComparison.Ordinal))
                return new(arg[(name.Length + 1)..], 1);
            return null;
        }
    }

    /// <summary>The options for running that come before the command: --docker, --port and --tool.</summary>
    private sealed class RunOptions
    {
        private readonly List<int> _ports = [];
        private readonly List<ToolSpec> _tools = [];

        /// <summary>Gets the first option given (--docker, --port or --tool), for errors; null if none was.</summary>
        public string? FirstName { get; private set; }

        /// <summary>Gets whether --docker was given.</summary>
        public bool Docker { get; private set; }

        /// <summary>Gets whether the command line is <c>pen --tool list</c>.</summary>
        public bool IsToolList { get; private set; }

        /// <summary>Gets the --port values, in order without duplicates.</summary>
        public IReadOnlyList<int> Ports => _ports;

        /// <summary>Gets the --tool values, as given.</summary>
        public IReadOnlyList<ToolSpec> Tools => _tools;

        /// <summary>Reads the option for running at <paramref name="index"/>, if it's one.</summary>
        /// <returns>How many arguments it took, or 0 if the argument there isn't an option for running.</returns>
        public int ReadAt(IReadOnlyList<string> args, int index)
        {
            if (args[index] == "--docker")
            {
                Docker = true;
                FirstName ??= "--docker";
                return 1;
            }
            if (OptionValue.Match(args, index, "--port") is { } port)
            {
                AddPort(port.Value);
                FirstName ??= "--port";
                return port.Width;
            }
            if (OptionValue.Match(args, index, "--tool") is { } tool)
            {
                if (tool.Value == "list")
                {
                    // Only the whole command line: pen --tool list.
                    if (args.Count != tool.Width)
                        throw CreateUsageError("--tool list takes nothing else");
                    IsToolList = true;
                    return tool.Width;
                }
                AddTool(tool.Value);
                FirstName ??= "--tool";
                return tool.Width;
            }
            return 0;
        }

        private void AddPort(string? value)
        {
            if (value == null)
                throw CreateUsageError("--port: missing the port number");
            if (!int.TryParse(value, NumberStyles.None, null, out var port) || !Port.IsValid(port))
                throw CreateUsageError($"--port: \"{value}\" isn't a port number (1 to 65535)");
            if (!_ports.Contains(port))
                _ports.Add(port);
        }

        private void AddTool(string? value)
        {
            if (value == null)
                throw CreateUsageError("--tool: missing the tool (name or name:version)");
            if (ToolSpec.TryParse(value, RecipeCatalog.Embedded, out var tool) is { } invalid)
                throw CreateUsageError($"--tool: {invalid}");
            _tools.Add(tool);
        }
    }
}
