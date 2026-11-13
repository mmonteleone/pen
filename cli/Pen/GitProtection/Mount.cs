namespace Pen.GitProtection;

/// <summary>A path mounted over itself in the agent container.</summary>
/// <param name="Path">The host path, mounted at the same path in the container.</param>
/// <param name="ReadOnly">Whether the mount is read-only.</param>
/// <param name="Verify">
/// Whether it's a single file that must not change during the session: saved at its start and compared at its end
/// (<see cref="ProtectedFiles"/>).
/// </param>
/// <remarks>
/// Directory mounts hold on every engine tested, but case variants of a name can get past a single-file mount.
/// </remarks>
internal sealed record Mount(string Path, bool ReadOnly, bool Verify = false);
