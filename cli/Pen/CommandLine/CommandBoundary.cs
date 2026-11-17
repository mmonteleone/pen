using System.CommandLine;
using SclCommand = System.CommandLine.Command;

namespace Pen.CommandLine;

/// <summary>Marks where the command to run starts, so that everything from its name on is passed to it.</summary>
/// <remarks>
/// Pen's options end at the command's name (pen claude --help is Claude's help), which System.CommandLine
/// can't express, so a -- goes in before the name and the parser takes everything after it as the command.
/// Scanning from the start, the options for running are skipped with their values; the first other argument
/// decides. One starting with - (--, help, version or an unknown option) is left to the parser, as is a
/// subcommand's name, except run's: after run and its options for running, help and -- are left to the
/// parser and anything else is the command. Any other argument is the command. Option and subcommand names
/// come from <see cref="CommandTree"/>, and values are found as the parser finds them: attached after the
/// first : or = of a known option, otherwise the next argument, whatever it starts with, unless it's --.
/// </remarks>
internal static class CommandBoundary
{
    private const string EndOfOptions = "--";

    /// <summary>Inserts -- before the command to run in <paramref name="args"/>, if there's one to mark.</summary>
    /// <returns>The arguments to parse.</returns>
    public static List<string> Insert(IReadOnlyList<string> args, CommandTree tree)
    {
        var result = args.ToList();
        var index = SkipRunOptions(args, 0, tree.RootOptions);
        if (index == args.Count || args[index].StartsWith('-'))
            return result;
        if (IsNamed(tree.Run, args[index]))
        {
            index = SkipRunOptions(args, index + 1, tree.RunOptions);
            if (index == args.Count || args[index] == EndOfOptions || IsNamed(tree.Help, args[index]))
                return result;
        }
        else if (tree.Root.Subcommands.Any(command => IsNamed(command, args[index])))
        {
            return result;
        }

        result.Insert(index, EndOfOptions);
        return result;
    }

    /// <summary>Skips the options for running, with their values, from <paramref name="start"/>.</summary>
    /// <returns>The index of the first argument that isn't one of them or a value, or the count when none is.</returns>
    private static int SkipRunOptions(IReadOnlyList<string> args, int start, RunOptionSet options)
    {
        var index = start;
        while (index < args.Count && FindOption(args[index], options) is { } found)
        {
            index++;
            var takesValue = found.Option.Arity.MaximumNumberOfValues > 0;
            if (takesValue && found.Attached is null or "" && index < args.Count && args[index] != EndOfOptions)
                index++;
        }
        return index;
    }

    /// <summary>Finds the option for running <paramref name="arg"/> is, alone or with a value after : or =.</summary>
    /// <returns>The option and the attached value (null when there's none), or null if it isn't one.</returns>
    private static FoundOption? FindOption(string arg, RunOptionSet options)
    {
        if (options.All.FirstOrDefault(option => IsNamed(option, arg)) is { } exact)
            return new(exact, null);
        var split = arg.AsSpan().IndexOfAny(':', '=');
        if (split < 0)
            return null;
        var name = arg[..split];
        return options.All.FirstOrDefault(option => IsNamed(option, name)) is { } option
            ? new(option, arg[(split + 1)..])
            : null;
    }

    private static bool IsNamed(Option option, string arg) => option.Name == arg || option.Aliases.Contains(arg);

    private static bool IsNamed(SclCommand command, string arg) => command.Name == arg || command.Aliases.Contains(arg);

    /// <summary>An option for running found in the arguments.</summary>
    /// <param name="Option">The option.</param>
    /// <param name="Attached">The value after : or =, possibly empty; null when it's given without one.</param>
    private sealed record FoundOption(Option Option, string? Attached);
}
