using System.CommandLine;
using System.CommandLine.Parsing;
using System.Globalization;
using Pen.Tools;
using SclCommand = System.CommandLine.Command;

namespace Pen.CommandLine;

/// <summary>Parses Pen's command line into a <see cref="CliInvocation"/>.</summary>
internal static class CommandParser
{
    /// <summary>The parser's settings: no POSIX bundling (-dp) and no response files (@file).</summary>
    /// <remarks>
    /// Bundling would let the parser read -dp differently from <see cref="CommandBoundary"/>, and a response file
    /// would turn an argument into others.
    /// </remarks>
    private static readonly ParserConfiguration Configuration = new()
    {
        EnablePosixBundling = false,
        ResponseFileTokenReplacer = null,
    };

    /// <summary>Parses the arguments Pen was started with into what they ask Pen to do.</summary>
    /// <exception cref="PenException">
    /// The arguments aren't valid; exit code 2, with the help command to run for usage.
    /// </exception>
    /// <remarks>
    /// A first argument that's a subcommand selects it; any other first argument that isn't an option is the command
    /// to run, as is everything after it. Options before the command are Pen's: --docker, --port and --tool (for
    /// running, also right after run), help and version. Any other one is an error. <paramref name="startDir"/> gives
    /// the start directory, or null when it can't be read, for the advice on a domain argument named like a file there
    /// (<see cref="ShellGlob"/>).
    /// </remarks>
    public static CliInvocation Parse(IReadOnlyList<string> args, Func<string?> startDir, RecipeCatalog catalog)
    {
        var tree = new CommandTree(startDir, catalog);
        var bounded = CommandBoundary.Insert(args, tree);
        // The root's name goes first, since System.CommandLine drops a first argument whose file name is the root's
        // name (pen pen x would lose a pen).
        var result = tree.Root.Parse([tree.Root.Name, .. bounded], Configuration);
        var parsed = result.CommandResult.Command;

        ThrowIfInformationValueGiven(tree, bounded, result);

        // Help clears every parse error but an unknown option before the command: pen allow bad --help is allow's
        // help, and pen --dockr --help is an error.
        if (tree.AsksForHelp(result))
        {
            ThrowIfUnknownRootOption(tree, result, help: true);
            return new BuiltinInvocation(result);
        }
        if (CommandTree.IsVersion(result.Action))
        {
            if (result.Errors.Count > 0)
                throw CreateUsageError(result.Errors[0].Message, tree.Root);
            return new BuiltinInvocation(result);
        }

        ThrowIfAttachedValueInvalid(tree, bounded, result);
        ThrowIfUnknownRootOption(tree, result, help: false);
        ThrowIfRunOptionInvalid(result, tree.RootOptions, tree.Root);
        ThrowIfInvalid(tree, result);
        if (parsed != tree.Root && parsed != tree.Run)
            ThrowIfRunOptionGiven(tree, result);
        return CreateInvocation(tree, result);
    }

    /// <summary>Refuses a value attached after : or = to help or version, empty or not.</summary>
    /// <remarks>
    /// This runs before help and version are acted on, which would otherwise show or print for --help=1 or read -h:-x
    /// as an unknown option. Only the arguments before the command boundary are looked at: the rest are the
    /// command's.
    /// </remarks>
    private static void ThrowIfInformationValueGiven(
        CommandTree tree,
        IReadOnlyList<string> bounded,
        ParseResult result)
    {
        foreach (var arg in bounded.TakeWhile(arg => arg != "--"))
        {
            var end = arg.IndexOfAny([':', '=']);
            if (end < 0)
                continue;
            var name = arg[..end];
            foreach (var option in new Option[] { tree.Help, tree.Version })
            {
                if (option.Name != name && !option.Aliases.Contains(name))
                    continue;
                var context = option == tree.Help ? result.CommandResult.Command : tree.Root;
                throw CreateUsageError($"{option.Name} takes no value", context);
            }
        }
    }

    /// <summary>
    /// Refuses a value attached after : or = to an on-or-off option (--docker=false), or an empty one (--port=).
    /// </summary>
    /// <remarks>
    /// For an on-or-off option the parser makes the value an argument, which would run false as the command or add
    /// x as a domain (allow --global=x a.com); for an empty one it takes the next argument as the value. Up to the
    /// --, each argument is one token, or an option token and, when something follows the : or =, a value token:
    /// that's how arguments and tokens are matched up here, and anything else is Pen's bug.
    /// </remarks>
    private static void ThrowIfAttachedValueInvalid(CommandTree tree, IReadOnlyList<string> bounded, ParseResult result)
    {
        var tokens = result.Tokens;
        var next = 0;
        SclCommand context = tree.Root;
        foreach (var arg in bounded)
        {
            if (next == tokens.Count || tokens[next].Type == TokenType.DoubleDash)
                return;
            var token = tokens[next++];
            if (token.Type == TokenType.Command)
                context = result.CommandResult.Command;
            if (token.Value == arg)
                continue;
            if (token.Type != TokenType.Option || !arg.StartsWith(token.Value, StringComparison.Ordinal)
                || arg[token.Value.Length] is not (':' or '='))
            {
                throw new InvalidOperationException($"the parser's token \"{token.Value}\" doesn't match \"{arg}\"");
            }
            var option = FindOption(context == tree.Root ? context.Options : [.. context.Options, tree.Help], token);
            if (option.Arity.MaximumNumberOfValues == 0)
                throw CreateUsageError($"{option.Name} takes no value", context);
            if (arg.Length == token.Value.Length + 1)
                throw CreateUsageError(FindMissingValueMessage(tree, option), context);
            next++;
        }
    }

