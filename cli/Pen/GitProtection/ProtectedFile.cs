namespace Pen.GitProtection;

/// <summary>
/// One saved file: its real path, its copy's name in the session's protected dir,
/// and its mode at the start. Adopted: a config.worktree first seen while another
/// session's agent could have planted it, or inherited from a session that adopted
/// it, so nothing vouches for it: it's never written back, and it's quarantined at
/// the end like one that isn't listed at all, changed or not.
/// </summary>
internal sealed record ProtectedFile(string Path, string Copy, UnixFileMode Mode, bool Adopted = false);
