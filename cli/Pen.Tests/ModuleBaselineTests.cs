namespace Pen.Tests;

/// <summary>
/// The module repos a session takes as the submodules' own, when other sessions of the repo are open: a repo another
/// session's agent made is never one of them, so it's quarantined however the sessions start and end. Each replay
/// first shows that git really runs the planted fsmonitor.
/// </summary>
public sealed class ModuleBaselineTests : IDisposable
{
    private const string Key = "pen-test-12345678";
    private const UnixFileMode Dir755 = (UnixFileMode)0b111_101_101;
    private static readonly string[] AllowFile = ["-c", "protocol.file.allow=always"];

    private readonly TempDir _t = new();
    private readonly Dictionary<string, FileLock> _locks = [];

    private string Runtime => Path.Combine(_t.Path, "runtime");

    public void Dispose()
    {
        foreach (var held in _locks.Values)
            held.Dispose();
        _t.Dispose();
    }

    /// <summary>
    /// Starts a session as Session does: the refusals, the module repos chosen by the open sessions' lists, its lock,
    /// the choice narrowed with the protected files and git records saved, then compose.yaml last.
    /// </summary>
    private SessionDir Start(string dir, string common, string id)
    {
        RepoGuard.CheckAtStart(dir);
        var mounts = RepoGuard.PrepareMounts(dir, "/nonexistent-home");
        var chosen = ModuleBaseline.ChooseAtStart(SessionRegistry.FindOpen(Key, Runtime), common, dir);

        var s = new SessionDir(Key, _t.Sub("runtime", "sessions", id));
        _locks[id] = FileLock.TryTake(s.LockFile, exclusive: true)!;
        ModuleBaseline.NarrowAndSave(new(s, mounts, chosen, SessionRegistry.FindOpen(Key, Runtime), common, dir));
        File.WriteAllText(s.ComposeFile, "");
        return s;
    }

    /// <summary>Ends a session as Session does: the check, then its files, compose.yaml first.</summary>
    private CheckResult End(string dir, SessionDir s)
    {
        var check = GitCheck.RunAtSessionEnd(dir, s);
        Assert.Null(check.Failure);
        Assert.False(check.RestoreIncomplete);
        Session.DeleteSessionFiles(s);
        Die(s);
        return check;
    }

    /// <summary>The session's Pen exits without ending it: its lock is free, and it's still open.</summary>
    private void Die(SessionDir s)
    {
        _locks[s.Id].Dispose();
        _locks.Remove(s.Id);
    }

