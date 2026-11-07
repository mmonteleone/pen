namespace Pen.Tests;

/// <summary>Finding and protecting the submodule repos kept in a git dir (submodules E1).</summary>
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
        RepoScan.FindModuleRepos(Path.Combine(repo, ".git")).Select(p => RepoGuard.Rel(repo, p)).ToList();

    private static List<(string, bool)> Mounts(string dir) =>
        RepoGuard.PrepareMounts(dir, "/nonexistent-home").Select(m => (RepoGuard.Rel(dir, m.Path), m.ReadOnly)).ToList();

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

    [Fact]
    public void StartStillRefusesSubmodulesInTheProject()
    {
        using var t = new TempDir();
        var super = Super(t);
        Assert.Contains("submodules", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(super)).Message);
    }
}
