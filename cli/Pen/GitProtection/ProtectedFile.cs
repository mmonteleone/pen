namespace Pen.GitProtection;

/// <summary>One file a session saved at its start.</summary>
/// <param name="Path">Its real path.</param>
/// <param name="Copy">The name of its copy in the session's protected dir.</param>
/// <param name="Mode">Its mode at the start.</param>
/// <param name="Adopted">
/// Whether it's a config.worktree first seen while another session's agent could have planted it, or inherited from
/// a session that adopted it.
/// </param>
/// <remarks>
/// Nothing vouches for an adopted file: it's never written back, and it's quarantined at the end like one that isn't
/// listed at all, changed or not.
/// </remarks>
internal sealed record ProtectedFile(string Path, string Copy, UnixFileMode Mode, bool Adopted);