    /// <summary>Finds what to say when <paramref name="option"/> is given without its value.</summary>
    private static string FindMissingValueMessage(CommandTree tree, Option option) =>
        tree.RootOptions.Valued.Concat(tree.RunOptions.Valued).FirstOrDefault(v => v.Option == option)?.Missing
            ?? $"{option.Name}: missing the value";

    /// <summary>Refuses an argument starting with - before the command that isn't one of Pen's options.</summary>
    /// <remarks>
    /// The parser doesn't report these: they become the root's command to run, even with a subcommand after them
    /// (pen --dockr allow x.com). Without help, the root's arguments that come before a -- are always these, since
    /// <see cref="CommandBoundary"/> puts one before the command. When <paramref name="help"/> was asked for, only the
    /// root's arguments up to the first that doesn't start with - are checked, since the command starts there
    /// (pen --help claude is help, pen --dockr claude --help an error).
    /// </remarks>
    private static void ThrowIfUnknownRootOption(CommandTree tree, ParseResult result, bool help)
    {
        if (result.GetResult(tree.RootCommandLine) is not { Tokens: var tokens })
            return;
        var afterDash = 0;
        if (result.CommandResult.Command == tree.Root)
            afterDash = result.Tokens.SkipWhile(t => t.Type != TokenType.DoubleDash).Skip(1).Count();
        var before = tokens.Take(tokens.Count - afterDash);
        if (help)
            before = before.TakeWhile(token => token.Value.StartsWith('-'));
        if (before.FirstOrDefault()?.Value is not { } first)
            return;
        throw CreateUsageError(
            first.StartsWith('-') ? $"unknown option \"{first}\"" : $"unexpected argument \"{first}\"", tree.Root);
    }

    /// <summary>Refuses a --port or --tool of <paramref name="options"/> without a valid value.</summary>
    /// <remarks>
    /// When a subcommand is parsed, the root's values are only parsed when read, so their errors aren't in the
    /// result's: they're read here, before anything else is checked.
    /// </remarks>
    private static void ThrowIfRunOptionInvalid(ParseResult result, RunOptionSet options, SclCommand owner)
    {
        foreach (var valued in options.Valued)
        {
            if (result.GetResult(valued.Option) is not { } optionResult)
                continue;
            if (optionResult.Tokens.Count < optionResult.IdentifierTokenCount)
                throw CreateUsageError(valued.Missing, owner);
            if (valued.FindError(optionResult.Tokens.Select(token => token.Value)) is { } error)
                throw CreateUsageError(error, owner);
        }
    }

    /// <summary>Refuses what the parser found wrong in the command that was parsed.</summary>
    private static void ThrowIfInvalid(CommandTree tree, ParseResult result)
    {
        var parsed = result.CommandResult.Command;
        if (result.UnmatchedTokens.Count > 0)
        {
            var name = parsed == tree.Root ? "" : $"pen {FormatName(result)}: ";
            // The command to run takes every argument after it, up to the most the parser allows.
            var commandLine = parsed == tree.Run ? tree.RunCommandLine : tree.RootCommandLine;
            var most = commandLine.Arity.MaximumNumberOfValues;
            var full = (parsed == tree.Root || parsed == tree.Run)
                && result.GetResult(commandLine) is { } given && given.Tokens.Count >= most;
            var problem = full
                ? string.Create(CultureInfo.InvariantCulture, $"too many arguments (at most {most:N0})")
                : $"unexpected argument \"{result.UnmatchedTokens[0]}\"";
            throw CreateUsageError(name + problem, parsed);
        }
        if (parsed == tree.Run)
            ThrowIfRunOptionInvalid(result, tree.RunOptions, tree.Run);
        if (result.Errors.Count > 0)
            throw CreateUsageError(result.Errors[0].Message, parsed);
    }

