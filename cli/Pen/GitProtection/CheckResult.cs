namespace Pen.GitProtection;

/// <summary>How a run of the .git check went: what it changed and found, and whether it finished.</summary>
/// <remarks>Git is only safe to run in the checked directory if <see cref="Failure"/> is null.</remarks>
internal sealed record CheckResult
{
    /// <summary>Gets what the check renamed before it stopped; an entry with no destination wasn't moved.</summary>
    public required List<Quarantined> Moved { get; init; }

    /// <summary>Gets why the check couldn't finish or vouch for the directory, or null if it did.</summary>
    public required string? Failure { get; init; }

    /// <summary>Gets the directories the check opened and left so, or null for a check that opens none.</summary>
    public List<ModeChange>? Opened { get; init; }

    /// <summary>
    /// Gets the protected files that changed with nothing to set aside, or null for a check that restores none.
    /// </summary>
    public List<RestoredFile>? Restored { get; init; }

    /// <summary>Gets whether the protected files weren't all put back, so the session's files must be kept.</summary>
    public bool RestoreIncomplete { get; init; }

    /// <summary>
    /// Gets the directories whose modes kept protected files from being put back, each once, or null for a check
    /// that restores none.
    /// </summary>
    public List<string>? RestoreBlocked { get; init; }

    /// <summary>Gets the paused operations the check found, which it leaves in place.</summary>
    public List<PausedOp>? Paused { get; init; }
}
