using System.IO.Enumeration;

namespace Pen.GitProtection;

/// <summary>A repo below the project root. Path is the entry that makes it one: the .git, or a bare repo's HEAD.</summary>
internal sealed record NestedRepo(string Path, NestedKind Kind)
{
    /// <summary>The repo's directory, for messages.</summary>
    public string RepoDir => System.IO.Path.GetDirectoryName(Path)!;
}
