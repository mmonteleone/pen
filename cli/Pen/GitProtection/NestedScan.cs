using System.IO.Enumeration;

namespace Pen.GitProtection;

/// <summary>What the walk of the tree found (Found), the directories it could enter but not list (Unlistable), and the entries it couldn't name (BadNames).</summary>
internal sealed record NestedScan(List<NestedRepo> Found, List<string> Unlistable, List<BadName> BadNames);
