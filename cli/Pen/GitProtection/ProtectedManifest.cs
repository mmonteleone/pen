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

/// <summary>One file a session saved at its start.</summary>
/// <param name="Path">Its real path.</param>
/// <param name="Copy">The name of its copy in the session's protected dir.</param>
/// <param name="Mode">Its mode at the start.</param>
/// <param name="Adopted">
/// Whether another session's agent could have planted it: it's in a module repo an open session didn't list at its
/// start (<see cref="ModuleBaseline"/>), or it's a config.worktree first seen while one could write there, or it's
/// inherited from a session that adopted it.
/// </param>
/// <remarks>
/// Nothing vouches for an adopted file: it's never written back, and it's quarantined at the end, changed or not,
/// with its module repo, or like a config.worktree that isn't listed at all.
/// </remarks>
internal sealed record ProtectedFile(string Path, string Copy, UnixFileMode Mode, bool Adopted);
