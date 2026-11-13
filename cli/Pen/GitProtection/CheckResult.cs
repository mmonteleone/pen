using System.Text;

namespace Pen.GitProtection;

/// <summary>
/// How a session-end check went: what it renamed before it stopped, and why it
/// couldn't finish (null if it did). Git is only safe to run if Failure is null.
/// </summary>
internal sealed record CheckResult(List<Quarantined> Moved, string? Failure, List<ModeChange>? Opened = null, List<RestoredFile>? Restored = null, bool RestoreIncomplete = false, List<string>? RestoreBlocked = null, List<PausedOp>? Paused = null);
