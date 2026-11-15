namespace Pen.Signing;

/// <summary>The base that pen sign signs the commits after.</summary>
/// <param name="Name">The base as named on the command line, the upstream's short name, or origin/HEAD's ref.</param>
/// <param name="Commit">The commit it resolves to.</param>
internal sealed record ResolvedBase(string Name, string Commit);
