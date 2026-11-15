namespace Pen.Tests;

/// <summary>
/// Finding and protecting the submodule repos kept in a git dir (submodules
/// E1); accepting their checkouts and quarantining only new ones (E2);
/// starting with submodules in the project (E3).
/// </summary>
public class ModuleRepoTests
{
    private static readonly string[] AllowFile = ["-c", "protocol.file.allow=always"];

    private static void Sub(string dir, params string[] args) => Git.Run(dir, [.. AllowFile, "submodule", .. args]);

    /// <summary>
    /// A repo with the submodule libs/mid (a slash in its name), which has the
    /// nested submodule deps/leaf, both initialized.
    /// </summary>
    private static string Super(TempDir t)
    {
        var leaf = Git.Repo(t.Sub("leaf"));
        var mid = Git.Repo(t.Sub("mid"));
        Sub(mid, "add", "-q", leaf, "deps/leaf");
        Git.Run(mid, "commit", "-q", "-m", "leaf");
        var super = Git.Repo(t.Sub("super"));
        Sub(super, "add", "-q", mid, "libs/mid");
        Git.Run(super, "commit", "-q", "-m", "mid");
        Sub(super, "update", "-q", "--init", "--recursive");
        return super;
    }

    private static List<string> Found(string repo) =>
        RepoScan.FindModuleRepos(Path.Combine(repo, ".git")).Select(p => RepoGuard.FormatPath(repo, p)).ToList();

    private static List<(string, bool)> Mounts(string dir) =>
        RepoGuard.PrepareMounts(dir, "/nonexistent-home").Select(m => (RepoGuard.FormatPath(dir, m.Path), m.ReadOnly)).ToList();

    [Fact]
    public void FindsSlashNamedAndNestedModuleRepos_ParentsFirst()
    {
        using var t = new TempDir();
        var super = Super(t);
        Assert.Equal([".git/modules/libs/mid", ".git/modules/libs/mid/modules/deps/leaf"], Found(super));
    }

