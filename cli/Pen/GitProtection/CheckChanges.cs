namespace Pen.GitProtection;

/// <summary>
/// What a run of the .git check renamed, put back and opened, kept as it goes, so a run that stops part way still
/// has what was done before it stopped.
/// </summary>
/// <remarks>
/// One <see cref="GitCheckRun"/> owns it and passes it to its <see cref="ProtectedFileRestore"/>, which runs first:
/// each list holds the protected files' entries first, in the order they were made.
/// </remarks>
internal sealed class CheckChanges
{
    /// <summary>Gets what was renamed, or couldn't be renamed or restored, in order.</summary>
    public List<Quarantined> Moved { get; } = [];

    /// <summary>Gets the protected files that changed with nothing to set aside, in order.</summary>
    public List<RestoredFile> Restored { get; } = [];

    /// <summary>Gets the directories opened and left so, in order.</summary>
    public List<ModeChange> Opened { get; } = [];

    /// <summary>Gets the directories whose modes kept protected files from being put back, in order.</summary>
    public List<string> Blocked { get; } = [];

    /// <summary>Opens a directory with <see cref="FileModes.TryOpen"/> and records the mode change in Opened.</summary>
    /// <returns>Whether it changed the mode.</returns>
    public bool TryOpenRecording(string path, bool write)
    {
        if (!FileModes.TryOpen(path, write, out var change))
            return false;
        Opened.Add(change);
        return true;
    }
}
