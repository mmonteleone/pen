namespace Pen.Tests;

/// <summary>Scans a project's tree for nested repos the way the start check does, for tests.</summary>
internal static class NestedScans
{
    /// <summary>
    /// Scans the tree below <paramref name="root"/>, taking the checkouts of the submodule repos now in
    /// <paramref name="commonGitDir"/> (other than symlinks) as the project's own, as
    /// <see cref="RepoGuard.CheckAtStart"/> does.
    /// </summary>
    public static NestedScan Scan(string root, string commonGitDir)
    {
        var moduleRepos = RepoScan.FindModuleRepos(commonGitDir).Where(repo => !PathChecks.IsLink(repo)).ToList();
        return RepoScan.ScanNested(root, commonGitDir, moduleRepos, tryOpen: null);
    }

    /// <summary>Finds every repo below <paramref name="root"/>, with root/.git as the common git dir.</summary>
    public static List<NestedRepo> Find(string root) =>
        Scan(root, Path.Combine(root, ".git")).Repos;

    /// <summary>
    /// Finds every repo below <paramref name="root"/>, with root/.git as the common git dir and the checkouts of
    /// <paramref name="moduleRepos"/> as the project's own.
    /// </summary>
    public static List<NestedRepo> Find(string root, IReadOnlyCollection<string> moduleRepos) =>
        RepoScan.ScanNested(root, Path.Combine(root, ".git"), moduleRepos, tryOpen: null).Repos;

    /// <summary>Whether <paramref name="gitFile"/> is the .git file of a worktree of the project itself.</summary>
    public static bool IsProjectWorktree(string root, string gitFile) =>
        RepoScan.ResolveWorktree(gitFile) == Path.Combine(root, ".git");
}