    [Fact]
    public void FindsNothingWithoutModules()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Assert.Empty(Found(repo));
        // A directory without a git layout, and a stray file, aren't repos.
        File.WriteAllText(Path.Combine(t.Sub("repo", ".git", "modules", "half"), "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(repo, ".git", "modules", "stray"), "");
        Assert.Empty(Found(repo));
    }

    [Fact]
    public void FindsAWorktreesOwnModuleRepos()
    {
        using var t = new TempDir();
        var super = Super(t);
        Git.Run(super, "worktree", "add", "-q", ".claude/worktrees/w");
        Sub(Path.Combine(super, ".claude", "worktrees", "w"), "update", "-q", "--init", "--recursive");
        Assert.Equal(
            [".git/modules/libs/mid", ".git/modules/libs/mid/modules/deps/leaf",
             ".git/worktrees/w/modules/libs/mid", ".git/worktrees/w/modules/libs/mid/modules/deps/leaf"],
            Found(super));
    }

    [Fact]
    public void ListsSymlinksWithoutFollowingThem()
    {
        using var t = new TempDir();
        var other = Git.Repo(t.Sub("other"));
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateSymbolicLink(Path.Combine(t.Sub("repo", ".git", "modules", "a"), "b"), Path.Combine(other, ".git"));
        Assert.Equal([".git/modules/a/b"], Found(repo));

        var repo2 = Git.Repo(t.Sub("repo2"));
        Directory.CreateSymbolicLink(Path.Combine(repo2, ".git", "modules"), Path.Combine(other, ".git"));
        Assert.Equal([".git/modules"], Found(repo2));
    }

    [Fact]
    public void MountsProtectEachModuleRepo()
    {
        using var t = new TempDir();
        var super = Super(t);
        var leafHooks = Path.Combine(super, ".git", "modules", "libs", "mid", "modules", "deps", "leaf", "hooks");
        Directory.Delete(leafHooks, recursive: true);
        var m = Mounts(super);
        foreach (var r in new[] { ".git/modules/libs/mid", ".git/modules/libs/mid/modules/deps/leaf" })
        {
            Assert.Contains((r, false), m);
            Assert.Contains((r + "/config", true), m);
            Assert.Contains((r + "/hooks", true), m);
            Assert.True(m.IndexOf((r, false)) < m.IndexOf((r + "/config", true)));
        }
        Assert.True(Directory.Exists(leafHooks), "a missing hooks dir is created");
        Assert.True(m.IndexOf((".git/modules/libs/mid", false)) < m.IndexOf((".git/modules/libs/mid/modules/deps/leaf", false)));
    }

    [Fact]
    public void A_ModuleReposWorktreeConfigAndIncludesAreProtectedToo()
    {
        using var t = new TempDir();
        var super = Super(t);
        var mid = Path.Combine(super, ".git", "modules", "libs", "mid");
        Git.Run(Path.Combine(super, "libs", "mid"), "config", "extensions.worktreeConfig", "true");
        Git.Run(Path.Combine(super, "libs", "mid"), "config", "include.path", "local.inc");
        var m = Mounts(super);
        Assert.Contains((".git/modules/libs/mid/config.worktree", true), m);
        Assert.Contains((".git/modules/libs/mid/local.inc", true), m);
        Assert.True(File.Exists(Path.Combine(mid, "local.inc")));
    }

    [Fact]
    public void ModuleReposOfAnInternalWorktreeAreProtected_AnExternalOnesAreReadOnlyWithItsEntry()
    {
        using var t = new TempDir();
        var super = Super(t);
        Git.Run(super, "worktree", "add", "-q", ".claude/worktrees/in");
        Sub(Path.Combine(super, ".claude", "worktrees", "in"), "update", "-q", "--init");
        Git.Run(super, "worktree", "add", "-q", Path.Combine(t.Path, "out"));
        Sub(Path.Combine(t.Path, "out"), "update", "-q", "--init");
        var m = Mounts(super);
        Assert.Contains((".git/worktrees/in/modules/libs/mid", false), m);
        Assert.Contains((".git/worktrees/in/modules/libs/mid/config", true), m);
        Assert.Contains((".git/worktrees/in/modules/libs/mid/hooks", true), m);
        Assert.Contains((".git/worktrees/out", true), m);
        Assert.DoesNotContain(m, x => x.Item1.StartsWith(".git/worktrees/out/", StringComparison.Ordinal));
    }

    [Fact]
    public void A_LinkedWorktreeProjectProtectsTheCommonDirsModuleRepos()
    {
        using var t = new TempDir();
        var super = Super(t);
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(super, "worktree", "add", "-q", wt);
        Sub(wt, "update", "-q", "--init");
        var m = RepoGuard.PrepareMounts(wt, "/nonexistent-home").Select(x => (x.Path, x.ReadOnly)).ToList();
        var common = Path.Combine(super, ".git");
        Assert.Contains((Path.Combine(common, "modules", "libs", "mid", "config"), true), m);
        Assert.Contains((Path.Combine(common, "modules", "libs", "mid", "modules", "deps", "leaf", "hooks"), true), m);
        // The worktree's own entry is visible (writable), so its module repo gets mounts of its own.
        Assert.Contains((Path.Combine(common, "worktrees", "wt", "modules", "libs", "mid"), false), m);
        Assert.Contains((Path.Combine(common, "worktrees", "wt", "modules", "libs", "mid", "config"), true), m);
    }

    [Fact]
    public void RefusesASymlinkedModuleRepo()
    {
        using var t = new TempDir();
        var other = Git.Repo(t.Sub("other"));
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateSymbolicLink(Path.Combine(t.Sub("repo", ".git", "modules"), "x"), Path.Combine(other, ".git"));
        Assert.Contains(".git/modules/x is a symlink", Assert.Throws<PenException>(() => Mounts(repo)).Message);
    }

    [Fact]
    public void RefusesAModuleRepoWithACommondir()
    {
        using var t = new TempDir();
        var super = Super(t);
        File.WriteAllText(Path.Combine(super, ".git", "modules", "libs", "mid", "modules", "deps", "leaf", "commondir"), "/elsewhere\n");
        Assert.Contains(".git/modules/libs/mid/modules/deps/leaf/commondir exists",
            Assert.Throws<PenException>(() => Mounts(super)).Message);

        // HEAD plus commondir alone is a git dir to git, so it's found and refused too.
        using var t2 = new TempDir();
        var repo = Git.Repo(t2.Sub("repo"));
        var fake = t2.Sub("repo", ".git", "modules", "fake");
        File.WriteAllText(Path.Combine(fake, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(fake, "commondir"), "../..\n");
        Assert.Contains(".git/modules/fake/commondir exists", Assert.Throws<PenException>(() => Mounts(repo)).Message);
    }

    // --- E3: submodules in the project itself ---

    [Fact]
    public void StartAcceptsInitializedSubmodules_NestedAndSlashNamed()
    {
        using var t = new TempDir();
        var super = Super(t);
        RepoGuard.CheckAtStart(super);
        // ...and uninitialized ones: .gitmodules alone, module repos without checkouts.
        Sub(super, "deinit", "-q", "-f", "--all");
        Assert.False(File.Exists(Path.Combine(super, "libs", "mid", ".git")));
        RepoGuard.CheckAtStart(super);
    }

    [Fact]
    public void StartRefusesACheckoutPointingAtAnUnknownRepo()
    {
        using var t = new TempDir();
        var super = Super(t);
        var other = Git.Repo(t.Sub("other"));
        File.WriteAllText(Path.Combine(super, "libs", "mid", ".git"), $"gitdir: {Path.Combine(other, ".git")}\n");
        var msg = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(super)).Message;
        Assert.Contains("\n  libs/mid (a .git file that isn't one of this repo's worktrees or submodules)", msg);
        Assert.DoesNotContain("absorbgitdirs", msg);
    }

    [Fact]
    public void StartRefusesASubmoduleWithItsOwnGitDir_SuggestingAbsorbgitdirs()
    {
        using var t = new TempDir();
        var lib = Git.Repo(t.Sub("lib"));
        var super = Git.Repo(t.Sub("super"));
        // A clone in place, then added as is: git keeps its .git dir (the old layout).
        Git.Run(super, "clone", "-q", lib, "libs/x");
        Sub(super, "add", "-q", lib, "libs/x");
        Git.Run(super, "commit", "-q", "-m", "x");
        Assert.True(Directory.Exists(Path.Combine(super, "libs", "x", ".git")));

        var msg = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(super)).Message;
        Assert.Contains("\n  libs/x (a submodule with its own .git dir)", msg);
        Assert.Contains("git submodule absorbgitdirs", msg);
        Assert.DoesNotContain("run pen in each repo instead", msg);

        // With a plain nested repo too, both pieces of advice.
        Git.Run(super, "init", "-q", "other");
        msg = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(super)).Message;
        Assert.Contains("\n  other\n", msg);
        Assert.Contains("git submodule absorbgitdirs", msg);
        Assert.Contains("run pen in each repo instead", msg);
        Directory.Delete(Path.Combine(super, "other"), recursive: true);

