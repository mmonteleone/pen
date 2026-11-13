using Pen.Commands;
using Pen.Platform;
using Pen.Tools;

namespace Pen.CommandLine;

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
