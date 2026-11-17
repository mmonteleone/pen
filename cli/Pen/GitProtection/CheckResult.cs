namespace Pen.GitProtection;

/// <summary>How a run of the .git check went: what it changed and found, and whether it finished.</summary>
/// <remarks>
/// Git is only safe to run in the checked directory if <see cref="Failure"/> is null. The lists are copies of what
/// the run had collected when it ended, stopped part way or not.
/// </remarks>
internal sealed record CheckResult
{
    /// <summary>
    /// Gets what the check renamed or removed before it stopped; a <see cref="Quarantined.Failed"/> entry wasn't.
    /// </summary>
    public required IReadOnlyList<Quarantined> Moved { get; init; }

    /// <summary>Gets why the check couldn't finish or vouch for the directory, or null if it did.</summary>
    public required string? Failure { get; init; }

    /// <summary>Gets the directories the check made listable (or writable) and left so.</summary>
    public IReadOnlyList<ModeChange> ModeChanges { get; init; } = [];

    /// <summary>Gets the protected files that changed with nothing to set aside.</summary>
    public IReadOnlyList<RestoredFile> Restored { get; init; } = [];

    /// <summary>Gets whether the protected files weren't all put back, so the session's files must be kept.</summary>
    public bool RestoreIncomplete { get; init; }

    /// <summary>Gets the directories whose modes kept protected files from being put back, each once.</summary>
    public IReadOnlyList<string> RestoreBlocked { get; init; } = [];

    /// <summary>Gets the paused operations the check found, which it leaves in place.</summary>
    public IReadOnlyList<PausedOp> Paused { get; init; } = [];
}

/// <summary>
/// One thing the check renamed or removed, or couldn't: <see cref="Moved"/>, <see cref="LinkRemoved"/> or
/// <see cref="Failed"/>, which are all there are.
/// </summary>
internal abstract record Quarantined
{
    private Quarantined(string from, string why)
    {
        From = from;
        Why = why;
    }

    /// <summary>Gets what was moved or removed, or couldn't be (or couldn't be restored).</summary>
    public string From { get; }

    /// <summary>Gets why it was moved or removed, and if it couldn't be, why not.</summary>
    public string Why { get; }

    /// <summary>A rename the check made, which renaming <see cref="To"/> back to From undoes.</summary>
    /// <param name="From">What was moved; for a protected file written back in place, that file.</param>
    /// <param name="To">
    /// Where it went; for a protected file written back in place, where its changed version was kept.
    /// </param>
    /// <param name="Why">Why it was moved.</param>
    /// <param name="Inner">A second rename inside the moved item (a .git directory's HEAD), if any.</param>
    internal sealed record Moved(string From, string To, string Why, InnerRename? Inner = null)
        : Quarantined(From, Why);

    /// <summary>
    /// A symlink the check removed rather than renamed: it holds no data, and under any name leads to the same place.
    /// </summary>
    /// <param name="From">The symlink.</param>
    /// <param name="Why">Why it was removed.</param>
    /// <param name="LinkTarget">What it held, so it can be made again.</param>
    /// <param name="LinkTargetEscaped">
    /// Whether what it held isn't valid UTF-8, so <paramref name="LinkTarget"/> shows it with octal escapes and names
    /// it only for reading.
    /// </param>
    internal sealed record LinkRemoved(string From, string Why, string LinkTarget, bool LinkTargetEscaped = false)
        : Quarantined(From, Why);

    /// <summary>
    /// Something the check couldn't move or remove, or a protected file it couldn't restore: it fails the check.
    /// </summary>
    /// <param name="From">What couldn't be moved, removed or restored.</param>
    /// <param name="Why">Why it was to be, and why it couldn't be.</param>
    internal sealed record Failed(string From, string Why) : Quarantined(From, Why);
}

/// <summary>A rename inside an item the check moved.</summary>
/// <param name="From">The name it had, inside the item's new place.</param>
/// <param name="To">The name it has now.</param>
internal sealed record InnerRename(string From, string To);

/// <summary>A protected file that changed with nothing to set aside.</summary>
/// <param name="Path">The file.</param>
/// <param name="What">What happened to it: it was missing, its directory went, or only its mode changed.</param>
internal sealed record RestoredFile(string Path, string What);

/// <summary>A directory the check made listable (or writable) and left that way.</summary>
/// <param name="Path">The directory.</param>
/// <param name="Was">Its mode before.</param>
/// <param name="Now">Its mode now.</param>
internal sealed record ModeChange(string Path, UnixFileMode Was, UnixFileMode Now);
