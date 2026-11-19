using System.Diagnostics;

namespace Pen.Tests;

/// <summary>
/// A repo the agent puts inside the git dir where git keeps none, which host git run from that directory (through a
/// symlink in the project, say) would use: refused at the start and quarantined at the end, as is a symlink into the
/// git dir. Each replay first shows that git really runs the planted fsmonitor.
/// </summary>
public class GitDirRepoTests
{
    private const string Key = "pen-test-12345678";
    private const UnixFileMode Dir755 = (UnixFileMode)0b111_101_101;
    private static readonly string[] AllowFile = ["-c", "protocol.file.allow=always"];

    private static SessionDir Started(TempDir t, string dir, string common, string id = "aaaaaaaa")
    {
        RepoGuard.CheckAtStart(dir);
        // As SessionRunner does, with no other session open: every module repo found is listed.
        var lists = ModuleBaseline.ReadOpenLists([], common, dir);
        var mounts = new MountPlanner(dir, "/nonexistent-home").EnsureTargetsAndPlan();
        var found = RepoScan.FindModuleRepos(common);
        var modules = ModuleBaseline.Choose(found, lists);
        var s = new SessionDir(Key, t.Sub("runtime", "sessions", id));
        File.WriteAllText(s.StartedWithGit, common + "\n");
        s.WriteModules(modules);
        ProtectedFiles.Save(s, mounts, dir, [], found.Except(modules, StringComparer.Ordinal).ToList());
        return s;
    }

