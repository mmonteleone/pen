using Pen.Tools;

namespace Pen.CommandLine;

/// <summary>A parsed command line: the command Pen runs and its options.</summary>
/// <param name="Kind">The command.</param>
/// <param name="Args">
/// For Run, the command to run in the sandbox (empty: a shell); for Allow, the domains (lowercased);
/// for Sign, the base, if given; for Help, the subcommand to describe, if any.
/// </param>
internal sealed record Command(CommandKind Kind, IReadOnlyList<string> Args)
{
    /// <summary>Gets whether pen upgrade --clean was given.</summary>
    public bool Clean { get; init; }

    /// <summary>Gets whether pen allow --global was given.</summary>
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
}
