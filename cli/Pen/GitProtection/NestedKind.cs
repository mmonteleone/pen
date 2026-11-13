using System.IO.Enumeration;

namespace Pen.GitProtection;

internal enum NestedKind
{
    /// <summary>A .git directory: a repo with its own config and hooks.</summary>
    GitDir,
    /// <summary>A .git file (gitfile) that isn't one of the project's own worktrees or submodule checkouts.</summary>
    GitFile,
    /// <summary>A .git symlink.</summary>
    GitLink,
    /// <summary>A directory git takes for a bare repo: HEAD plus objects/ and refs/, or HEAD plus commondir.</summary>
    Bare,
}
