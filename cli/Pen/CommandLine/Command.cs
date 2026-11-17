using System.CommandLine;
using Pen.Tools;

namespace Pen.CommandLine;

/// <summary>A parsed command line: the command Pen runs and its options.</summary>
/// <param name="Kind">The command.</param>
/// <param name="Args">
/// For Run, the command to run in the sandbox (empty: a shell); for Allow and Deny, the domains (lowercased);
/// for Sign, the base, if given.
/// </param>
internal sealed record Command(CommandKind Kind, IReadOnlyList<string> Args)
{
    /// <summary>Gets whether pen upgrade --no-cache was given.</summary>
    public bool NoCache { get; init; }

    /// <summary>Gets whether pen clean --stale was given.</summary>
    public bool Stale { get; init; }

    /// <summary>Gets whether pen allow --global or pen deny --global was given.</summary>
    public bool Global { get; init; }

    /// <summary>Gets whether pen --docker was given.</summary>
    public bool Docker { get; init; }

    /// <summary>Gets whether pen allow --defaults was given.</summary>
    public bool Defaults { get; init; }

    /// <summary>Gets whether pen trust --yes was given.</summary>
    public bool Yes { get; init; }

    /// <summary>Gets whether pen trust --list was given.</summary>
    public bool List { get; init; }

    /// <summary>Gets the pen --port values, in order without duplicates.</summary>
    public IReadOnlyList<int> Ports { get; init; } = [];

    /// <summary>Gets the pen --tool values, canonical.</summary>
    public IReadOnlyList<ToolSpec> Tools { get; init; } = [];

    /// <summary>Gets, for Builtin, the parse to invoke (<see cref="CommandTree.InvokeBuiltin"/>) for the help or
    /// version it asks for.</summary>
    public ParseResult? ParseResult { get; init; }
}
