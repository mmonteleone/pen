namespace Pen.GitProtection;

/// <summary>What the walk of a git dir's submodule repos found.</summary>
/// <param name="Repos">The module repos, parents before their nested repos.</param>
/// <param name="UnreadableDirs">The directories it couldn't read, so what's in them is unknown.</param>
/// <param name="BadNames">The entries whose names aren't valid UTF-8, of any type.</param>
internal sealed record ModuleScan(List<string> Repos, List<string> UnreadableDirs, List<BadName> BadNames);
