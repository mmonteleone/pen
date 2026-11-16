namespace Pen.GitProtection;

/// <summary>A bind mount in the agent container: a host path over itself, or a host file over another path.</summary>
/// <param name="Path">
/// The path in the container, and the host path mounted there unless <paramref name="Source"/> is set.
/// </param>
/// <param name="ReadOnly">Whether the mount is read-only.</param>
/// <param name="Verify">
/// Whether it's a single file that must not change during the session: saved at its start and compared at its end
/// (<see cref="ProtectedFiles"/>).
/// </param>
/// <param name="Source">
/// The host path mounted at <paramref name="Path"/>, or null for <paramref name="Path"/> itself.
/// </param>
/// <remarks>
/// Directory mounts hold on every engine tested, but case variants of a name can get past a single-file mount.
/// </remarks>
internal sealed record Mount(string Path, bool ReadOnly, bool Verify = false, string? Source = null);
