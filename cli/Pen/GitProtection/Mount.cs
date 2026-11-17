namespace Pen.GitProtection;

/// <summary>A bind mount in the agent container: a host path over itself, or a host file over another path.</summary>
/// <remarks>
/// Path is the path in the container, and the host path mounted there unless Source names another. Verify marks a
/// single file that must not change during the session: it's saved at the start and compared at the end
/// (<see cref="ProtectedFiles"/>). Directory mounts hold on every engine tested, but case variants of a name can get
/// past a single-file mount.
/// </remarks>
internal sealed record Mount(string Path, bool ReadOnly, bool Verify = false, string? Source = null);
