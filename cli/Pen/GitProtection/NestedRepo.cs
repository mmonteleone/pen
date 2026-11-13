namespace Pen.GitProtection;

/// <summary>A repo below the project root.</summary>
/// <param name="Path">The entry that makes it one: the .git, or a bare repo's HEAD.</param>
/// <param name="Kind">What sort of entry <paramref name="Path"/> is.</param>
internal sealed record NestedRepo(string Path, NestedKind Kind)
{
    /// <summary>Gets the repo's directory, for messages.</summary>
    public string RepoDir => System.IO.Path.GetDirectoryName(Path)!;
}
