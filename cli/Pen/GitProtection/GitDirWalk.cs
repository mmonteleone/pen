using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// Walks the git dirs the .git check looks in for paused state: a git dir, its worktree entries, then each of its
/// module repos that isn't a symlink and that repo's worktree entries.
/// </summary>
/// <remarks>
/// <see cref="PausedGit"/> and <see cref="PromptHazardScan"/> both walk this way, so they look in the same places.
/// </remarks>
internal static class GitDirWalk
{
    /// <summary>Visits <paramref name="gitDir"/>, if it's a directory, and the git dirs that go with it.</summary>
    /// <param name="gitDir">The git dir.</param>
    /// <param name="visit">
    /// Called with the git dir, then each worktree entry it can look in, in ordinal order; then the same for each
    /// module repo, in the order <see cref="RepoScan.FindModuleRepos"/> gives them.
    /// </param>
    /// <param name="couldntLook">
    /// Called with a worktrees dir if it can't be listed, or each entry in it that can't be looked in.
    /// </param>
    /// <param name="tryMakeListable">
    /// Makes a directory that can't be listed or entered listable, or null to change no modes. It's tried first.
    /// </param>
    public static void Visit(
        string gitDir,
        Action<string> visit,
        Action<string> couldntLook,
        Func<string, bool>? tryMakeListable)
    {
        if (!Directory.Exists(gitDir))
            return;
        VisitWithWorktrees(gitDir, visit, couldntLook, tryMakeListable);
        foreach (var repo in RepoScan.FindModuleRepos(gitDir))
        {
            if (!PathChecks.IsLink(repo))
                VisitWithWorktrees(repo, visit, couldntLook, tryMakeListable);
        }
    }

    /// <summary>Visits a git dir and its worktree entries, naming what it can't look in.</summary>
    private static void VisitWithWorktrees(
        string gitDir,
        Action<string> visit,
        Action<string> couldntLook,
        Func<string, bool>? tryMakeListable)
    {
        visit(gitDir);
        var worktrees = Path.Combine(gitDir, "worktrees");
        if (!PathChecks.ExistsOrIsLink(worktrees))
            return;
        if (!PathChecks.IsLink(worktrees) && Directory.Exists(worktrees) && !Native.CanList(worktrees))
            tryMakeListable?.Invoke(worktrees);
        if (PathChecks.IsLink(worktrees) || !Directory.Exists(worktrees) || !Native.CanList(worktrees))
        {
            couldntLook(worktrees);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(worktrees).Order(StringComparer.Ordinal))
        {
            // Git keeps only directories here. One Pen can't enter is what git would read once the user fixes the
            // mode, so it can't pass as empty.
            if (!PathChecks.IsLink(entry) && Directory.Exists(entry) && !Native.CanEnter(entry))
                tryMakeListable?.Invoke(entry);
            if (PathChecks.IsLink(entry) || !Path.Exists(entry) || (Directory.Exists(entry) && !Native.CanEnter(entry)))
                couldntLook(entry);
            else if (Directory.Exists(entry))
                visit(entry);
        }
    }
}
