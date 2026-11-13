using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// What a session saved. Files: every single-file mount that must not change
/// (<see cref="Mount.Verify"/>). ReadOnlyDirs: the directories mounted read-only
/// as a whole, whose contents the sandbox can't change or add to.
/// </summary>
internal sealed record ProtectedManifest(List<ProtectedFile> Files, List<string> ReadOnlyDirs)
{
    /// <summary>
    /// Whether a config.worktree found at the session's end is one this session
    /// vouches for: saved at its start and not adopted, or in a directory its
    /// sandbox had read-only as a whole. Never a symlink, which only reads as
    /// the file it leads to.
    /// </summary>
    public bool Vouches(string path)
    {
        if (RepoGuard.IsLink(path))
            return false;
        var real = Native.RealPath(path) ?? path;
        return Files.Any(f => !f.Adopted && (f.Path == path || f.Path == real))
            || ReadOnlyDirs.Any(d => RepoGuard.IsInside(d, path) || RepoGuard.IsInside(d, real));
    }
}
