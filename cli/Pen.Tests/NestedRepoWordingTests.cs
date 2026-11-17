namespace Pen.Tests;

/// <summary>The nested-repo wordings are output users read and tests elsewhere match, so they're pinned exactly.</summary>
public class NestedRepoWordingTests
{
    private const string BareQuarantinedInGitDir =
        "a directory inside the git dir that git takes for a git dir of its own, where git keeps none outside " +
        "modules/ and worktrees/: git run from it (through a symlink in the project, say) uses its config and " +
        "hooks, which were writable from the sandbox";

    private const string GitFileQuarantinedInGitDir =
        "a .git file inside the git dir that isn't one of this repo's worktrees or submodules: git run from that " +
        "directory (through a symlink in the project, say) follows it to another directory";

    private const string GitLinkQuarantinedInGitDir =
        "a .git symlink inside the git dir: git run from that directory (through a symlink in the project, say) " +
        "follows it to another directory";

    private const string GitDirQuarantinedInGitDir =
        "a repo inside the git dir, where git keeps none outside modules/: git run from that directory (through a " +
        "symlink in the project, say) uses it, and its config and hooks were writable from the sandbox";

    private static readonly (
        NestedKind Kind, string Refused, string RefusedInGitDir, string Quarantined, string QuarantinedInGitDir)[]
        Wordings =
    [
        (
            NestedKind.GitDir, "", " (a .git dir)",
            "a nested repo: its config and hooks were writable from the sandbox", GitDirQuarantinedInGitDir
        ),
        (
            NestedKind.GitFile,
            " (a .git file that isn't one of this repo's worktrees or submodules)",
            " (a .git file that isn't one of this repo's worktrees or submodules)",
            "a .git file that isn't one of this repo's worktrees or submodules: it points git at another directory",
            GitFileQuarantinedInGitDir
        ),
        (
            NestedKind.GitLink, " (a .git symlink)", " (a .git symlink)",
            "a .git symlink: it points git at another directory", GitLinkQuarantinedInGitDir
        ),
        (
            NestedKind.Bare, " (a bare repo)", " (laid out as a git dir of its own)",
            "a bare repo: git takes this directory as one, with config and hooks from the sandbox",
            BareQuarantinedInGitDir
        ),
    ];

    [Fact]
    public void EachKindIsWordedExactly()
    {
        foreach (var (kind, refused, refusedInGitDir, quarantined, quarantinedInGitDir) in Wordings)
        {
            var repo = new NestedRepo("/p/x/.git", kind);
            Assert.Equal(refused, NestedRepoWording.DescribeRefused(kind));
            Assert.Equal(refusedInGitDir, NestedRepoWording.DescribeRefusedInGitDir(repo));
            Assert.Equal(quarantined, NestedRepoWording.DescribeQuarantined(kind));
            Assert.Equal(quarantinedInGitDir, NestedRepoWording.DescribeQuarantinedInGitDir(repo));
        }
    }

    [Fact]
    public void EveryKindIsPinned() =>
        Assert.Equal(
            Enum.GetValues<NestedKind>().Order(), Wordings.Select(w => w.Kind).Order());

    [Fact]
    public void ABareRepoInTheGitDirIsWordedByPlace()
    {
        var entry = new NestedRepo("/g/worktrees/w", NestedKind.Bare, GitDirRegion.WorktreeEntry);
        var refs = new NestedRepo("/g/refs/x", NestedKind.Bare, GitDirRegion.Refs);

        Assert.Equal(
            " (a worktree entry whose commondir doesn't lead back to this repo)",
            NestedRepoWording.DescribeRefusedInGitDir(entry));
        Assert.Equal(BareQuarantinedInGitDir, NestedRepoWording.DescribeQuarantinedInGitDir(entry));
        Assert.Equal(" (laid out as a git dir of its own)", NestedRepoWording.DescribeRefusedInGitDir(refs));
        Assert.Equal(
            "ref names (a branch named HEAD beside ones under objects/ and refs/, say) that lay this directory out " +
            "as a git dir: git run from it (through a symlink in the project, say) takes it for one and uses its " +
            "config and hooks, which were writable from the sandbox. Renaming its HEAD breaks that; rename those " +
            "refs before putting it back",
            NestedRepoWording.DescribeQuarantinedInGitDir(refs));
    }
}