    /// <summary>Refuses an option for running given before a subcommand other than run.</summary>
    private static void ThrowIfRunOptionGiven(CommandTree tree, ParseResult result)
    {
        // Before the subcommand, every option is the root's, and help and version have been handled.
        var first = result.Tokens.TakeWhile(token => token.Type != TokenType.Command)
            .FirstOrDefault(token => token.Type == TokenType.Option);
        if (first is not null)
        {
            var option = FindOption(tree.RootOptions.All, first);
            throw CreateUsageError(
                $"{option.Name} goes only with a command to run, not pen {FormatName(result)}", tree.Root);
        }
    }

    /// <summary>Creates the invocation a valid parse asks for.</summary>
    private static CliInvocation CreateInvocation(CommandTree tree, ParseResult result)
    {
        var parsed = result.CommandResult.Command;
        if (parsed == tree.Root)
            return CreateRun(result, tree.RootCommandLine, [tree.RootOptions], tree.Root, tree.Catalog);
        if (parsed == tree.Run)
            return CreateRun(result, tree.RunCommandLine, [tree.RootOptions, tree.RunOptions], tree.Run, tree.Catalog);
        if (parsed == tree.Allow)
        {
            if (result.GetValue(tree.AllowDefaults))
                return new ListDefaultDomainsInvocation();
            return result.GetValue(tree.AllowDomains) is { Length: > 0 } domains
                ? new AllowDomainsInvocation([.. domains], result.GetValue(tree.AllowGlobal))
                : new ListBlockedInvocation();
        }
        if (parsed == tree.Deny)
        {
            return result.GetValue(tree.DenyDomains) is { Length: > 0 } domains
                ? new DenyDomainsInvocation([.. domains], result.GetValue(tree.DenyGlobal))
                : new ListDeniedInvocation();
        }
        if (parsed == tree.Trust)
            return new TrustInvocation(ReadTrustMode(tree, result));
        if (parsed == tree.Clean)
            return result.GetValue(tree.CleanStale) ? new CleanStaleInvocation() : new CleanInvocation();
        if (parsed == tree.Sign)
            return new SignInvocation(ReadTokens(result, tree.SignBase) is [var given] ? given : null);
        if (parsed == tree.Upgrade)
            return new UpgradeInvocation(result.GetValue(tree.NoCache));
        if (parsed == tree.Ps)
            return new PsInvocation();
        if (parsed == tree.Tools)
            return new ToolsInvocation();
        if (parsed == tree.Init)
            return new InitInvocation();
        throw new InvalidOperationException($"unhandled command \"{parsed.Name}\"");
    }

    /// <summary>Reads pen trust's mode; --yes and --list together were refused already.</summary>
    private static TrustMode ReadTrustMode(CommandTree tree, ParseResult result)
    {
        if (result.GetValue(tree.TrustYes))
            return TrustMode.ApproveAll;
        return result.GetValue(tree.TrustList) ? TrustMode.List : TrustMode.Review;
    }

    /// <summary>Reads an argument's values exactly as given.</summary>
    private static IReadOnlyList<string> ReadTokens(ParseResult result, Argument argument) =>
        result.GetResult(argument)?.Tokens.Select(token => token.Value).ToArray() ?? [];

    /// <summary>
    /// Creates the run of <paramref name="commandLine"/>, with the options for running from each place they were
    /// given (before and after run).
    /// </summary>
    private static RunInvocation CreateRun(
        ParseResult result,
        Argument commandLine,
        RunOptionSet[] places,
        SclCommand owner,
        RecipeCatalog catalog)
    {
        var ports = new List<int>();
        foreach (var port in places.SelectMany(place => result.GetValue(place.Port) ?? []))
        {
            if (!ports.Contains(port))
                ports.Add(port);
        }
        var tools = places.SelectMany(place => result.GetValue(place.Tool) ?? []);
        if (!ToolSpec.TryCanonicalize(tools, catalog, out var canonical, out var conflict))
            throw CreateUsageError($"--tool: {conflict}", owner);
        return new(
            ReadTokens(result, commandLine),
            Docker: places.Any(place => result.GetValue(place.Docker)),
            Ports: ports,
            Tools: canonical);
    }

    /// <summary>
    /// Finds the option of <paramref name="options"/> that the parser read <paramref name="token"/> as.
    /// </summary>
    private static Option FindOption(IEnumerable<Option> options, Token token) =>
        options.Single(option => option.Name == token.Value || option.Aliases.Contains(token.Value));

    /// <summary>Formats the parsed subcommand's name as it was given (ls for pen ls).</summary>
    private static string FormatName(ParseResult result) => result.CommandResult.IdentifierToken.Value;

    /// <summary>
    /// Creates the error for an invalid command line: the message, then the help of <paramref name="command"/> to run
    /// for usage.
    /// </summary>
    private static PenException CreateUsageError(string message, SclCommand command)
    {
        var help = command is RootCommand ? "pen --help" : $"pen {command.Name} --help";
        return new($"{message}\nRun '{help}' for usage.", exitCode: 2);
    }
}
