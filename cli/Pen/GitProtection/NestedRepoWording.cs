namespace Pen.GitProtection;

/// <summary>
/// Words what makes a repo the scans found one, by its <see cref="NestedKind"/>: for the refusal at session start
/// (after its path) and for the quarantine at session end, in the tree and in the git dir.
/// </summary>
/// <remarks>Each kind's wordings are kept together, so a new kind is worded for every message at once.</remarks>
internal static class NestedRepoWording
{
    private static readonly Wording Bare = new(
        Refused: " (a bare repo)",
        RefusedInGitDir: " (laid out as a git dir of its own)",
        Quarantined: "a bare repo: git takes this directory " +
            "as one, with config and hooks from the sandbox",
        QuarantinedInGitDir: "a directory inside the git dir that git takes for a git dir of its own, where git " +
            "keeps none outside modules/ and worktrees/: git run from it (through a symlink in the project, say) " +
            "uses its config and hooks, which were writable from the sandbox");

    private static readonly Wording GitFile = new(
        Refused: " (a .git file that isn't one of this repo's worktrees or submodules)",
        RefusedInGitDir: " (a .git file that isn't one of this repo's worktrees or submodules)",
        Quarantined: "a .git file that isn't one of this repo's " +
            "worktrees or submodules: it points git at another directory",
        QuarantinedInGitDir: "a .git file inside the git dir that isn't one of this repo's worktrees or " +
            "submodules: git run from that directory (through a symlink in the project, say) follows it to " +
            "another directory");

    private static readonly Wording GitLink = new(
        Refused: " (a .git symlink)",
        RefusedInGitDir: " (a .git symlink)",
        Quarantined: "a .git symlink: it points git at another directory",
        QuarantinedInGitDir: "a .git symlink inside the git dir: git run from that directory (through a " +
            "symlink in the project, say) follows it to another directory");

    private static readonly Wording GitDir = new(
        Refused: "",
        RefusedInGitDir: " (a .git dir)",
        Quarantined: "a nested repo: its config and hooks were writable from the sandbox",
        QuarantinedInGitDir: "a repo inside the git dir, where git keeps none outside modules/: git run from that " +
            "directory (through a symlink in the project, say) uses it, and its config and hooks were writable " +
            "from the sandbox");

    /// <summary>Describes what makes a nested repo one, for the refusal; nothing for a plain .git dir.</summary>
    public static string DescribeRefused(NestedKind kind) => For(kind).Refused;

    /// <summary>Describes what makes a repo in the git dir one, for the refusal.</summary>
    public static string DescribeRefusedInGitDir(NestedRepo repo) => repo switch
    {
        { Place: GitDirPlace.WorktreeEntry, Kind: NestedKind.Bare } =>
            " (a worktree entry whose commondir doesn't lead back to this repo)",
        _ => For(repo.Kind).RefusedInGitDir,
    };

    /// <summary>Words why a nested repo of this kind is quarantined.</summary>
    public static string DescribeQuarantined(NestedKind kind) => For(kind).Quarantined;

    /// <summary>Words why a repo in the git dir is quarantined.</summary>
    public static string DescribeQuarantinedInGitDir(NestedRepo repo) => repo switch
    {
        { Place: GitDirPlace.Refs, Kind: NestedKind.Bare } =>
            "ref names (a branch named HEAD beside ones under objects/ and refs/, say) that lay this directory " +
            "out as a git dir: git run from it (through a symlink in the project, say) takes it for one and uses " +
            "its config and hooks, which were writable from the sandbox. Renaming its HEAD breaks that; rename " +
            "those refs before putting it back",
        _ => For(repo.Kind).QuarantinedInGitDir,
    };

    /// <summary>Gets the wordings for <paramref name="kind"/>; a plain .git dir's for any other value.</summary>
    private static Wording For(NestedKind kind) => kind switch
    {
        NestedKind.Bare => Bare,
        NestedKind.GitFile => GitFile,
        NestedKind.GitLink => GitLink,
        _ => GitDir,
    };

    /// <summary>One kind's wordings.</summary>
    /// <param name="Refused">After its path in the refusal of a repo in the tree.</param>
    /// <param name="RefusedInGitDir">After its path in the refusal of a repo in the git dir.</param>
    /// <param name="Quarantined">Why one in the tree is quarantined.</param>
    /// <param name="QuarantinedInGitDir">Why one in the git dir is quarantined.</param>
    private sealed record Wording(
        string Refused,
        string RefusedInGitDir,
        string Quarantined,
        string QuarantinedInGitDir);
}
