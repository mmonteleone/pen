namespace Pen.GitProtection;

/// <summary>
/// What a run of the .git check renamed, put back and made listable, kept as it goes, so a run that stops part way
/// still has what was done before it stopped.
/// </summary>
/// <remarks>
/// One <see cref="GitCheck"/> owns it and passes it to its <see cref="ProtectedFileRestore"/>, which runs first:
/// each list holds the protected files' entries first, in the order they were made.
/// </remarks>
internal sealed class CheckChanges
{
    private readonly List<Quarantined> _moved = [];
    private readonly List<RestoredFile> _restored = [];
    private readonly List<ModeChange> _modeChanges = [];
    private readonly List<string> _blocked = [];

    /// <summary>Gets what was renamed, or couldn't be renamed or restored, in order.</summary>
    public IReadOnlyList<Quarantined> Moved => _moved;

    /// <summary>Gets the protected files that changed with nothing to set aside, in order.</summary>
    public IReadOnlyList<RestoredFile> Restored => _restored;

    /// <summary>Gets the directories made listable (or writable) and left so, in order.</summary>
    public IReadOnlyList<ModeChange> ModeChanges => _modeChanges;

    /// <summary>Gets the directories whose modes kept protected files from being put back, in order.</summary>
    public IReadOnlyList<string> Blocked => _blocked;

    /// <summary>Records a rename, or a rename or restore that failed, in Moved.</summary>
    public void Add(Quarantined moved) => _moved.Add(moved);

    /// <summary>Records a protected file that changed with nothing to set aside in Restored.</summary>
    public void Add(RestoredFile restored) => _restored.Add(restored);

    /// <summary>Records a directory mode change in ModeChanges.</summary>
    public void Add(ModeChange change) => _modeChanges.Add(change);

    /// <summary>Records a directory whose mode kept a protected file from being put back in Blocked.</summary>
    public void AddBlocked(string directory) => _blocked.Add(directory);

    /// <summary>
    /// Makes a directory listable (and writable too, with <paramref name="write"/>) with
    /// <see cref="FileModes.TryMakeListable"/> and records the mode change in ModeChanges.
    /// </summary>
    /// <returns>Whether it changed the mode.</returns>
    public bool TryMakeListableRecording(string path, bool write)
    {
        if (!FileModes.TryMakeListable(path, write, out var change))
            return false;
        Add(change);
        return true;
    }
}