    /// <summary>A repo with a submodule m (its repo in .git/modules/m) and a worktree w outside it.</summary>
    private static (string Repo, string Git) RepoWithModuleAndWorktree(TempDir t)
    {
        var lib = Git.Repo(t.Sub("lib"));
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, [.. AllowFile, "submodule", "add", "-q", lib, "m"]);
        Git.Run(repo, "commit", "-q", "-m", "m");
        Git.Run(repo, "worktree", "add", "-q", "--detach", Path.Combine(t.Path, "w"));
        return (repo, Path.Combine(repo, ".git"));
    }

    /// <summary>Runs a shell script in <paramref name="dir"/>, skipping where it needs a name the file system refuses.</summary>
    private static void Sh(string dir, string script)
    {
        var psi = new ProcessStartInfo("sh", ["-c", script])
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var r = Process.RunAndCaptureText(psi);
        Assert.SkipWhen(r.ExitStatus.ExitCode != 0
            && r.StandardError.Contains("llegal byte sequence", StringComparison.Ordinal),
            "the file system refuses names that aren't valid UTF-8");
        Assert.True(r.ExitStatus.ExitCode == 0, r.StandardError);
    }

    private static (string Hook, string Marker) Canary(TempDir t)
    {
        var marker = Path.Combine(t.Path, "ran");
        var hook = Path.Combine(t.Path, "hook.sh");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, Dir755);
        return (hook, marker);
    }

    /// <summary>Runs git status in <paramref name="dir"/>, whether or not git finds a repo there.</summary>
    private static void Status(string dir)
    {
        try
        {
            Git.Run(dir, "status");
        }
        catch (InvalidOperationException)
        {
            // No work tree there once the plant is gone: nothing to run.
        }
    }

    private static void AssertFires(string dir, string marker)
    {
        File.Delete(marker);
        Status(dir);
        Assert.True(File.Exists(marker), "the replay isn't real: the planted fsmonitor didn't run before the check");
        File.Delete(marker);
    }

    private static void AssertDoesntFire(string dir, string marker)
    {
        File.Delete(marker);
        Status(dir);
        Assert.False(File.Exists(marker), "the planted fsmonitor ran");
    }

    /// <summary>
    /// A config whose fsmonitor git runs on status. Version 0 makes git honour core.worktree in a git dir it finds
    /// by its layout alone.
    /// </summary>
    private static string PlantConfig(string hook, string worktree) =>
        $"[core]\n\trepositoryformatversion = 0\n\tbare = false\n\tworktree = {worktree}\n\tfsmonitor = {hook}\n";

    /// <summary>Makes <paramref name="dir"/> a git dir by its layout (is_git_directory), with that config.</summary>
    private static string Layout(string dir, string config)
    {
        Directory.CreateDirectory(Path.Combine(dir, "objects"));
        Directory.CreateDirectory(Path.Combine(dir, "refs"));
        File.WriteAllText(Path.Combine(dir, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(dir, "config"), config);
        return dir;
    }

    /// <summary>
    /// Makes <paramref name="dir"/> a git dir by a layout git takes too: objects and refs that are executable files
    /// (they pass access(X_OK)), or a HEAD that's a symlink to refs/heads, a directory.
    /// </summary>
    /// <returns>The HEAD, which the end check moves.</returns>
    private static string OtherLayout(string dir, string form, string config)
    {
        Directory.CreateDirectory(dir);
        if (form == "bare exec files")
        {
            foreach (var name in new[] { "objects", "refs" })
            {
                File.WriteAllText(Path.Combine(dir, name), "");
                File.SetUnixFileMode(Path.Combine(dir, name), Dir755);
            }
            File.WriteAllText(Path.Combine(dir, "HEAD"), "ref: refs/heads/main\n");
        }
        else // "bare head symlink"
        {
            Directory.CreateDirectory(Path.Combine(dir, "objects"));
            Directory.CreateDirectory(Path.Combine(dir, "refs", "heads"));
            File.CreateSymbolicLink(Path.Combine(dir, "HEAD"), "refs/heads");
        }
        File.WriteAllText(Path.Combine(dir, "config"), config);
        return Path.Combine(dir, "HEAD");
    }

    /// <summary>
    /// Plants one form of repo at <paramref name="position"/> in the git dir, whose config runs the hook.
    /// </summary>
    /// <returns>What the end check should move: the .git, or the HEAD of a directory git takes for a git dir.</returns>
    private static string Plant(TempDir t, string git, string form, string position, string hook)
    {
        var dir = Path.Combine(git, position);
        Directory.CreateDirectory(dir);
        var worktree = t.Sub("plant-worktree");
        var dotGit = Path.Combine(dir, ".git");
        switch (form)
        {
            case "git dir":
                Layout(dotGit, PlantConfig(hook, worktree));
                return dotGit;
            case "gitfile":
                File.WriteAllText(dotGit, $"gitdir: {Layout(t.Sub("forged"), PlantConfig(hook, worktree))}\n");
                return dotGit;
            case "git symlink":
                Directory.CreateSymbolicLink(dotGit, Layout(t.Sub("forged"), PlantConfig(hook, worktree)));
                return dotGit;
            case "bare":
                Layout(dir, PlantConfig(hook, worktree));
                return Path.Combine(dir, "HEAD");
            case "bare exec files" or "bare head symlink":
                return OtherLayout(dir, form, PlantConfig(hook, worktree));
            default: // "commondir": git takes the work tree from the entry's config.worktree
                var common = Layout(t.Sub("forged-common"), "[core]\n\trepositoryformatversion = 1\n\tbare = false\n" +
                    $"\tfsmonitor = {hook}\n[extensions]\n\tworktreeConfig = true\n");
                File.WriteAllText(Path.Combine(dir, "HEAD"), "ref: refs/heads/main\n");
                File.WriteAllText(Path.Combine(dir, "commondir"), common + "\n");
                File.WriteAllText(Path.Combine(dir, "config.worktree"), $"[core]\n\tworktree = {worktree}\n");
                return Path.Combine(dir, "HEAD");
        }
    }

    // ---- The replays ----

    [Theory]
    [InlineData("git dir", "")]
    [InlineData("git dir", "foo")]
    [InlineData("git dir", "foo/a/b/c/d")]
    [InlineData("git dir", "modules/m/foo")]
    [InlineData("git dir", "worktrees/w/foo")]
    [InlineData("git dir", "logs/refs/heads")]
    [InlineData("gitfile", "")]
    [InlineData("gitfile", "foo/bar")]
    [InlineData("gitfile", "modules/m/foo")]
    [InlineData("git symlink", "")]
    [InlineData("git symlink", "foo/bar")]
    [InlineData("git symlink", "worktrees/w/foo")]
    [InlineData("bare", "foo/bar")]
    [InlineData("bare", "foo/a/b/c/d")]
    [InlineData("bare", "modules/m/foo")]
    [InlineData("bare", "worktrees/w/foo")]
    [InlineData("bare exec files", "foo/bar")]
    [InlineData("bare exec files", "modules/m/foo")]
    [InlineData("bare head symlink", "foo/bar")]
    [InlineData("bare head symlink", "worktrees/w/foo")]
    [InlineData("git dir", "worktrees/w/objects/aa")]
    [InlineData("bare", "objects/ab")] // the object stores are walked too
    [InlineData("git dir", "objects/ab")]
    [InlineData("git dir", "lfs/objects/ab/cd")]
    [InlineData("bare", "lfs/objects/ab/cd")]
    [InlineData("bare", "annex/objects/Xk/Pq")]
    [InlineData("gitfile", "annex/objects/Xk/Pq/KEY")]
    [InlineData("bare head symlink", "modules/m/objects/ab")]
    [InlineData("git dir", "modules/m/lfs/objects/ab/cd")]
    [InlineData("bare", "x.pen-quarantine/y")] // and what Pen put aside, whatever its name
    [InlineData("git dir", "foo/.git.pen-quarantine/bar")]
    [InlineData("bare", "modules/old.pen-quarantine/y")]
    [InlineData("commondir", "foo/bar")]
    [InlineData("commondir", "worktrees/evil")]
    [InlineData("commondir", "modules/m/worktrees/evil")]
    public void Replay_ARepoInTheGitDir_IsRefusedAtTheStart_AndQuarantinedAtTheEnd(string form, string position)
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var plant = Plant(t, git, form, position, hook);
        var dir = Path.Combine(git, position);
        AssertFires(dir, marker);

        Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo));
        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == plant && q is Quarantined.Moved);
        AssertDoesntFire(dir, marker);
        Assert.Contains("inside the git dir", GitCheckWording.Warning(repo, check.Moved));
        RepoGuard.CheckAtStart(repo);
    }

    /// <summary>Asserts that the end check removed <paramref name="link"/> rather than renaming it.</summary>
    /// <returns>The removal.</returns>
    private static Quarantined AssertRemoved(string dir, CheckResult check, string link, string target)
    {
        var removed = Assert.IsType<Quarantined.LinkRemoved>(Assert.Single(check.Moved, q => q.From == link));
        Assert.Equal(target, removed.LinkTarget);
        Assert.Contains("a symlink into the git dir", removed.Why);
        Assert.False(PathChecks.ExistsOrIsLink(link));
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(link)!, Path.GetFileName(link) + "*"));
        var warning = GitCheckWording.Warning(dir, check.Moved);
        Assert.Contains($"{PathChecks.FormatPath(dir, link)} (a symlink to {target}) removed", warning);
        Assert.Contains($"  ln -s {Shell.Quote(target)} {Shell.Quote(link)}\n", warning + "\n");
        return removed;
    }

    [Theory]
    [InlineData("")]
    [InlineData("foo")]
    [InlineData("modules/m")]
    public void Replay_ASymlinkIntoTheGitDir_IsRefusedAtTheStart_AndRemovedAtTheEnd(string target)
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var docs = Path.Combine(repo, "docs");
        var to = Directory.CreateDirectory(Path.Combine(git, target)).FullName.TrimEnd('/');
        Directory.CreateSymbolicLink(docs, to);
        // The link alone is refused.
        Assert.Contains("these symlinks lead into its git dir", Assert.Throws<PenException>(() =>
            RepoGuard.CheckAtStart(repo)).Message);
        var plant = Plant(t, git, "git dir", target, hook);
        AssertFires(docs, marker);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == plant && q is Quarantined.Moved);
        AssertRemoved(repo, check, docs, to);
        AssertDoesntFire(Path.Combine(git, target), marker);
        RepoGuard.CheckAtStart(repo);
    }

    /// <summary>
    /// A symlink of any name leads into the git dir, where what it reaches is found by the walk of the whole git dir,
    /// in an object store too, or in a directory git run through it looks in next.
    /// </summary>
    [Theory]
    [InlineData("docs.pen-quarantine", "x.pen-quarantine", "bare", "x.pen-quarantine", null)]
    [InlineData("docs.pen-quarantine", "objects/ab", "git dir", "objects/ab", null)]
    [InlineData("docs.pen-quarantine", "foo", "bare exec files", "foo", null)]
    [InlineData("docs", "foo", "git dir", "objects/zz/q", "objects/zz/q")] // .git/foo -> objects/zz/q
    [InlineData("docs", "objects/zz/q", "git dir", "objects/zz", null)] // git looks in objects/zz/.git next
    [InlineData("docs", "lfs/objects/ab", "bare head symlink", "lfs/objects/ab", null)]
    [InlineData("docs", "modules/m/objects/ab", "gitfile", "modules/m/objects/ab", null)]
    [InlineData("sub/.git", "objects/ab", "bare", "objects/ab", null)] // git takes it for sub's git dir
    public void Replay_WhatASymlinkLeadsTo_IsQuarantinedWhateverItsName_AndTheSymlinkIsRemoved(
        string link, string target, string form, string position, string? inGitDirLinkTo)
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var plant = Plant(t, git, form, position, hook);
        var to = Path.Combine(git, target);
        Directory.CreateDirectory(Path.Combine(git, position));
        if (inGitDirLinkTo != null)
            Directory.CreateSymbolicLink(to, inGitDirLinkTo);
        else
            Directory.CreateDirectory(to);
        var path = Path.Combine(repo, link);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Directory.CreateSymbolicLink(path, to);
        var gitRunsIn = Path.GetFileName(link) == ".git" ? Path.GetDirectoryName(path)! : path;
        AssertFires(gitRunsIn, marker);
        // What it reaches is named at the start too.
        var refusal = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message;
        Assert.Contains(PathChecks.FormatPath(repo, Path.GetDirectoryName(plant)!), refusal);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == plant && q is Quarantined.Moved);
        AssertRemoved(repo, check, path, to);
        AssertDoesntFire(Native.RealPath(to)!, marker);
        RepoGuard.CheckAtStart(repo);
    }

    /// <summary>Git takes these layouts for a git dir too, so in the tree they're bare repos.</summary>
    [Theory]
    [InlineData("bare exec files")]
    [InlineData("bare head symlink")]
    public void Replay_ALayoutGitTakesForAGitDir_InTheTree_IsRefusedAtTheStart_AndQuarantinedAtTheEnd(string form)
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var sub = Path.Combine(repo, "p", "sub");
        var head = OtherLayout(sub, form, PlantConfig(hook, t.Sub("plant-worktree")));
        AssertFires(sub, marker);

        Assert.Contains("p/sub (a bare repo)", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message);
        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == head && q is Quarantined.Moved);
        AssertDoesntFire(sub, marker);
        RepoGuard.CheckAtStart(repo);
    }

    /// <summary>Ref names can lay a directory out as a git dir; git takes it for one, so it's moved, saying so.</summary>
    [Fact]
    public void Replay_RefNamesThatLayOutAGitDir_AreQuarantined_SayingWhatTheyAre()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        Git.Run(repo, "update-ref", "refs/remotes/origin/main", "HEAD");
        var s = Started(t, repo, git);
        Git.Run(repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        Git.Run(repo, "update-ref", "refs/remotes/origin/refs/x", "HEAD");
        Git.Run(repo, "update-ref", "refs/remotes/origin/objects/y", "HEAD");
        var origin = Path.Combine(git, "refs", "remotes", "origin");
        File.WriteAllText(Path.Combine(origin, "config"), PlantConfig(hook, t.Sub("plant-worktree")));
        AssertFires(origin, marker);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        var moved = Assert.Single(check.Moved, q => q.From == Path.Combine(origin, "HEAD"));
        Assert.StartsWith("ref names (a branch named HEAD beside ones under objects/ and refs/, say)", moved.Why);
        Assert.Contains("rename those refs before putting it back", moved.Why);
        AssertDoesntFire(origin, marker);
    }

    [Fact]
    public void Replay_InALinkedWorktree_TheMainGitDirAndItsOwnEntryAreChecked()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var wt = Path.Combine(t.Path, "w");
        var s = Started(t, wt, git);
        var plant = Plant(t, git, "git dir", "foo", hook);
        var docs = Path.Combine(wt, "docs");
        Directory.CreateSymbolicLink(docs, Path.Combine(git, "foo"));
        var entry = Path.Combine(wt, "entry");
        Directory.CreateSymbolicLink(entry, Path.Combine(git, "worktrees", "w"));
        AssertFires(docs, marker);
        Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt));

        var check = GitCheck.RunAtSessionEnd(wt, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == plant);
        Assert.Contains(check.Moved, q => q.From == docs && q is Quarantined.LinkRemoved);
        Assert.Contains(check.Moved, q => q.From == entry && q is Quarantined.LinkRemoved);
        AssertDoesntFire(Path.Combine(git, "foo"), marker);
        RepoGuard.CheckAtStart(wt);
        RepoGuard.CheckAtStart(repo);
    }

    [Fact]
    public void Replay_PenSignsCheck_QuarantinesItToo()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var plant = Plant(t, git, "git dir", "foo/bar", hook);
        var docs = Path.Combine(repo, "docs");
        Directory.CreateSymbolicLink(docs, Path.Combine(git, "foo", "bar"));
        AssertFires(docs, marker);

        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == plant);
        Assert.Contains(check.Moved, q => q.From == docs && q is Quarantined.LinkRemoved);
        AssertDoesntFire(Path.Combine(git, "foo", "bar"), marker);
    }

    /// <summary>
    /// A .git directory's contents aren't walked as the tree's are, but once it's moved aside, to a name the agent
    /// can predict, git can be run in them through a symlink: so they're walked then, in the same check.
    /// </summary>
    [Fact]
    public void Replay_ARepoInsideARepoInTheGitDir_IsQuarantinedOnceTheOuterOneIsMovedAside()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var outer = Plant(t, git, "git dir", "foo", hook);
        Layout(Path.Combine(outer, "x", ".git"), PlantConfig(hook, t.Sub("plant-worktree")));
        Layout(Path.Combine(outer, "y", "z"), PlantConfig(hook, t.Sub("plant-worktree")));
        var docs = Path.Combine(repo, "docs");
        Directory.CreateSymbolicLink(docs, Path.Combine(outer + GitQuarantine.Suffix, "x")); // leads nowhere yet
        Directory.CreateDirectory(outer + GitQuarantine.Suffix);
        AssertFires(Path.Combine(outer, "x"), marker);
        AssertFires(Path.Combine(outer, "y", "z"), marker);
        Directory.Delete(outer + GitQuarantine.Suffix);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        var aside = outer + GitQuarantine.Suffix;
        Assert.Contains(check.Moved, q => q.From == outer && q is Quarantined.Moved m && m.To == aside);
        Assert.Contains(check.Moved, q => q.From == Path.Combine(aside, "x", ".git"));
        Assert.Contains(check.Moved, q => q.From == Path.Combine(aside, "y", "z", "HEAD"));
        Assert.Contains(check.Moved, q => q.From == docs && q is Quarantined.LinkRemoved);
        AssertDoesntFire(Path.Combine(aside, "x"), marker);
        AssertDoesntFire(Path.Combine(aside, "y", "z"), marker);
        RepoGuard.CheckAtStart(repo);
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, Started(t, repo, git, "bbbbbbbb")).Moved);
    }

    // ---- What the walk of the git dir finds, and skips ----

    private static List<string> Found(string git, IReadOnlyCollection<string>? modules = null) =>
        RepoScan.ScanGitDir(git, modules ?? RepoScan.FindModuleRepos(git), tryMakeListable: null).Repos
            .Select(r => Path.GetRelativePath(git, r.Path)).ToList();

    [Fact]
    public void TheObjectStores_AreWalked_OfTheGitDirItsModuleReposAndItsWorktreeEntries()
    {
        using var t = new TempDir();
        var (_, git) = RepoWithModuleAndWorktree(t);
        string[] stores =
        [
            "annex/objects/aa", "lfs/objects/aa", "modules/m/annex/objects/aa", "modules/m/lfs/objects/aa",
            "modules/m/objects/aa", "objects", "objects/aa", "worktrees/w/lfs/objects/aa", "worktrees/w/objects/aa",
        ];
        foreach (var store in stores)
            Directory.CreateDirectory(Path.Combine(git, store, ".git"));

        Assert.Equal(stores.Select(store => store + "/.git"), Found(git).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void AnObjectsDirOfADirectoryThatIsntOne_IsWalked()
    {
        using var t = new TempDir();
        var (_, git) = RepoWithModuleAndWorktree(t);
        foreach (var store in new[] { "foo/objects/aa", "lfs/foo/objects/aa", "foo/lfs/objects/aa", "info/objects/aa" })
            Directory.CreateDirectory(Path.Combine(git, store, ".git"));

        Assert.Equal(
            ["foo/lfs/objects/aa/.git", "foo/objects/aa/.git", "info/objects/aa/.git", "lfs/foo/objects/aa/.git"],
            Found(git).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// What Pen put aside is walked like the rest, by layout rather than name: one whose layout it broke (its HEAD
    /// moved aside) isn't a repo, but a repo still inside one is, as git run there would use it.
    /// </summary>
    [Fact]
    public void WhatPenPutAside_IsWalked_AndOnlyARepoStillInsideItIsFound()
    {
        using var t = new TempDir();
        var (_, git) = RepoWithModuleAndWorktree(t);
        Directory.CreateDirectory(Path.Combine(git, "modules", "x.pen-quarantine", "modules", "y", ".git"));
        Layout(Path.Combine(git, "foo", ".git.pen-quarantine", "modules", "z"), "");
        var broken = Layout(Path.Combine(git, "foo", ".git.pen-quarantine"), "");
        File.Move(Path.Combine(broken, "HEAD"), Path.Combine(broken, "HEAD" + GitQuarantine.Suffix));
        var brokenBare = Layout(Path.Combine(git, "bar"), "");
        File.Move(Path.Combine(brokenBare, "HEAD"), Path.Combine(brokenBare, "HEAD" + GitQuarantine.Suffix));

        Assert.Equal(["foo/.git.pen-quarantine/modules/z/HEAD", "modules/x.pen-quarantine/modules/y/.git"],
            Found(git).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ADirectoryWithAGitDirsLayout_IsARepoUnlessItsTheGitDirsOwn()
    {
        using var t = new TempDir();
        var (_, git) = RepoWithModuleAndWorktree(t);
        Assert.Empty(Found(git)); // the git dir, modules/m and worktrees/w
        Layout(Path.Combine(git, "refs", "x"), "");
        Directory.CreateDirectory(Path.Combine(git, "logs", "objects")); // logs/ has HEAD and refs/ already
        // A worktrees dir that isn't the git dir's: its entries lead to its owner, which isn't one either.
        var owner = Layout(Path.Combine(git, "foo"), "");
        File.Delete(Path.Combine(owner, "HEAD"));
        var entry = Directory.CreateDirectory(Path.Combine(owner, "worktrees", "n")).FullName;
        File.WriteAllText(Path.Combine(entry, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(entry, "commondir"), "../..\n");

        Assert.Equal(["foo/worktrees/n/HEAD", "logs/HEAD", "refs/x/HEAD"], Found(git).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("bare exec files")]
    [InlineData("bare head symlink")]
    public void AModuleRepo_IsFoundInAnyLayoutGitTakesForAGitDir(string form)
    {
        using var t = new TempDir();
        var (_, git) = RepoWithModuleAndWorktree(t);
        OtherLayout(Path.Combine(git, "modules", "x"), form, "");

        Assert.Equal([Path.Combine(git, "modules", "m"), Path.Combine(git, "modules", "x")],
            RepoScan.FindModuleRepos(git));
    }

    [Fact]
    public void RemovingASymlink_NeverRemovesWhatsThereInstead()
    {
        using var t = new TempDir();
        var dir = t.Sub("p", "dir");
        File.WriteAllText(Path.Combine(dir, "f"), "x");
        File.WriteAllText(Path.Combine(t.Path, "p", "file"), "x");

        foreach (var path in new[] { dir, Path.Combine(t.Path, "p", "file") })
        {
            var attempt = GitQuarantine.RemoveLink(Path.Combine(t.Path, "p"), path, "why");

            Assert.IsType<Quarantined.Failed>(attempt);
            Assert.Contains("COULD NOT REMOVE IT (it isn't a symlink any more)", attempt.Why);
        }
        Assert.True(File.Exists(Path.Combine(dir, "f")));
        Assert.True(File.Exists(Path.Combine(t.Path, "p", "file")));
    }

    /// <summary>
    /// Another session's agent could swap a directory on the way to a symlink for a symlink to elsewhere, between
    /// the scan and the removal: the removal never goes through it.
    /// </summary>
    [Fact]
    public void RemovingASymlink_NeverFollowsASymlinkSwappedInOnTheWay()
    {
        using var t = new TempDir();
        var project = t.Sub("p");
        var link = Path.Combine(t.Sub("p", "a", "b"), "docs");
        Directory.CreateSymbolicLink(link, Path.Combine(project, ".git"));
        // Elsewhere, a symlink of the same name the removal must not reach.
        var victim = Path.Combine(t.Sub("elsewhere", "b"), "docs");
        File.CreateSymbolicLink(victim, "precious");
        Directory.Move(Path.Combine(project, "a"), Path.Combine(project, "a-moved"));
        Directory.CreateSymbolicLink(Path.Combine(project, "a"), Path.Combine(t.Path, "elsewhere"));

        var attempt = GitQuarantine.RemoveLink(project, link, "why");

        Assert.IsType<Quarantined.Failed>(attempt);
        Assert.Contains("isn't a directory any more", attempt.Why);
        Assert.True(PathChecks.IsLink(victim));
        Assert.True(PathChecks.IsLink(Path.Combine(project, "a-moved", "b", "docs")));
    }

    [Fact]
    public void ASymlinkIntoTheGitDirThatCantBeRemoved_FailsTheCheck()
    {
        using var t = new TempDir();
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var shut = t.Sub("repo", "src");
        var docs = Path.Combine(shut, "docs");
        Directory.CreateSymbolicLink(docs, Path.Combine(git, "refs"));
        File.SetUnixFileMode(shut, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);

            var failed = Assert.Single(check.Moved);
            Assert.IsType<Quarantined.Failed>(failed);
            Assert.Contains("COULD NOT REMOVE IT (permission denied", failed.Why);
            Assert.Contains("a file named above couldn't be moved out of git's way, removed or restored", check.Failure);
            Assert.True(PathChecks.IsLink(docs));
        }
        finally
        {
            File.SetUnixFileMode(shut, Dir755);
        }
    }

    [Fact]
    public void RemovingASymlink_WhoseTargetIsntUtf8_SaysToMakeItAgainByHand()
    {
        using var t = new TempDir();
        var project = t.Sub("p");
        var link = Path.Combine(project, "docs");
        Sh(project, "ln -s \"$(printf '.git/objects/\\377x')\" docs");

        var removed = GitQuarantine.RemoveLink(project, link, "why");

        Assert.True(Assert.IsType<Quarantined.LinkRemoved>(removed).LinkTargetEscaped);
        Assert.Equal(@".git/objects/\377x", ((Quarantined.LinkRemoved)removed).LinkTarget);
        Assert.False(PathChecks.ExistsOrIsLink(link));
        var warning = GitCheckWording.Warning(project, [removed]);
        Assert.Contains("pen: They've been moved out of git's way, or removed:", warning);
        Assert.Contains(@"docs (a symlink to .git/objects/\377x) removed", warning);
        Assert.Contains("since what it held isn't valid UTF-8 (shown above with octal escapes): make it again by hand",
            warning);
        Assert.DoesNotContain("ln -s", warning);
    }

    [Fact]
    public void AModuleRepoThatIsntKept_IsARepo()
    {
        using var t = new TempDir();
        var (_, git) = RepoWithModuleAndWorktree(t);

        Assert.Equal(["modules/m/HEAD"], Found(git, modules: []));
    }

    /// <summary>A worktree entry is the git dir's own when its commondir leads back, as git resolves it.</summary>
    [Theory]
    [InlineData("../..", "git")]
    [InlineData("../../", "git")]
    [InlineData("{git}/", "git")] // libgit2 writes the full path, with a trailing slash
    [InlineData("{git}", "git")]
    [InlineData("../../../.git", "git")]
    [InlineData("{elsewhere}", "elsewhere")]
    [InlineData("../../modules/m", "modules/m")] // protected too, but not the entry's owner
    [InlineData("l/../../..", "elsewhere")] // ../.. as text, but git leaves the symlink's target
    public void AWorktreeEntrysCommondir_DecidesWhetherItsTheGitDirsOwn(string commondir, string leadsTo)
    {
        using var t = new TempDir();
        var (_, git) = RepoWithModuleAndWorktree(t);
        var elsewhere = Layout(t.Sub("elsewhere"), "");
        var entry = Path.Combine(git, "worktrees", "w");
        Directory.CreateSymbolicLink(Path.Combine(entry, "l"), t.Sub("elsewhere", "x", "y", "z"));
        var value = commondir.Replace("{git}", git, StringComparison.Ordinal)
            .Replace("{elsewhere}", elsewhere, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(entry, "commondir"), value + "\n");

        // What git itself takes for the common dir.
        var expected = leadsTo switch { "git" => git, "elsewhere" => elsewhere, _ => Path.Combine(git, leadsTo) };
        Assert.Equal(expected, Native.RealPath(Git.Run(entry, "rev-parse", "--git-common-dir").TrimEnd('\n')));
        Assert.Equal(leadsTo == "git" ? [] : ["worktrees/w/HEAD"], Found(git));
    }

    [Fact]
    public void ACommondirGitWouldReadDifferently_IsntTheGitDirsOwn()
    {
        using var t = new TempDir();
        var (_, git) = RepoWithModuleAndWorktree(t);
        var commondir = Path.Combine(git, "worktrees", "w", "commondir");
        File.WriteAllBytes(commondir, [0xEF, 0xBB, 0xBF, .. "../..\n"u8]);
        Assert.Equal(["worktrees/w/HEAD"], Found(git));

        File.Delete(commondir);
        File.WriteAllText(Path.Combine(t.Path, "real-commondir"), "../..\n");
        File.CreateSymbolicLink(commondir, Path.Combine(t.Path, "real-commondir"));
        Assert.Equal(["worktrees/w/HEAD"], Found(git));
    }

    [Fact]
    public void AWorktreeOfTheProjectUnderTmp_IsAllowed_AsGitSubrepoMakesThem()
    {
        using var t = new TempDir();
        var (repo, git) = RepoWithModuleAndWorktree(t);
        Git.Run(repo, "worktree", "add", "-q", "--detach", ".git/tmp/subrepo/x");

        Assert.Empty(Found(git));
        Directory.CreateDirectory(Path.Combine(git, "tmp", "subrepo", "x", "src", ".git"));
        Assert.Equal(["tmp/subrepo/x/src/.git"], Found(git));
    }

    [Fact]
    public void ADirectoryInTheGitDirThatCantBeListed_IsReported()
    {
        using var t = new TempDir();
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var shut = Directory.CreateDirectory(Path.Combine(git, "foo")).FullName;
        File.SetUnixFileMode(shut, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Assert.Equal([shut], RepoScan.ScanGitDir(git, RepoScan.FindModuleRepos(git), tryMakeListable: null).UnlistableDirs);
            Assert.Contains(".git/foo", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message);
            var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);
            Assert.Contains("pen can't list these directories", check.Failure);
            Assert.Contains(".git/foo", check.Failure);
        }
        finally
        {
            File.SetUnixFileMode(shut, Dir755);
        }
    }

    [Fact]
    public void AtTheEnd_ADirectoryInTheGitDirThatCantBeListed_IsOpenedAndChecked()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var plant = Plant(t, git, "git dir", "foo/bar", hook);
        var shut = Path.Combine(git, "foo");
        File.SetUnixFileMode(shut, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            AssertFires(Path.Combine(shut, "bar"), marker);
            var check = GitCheck.RunAtSessionEnd(repo, s);

            Assert.Null(check.Failure);
            Assert.Contains(check.ModeChanges, o => o.Path == shut);
            Assert.Contains(check.Moved, q => q.From == plant);
        }
        finally
        {
            File.SetUnixFileMode(shut, Dir755);
        }
    }

    // ---- Symlinks in the tree ----

    private static List<string> Links(string repo) =>
        NestedScans.Scan(repo, Path.Combine(repo, ".git")).GitDirLinks.Select(l => Path.GetRelativePath(repo, l)).ToList();

    [Fact]
    public void OnlyASymlinkToADirectoryAtOrInsideTheGitDir_IsReported()
    {
        using var t = new TempDir();
        var (repo, git) = RepoWithModuleAndWorktree(t);
        Directory.CreateDirectory(Path.Combine(repo, "src", "deep"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "src", "deep", "to-git"), git);
        Directory.CreateSymbolicLink(Path.Combine(repo, "to-refs"), "../repo/.git/refs/heads");
        Directory.CreateSymbolicLink(Path.Combine(repo, "to-module"), Path.Combine(git, "modules", "m"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "via-link"), Path.Combine(repo, "to-refs"));
        File.CreateSymbolicLink(Path.Combine(repo, "to-head"), Path.Combine(git, "HEAD")); // git can't start in a file
        Directory.CreateSymbolicLink(Path.Combine(repo, "dangling"), Path.Combine(git, "nothing-here"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "dangling-in-tree"), Path.Combine(repo, "src", "nothing"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "dangling-outside"), Path.Combine(t.Path, "x", "y"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "to-src"), Path.Combine(repo, "src"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "to-root"), repo);
        Directory.CreateSymbolicLink(Path.Combine(repo, "outside"), t.Path);
        Directory.CreateSymbolicLink(Path.Combine(repo, "to-git-like"), t.Sub("repo", ".gitx"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "docs.pen-quarantine"), git); // whatever its name
        Directory.CreateSymbolicLink(Path.Combine(t.Sub("repo", "sub"), ".git"), git); // a .git symlink too

        Assert.Equal(
            ["dangling", "docs.pen-quarantine", "src/deep/to-git", "sub/.git", "to-module", "to-refs", "via-link"],
            Links(repo));
        Assert.DoesNotContain(NestedScans.Find(repo), r => r.Path == Path.Combine(repo, "sub", ".git"));
    }

    [Fact]
    public void ASymlinkIntoAWorktreesCommonGitDir_IsReported()
    {
        using var t = new TempDir();
        var (_, git) = RepoWithModuleAndWorktree(t);
        var wt = Path.Combine(t.Path, "w");
        Directory.CreateSymbolicLink(Path.Combine(wt, "to-main"), Path.Combine(git, "foo-missing"));
        Directory.CreateSymbolicLink(Path.Combine(wt, "to-entry"), Path.Combine(git, "worktrees", "w"));
        Directory.CreateSymbolicLink(Path.Combine(wt, "to-checkout"), Path.Combine(git, ".."));

        Assert.Equal(["to-entry", "to-main"], NestedScans.Scan(wt, git).GitDirLinks.Select(l => Path.GetFileName(l)));
        Assert.Contains("to-entry", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
    }

    /// <summary>
    /// A symlink that leads nowhere now counts if it would lead into the git dir once a directory it names is made:
    /// where it stops at a missing directory, or where it resolves once that's made and left by "..". One whose target
    /// isn't valid UTF-8 counts, as nothing can follow it.
    /// </summary>
    [Theory]
    [InlineData(".git/objects/ab/../zz", true)] // resolves as far as objects
    [InlineData(".git/nothing/x/y", true)]
    [InlineData("missing/../.git/objects/ab", true)] // once missing is made, inside .git
    [InlineData("missing/../.git", true)]
    [InlineData(".git/objects/zz/../../..", true)] // it ends in the project, but it stops at objects
    [InlineData("src/chain/x", true)] // src/chain leads nowhere, but into .git
    [InlineData("src/missing", false)]
    [InlineData("src/missing/../../outside", false)]
    [InlineData("../elsewhere/missing", false)]
    public void ASymlinkThatLeadsNowhereYet_CountsByWhereItWouldLead(string target, bool counts)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateSymbolicLink(Path.Combine(t.Sub("repo", "src"), "chain"), "../.git/nothing");
        Directory.CreateSymbolicLink(Path.Combine(repo, "docs"), target);
        Assert.False(Directory.Exists(Path.Combine(repo, "docs")));

        Assert.Equal(counts, Links(repo).Contains("docs"));
    }

    private static List<string> Untraced(string repo) =>
        NestedScans.Scan(repo, Path.Combine(repo, ".git")).UntracedLinks
            .Select(l => Path.GetRelativePath(repo, l)).ToList();

    [Fact]
    public void ASymlinkWhoseTargetIsntUtf8_IsOneThatCantBeTraced()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Sh(repo, "mkdir src && ln -s \"$(printf 'src/\\377x')\" docs");

        Assert.Empty(Links(repo));
        Assert.Equal(["docs"], Untraced(repo));
        Assert.Contains("pen can't tell where these symlinks lead",
            Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message);
    }

    /// <summary>
    /// A dangling symlink met on the way is spliced in as the kernel splices it: x/../.. through x, a symlink to
    /// a/b, is a/b/../.., which leads into .git once a/b is made, though x alone leads nowhere near it.
    /// </summary>
    [Fact]
    public void ADanglingSymlinkChainedThroughDotDot_Counts()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateDirectory(Path.Combine(repo, ".git", "foo"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "x"), "a/b");
        var link = Path.Combine(repo, "L");
        Directory.CreateSymbolicLink(link, "x/../../.git/foo");

        Assert.Equal(["L"], Links(repo));
        Directory.CreateDirectory(Path.Combine(repo, "a", "b"));
        Assert.Equal(Native.RealPath(Path.Combine(repo, ".git", "foo")), Native.RealPath(link));
    }

    /// <summary>
    /// A chain of dangling symlinks, each to the next, the last into .git: the first counts, as every symlink up to
    /// the kernel's limit is followed.
    /// </summary>
    [Fact]
    public void ALongDanglingChainIntoTheGitDir_Counts()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        const int count = 40;
        for (var i = 0; i < count - 1; i++)
            Directory.CreateSymbolicLink(Path.Combine(repo, $"l{i:00}"), $"l{i + 1:00}");
        Directory.CreateSymbolicLink(Path.Combine(repo, $"l{count - 1:00}"), ".git/missing");

        var links = Links(repo);
        Assert.Contains("l00", links);
        Assert.Equal(count, links.Count);
    }

    /// <summary>
    /// Past a missing directory, a ".." after a symlink goes up from where the symlink leads, as the kernel goes once
    /// the missing directory is made: m/../.git/L3/../../.., with L3 a symlink to a/b/c in .git, is .git itself.
    /// </summary>
    [Fact]
    public void ADotDotPastAMissingDirectoryAndASymlink_GoesUpFromWhereTheSymlinkLeads()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        Directory.CreateDirectory(Path.Combine(git, "a", "b", "c"));
        Directory.CreateSymbolicLink(Path.Combine(git, "L3"), "a/b/c");
        var link = Path.Combine(repo, "L1");
        Directory.CreateSymbolicLink(link, "m/../.git/L3/../../..");

        Assert.Equal(GitDirLinkCheck.Lead.Inside, GitDirLinkCheck.For([git])!.Trace(link));
        Assert.Equal(["L1"], Links(repo));
        Directory.CreateDirectory(Path.Combine(repo, "m"));
        Assert.Equal(Native.RealPath(git), Native.RealPath(link));
    }

    /// <summary>
    /// A live symlink to an ordinary directory whose real path is nearly PATH_MAX long, many directories deep, leads
    /// elsewhere: the way up from it is walked without the path looked at growing past PATH_MAX.
    /// </summary>
    [Fact]
    public void ASymlinkToADirectoryWithALongRealPath_LeadsElsewhere()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var real = Native.RealPath(t.Path)!;
        var deep = real + string.Concat(Enumerable.Repeat("/d", (980 - real.Length) / 2));
        Directory.CreateDirectory(deep);
        var link = Path.Combine(repo, "docs");
        Directory.CreateSymbolicLink(link, deep);

        Assert.Equal(GitDirLinkCheck.Lead.Elsewhere, GitDirLinkCheck.For([Path.Combine(repo, ".git")])!.Trace(link));
    }

    /// <summary>
    /// A dangling symlink whose target, through a live symlink, goes into a directory and back out many times before
    /// what's missing resolves as far as it gets, however long the path it spells.
    /// </summary>
    [Theory]
    [InlineData("to-git", true)]
    [InlineData("to-src", false)]
    public void ADanglingSymlinkWithALongTarget_ThroughALiveSymlink_Resolves(string via, bool inside)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        Directory.CreateDirectory(Path.Combine(git, "a"));
        Directory.CreateDirectory(Path.Combine(repo, "src", "a"));
        Directory.CreateSymbolicLink(Path.Combine(repo, "to-git"), ".git");
        Directory.CreateSymbolicLink(Path.Combine(repo, "to-src"), "src");
        var target = via + string.Concat(Enumerable.Repeat("/a/..", 190)) + "/missing";
        var link = Path.Combine(repo, "docs");
        Directory.CreateSymbolicLink(link, target);

        Assert.True(repo.Length + target.Length > 1024);
        Assert.Equal(inside ? GitDirLinkCheck.Lead.Inside : GitDirLinkCheck.Lead.Elsewhere,
            GitDirLinkCheck.For([git])!.Trace(link));
    }

    /// <summary>
    /// A symlink to a directory whose ".." can't be searched may lead anywhere, so it's refused, but as one Pen can't
    /// trace rather than as one into the git dir; the end check removes it, saying so.
    /// </summary>
    [Fact]
    public void ASymlinkPastADirectoryThatCantBeSearched_IsRefusedAsOneThatCantBeTraced()
    {
        using var t = new TempDir();
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var shut = t.Sub("outside", "shut");
        Directory.CreateDirectory(Path.Combine(shut, "in"));
        var docs = Path.Combine(repo, "docs");
        Directory.CreateSymbolicLink(docs, Path.Combine(shut, "in"));
        File.SetUnixFileMode(shut, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            Assert.SkipWhen(Directory.Exists(Path.Combine(shut, "in")), "the shut directory can still be searched");
            Assert.Empty(Links(repo));
            Assert.Equal(["docs"], Untraced(repo));
            var message = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message;
            Assert.Contains("pen can't tell where these symlinks lead (a directory on the way can't be searched", message);
            Assert.DoesNotContain("lead into its git dir (or would", message);

            var check = GitCheck.RunAtSessionEnd(repo, s);

            Assert.Null(check.Failure);
            var removed = Assert.Single(check.Moved, q => q.From == docs);
            Assert.IsType<Quarantined.LinkRemoved>(removed);
            Assert.Contains("a symlink pen can't trace", removed.Why);
        }
        finally
        {
            File.SetUnixFileMode(shut, Dir755);
        }
    }

    /// <summary>
    /// A repo inside something an earlier session's end check put aside (one whose module repo it left intact) is
    /// refused like any other, with a word on where it came from.
    /// </summary>
    [Theory]
    [InlineData(".git/foo.pen-quarantine/bar")]
    [InlineData("sub/.git.pen-quarantine/modules/m")]
    [InlineData(".git/foo.pen-quarantine-2/bar")]
    public void ARepoInsideAQuarantine_IsRefused_SayingPenPutItAside(string position)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Layout(Path.Combine(repo, position), "[core]\n\tbare = true\n");

        var message = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message;

        Assert.Contains(position, message);
        Assert.Contains("inside what pen put aside at the end of an earlier session", message);
        Assert.DoesNotContain("run pen in each repo instead", message);
    }

    /// <summary>
    /// In a linked worktree, only the names below the common git dir decide whether a repo in it is in something Pen
    /// put aside, not those of the main checkout's own path.
    /// </summary>
    [Fact]
    public void ARepoInTheCommonGitDir_OfAMainCheckoutNamedLikeAQuarantine_IsntSaidToBePutAside()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("main.pen-quarantine"));
        var wt = Path.Combine(t.Path, "w");
        Git.Run(repo, "worktree", "add", "-q", "--detach", wt);
        Layout(Path.Combine(repo, ".git", "foo"), "[core]\n\tbare = true\n");

        var message = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message;

        Assert.Contains("foo", message);
        Assert.DoesNotContain("inside what pen put aside", message);
    }

    /// <summary>A free two-hex-digit name in objects/, as a commit's first object there would make it.</summary>
    private static string FreeObjectDir(string git)
    {
        for (var i = 0; i < 256; i++)
        {
            var dir = Path.Combine(git, "objects", i.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            if (!Directory.Exists(dir))
                return dir;
        }
        throw new InvalidOperationException("no free objects dir");
    }

    /// <summary>
    /// The plant sits in the object store, and docs leads to it through objects/ab, which isn't there yet: once a
    /// commit makes it, git in docs runs the plant. The walk finds the plant itself; the symlink is removed too.
    /// </summary>
    [Fact]
    public void Replay_ASymlinkThatComesAliveWhenACommitMakesAnObjectsDir_IsRemoved_AndThePlantQuarantined()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var plant = Plant(t, git, "bare", "objects/zz", hook);
        var ab = FreeObjectDir(git);
        var docs = Path.Combine(repo, "docs");
        Directory.CreateSymbolicLink(docs, $".git/objects/{Path.GetFileName(ab)}/../zz");
        Directory.CreateDirectory(ab);
        AssertFires(docs, marker);
        Directory.Delete(ab);
        Assert.False(Directory.Exists(docs));
        Assert.Equal(["docs"], Links(repo));
        Assert.Contains(".git/objects/zz (laid out as a git dir of its own)",
            Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == plant && q is Quarantined.Moved);
        AssertRemoved(repo, check, docs, $".git/objects/{Path.GetFileName(ab)}/../zz");
        Directory.CreateDirectory(ab);
        AssertDoesntFire(Path.Combine(git, "objects", "zz"), marker);
        RepoGuard.CheckAtStart(repo);
    }

    /// <summary>
    /// On macOS, /System/Volumes/Data/… is the same directory as /… through a firmlink, which realpath leaves as
    /// written: a symlink spelled that way still leads into the git dir.
    /// </summary>
    [Fact]
    public void Replay_ASymlinkThroughAFirmlinkAlias_StillLeadsIntoTheGitDir()
    {
        using var t = new TempDir();
        var alias = "/System/Volumes/Data" + t.Path;
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Directory.Exists(alias) && Native.RealPath(alias) != t.Path,
            "needs a macOS firmlink that realpath doesn't resolve");
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var docs = Path.Combine(repo, "docs");
        Directory.CreateSymbolicLink(docs, "/System/Volumes/Data" + Path.Combine(git, "refs"));
        Assert.Contains("these symlinks lead into its git dir", Assert.Throws<PenException>(() =>
            RepoGuard.CheckAtStart(repo)).Message);
        File.Delete(docs);
        var plant = Plant(t, git, "bare", "objects/zz", hook);
        Directory.CreateSymbolicLink(docs, "/System/Volumes/Data" + Path.Combine(git, "objects", "zz"));
        AssertFires(docs, marker);
        Assert.Equal(["docs"], Links(repo));

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == plant && q is Quarantined.Moved);
        AssertRemoved(repo, check, docs, "/System/Volumes/Data" + Path.Combine(git, "objects", "zz"));
        AssertDoesntFire(Path.Combine(git, "objects", "zz"), marker);
    }

    /// <summary>
    /// A symlink committed on a branch isn't in the tree until the user checks the branch out, after the session: so
    /// the plant it leads to, in the object store, is found by the walk of the git dir itself.
    /// </summary>
    [Fact]
    public void Replay_ASymlinkCommittedOnABranch_ArrivesAtCheckout_SoThePlantIsFoundByTheWalk()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var docs = Path.Combine(repo, "docs");
        Git.Run(repo, "checkout", "-q", "-b", "evil");
        File.CreateSymbolicLink(docs, ".git/objects/zz");
        Git.Run(repo, "add", "docs");
        Git.Run(repo, "commit", "-q", "-m", "docs");
        Git.Run(repo, "checkout", "-q", "main");
        Assert.False(PathChecks.ExistsOrIsLink(docs));
        var s = Started(t, repo, git);
        var plant = Plant(t, git, "bare", "objects/zz", hook);
        Git.Run(repo, "checkout", "-q", "evil");
        AssertFires(docs, marker);
        Git.Run(repo, "checkout", "-q", "main");
        Assert.False(PathChecks.ExistsOrIsLink(docs));
        Assert.Contains(".git/objects/zz (laid out as a git dir of its own)",
            Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == plant && q is Quarantined.Moved);
        Git.Run(repo, "checkout", "-q", "evil");
        AssertDoesntFire(docs, marker);
    }

    /// <summary>
    /// A nested .git directory isn't entered by the walk of the tree, so a symlink in it into the git dir would
    /// outlive its quarantine (git in sub/.git.pen-quarantine/docs reaches the git dir): the end check walks it once
    /// it's moved aside, and removes the symlink.
    /// </summary>
    [Fact]
    public void Replay_ASymlinkInsideANestedGitDir_IsRemovedOnceThatIsMovedAside()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var plant = Plant(t, git, "bare", "objects/zz", hook);
        var sub = Git.Repo(t.Sub("repo", "sub"));
        var docs = Path.Combine(sub, ".git", "docs");
        Directory.CreateSymbolicLink(docs, Path.Combine(git, "objects", "zz"));
        Directory.CreateSymbolicLink(Path.Combine(sub, ".git", "refs-link"), Path.Combine(git, "refs"));
        AssertFires(docs, marker);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        var aside = Path.Combine(sub, ".git" + GitQuarantine.Suffix);
        Assert.Contains(check.Moved, q => q.From == Path.Combine(sub, ".git") && q is Quarantined.Moved m && m.To == aside);
        Assert.Contains(check.Moved, q => q.From == plant && q is Quarantined.Moved);
        AssertRemoved(repo, check, Path.Combine(aside, "docs"), Path.Combine(git, "objects", "zz"));
        AssertRemoved(repo, check, Path.Combine(aside, "refs-link"), Path.Combine(git, "refs"));
        AssertDoesntFire(Path.Combine(git, "objects", "zz"), marker);
    }

    /// <summary>
    /// A repo inside a nested .git directory (its module repo, say) is reachable once that's moved aside, through a
    /// symlink to the name it's moved to, which leads nowhere until then: the end check walks it once it's moved.
    /// </summary>
    [Fact]
    public void Replay_ARepoInsideANestedGitDir_IsQuarantinedOnceThatIsMovedAside()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var s = Started(t, repo, git);
        var sub = Git.Repo(t.Sub("repo", "sub"));
        var inner = Layout(Path.Combine(sub, ".git", "modules", "x"), PlantConfig(hook, t.Sub("plant-worktree")));
        var aside = Path.Combine(sub, ".git" + GitQuarantine.Suffix);
        var docs = Path.Combine(repo, "docs");
        Directory.CreateSymbolicLink(docs, Path.Combine(aside, "modules", "x"));
        AssertFires(inner, marker);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == Path.Combine(sub, ".git") && q is Quarantined.Moved m && m.To == aside);
        Assert.Contains(check.Moved, q => q.From == Path.Combine(aside, "modules", "x", "HEAD") && q is Quarantined.Moved);
        AssertDoesntFire(docs, marker);
        RepoGuard.CheckAtStart(repo);
    }

    // ---- What stays: real layouts git and its tools make ----

    /// <summary>Starts and ends a session with nothing moved and nothing failing, then a pen sign check.</summary>
    private static void AssertStays(TempDir t, string dir, string? common = null, string id = "aaaaaaaa")
    {
        common ??= Path.Combine(dir, ".git");
        Assert.Empty(RepoScan.ScanGitDir(common, RepoScan.FindModuleRepos(common), tryMakeListable: null).Repos);
        var s = Started(t, dir, common, id);
        var end = GitCheck.RunAtSessionEnd(dir, s);
        Assert.Null(end.Failure);
        Assert.Empty(end.Moved);
        var sign = GitCheck.RunWithoutSession(dir, startedWithGit: true, common, modulesAtStart: null);
        Assert.Null(sign.Failure);
        Assert.Empty(sign.Moved);
        RepoGuard.CheckAtStart(dir);
    }

    private static void Write(string path, string text = "x\n")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    [Fact]
    public void GitLfsStorage_Stays()
    {
        using var t = new TempDir();
        var (repo, git) = RepoWithModuleAndWorktree(t);
        var oid = new string('a', 64);
        foreach (var root in new[] { git, Path.Combine(git, "modules", "m") })
        {
            Write(Path.Combine(root, "lfs", "objects", "aa", "aa", oid));
            Write(Path.Combine(root, "lfs", "tmp", "123456789"));
            Write(Path.Combine(root, "lfs", "incomplete", oid));
            Directory.CreateDirectory(Path.Combine(root, "lfs", "cache"));
        }

        AssertStays(t, repo);
    }

    [Fact]
    public void GitAnnexStorage_Stays()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var annex = Path.Combine(repo, ".git", "annex");
        const string key = "SHA256E-s5--5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03.txt";
        Write(Path.Combine(annex, "objects", "Xk", "Pq", key, key));
        Write(Path.Combine(annex, "journal", "uuid.log"));
        Write(Path.Combine(annex, "index"));
        Write(Path.Combine(annex, "keysdb", "db"));
        Write(Path.Combine(annex, "fsck", "fsckstate"));
        Directory.CreateDirectory(Path.Combine(annex, "othertmp"));
        Directory.CreateDirectory(Path.Combine(annex, "misctmp"));
        Git.Run(repo, "branch", "git-annex");

        AssertStays(t, repo);
    }

    [Fact]
    public void GitSvnMetadata_Stays()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var svn = Path.Combine(repo, ".git", "svn");
        Write(Path.Combine(svn, ".metadata"), "[svn-remote \"svn\"]\n\treposRoot = https://svn.example.invalid/r\n");
        var remote = Path.Combine(svn, "refs", "remotes", "git-svn");
        Write(Path.Combine(remote, ".rev_map.0b1a2c3d-0000-0000-0000-000000000000"));
        Write(Path.Combine(remote, "index"));
        Write(Path.Combine(remote, "unhandled.log"));
        Write(Path.Combine(svn, "refs", "remotes", "origin", "trunk", "index"));

        AssertStays(t, repo);
    }

    [Fact]
    public void RefsLogsAndInfo_Stay()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        for (var i = 0; i < 30; i++)
        {
            Git.Run(repo, "branch", $"topic/a{i}/x");
            Git.Run(repo, "tag", $"v{i}");
        }
        Git.Run(repo, "pack-refs", "--all");
        Git.Run(repo, "branch", "loose/one");
        Git.Run(repo, "update-ref", "refs/remotes/origin/HEAD", "HEAD");
        Git.Run(repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        Git.Run(repo, "update-ref", "refs/remotes/origin/main", "HEAD");
        Git.Run(repo, "update-server-info");
        Git.Run(repo, "notes", "add", "-m", "n");
        Git.Run(repo, "stash", "list");
        Write(Path.Combine(repo, ".git", "info", "exclude"), "*.tmp\n");

        AssertStays(t, repo);
    }

    [Fact]
    public void RerereCache_Stays()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "config", "rerere.enabled", "true");
        var file = Path.Combine(repo, "f");
        File.WriteAllText(file, "base\n");
        Git.Run(repo, "add", "f");
        Git.Run(repo, "commit", "-q", "-m", "base");
        Git.Run(repo, "checkout", "-q", "-b", "other");
        File.WriteAllText(file, "other\n");
        Git.Run(repo, "commit", "-q", "-am", "other");
        Git.Run(repo, "checkout", "-q", "main");
        File.WriteAllText(file, "main\n");
        Git.Run(repo, "commit", "-q", "-am", "main");
        Assert.Throws<InvalidOperationException>(() => Git.Run(repo, "merge", "-q", "other"));
        File.WriteAllText(file, "both\n");
        Git.Run(repo, "add", "f");
        Git.Run(repo, "commit", "-q", "--no-edit");
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(repo, ".git", "rr-cache")));

        AssertStays(t, repo);
    }

    /// <summary>Two commits on a branch that each change f, and a main that changes it too.</summary>
    private static string Diverged(TempDir t)
    {
        var repo = Git.Repo(t.Sub("repo"));
        var file = Path.Combine(repo, "f");
        File.WriteAllText(file, "base\n");
        Git.Run(repo, "add", "f");
        Git.Run(repo, "commit", "-q", "-m", "base");
        Git.Run(repo, "checkout", "-q", "-b", "other");
        File.WriteAllText(file, "one\n");
        Git.Run(repo, "commit", "-q", "-am", "one");
        File.WriteAllText(file, "two\n");
        Git.Run(repo, "commit", "-q", "-am", "two");
        Git.Run(repo, "checkout", "-q", "main");
        File.WriteAllText(file, "main\n");
        Git.Run(repo, "commit", "-q", "-am", "main");
        return repo;
    }

    [Fact]
    public void APausedCherryPick_Stays()
    {
        using var t = new TempDir();
        var repo = Diverged(t);
        Assert.Throws<InvalidOperationException>(() => Git.Run(repo, "cherry-pick", "main..other"));
        Assert.True(Directory.Exists(Path.Combine(repo, ".git", "sequencer")));

        AssertStays(t, repo);
    }

    [Fact]
    public void APausedInteractiveRebase_Stays()
    {
        using var t = new TempDir();
        var repo = Diverged(t);
        Git.Run(repo, "checkout", "-q", "other");
        Git.Run(repo, "-c", "sequence.editor=sh -c 'printf \"break\\n\" >\"$1\"' -", "rebase", "-q", "-i", "HEAD~1");
        Assert.True(Directory.Exists(Path.Combine(repo, ".git", "rebase-merge")));

        AssertStays(t, repo);
    }

    [Fact]
    public void APausedAm_Stays()
    {
        using var t = new TempDir();
        var repo = Diverged(t);
        var patches = t.Sub("patches");
        Git.Run(repo, "format-patch", "-q", "-o", patches, "main..other");
        Assert.Throws<InvalidOperationException>(() =>
            Git.Run(repo, ["am", "-q", .. Directory.GetFiles(patches).Order(StringComparer.Ordinal)]));
        Assert.True(Directory.Exists(Path.Combine(repo, ".git", "rebase-apply")));

        AssertStays(t, repo);
    }

    [Fact]
    public void InternalAndExternalWorktrees_WithABisectInOne_Stay()
    {
        using var t = new TempDir();
        var repo = Diverged(t);
        Git.Run(repo, "worktree", "add", "-q", "--detach", ".claude/worktrees/in");
        var outside = Path.Combine(t.Path, "outside");
        Git.Run(repo, "worktree", "add", "-q", "--detach", outside, "other");
        Git.Run(outside, "bisect", "start");
        Git.Run(outside, "bisect", "bad");
        Git.Run(outside, "bisect", "good", "HEAD~2");
        Assert.True(File.Exists(Path.Combine(repo, ".git", "worktrees", "outside", "BISECT_START")));

        AssertStays(t, repo);
        AssertStays(t, outside, Path.Combine(repo, ".git"), "bbbbbbbb");
    }

    [Fact]
    public void NestedSubmodules_WithAWorktreesOwnModules_Stay()
    {
        using var t = new TempDir();
        var leaf = Git.Repo(t.Sub("leaf"));
        var mid = Git.Repo(t.Sub("mid"));
        Git.Run(mid, [.. AllowFile, "submodule", "add", "-q", leaf, "deps/leaf"]);
        Git.Run(mid, "commit", "-q", "-m", "leaf");
        var super = Git.Repo(t.Sub("super"));
        Git.Run(super, [.. AllowFile, "submodule", "add", "-q", mid, "libs/mid"]);
        Git.Run(super, "commit", "-q", "-m", "mid");
        Git.Run(super, [.. AllowFile, "submodule", "update", "-q", "--init", "--recursive"]);
        Git.Run(super, "worktree", "add", "-q", ".claude/worktrees/in");
        var inside = Path.Combine(super, ".claude", "worktrees", "in");
        Git.Run(inside, [.. AllowFile, "submodule", "update", "-q", "--init", "--recursive"]);
        Git.Run(Path.Combine(super, "libs", "mid"), "worktree", "add", "-q", "--detach", Path.Combine(t.Path, "mid-wt"));
        Assert.True(Directory.Exists(Path.Combine(super, ".git", "worktrees", "in", "modules", "libs", "mid", "modules")));
        Assert.True(Directory.Exists(Path.Combine(super, ".git", "modules", "libs", "mid", "worktrees", "mid-wt")));

        AssertStays(t, super);
    }

    [Fact]
    public void AWorktreeUnderTheGitDirsTmp_Stays()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "--detach", ".git/tmp/subrepo/x");
        Git.Run(Path.Combine(repo, ".git", "tmp", "subrepo", "x"), "status");

        AssertStays(t, repo);
    }

    [Fact]
    public void AReftableRepo_Stays()
    {
        using var t = new TempDir();
        var repo = t.Sub("repo");
        Git.Run(repo, "init", "-q", "--ref-format=reftable");
        Git.Run(repo, "commit", "-q", "--allow-empty", "-m", "init");
        Git.Run(repo, "branch", "two");
        Assert.True(Directory.Exists(Path.Combine(repo, ".git", "reftable")));

        AssertStays(t, repo);
    }

    [Fact]
    public void ALibgit2Worktree_Stays()
    {
        using var t = new TempDir();
        var (repo, git) = RepoWithModuleAndWorktree(t);
        File.WriteAllText(Path.Combine(git, "worktrees", "w", "commondir"), git + "/\n");
        Git.Run(Path.Combine(t.Path, "w"), "status");

        AssertStays(t, repo);
    }

    /// <summary>
    /// Large stores, as Git, Git LFS and git-annex lay them out (annex's read-only), are walked and nothing in them
    /// is a repo.
    /// </summary>
    [Fact]
    public void LargeObjectStores_Stay()
    {
        using var t = new TempDir();
        var repo = Diverged(t);
        Git.Run(repo, "gc", "-q");
        File.WriteAllText(Path.Combine(repo, "g"), "loose\n");
        Git.Run(repo, "add", "g");
        Git.Run(repo, "commit", "-q", "-m", "loose");
        var git = Path.Combine(repo, ".git");
        Assert.NotEmpty(Directory.GetDirectories(Path.Combine(git, "objects", "pack")).Concat(
            Directory.GetFiles(Path.Combine(git, "objects", "pack"))));
        var oid = new string('a', 64);
        for (var i = 0; i < 40; i++)
        {
            for (var j = 0; j < 10; j++)
                Write(Path.Combine(git, "lfs", "objects", $"{i:x2}", $"{j:x2}", oid));
        }
        const string key = "SHA256E-s5--5891b5b522d5df086d0ff0b110fbd9d21bb4fc7163af34d08286a2e846f6be03.txt";
        var keyDirs = new List<string>();
        for (var i = 0; i < 20; i++)
        {
            var keyDir = Path.Combine(git, "annex", "objects", $"X{i}", "Pq", key);
            Write(Path.Combine(keyDir, key));
            File.SetUnixFileMode(Path.Combine(keyDir, key), UnixFileMode.UserRead);
            File.SetUnixFileMode(keyDir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            keyDirs.Add(keyDir);
        }
        try
        {
            AssertStays(t, repo);
        }
        finally
        {
            foreach (var keyDir in keyDirs)
                File.SetUnixFileMode(keyDir, Dir755);
        }
    }

    [Fact]
    public void AModuleRepoPenPutAside_WithTheReposNestedInIt_IsntReportedAgain()
    {
        using var t = new TempDir();
        var leaf = Git.Repo(t.Sub("leaf"));
        var mid = Git.Repo(t.Sub("mid"));
        Git.Run(mid, [.. AllowFile, "submodule", "add", "-q", leaf, "deps/leaf"]);
        Git.Run(mid, "commit", "-q", "-m", "leaf");
        var super = Git.Repo(t.Sub("super"));
        Git.Run(super, [.. AllowFile, "submodule", "add", "-q", mid, "libs/mid"]);
        Git.Run(super, "commit", "-q", "-m", "mid");
        Git.Run(super, [.. AllowFile, "submodule", "update", "-q", "--init", "--recursive"]);
        var first = GitCheck.RunWithoutSession(super, startedWithGit: true, commonGitDir: null, modulesAtStart: []);
        Assert.Contains(first.Moved, q => q is Quarantined.Moved m && m.To.EndsWith("modules/libs/mid.pen-quarantine", StringComparison.Ordinal));

        AssertStays(t, super);
    }
}
