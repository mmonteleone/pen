namespace Pen.GitProtection;

/// <summary>
/// What the walk of a git dir's submodule repos found (Repos), couldn't read (Unreadable) and couldn't name (BadNames).
/// </summary>
internal sealed record ModuleScan(List<string> Repos, List<string> Unreadable, List<BadName> BadNames);
