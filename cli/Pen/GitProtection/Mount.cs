using System.Security.Cryptography;
using System.Text;

namespace Pen.GitProtection;

/// <summary>
/// A path mounted over itself in the agent container. Verify: a single file
/// that must not change during the session, saved at its start and compared at
/// its end (<see cref="ProtectedFiles"/>): directory mounts hold on every engine
/// tested, but case variants of a name can get past a single-file mount.
/// </summary>
internal sealed record Mount(string Path, bool ReadOnly, bool Verify = false);