    private (string Hook, string Marker) Canary()
    {
        var marker = Path.Combine(_t.Path, "ran");
        var hook = Path.Combine(_t.Path, "hook.sh");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, Dir755);
        return (hook, marker);
    }

    private (string Repo, string Git) Repo()
    {
        var repo = Git.Repo(_t.Sub("repo"));
        return (repo, Path.Combine(repo, ".git"));
    }

    /// <summary>
    /// What an agent can make: a module repo in <paramref name="parent"/>/modules with an fsmonitor in its config, and
    /// its checkout, which git and the check both take for a submodule's.
    /// </summary>
    /// <returns>The module repo and its checkout.</returns>
    private static (string Module, string Checkout) Plant(string parent, string checkout, string hook)
    {
        var module = Path.Combine(parent, "modules", Path.GetFileName(checkout));
        Directory.CreateDirectory(Path.Combine(parent, "modules"));
        Git.Run(Path.GetDirectoryName(checkout)!, "init", "-q", "--separate-git-dir=" + module, checkout);
        File.AppendAllText(Path.Combine(module, "config"), $"[core]\n\tworktree = {checkout}\n\tfsmonitor = {hook}\n");
        return (module, checkout);
    }

    /// <summary>The agent of another session makes it again, where the first session's end moved it from.</summary>
    private static void Recreate(string module, string checkout, string hook)
    {
        Assert.False(Directory.Exists(module), "the plant should have been moved aside by now");
        File.Delete(Path.Combine(checkout, ".git"));
        Plant(Path.GetDirectoryName(Path.GetDirectoryName(module)!)!, checkout, hook);
    }

    private static void AssertFires(string dir, string marker)
    {
        File.Delete(marker);
        Git.Run(dir, "status");
        Assert.True(File.Exists(marker), "the replay isn't real: the planted fsmonitor didn't run before the check");
        File.Delete(marker);
    }

    private static void AssertDoesntFire(string dir, string marker)
    {
        File.Delete(marker);
        try
        {
            Git.Run(dir, "status");
        }
        catch (InvalidOperationException)
        {
            // no repo there any more
        }
        Assert.False(File.Exists(marker), "the planted fsmonitor ran");
    }

    private static void AssertMoved(CheckResult check, string module) =>
        Assert.Contains(check.Moved, q => q.From == module && q is Quarantined.Moved);

    // ---- Replays ----

    [Fact]
    public void Replay_APlantAdoptedAlongside_ThenMadeAgainByTheSecondsAgent_IsQuarantinedAtItsEnd()
    {
        var (hook, marker) = Canary();
        var (repo, git) = Repo();
        var a = Start(repo, git, "aaaaaaaa");
        var (module, checkout) = Plant(git, Path.Combine(repo, "evil"), hook);
        AssertFires(checkout, marker);

        var b = Start(repo, git, "bbbbbbbb");
        Assert.DoesNotContain(module, b.ReadModules()!);
        AssertMoved(End(repo, a), module);
        Recreate(module, checkout, hook);
        AssertFires(checkout, marker);
        var last = End(repo, b);

        AssertMoved(last, module);
        Assert.Contains(last.Moved, q => q.From == Path.Combine(checkout, ".git"));
        Assert.Contains("another session could have made", Assert.Single(last.Moved, q => q.From == module).Why);
        AssertDoesntFire(checkout, marker);
    }

    [Fact]
    public void Replay_TheSecondSessionEndingFirst_QuarantinesThePlant_AndSoDoesThePlantersEnd()
    {
        var (hook, marker) = Canary();
        var (repo, git) = Repo();
        var a = Start(repo, git, "aaaaaaaa");
        var (module, checkout) = Plant(git, Path.Combine(repo, "evil"), hook);
        AssertFires(checkout, marker);
        var b = Start(repo, git, "bbbbbbbb");

        AssertMoved(End(repo, b), module);
        AssertDoesntFire(checkout, marker);
        Recreate(module, checkout, hook);
        AssertFires(checkout, marker);
        AssertMoved(End(repo, a), module);

        AssertDoesntFire(checkout, marker);
    }

    [Fact]
    public void Replay_AChainOfSessions_DoesntTurnAPlantIntoABaseline()
    {
        var (hook, marker) = Canary();
        var (repo, git) = Repo();
        var a = Start(repo, git, "aaaaaaaa");
        var (module, checkout) = Plant(git, Path.Combine(repo, "evil"), hook);
        AssertFires(checkout, marker);
        var b = Start(repo, git, "bbbbbbbb");
        AssertMoved(End(repo, a), module);
        Recreate(module, checkout, hook);

        // C starts with only B open, whose list doesn't have it either.
        var c = Start(repo, git, "cccccccc");
        Assert.DoesNotContain(module, c.ReadModules()!);
        Assert.True(Assert.Single(ProtectedFiles.Read(c)!.Files, f => f.Path == Path.Combine(module, "config")).Adopted);
        AssertMoved(End(repo, b), module);
        Recreate(module, checkout, hook);
        AssertFires(checkout, marker);
        AssertMoved(End(repo, c), module);

        AssertDoesntFire(checkout, marker);
    }

    [Fact]
    public void Replay_ThreeSessionsAlongsideThePlant_EachQuarantinesIt_InAnyOrder()
    {
        var (hook, marker) = Canary();
        var (repo, git) = Repo();
        var a = Start(repo, git, "aaaaaaaa");
        var (module, checkout) = Plant(git, Path.Combine(repo, "evil"), hook);
        var b = Start(repo, git, "bbbbbbbb");
        var c = Start(repo, git, "cccccccc");
        Assert.DoesNotContain(module, b.ReadModules()!);
        Assert.DoesNotContain(module, c.ReadModules()!);

        foreach (var s in new[] { c, a, b })
        {
            if (!Directory.Exists(module))
                Recreate(module, checkout, hook);
            AssertFires(checkout, marker);
            AssertMoved(End(repo, s), module);
            AssertDoesntFire(checkout, marker);
        }
    }

    [Fact]
    public void Replay_ASessionWhosePenDied_StillCounts_ItsAgentMayBeRunning()
    {
        var (hook, marker) = Canary();
        var (repo, git) = Repo();
        var a = Start(repo, git, "aaaaaaaa");
        Die(a);
        Assert.Empty(SessionRegistry.FindRunning(Key, Runtime));
        var (module, checkout) = Plant(git, Path.Combine(repo, "evil"), hook);
        AssertFires(checkout, marker);

        var b = Start(repo, git, "bbbbbbbb");

        Assert.DoesNotContain(module, b.ReadModules()!);
        AssertMoved(End(repo, b), module);
        AssertDoesntFire(checkout, marker);
    }

    [Fact]
    public void Replay_ThePlantsConfig_IsAdopted_AndTheRestoreDoesntWriteItBack()
    {
        var (hook, marker) = Canary();
        var (repo, git) = Repo();
        Start(repo, git, "aaaaaaaa");
        var (module, checkout) = Plant(git, Path.Combine(repo, "evil"), hook);
        AssertFires(checkout, marker);
        var config = Path.Combine(module, "config");
        var b = Start(repo, git, "bbbbbbbb");
        Assert.True(Assert.Single(ProtectedFiles.Read(b)!.Files, f => f.Path == config).Adopted);

        // A's agent covers its tracks, or B's changes it: either way, it isn't put back.
        File.WriteAllText(config, "[core]\n\tbare = false\n");
        var check = End(repo, b);

        Assert.DoesNotContain(check.Moved, q => q.From == config);
        Assert.DoesNotContain(check.Restored, r => r.Path == config);
        var moved = Assert.IsType<Quarantined.Moved>(Assert.Single(check.Moved, q => q.From == module)).To;
        Assert.Equal("[core]\n\tbare = false\n", File.ReadAllText(Path.Combine(moved, "config")));
        AssertDoesntFire(checkout, marker);
    }

    [Fact]
    public void Replay_APlantInAWorktreesOwnModules_IsQuarantined()
    {
        var (hook, marker) = Canary();
        var (repo, git) = Repo();
        var wt = Path.Combine(_t.Path, "w");
        Git.Run(repo, "worktree", "add", "-q", "--detach", wt);
        var a = Start(wt, git, "aaaaaaaa");
        var (module, checkout) = Plant(Path.Combine(git, "worktrees", "w"), Path.Combine(wt, "x"), hook);
        Assert.Contains(module, RepoScan.FindModuleRepos(git));
        AssertFires(checkout, marker);

        var b = Start(wt, git, "bbbbbbbb");
        Assert.DoesNotContain(module, b.ReadModules()!);
        AssertMoved(End(wt, a), module);
        Recreate(module, checkout, hook);
        AssertFires(checkout, marker);

        AssertMoved(End(wt, b), module);
        AssertDoesntFire(checkout, marker);
    }

    // ---- Regressions ----

    [Fact]
    public void SubmodulesThatPredateBothSessions_AreKeptByBoth()
    {
        var (repo, git) = Repo();
        var lib = Git.Repo(_t.Sub("lib"));
        Git.Run(repo, [.. AllowFile, "submodule", "add", "-q", lib, "m"]);
        Git.Run(repo, "commit", "-q", "-m", "m");
        var module = Path.Combine(git, "modules", "m");

        var a = Start(repo, git, "aaaaaaaa");
        var b = Start(repo, git, "bbbbbbbb");

        Assert.Equal([module], a.ReadModules()!);
        Assert.Equal([module], b.ReadModules()!);
        Assert.All(ProtectedFiles.Read(b)!.Files, f => Assert.False(f.Adopted));
        Assert.Empty(End(repo, b).Moved);
        Assert.Empty(End(repo, a).Moved);
        Assert.True(Directory.Exists(module));
        Git.Run(Path.Combine(repo, "m"), "status");
    }

    [Fact]
    public void ASessionStartedAfterTheOthersEnded_HasTheUsualBaseline()
    {
        var (repo, git) = Repo();
        var a = Start(repo, git, "aaaaaaaa");
        Assert.Empty(End(repo, a).Moved);
        var lib = Git.Repo(_t.Sub("lib"));
        Git.Run(repo, [.. AllowFile, "submodule", "add", "-q", lib, "m"]);
        var module = Path.Combine(git, "modules", "m");

        var c = Start(repo, git, "cccccccc");

        Assert.Equal([module], c.ReadModules()!);
        Assert.All(ProtectedFiles.Read(c)!.Files, f => Assert.False(f.Adopted));
        Assert.Empty(End(repo, c).Moved);
        Assert.True(Directory.Exists(module));
    }

    // ---- The open sessions' lists ----

    [Fact]
    public void ASessionOpenWithoutAModulesRecord_RefusesTheStart_NamingIt()
    {
        var (repo, git) = Repo();
        var older = new SessionDir(Key, _t.Sub("runtime", "sessions", "aaaaaaaa"));
        File.WriteAllText(older.StartedWithGit, git + "\n");
        File.WriteAllText(older.ComposeFile, "");

        var e = Assert.Throws<PenException>(() => Start(repo, git, "bbbbbbbb"));

        Assert.Contains("refusing to run in", e.Message);
        Assert.Contains($"\n  {Key}-aaaaaaaa-agent\nexit it first", e.Message);
    }

    [Theory]
    [InlineData("no started-with-git")]
    [InlineData("no common dir recorded")]
    [InlineData("another common dir")]
    [InlineData("no modules")]
    public void ReadOpenLists_RefusesAnOpenSessionWithNoListForThisGitDir(string how)
    {
        var git = _t.Sub("repo", ".git");
        var s = new SessionDir(Key, _t.Sub("runtime", "sessions", "aaaaaaaa"));
        File.WriteAllText(s.ComposeFile, "");
        if (how != "no modules")
            s.WriteModules([]);
        if (how == "no common dir recorded")
            File.WriteAllText(s.StartedWithGit, "");
        else if (how == "another common dir")
            File.WriteAllText(s.StartedWithGit, _t.Sub("other", ".git") + "\n");
        else if (how != "no started-with-git")
            File.WriteAllText(s.StartedWithGit, git + "\n");

        var e = Assert.Throws<PenException>(() => ModuleBaseline.ReadOpenLists([s], git, _t.Path));

        Assert.Contains($"{Key}-aaaaaaaa-agent", e.Message);
    }

    [Fact]
    public void ReadOpenLists_NamesEverySessionWithoutAList()
    {
        var git = _t.Sub("repo", ".git");
        var open = new List<SessionDir>();
        foreach (var id in new[] { "aaaaaaaa", "bbbbbbbb", "cccccccc" })
        {
            var s = new SessionDir(Key, _t.Sub("runtime", "sessions", id));
            File.WriteAllText(s.ComposeFile, "");
            if (id == "bbbbbbbb")
            {
                File.WriteAllText(s.StartedWithGit, git + "\n");
                s.WriteModules([]);
            }
            open.Add(s);
        }

        var e = Assert.Throws<PenException>(() => ModuleBaseline.ReadOpenLists(open, git, _t.Path));

        Assert.Contains($"\n  {Key}-aaaaaaaa-agent\n  {Key}-cccccccc-agent\nexit them first", e.Message);
    }

    [Fact]
    public void ReadOpenLists_SkipsASessionThatEndedWhileItLooked()
    {
        var (repo, git) = Repo();
        var a = Start(repo, git, "aaaaaaaa");
        var open = SessionRegistry.FindOpen(Key, Runtime);
        File.Delete(a.ComposeFile);
        File.Delete(a.StartedWithGit);
        File.Delete(a.ModulesFile);

        Assert.Empty(ModuleBaseline.ReadOpenLists(open, git, repo));
    }

    [Fact]
    public void ReadOpenLists_ReadsEachOpenSessionsList()
    {
        var git = _t.Sub("repo", ".git");
        var a = new SessionDir(Key, _t.Sub("runtime", "sessions", "aaaaaaaa"));
        var b = new SessionDir(Key, _t.Sub("runtime", "sessions", "bbbbbbbb"));
        foreach (var s in new[] { a, b })
        {
            File.WriteAllText(s.StartedWithGit, git + "\n");
            File.WriteAllText(s.ComposeFile, "");
        }
        a.WriteModules(["/r/.git/modules/m", "/r/.git/modules/n"]);
        b.WriteModules([]);

        var lists = ModuleBaseline.ReadOpenLists([a, b], git, _t.Path);

        Assert.Equal([["/r/.git/modules/m", "/r/.git/modules/n"], []], lists.Select(l => l.ToArray()));
    }

    [Fact]
    public void ChooseAtStart_LeavesOutARepoAnOpenSessionDidntList_ThoughItsPenDied()
    {
        var (hook, _) = Canary();
        var (repo, git) = Repo();
        var lib = Git.Repo(_t.Sub("lib"));
        Git.Run(repo, [.. AllowFile, "submodule", "add", "-q", lib, "m"]);
        var kept = Path.Combine(git, "modules", "m");
        Die(Start(repo, git, "aaaaaaaa"));
        var (module, _) = Plant(git, Path.Combine(repo, "evil"), hook);

        var chosen = ModuleBaseline.ChooseAtStart(SessionRegistry.FindOpen(Key, Runtime), git, repo);

        Assert.Equal([kept], chosen.Listed);
        Assert.Equal([module], chosen.Unlisted);
    }

    [Fact]
    public void NarrowAndSave_LeavesOutARepoASessionOpenedSinceTheChoiceDidntList()
    {
        var (hook, _) = Canary();
        var (repo, git) = Repo();
        var (module, _) = Plant(git, Path.Combine(repo, "evil"), hook);
        var mounts = RepoGuard.PrepareMounts(repo, "/nonexistent-home");
        var chosen = ModuleBaseline.ChooseAtStart(SessionRegistry.FindOpen(Key, Runtime), git, repo);
        Assert.Equal([module], chosen.Listed);
        // A session that opened after that read, listing nothing (its Pen has died since): the repo the scan found
        // is one its agent could have made.
        var a = new SessionDir(Key, _t.Sub("runtime", "sessions", "aaaaaaaa"));
        File.WriteAllText(a.StartedWithGit, git + "\n");
        a.WriteModules([]);
        File.WriteAllText(a.ComposeFile, "");
        var s = new SessionDir(Key, _t.Sub("runtime", "sessions", "bbbbbbbb"));

        var narrowed = ModuleBaseline.NarrowAndSave(
            new(s, mounts, chosen, SessionRegistry.FindOpen(Key, Runtime), git, repo));

        Assert.Empty(narrowed.Listed);
        Assert.Equal([module], narrowed.Unlisted);
        Assert.Empty(s.ReadModules()!);
        Assert.Equal((true, git), s.ReadGit());
        Assert.True(Assert.Single(ProtectedFiles.Read(s)!.Files, f => f.Path == Path.Combine(module, "config")).Adopted);
    }

    [Fact]
    public void Choose_KeepsWhatEveryListHas_InTheOrderFound_ComparedOrdinally()
    {
        string[] found = ["/g/modules/b", "/g/modules/a", "/g/modules/c", "/g/modules/D"];

        Assert.Equal(found, ModuleBaseline.Choose(found, []));
        Assert.Equal(["/g/modules/b", "/g/modules/a"], ModuleBaseline.Choose(found,
        [
            ["/g/modules/a", "/g/modules/b", "/g/modules/d"],
            ["/g/modules/a", "/g/modules/b", "/g/modules/c", "/g/modules/x"],
        ]));
        Assert.Empty(ModuleBaseline.Choose(found, [found, []]));
    }

    [Fact]
    public void FindOpen_HasEveryOpenSession_LiveOrNot_ById()
    {
        var ids = new[] { "cccccccc", "aaaaaaaa", "bbbbbbbb", "dddddddd" };
        foreach (var id in ids)
        {
            var s = new SessionDir(Key, _t.Sub("runtime", "sessions", id));
            if (id != "dddddddd")
                File.WriteAllText(s.ComposeFile, "");
            if (id == "aaaaaaaa")
                _locks[id] = FileLock.TryTake(s.LockFile, exclusive: true)!;
        }

        Assert.Equal(["aaaaaaaa", "bbbbbbbb", "cccccccc"], SessionRegistry.FindOpen(Key, Runtime).Select(s => s.Id));
        Assert.Equal(["aaaaaaaa"], SessionRegistry.FindRunning(Key, Runtime).Select(s => s.Id));
    }
}
