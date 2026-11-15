namespace Pen.GitProtection;

/// <summary>How a run of the .git check went: what it changed and found, and whether it finished.</summary>
/// <remarks>Git is only safe to run in the checked directory if <see cref="Failure"/> is null.</remarks>
internal sealed record CheckResult
{
    /// <summary>Gets what the check renamed before it stopped; an entry with no destination wasn't moved.</summary>
    public required List<Quarantined> Moved { get; init; }

    /// <summary>Gets why the check couldn't finish or vouch for the directory, or null if it did.</summary>
    public required string? Failure { get; init; }

    /// <summary>Gets the directories the check opened and left so.</summary>
    public List<ModeChange> Opened { get; init; } = [];

    /// <summary>Gets the protected files that changed with nothing to set aside.</summary>
    public List<RestoredFile> Restored { get; init; } = [];

    /// <summary>Gets whether the protected files weren't all put back, so the session's files must be kept.</summary>
    public bool RestoreIncomplete { get; init; }

    /// <summary>Gets the directories whose modes kept protected files from being put back, each once.</summary>
    public List<string> RestoreBlocked { get; init; } = [];

    /// <summary>Gets the paused operations the check found, which it leaves in place.</summary>
    public List<PausedOp> Paused { get; init; } = [];
}

/// <summary>One rename the check made, or one it couldn't make.</summary>
/// <param name="From">What was moved.</param>
/// <param name="To">Where it went, or empty if it couldn't be moved (or restored).</param>
/// <param name="Why">Why it was moved, and if it couldn't be, why not.</param>
/// <param name="Inner">A second rename inside the moved item (a .git directory's HEAD), if any.</param>
internal sealed record Quarantined(string From, string To, string Why, (string From, string To)? Inner = null)
{
    /// <summary>Whether it couldn't be moved (or restored): <see cref="To"/> is empty.</summary>
    public bool Failed => To.Length == 0;
}

/// <summary>A protected file that changed with nothing to set aside.</summary>
/// <param name="Path">The file.</param>
/// <param name="What">What happened to it: it was missing, its directory went, or only its mode changed.</param>
internal sealed record RestoredFile(string Path, string What);

/// <summary>A directory the check opened and left that way.</summary>
/// <param name="Path">The directory.</param>
/// <param name="Was">Its mode before.</param>
/// <param name="Now">Its mode now.</param>
internal sealed record ModeChange(string Path, UnixFileMode Was, UnixFileMode Now);
