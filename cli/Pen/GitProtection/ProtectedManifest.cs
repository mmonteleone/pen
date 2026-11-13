using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>What a session saved at its start.</summary>
/// <param name="Files">Every single-file mount that must not change (<see cref="Mount.Verify"/>).</param>
/// <param name="ReadOnlyDirs">
/// The directories mounted read-only as a whole, whose contents the sandbox can't change or add to.
/// </param>
internal sealed record ProtectedManifest(List<ProtectedFile> Files, List<string> ReadOnlyDirs)
{
    /// <summary>Tells whether this session vouches for a config.worktree found at its end.</summary>
    /// <returns>
    /// True if it was saved at the start and not adopted, or is in a directory the sandbox had read-only as a whole.
    /// </returns>
    /// <remarks>Never a symlink, which only reads as the file it leads to.</remarks>
    public bool Vouches(string path)
    {
        if (RepoGuard.IsLink(path))
            return false;
        var real = Native.RealPath(path) ?? path;
        return Files.Any(file => !file.Adopted && (file.Path == path || file.Path == real))
            || ReadOnlyDirs.Any(dir => RepoGuard.IsInside(dir, path) || RepoGuard.IsInside(dir, real));
    }
}
