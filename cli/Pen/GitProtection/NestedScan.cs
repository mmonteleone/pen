namespace Pen.GitProtection;

/// <summary>What the walk of a project's tree found.</summary>
/// <param name="Repos">The repos below the root, in ordinal order of their paths.</param>
/// <param name="UnlistableDirs">The directories it could enter but not list, in ordinal order.</param>
/// <param name="BadNames">The entries it couldn't name that could hold a repo, in ordinal order.</param>
internal sealed record NestedScan(List<NestedRepo> Repos, List<string> UnlistableDirs, List<BadName> BadNames);
