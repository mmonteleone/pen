namespace Pen;

internal enum CommandKind { Run, Upgrade, Allow, Sign, Reset, Help, Version }

/// <summary>A parsed command line. For Run, Args is the command to run in the sandbox (empty: a shell).</summary>
internal sealed record Command(CommandKind Kind, IReadOnlyList<string> Args, bool Clean = false);

internal static class Cli
{
    public const string Usage = """
        usage:
          pen                       interactive shell in this project's sandbox
          pen <cmd…>                run a command in the sandbox, e.g. pen claude
          pen run <cmd…>            same, for a command whose name matches a subcommand
          pen upgrade [--clean]     newer base image and agents; --clean skips the cache
          pen allow <domain…> [--global]
                                    allow domains, applied to running sessions too
          pen allow                 list recently blocked domains
          pen sign [<base>]         on the host: re-sign unpushed commits with your key
          pen reset                 wipe this project's volumes (asks first)
        """;

    private static readonly Dictionary<string, CommandKind> Subcommands = new(StringComparer.Ordinal)
    {
        ["run"] = CommandKind.Run,
        ["upgrade"] = CommandKind.Upgrade,
        ["allow"] = CommandKind.Allow,
        ["sign"] = CommandKind.Sign,
        ["reset"] = CommandKind.Reset,
    };

    /// <summary>
    /// A first argument that's a subcommand selects it; any other first argument
    /// is the command to run. Options before the command are pen's, and pen has
    /// none yet beyond help and version, so any other one is an error.
    /// </summary>
    public static Command Parse(IReadOnlyList<string> args)
    {
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