        // Doing what it says fixes it.
        Sub(super, "absorbgitdirs");
        Assert.True(File.Exists(Path.Combine(super, "libs", "x", ".git")));
        RepoGuard.CheckAtStart(super);
    }

    [Fact]
    public void StartAcceptsAnExternalWorktreeWithItsOwnSubmodules_ProtectingTheirRepos()
    {
        using var t = new TempDir();
        var super = Super(t);
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(super, "worktree", "add", "-q", wt);
        Sub(wt, "update", "-q", "--init", "--recursive");
        Assert.True(File.Exists(Path.Combine(wt, "libs", "mid", "deps", "leaf", ".git")));
        RepoGuard.CheckAtStart(wt);

        var m = RepoGuard.PrepareMounts(wt, "/nonexistent-home").Select(x => (x.Path, x.ReadOnly)).ToList();
        var mods = Path.Combine(super, ".git", "worktrees", "wt", "modules", "libs", "mid");
        Assert.Contains((Path.Combine(mods, "config"), true), m);
        Assert.Contains((Path.Combine(mods, "hooks"), true), m);
        Assert.Contains((Path.Combine(mods, "modules", "deps", "leaf", "config"), true), m);
        // The main checkout's module repos are protected too: the main checkout uses them.
        Assert.Contains((Path.Combine(super, ".git", "modules", "libs", "mid", "config"), true), m);
    }

    [Fact]
    public void IsSubmodulePath_ReadsTheGitmodulesOfEachSuperproject()
    {
        using var t = new TempDir();
        var root = t.Sub("root");
        File.WriteAllText(Path.Combine(root, ".gitmodules"), "[submodule \"a\"]\n\tpath = libs/a/\n");
        File.WriteAllText(Path.Combine(t.Sub("root", "libs", "a"), ".gitmodules"), "[submodule \"b\"]\n\tpath = deps/b\n");
        Assert.True(RepoGuard.IsSubmodulePath(root, Path.Combine(root, "libs", "a")));
        Assert.True(RepoGuard.IsSubmodulePath(root, Path.Combine(root, "libs", "a", "deps", "b")));
        Assert.False(RepoGuard.IsSubmodulePath(root, Path.Combine(root, "libs", "c")));
        Assert.False(RepoGuard.IsSubmodulePath(root, Path.Combine(root, "libs", "a", "deps")));
        // .gitmodules above the project doesn't count.
        File.WriteAllText(Path.Combine(t.Path, ".gitmodules"), "[submodule \"r\"]\n\tpath = root/x\n");
        Assert.False(RepoGuard.IsSubmodulePath(root, Path.Combine(root, "x")));
    }

    // --- E2: submodule checkouts in the tree, and the session-end check ---

    private static List<string> Nested(string repo, IReadOnlyCollection<string>? modules = null) =>
        (modules == null ? NestedScans.Find(repo) : NestedScans.Find(repo, modules)).Select(n => RepoGuard.FormatPath(repo, n.Path)).ToList();

    private static void SetWorktree(string super, string module, string value) =>
        Git.Run(super, "config", "-f", Path.Combine(super, ".git", "modules", module, "config"), "core.worktree", value);

    [Fact]
    public void SubmoduleCheckoutsAreTheProjectsOwn()
    {
        using var t = new TempDir();
        var super = Super(t);
        Assert.True(File.Exists(Path.Combine(super, "libs", "mid", ".git")));
        Assert.True(File.Exists(Path.Combine(super, "libs", "mid", "deps", "leaf", ".git")));
        Assert.Empty(Nested(super));
        // Known only by the list given: with none, both checkouts are flagged.
        Assert.Equal(["libs/mid/.git", "libs/mid/deps/leaf/.git"], Nested(super, []));
    }

    [Fact]
    public void A_ForgedCoreWorktreeIsFlagged()
    {
        using var t = new TempDir();
        var super = Super(t);
        SetWorktree(super, "libs/mid", Path.Combine(super, "elsewhere"));
        Directory.CreateDirectory(Path.Combine(super, "elsewhere"));
        Assert.Equal(["libs/mid/.git"], Nested(super));

        // One that doesn't resolve, or is missing, is flagged too.
        SetWorktree(super, "libs/mid", "../../../nonexistent");
        Assert.Equal(["libs/mid/.git"], Nested(super));
        Git.Run(super, "config", "-f", Path.Combine(super, ".git", "modules", "libs", "mid", "config"), "--unset", "core.worktree");
        Assert.Equal(["libs/mid/.git"], Nested(super));
    }

    [Fact]
    public void A_GitFilePointingAtAnotherModuleOrNotAtOneIsFlagged()
    {
        using var t = new TempDir();
        var super = Super(t);
        // The leaf's checkout pointed at mid's repo: mid's core.worktree isn't the leaf's dir.
        var leafGit = Path.Combine(super, "libs", "mid", "deps", "leaf", ".git");
        File.WriteAllText(leafGit, $"gitdir: {Path.Combine(super, ".git", "modules", "libs", "mid")}\n");
        Assert.Equal(["libs/mid/deps/leaf/.git"], Nested(super));

        // A copy of mid's .git file elsewhere: right repo, wrong dir.
        using var t2 = new TempDir();
        var super2 = Super(t2);
        var copy = Directory.CreateDirectory(Path.Combine(super2, "copy")).FullName;
        File.WriteAllText(Path.Combine(copy, ".git"), $"gitdir: {Path.Combine(super2, ".git", "modules", "libs", "mid")}\n");
        Assert.Equal(["copy/.git"], Nested(super2));

        // A repo in modules/ that's a quarantined one's nested repo isn't a module repo.
        using var t3 = new TempDir();
        var super3 = Super(t3);
        var mid3 = Path.Combine(super3, ".git", "modules", "libs", "mid");
        Directory.Move(mid3, mid3 + GitCheck.Suffix);
        Assert.Empty(Found(super3));
    }

    [Fact]
    public void A_GitFileReachingTheModuleRepoThroughASymlinkIsFlagged()
    {
        using var t = new TempDir();
        var super = Super(t);
        Directory.CreateSymbolicLink(Path.Combine(super, ".git", "mods"), Path.Combine(super, ".git", "modules"));
        File.WriteAllText(Path.Combine(super, "libs", "mid", ".git"), "gitdir: ../../.git/mods/libs/mid\n");
        Assert.Equal(["libs/mid/.git"], Nested(super));
    }

    [Fact]
    public void StartAcceptsAnInitializedSubmoduleInAnInternalWorktree()
    {
        using var t = new TempDir();
        var lib = Git.Repo(t.Sub("lib"));
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "-b", "feat", ".claude/worktrees/w");
        var w = Path.Combine(repo, ".claude", "worktrees", "w");
        Sub(w, "add", "-q", lib, "libs/x");
        Git.Run(w, "commit", "-q", "-m", "x");
        Assert.True(File.Exists(Path.Combine(w, "libs", "x", ".git")));
        RepoGuard.CheckAtStart(repo); // no .gitmodules in the main checkout, so only the checkout's .git file is in question
    }

    [Fact]
    public void MountPointsBetweenTheGitDirAndEachModuleRepo()
    {
        using var t = new TempDir();
        var super = Super(t);
        Git.Run(super, "worktree", "add", "-q", ".claude/worktrees/in");
        Sub(Path.Combine(super, ".claude", "worktrees", "in"), "update", "-q", "--init");
        var m = Mounts(super);
        foreach (var p in new[]
        {
            ".git/modules", ".git/modules/libs",
            ".git/modules/libs/mid/modules", ".git/modules/libs/mid/modules/deps",
            ".git/worktrees/in", ".git/worktrees/in/modules", ".git/worktrees/in/modules/libs",
        })
            Assert.Contains((p, false), m);
        Assert.Contains((".git/worktrees/in/commondir", true), m);
        Assert.DoesNotContain(m, x => x == (".git/modules", true) || x == (".git/worktrees/in", true));
    }

    [Fact]
    public void EndQuarantinesOnlyModuleReposMadeDuringTheSession()
    {
        using var t = new TempDir();
        var super = Super(t);
        var atStart = RepoScan.FindModuleRepos(Path.Combine(super, ".git"));

        // During the session: a new submodule, and one nested in an existing one.
        var other = Git.Repo(t.Sub("other"));
        Sub(super, "add", "-q", other, "libs/new");
        Sub(Path.Combine(super, "libs", "mid"), "add", "-q", other, "deps/new");

        var moved = GitCheck.RunWithoutOpening(super, startedWithGit: true, commonGitDir: null, modulesAtStart: atStart).Moved;
        Assert.Equal(
            [".git/modules/libs/mid/modules/deps/new", ".git/modules/libs/new", "libs/mid/deps/new/.git", "libs/new/.git"],
            moved.Select(q => RepoGuard.FormatPath(super, q.From)).Order(StringComparer.Ordinal));
        Assert.All(moved, q => Assert.NotEqual("", q.To));
        Assert.Equal(atStart, RepoScan.FindModuleRepos(Path.Combine(super, ".git")));
        Assert.True(File.Exists(Path.Combine(super, "libs", "mid", ".git")));
        Assert.Empty(GitCheck.RunWithoutOpening(super, startedWithGit: true, commonGitDir: null, modulesAtStart: atStart).Moved);
    }

    [Fact]
    public void EndQuarantinesANewRepoWithItsNestedOnes()
    {
        using var t = new TempDir();
        var super = Super(t);
        var mid = Path.Combine(super, ".git", "modules", "libs", "mid");
        var moved = GitCheck.RunWithoutOpening(super, startedWithGit: true, commonGitDir: null, modulesAtStart: []).Moved;
        // mid goes, and leaf with it: only one move for the pair, then both checkouts.
        Assert.Equal([".git/modules/libs/mid", "libs/mid/.git", "libs/mid/deps/leaf/.git"],
            moved.Select(q => RepoGuard.FormatPath(super, q.From)).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(mid));
        Assert.Empty(RepoScan.FindModuleRepos(Path.Combine(super, ".git")));
    }

    [Fact]
    public void EndQuarantinesACommondirInAKnownModuleRepo_AndAModuleSymlink()
    {
        using var t = new TempDir();
        var super = Super(t);
        var git = Path.Combine(super, ".git");
        var atStart = RepoScan.FindModuleRepos(git);
        File.WriteAllText(Path.Combine(git, "modules", "libs", "mid", "commondir"), "/elsewhere\n");
        var other = Git.Repo(t.Sub("other"));
        Directory.CreateSymbolicLink(Path.Combine(git, "modules", "evil"), Path.Combine(other, ".git"));

        var moved = GitCheck.RunWithoutOpening(super, startedWithGit: true, commonGitDir: null, modulesAtStart: atStart).Moved;
        Assert.Equal([".git/modules/evil", ".git/modules/libs/mid/commondir"],
            moved.Select(q => RepoGuard.FormatPath(super, q.From)).Order(StringComparer.Ordinal));
        // With the commondir gone, mid's checkout is still the project's own.
        Assert.True(File.Exists(Path.Combine(super, "libs", "mid", ".git")));
    }

    [Fact]
    public void SignKeepsTheModuleReposThereNow()
    {
        using var t = new TempDir();
        var super = Super(t);
        Assert.Empty(GitCheck.RunWithoutOpening(super, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved);
    }

    [Fact]
    public void SessionDirSavesTheModuleRepos()
    {
        using var t = new TempDir();
        var s = new SessionDir("pen-x-00000000", t.Sub("s"));
        Assert.Null(s.ReadModules());
        s.WriteModules([]);
        Assert.Equal([], s.ReadModules()!);
        s.WriteModules(["/r/.git/modules/a", "/r/.git/modules/b\nc"]);
        Assert.Equal(["/r/.git/modules/a", "/r/.git/modules/b\nc"], s.ReadModules()!);
    }

    [Fact]
    public void ModuleCheckoutNamedThroughASymlink_IsntVouchedFor_EvenIfListed()
    {
        using var t = new TempDir();
        var real = t.Sub("gitdir", "modules", "m");
        File.WriteAllText(Path.Combine(real, "config"), "[core]\n\tworktree = ../../../checkout\n");
        var gitdir = Path.Combine(t.Path, "gitdir");
        Directory.CreateSymbolicLink(Path.Combine(gitdir, "mods"), Path.Combine(gitdir, "modules"));
        var viaLink = Path.Combine(t.Path, "gitdir", "mods", "m");
        var gitFile = Path.Combine(t.Sub("checkout"), ".git");
        File.WriteAllText(gitFile, $"gitdir: {real}\n");
        var listedReal = new HashSet<string>(StringComparer.Ordinal) { real };
        Assert.Equal(real, RepoScan.ResolveModuleCheckout(gitFile, listedReal));
        File.WriteAllText(gitFile, $"gitdir: {viaLink}\n");
        var listedViaLink = new HashSet<string>(StringComparer.Ordinal) { viaLink };
        Assert.Null(RepoScan.ResolveModuleCheckout(gitFile, listedViaLink));
    }
}
