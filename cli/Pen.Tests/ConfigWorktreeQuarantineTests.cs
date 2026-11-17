namespace Pen.Tests;

/// <summary>
/// A config.worktree nothing vouches for is quarantined at session end: the review's
/// forged-worktree replay, and what another session could have planted.
/// </summary>
public class ConfigWorktreeQuarantineTests
{
    private const string Key = "pen-test-12345678";
    private const UnixFileMode Dir755 = (UnixFileMode)0b111_101_101;

    private static SessionDir Started(TempDir t, string dir, string common, string id = "aaaaaaaa", IReadOnlyList<SessionDir>? running = null)
    {
        // As Session does: the module repos every open session listed, and the rest unlisted.
        var open = running ?? [];
        var lists = ModuleBaseline.ReadOpenLists(open, common, dir);
        var mounts = RepoGuard.PrepareMounts(dir, "/nonexistent-home");
        var found = RepoScan.FindModuleRepos(common);
        var modules = ModuleBaseline.Choose(found, lists);
        var s = new SessionDir(Key, t.Sub("runtime", "sessions", id));
        File.WriteAllText(s.StartedWithGit, common + "\n");
        s.WriteModules(modules);
        ProtectedFiles.Save(s, mounts, dir, open, found.Except(modules, StringComparer.Ordinal).ToList());
        return s;
    }

    private static (string Repo, string Git) Repo(TempDir t)
    {
        var repo = Git.Repo(t.Sub("repo"));
        return (repo, Path.Combine(repo, ".git"));
    }

    private static (string Hook, string Marker) Canary(TempDir t)
    {
        var marker = Path.Combine(t.Path, "ran");
        var hook = Path.Combine(t.Path, "hook.sh");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, Dir755);
        return (hook, marker);
    }

    private static string Fsmonitor(string hook) => $"[core]\n\tfsmonitor = {hook}\n";

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
        Git.Run(dir, "status");
        Assert.False(File.Exists(marker), "the planted fsmonitor ran");
    }

    /// <summary>What an agent can make: a worktree git and the check both take for one, with config in its entry.</summary>
    private static (string Checkout, string File) ForgeWorktree(string repo, string git, string name, string content)
    {
        var checkout = Path.Combine(repo, name);
        Git.Run(repo, "worktree", "add", "-q", "--detach", checkout);
        var file = Path.Combine(git, "worktrees", name, "config.worktree");
        File.WriteAllText(file, content);
        return (checkout, file);
    }

    private static void TurnOn(string repo, string git, string how)
    {
        if (how == "directly")
        {
            Git.Run(repo, "config", "extensions.worktreeConfig", "true");
            return;
        }
        File.WriteAllText(Path.Combine(git, "on.inc"), "[extensions]\n\tworktreeConfig = true\n");
        Git.Run(repo, "config", "include.path", "on.inc");
    }

    // ---- The replay ----

    [Theory]
    [InlineData("directly")]
    [InlineData("through an include")]
    public void Replay_AForgedWorktreesConfig_StopsRunningAndIsReported(string how)
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        TurnOn(repo, git, how);
        var s = Started(t, repo, git);
        var (checkout, file) = ForgeWorktree(repo, git, "forged", Fsmonitor(hook));
        // git 2.56 reads extensions only from the repo's config file itself, so through
        // an include nothing fires yet; the file still can't stay for a later switch.
        if (how == "directly") AssertFires(checkout, marker);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        var q = Assert.Single(check.Moved);
        Assert.Equal(file, q.From);
        Assert.Equal(Fsmonitor(hook), File.ReadAllText(q.To));
        Assert.False(File.Exists(file));
        AssertDoesntFire(checkout, marker);
        Assert.Contains("mv ", GitCheckWording.Warning(repo, check.Moved));
        Assert.True(File.Exists(Path.Combine(checkout, ".git")), "the worktree itself stays");
    }

    [Fact]
    public void ANewOneInTheGitDir_IsQuarantinedEvenWithTheSettingOff()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var s = Started(t, repo, git);
        var file = Path.Combine(git, "config.worktree");
        File.WriteAllText(file, "[core]\n\tfsmonitor = /evil\n");

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Equal(file, Assert.Single(check.Moved).From);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void AnEntryNamedLikeAQuarantine_IsNoExemption()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var s = Started(t, repo, git);
        var (_, file) = ForgeWorktree(repo, git, "x.pen-quarantine", "[core]\n");

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Contains(check.Moved, q => q.From == file);
        Assert.False(File.Exists(file));
    }

    // ---- A forged entry that's its own common dir ----

    /// <summary>
    /// Git reads commondir as bytes. After a UTF-8 byte order mark, it names "&lt;entry&gt;/\uFEFF../..", which a
    /// directory named "\uFEFF.." makes the entry itself; after UTF-32BE's, a NUL comes first, so it names the entry.
    /// Either way git opens the entry as the repo, with its config.
    /// </summary>
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-32BE")]
    public void Replay_ACommondirWithAByteOrderMark_IsNoWorktreeOfTheProject(string encoding)
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        var s = Started(t, repo, git);
        var checkout = Path.Combine(repo, "forged");
        Git.Run(repo, "worktree", "add", "-q", "--detach", checkout);
        var entry = Path.Combine(git, "worktrees", "forged");
        File.WriteAllBytes(Path.Combine(entry, "commondir"), WorktreeTests.WithBom(encoding, "../..\n"));
        if (encoding == "utf-8")
            Directory.CreateDirectory(Path.Combine(entry, "\uFEFF.."));
        Directory.CreateDirectory(Path.Combine(entry, "objects"));
        Directory.CreateDirectory(Path.Combine(entry, "refs"));
        File.WriteAllText(Path.Combine(entry, "config"), Fsmonitor(hook));
        File.WriteAllText(Path.Combine(entry, "HEAD"), "ref: refs/heads/unborn\n");
        File.Delete(Path.Combine(entry, "index"));
        AssertFires(checkout, marker);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == Path.Combine(checkout, ".git"));
        AssertDoesntFire(checkout, marker);
    }

    // ---- A gitfile whose ".." comes after a symlink ----

    /// <summary>
    /// Points <paramref name="checkout"/>'s .git file (the checkout sits directly in the repo) at
    /// "l/../../<paramref name="named"/>", which reads as the repo's own <paramref name="named"/>. Git opens the value
    /// as written and takes its realpath, so each ".." leaves the symlink's target: with l leading to .git/foo/a/b, it
    /// opens .git/foo/<paramref name="named"/>, a git dir made here with an fsmonitor in its config.
    /// </summary>
    /// <returns>The .git file.</returns>
    private static string ForgeThroughASymlink(string git, string checkout, string named, string hook)
    {
        var forged = Path.Combine(git, "foo", named);
        Directory.CreateDirectory(Path.Combine(forged, "objects"));
        Directory.CreateDirectory(Path.Combine(forged, "refs"));
        File.WriteAllText(Path.Combine(forged, "HEAD"), "ref: refs/heads/unborn\n");
        File.WriteAllText(Path.Combine(forged, "config"), Fsmonitor(hook));
        Directory.CreateSymbolicLink(Path.Combine(checkout, "l"), Directory.CreateDirectory(
            Path.Combine(git, "foo", "a", "b")).FullName);
        var gitFile = Path.Combine(checkout, ".git");
        File.WriteAllText(gitFile, $"gitdir: l/../../{named}\n");
        return gitFile;
    }

    [Fact]
    public void Replay_AWorktreesGitFileThroughASymlink_IsNoWorktreeOfTheProject()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        var checkout = Path.Combine(repo, "wt");
        Git.Run(repo, "worktree", "add", "-q", "--detach", checkout);
        var s = Started(t, repo, git);
        var gitFile = ForgeThroughASymlink(git, checkout, ".git/worktrees/wt", hook);
        AssertFires(checkout, marker);
        Assert.Null(RepoScan.ResolveWorktree(gitFile));

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == gitFile);
        AssertDoesntFire(checkout, marker);
    }

    [Fact]
    public void Replay_AModuleCheckoutsGitFileThroughASymlink_IsNoCheckoutOfTheProject()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        var lib = Git.Repo(t.Sub("lib"));
        Git.Run(repo, "-c", "protocol.file.allow=always", "submodule", "add", "-q", lib, "m");
        Git.Run(repo, "commit", "-q", "-m", "m");
        var checkout = Path.Combine(repo, "m");
        var modules = RepoScan.FindModuleRepos(git).ToHashSet(StringComparer.Ordinal);
        Assert.NotNull(RepoScan.ResolveModuleCheckout(Path.Combine(checkout, ".git"), modules));
        var s = Started(t, repo, git);
        var gitFile = ForgeThroughASymlink(git, checkout, ".git/modules/m", hook);
        AssertFires(checkout, marker);
        Assert.Null(RepoScan.ResolveModuleCheckout(gitFile, modules));

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == gitFile);
        AssertDoesntFire(checkout, marker);
    }

    // ---- What stays ----

    [Fact]
    public void ExistingWorktreesFiles_InTheManifest_StayUntouched()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        Git.Run(repo, "config", "--worktree", "user.name", "mine");
        Git.Run(repo, "worktree", "add", "-q", "--detach", "in");
        var inGit = Path.Combine(git, "worktrees", "in", "config.worktree");
        File.WriteAllText(inGit, "[user]\n\tname = theirs\n");
        var own = Path.Combine(git, "config.worktree");
        var ownBefore = File.ReadAllBytes(own);
        var mtime = File.GetLastWriteTimeUtc(inGit);
        var s = Started(t, repo, git);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
        Assert.Equal(ownBefore, File.ReadAllBytes(own));
        Assert.Equal("[user]\n\tname = theirs\n", File.ReadAllText(inGit));
        Assert.Equal(mtime, File.GetLastWriteTimeUtc(inGit));
    }

    [Fact]
    public void AFileInAnEntryTheSessionHadReadOnly_StaysUntouched()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var outside = Path.Combine(t.Path, "outside");
        Git.Run(repo, "worktree", "add", "-q", "--detach", outside);
        var file = Path.Combine(git, "worktrees", "outside", "config.worktree");
        var s = Started(t, repo, git);
        File.WriteAllText(file, "[user]\n\tname = mine\n"); // the other worktree's own, made on the host

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Empty(check.Moved);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void ARetriedEnd_StillKnowsWhatTheSessionVouchedFor()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var s = Started(t, repo, git);
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, s).Moved);
        Assert.False(Directory.Exists(s.ProtectedDir));

        var retry = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(retry.Failure);
        Assert.Empty(retry.Moved);
    }

    [Fact]
    public void ASessionWithNoRecord_VouchesForNone()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var s = new SessionDir(Key, t.Sub("runtime", "sessions", "aaaaaaaa"));
        File.WriteAllText(s.StartedWithGit, git + "\n");
        s.WriteModules([]);
        var file = Path.Combine(git, "config.worktree");
        File.WriteAllText(file, "[core]\n");

        Assert.Equal(file, Assert.Single(GitCheck.RunAtSessionEnd(repo, s).Moved).From);
    }

    // ---- Sessions alongside each other ----

    [Fact]
    public void Replay_UnchangedPlant_AdoptedByASecondSession_CantPassAsUnchanged_EndingSecondFirst()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var first = Started(t, repo, git);
        var (checkout, file) = ForgeWorktree(repo, git, "forged", Fsmonitor(hook));
        var second = Started(t, repo, git, "bbbbbbbb", [first]);
        Assert.True(Assert.Single(ProtectedFiles.Read(second)!.Files, f => f.Path == file).Adopted);
        AssertFires(checkout, marker);

        var b = GitCheck.RunAtSessionEnd(repo, second);

        Assert.Null(b.Failure);
        Assert.Contains(b.Moved, q => q.From == file);
        AssertDoesntFire(checkout, marker);
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, first).Moved);
        AssertDoesntFire(checkout, marker);
    }

    [Fact]
    public void Replay_UnchangedPlant_PutBackAfterTheFirstEnds_IsStillQuarantined()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var first = Started(t, repo, git);
        var (checkout, file) = ForgeWorktree(repo, git, "forged", Fsmonitor(hook));
        var second = Started(t, repo, git, "bbbbbbbb", [first]);

        Assert.Contains(GitCheck.RunAtSessionEnd(repo, first).Moved, q => q.From == file);
        File.WriteAllText(file, Fsmonitor(hook)); // the second's agent, by a case variant of the name
        var b = GitCheck.RunAtSessionEnd(repo, second);

        Assert.Contains(b.Moved, q => q.From == file);
        AssertDoesntFire(checkout, marker);
    }

    [Fact]
    public void Replay_AChainOfSessions_DoesntTurnAPlantIntoABaseline()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var a = Started(t, repo, git);
        var (checkout, file) = ForgeWorktree(repo, git, "forged", Fsmonitor(hook));
        var b = Started(t, repo, git, "bbbbbbbb", [a]);
        var c = Started(t, repo, git, "cccccccc", [a, b]);
        Assert.True(Assert.Single(ProtectedFiles.Read(c)!.Files, f => f.Path == file).Adopted);

        Assert.Contains(GitCheck.RunAtSessionEnd(repo, a).Moved, q => q.From == file);
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, b).Moved);
        var last = GitCheck.RunAtSessionEnd(repo, c);

        Assert.Empty(last.Moved);
        Assert.Empty(last.Restored);
        Assert.False(File.Exists(file), "written back from a copy of what the agent planted");
        AssertDoesntFire(checkout, marker);
    }

    [Fact]
    public void BeingReadOnlyInOneSession_DoesntVouchForWhatAnotherCouldWrite()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", "--detach", wt);
        var main = Started(t, repo, git); // the worktree's entry is read-only as a whole there
        var inWt = Started(t, wt, git, "bbbbbbbb", [main]); // and writable here
        var file = Path.Combine(git, "worktrees", "wt", "config.worktree");
        File.WriteAllText(file, "[core]\n\tfsmonitor = /evil\n");

        var late = Started(t, wt, git, "cccccccc", [main, inWt]);

        Assert.True(Assert.Single(ProtectedFiles.Read(late)!.Files, f => f.Path == file).Adopted);
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, main).Moved);
        Assert.Contains(GitCheck.RunAtSessionEnd(wt, late).Moved, q => q.From == file);
    }

    [Fact]
    public void ARunningSessionWithNoRecord_CouldHavePlantedIt()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var older = new SessionDir(Key, t.Sub("runtime", "sessions", "aaaaaaaa"));
        // Its module repos are recorded, but its manifest is missing or damaged.
        File.WriteAllText(older.StartedWithGit, git + "\n");
        older.WriteModules([]);
        File.WriteAllText(older.ComposeFile, "");
        var file = Path.Combine(git, "config.worktree");
        File.WriteAllText(file, "[core]\n\tfsmonitor = /evil\n");

        var s = Started(t, repo, git, "bbbbbbbb", [older]);

        Assert.True(Assert.Single(ProtectedFiles.Read(s)!.Files, f => f.Path == file).Adopted);
    }

    [Fact]
    public void ASessionThatHasPutItsFilesBack_StillVouchesForThemToOneStartingNow()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        Git.Run(repo, "config", "--worktree", "user.name", "mine");
        var ending = Started(t, repo, git);
        File.WriteAllText(ending.ComposeFile, "");
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, ending).Moved);

        var s = Started(t, repo, git, "bbbbbbbb", [ending]);
        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Empty(check.Moved);
        Assert.True(File.Exists(Path.Combine(git, "config.worktree")));
    }

    [Fact]
    public void AFileASessionVouchesFor_IsInheritedAsVouchedFor()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var first = Started(t, repo, git);
        var second = Started(t, repo, git, "bbbbbbbb", [first]);
        var third = Started(t, repo, git, "cccccccc", [second]);

        Assert.All(ProtectedFiles.Read(third)!.Files, f => Assert.False(f.Adopted));
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, third).Moved);
    }

    // ---- Three sessions, one held in teardown: the baseline ----

    private static SessionDir StartedAt(TempDir t, string repo, string git, string id, int minutesAgo, params SessionDir[] running)
    {
        var s = Started(t, repo, git, id, running);
        s.WriteStarted(DateTime.UtcNow.AddMinutes(-minutesAgo));
        File.WriteAllText(s.ComposeFile, "");
        return s;
    }

    /// <summary>The order the sessions end in, after the first has put its files back and is held in compose down.</summary>
    [Theory]
    [InlineData("b,c")]
    [InlineData("c,b")]
    public void Replay_ASessionHeldInTeardown_DoesntLetAPlantBecomeABaseline(string order)
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var a = StartedAt(t, repo, git, "aaaaaaaa", 10);
        var b = StartedAt(t, repo, git, "bbbbbbbb", 5, a);
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, a).Moved);
        Assert.True(a.IsOpen);

        File.AppendAllText(config, Fsmonitor(hook)); // b's agent, by a case variant of the name
        AssertFires(repo, marker);
        var c = StartedAt(t, repo, git, "cccccccc", 0, a, b);
        var saved = Assert.Single(ProtectedFiles.Read(c)!.Files, f => f.Path == config);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(c.ProtectedDir, saved.Copy)));

        foreach (var s in order.Split(',').Select(id => id == "b" ? b : c))
        {
            Assert.Null(GitCheck.RunAtSessionEnd(repo, s).Failure);
            Assert.Equal(original, File.ReadAllBytes(config));
            AssertDoesntFire(repo, marker);
        }
        // a's stack finally goes, or fails to and it's ended again.
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, a).Moved);
        Assert.Equal(original, File.ReadAllBytes(config));
    }

    [Fact]
    public void ASessionKilledBeforeTheNextStarts_HasPutItsFilesBack_WhileAnotherIsHeldInTeardown()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var a = StartedAt(t, repo, git, "aaaaaaaa", 10);
        var b = StartedAt(t, repo, git, "bbbbbbbb", 5, a);
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, a).Moved);
        File.AppendAllText(config, Fsmonitor(hook));
        Assert.Null(GitCheck.RunAtSessionEnd(repo, b).Failure); // what the next Pen does first for a killed session

        var c = StartedAt(t, repo, git, "cccccccc", 0, a);
        File.AppendAllText(config, Fsmonitor(hook)); // c's agent
        Assert.Null(GitCheck.RunAtSessionEnd(repo, c).Failure);

        Assert.Equal(original, File.ReadAllBytes(config));
        AssertDoesntFire(repo, marker);
    }

    /// <summary>The oldest has listed a copy and removes it just after the new session reads its manifest.</summary>
    [Fact]
    public void AnOlderSessionsCopyGoingMidStart_FallsToTheNextSessions_NotTheDisk()
    {
        using var t = new TempDir();
        var (hook, _) = Canary(t);
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var a = StartedAt(t, repo, git, "aaaaaaaa", 10);
        var b = StartedAt(t, repo, git, "bbbbbbbb", 5, a);
        foreach (var f in ProtectedFiles.Read(a)!.Files) File.Delete(Path.Combine(a.ProtectedDir, f.Copy));
        File.AppendAllText(config, Fsmonitor(hook));

        var c = StartedAt(t, repo, git, "cccccccc", 0, a, b);

        var saved = Assert.Single(ProtectedFiles.Read(c)!.Files, f => f.Path == config);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(c.ProtectedDir, saved.Copy)));
    }

    [Fact]
    public void WithAllCopiesGone_TheFileOnDiskIsTheBaseline()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var a = StartedAt(t, repo, git, "aaaaaaaa", 10);
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, a).Moved);
        Git.Run(repo, "config", "user.name", "changed between sessions"); // a's agent is gone: this is the user

        var c = StartedAt(t, repo, git, "cccccccc", 0, a);

        var check = GitCheck.RunAtSessionEnd(repo, c);
        Assert.Empty(check.Moved);
        Assert.Contains("changed between sessions", File.ReadAllText(config));
    }

    [Fact]
    public void AConfigWorktreeThatTheHeldSessionVouchedFor_KeepsAYoungerSessionsCopy()
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        Git.Run(repo, "config", "--worktree", "user.name", "mine");
        var file = Path.Combine(git, "config.worktree");
        var original = File.ReadAllBytes(file);
        var a = StartedAt(t, repo, git, "aaaaaaaa", 10);
        var b = StartedAt(t, repo, git, "bbbbbbbb", 5, a);
        Assert.Empty(GitCheck.RunAtSessionEnd(repo, a).Moved);
        File.AppendAllText(file, Fsmonitor(hook));
        var c = StartedAt(t, repo, git, "cccccccc", 0, a, b);
        Assert.False(Assert.Single(ProtectedFiles.Read(c)!.Files, f => f.Path == file).Adopted);

        Assert.Null(GitCheck.RunAtSessionEnd(repo, c).Failure);
        Assert.Null(GitCheck.RunAtSessionEnd(repo, b).Failure);

        Assert.Equal(original, File.ReadAllBytes(file));
        AssertDoesntFire(repo, marker);
    }

    // ---- Everywhere git reads one ----

    private static void ModuleLayout(string gitDir)
    {
        Directory.CreateDirectory(Path.Combine(gitDir, "objects"));
        Directory.CreateDirectory(Path.Combine(gitDir, "refs"));
        File.WriteAllText(Path.Combine(gitDir, "HEAD"), "ref: refs/heads/main\n");
        File.WriteAllText(Path.Combine(gitDir, "config"), "");
    }

    [Fact]
    public void ModuleReposAndTheirWorktreeEntries_AreLookedThrough()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        Git.Run(repo, "worktree", "add", "-q", "--detach", "in");
        var entry = Path.Combine(git, "worktrees", "in");
        var module = Path.Combine(git, "modules", "lib");
        var inEntry = Path.Combine(entry, "modules", "deep", "lib");
        ModuleLayout(module);
        ModuleLayout(inEntry);
        var s = Started(t, repo, git);
        var planted = new[]
        {
            Path.Combine(entry, "config.worktree"),
            Path.Combine(module, "config.worktree"),
            Path.Combine(module, "worktrees", "x", "config.worktree"),
            Path.Combine(inEntry, "config.worktree"),
            Path.Combine(inEntry, "worktrees", "y", "config.worktree"),
        };
        foreach (var p in planted)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "[core]\n");
        }

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Null(check.Failure);
        foreach (var p in planted)
        {
            Assert.Contains(check.Moved, q => q.From == p);
            Assert.False(File.Exists(p));
        }
    }

    [Fact]
    public void ASymlink_OrASymlinkedEntry_GetsNoPassFromWhatItPointsTo()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var own = Path.Combine(git, "config.worktree");
        File.WriteAllText(own, "[user]\n\tname = mine\n");
        var s = Started(t, repo, git);
        var evil = Path.Combine(repo, "evil");
        Directory.CreateDirectory(evil);
        File.WriteAllText(Path.Combine(evil, "config.worktree"), "[core]\n");
        var worktrees = Path.Combine(git, "worktrees");
        Directory.CreateDirectory(Path.Combine(worktrees, "a"));
        var link = Path.Combine(worktrees, "a", "config.worktree");
        File.CreateSymbolicLink(link, own);
        var entryLink = Path.Combine(worktrees, "b");
        Directory.CreateSymbolicLink(entryLink, evil);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Contains(check.Moved, q => q.From == link);
        Assert.Contains(check.Moved, q => q.From == entryLink);
        Assert.False(PathChecks.Exists(link));
        Assert.False(PathChecks.Exists(entryLink));
        Assert.True(File.Exists(Path.Combine(evil, "config.worktree")), "only the symlink moves");
        Assert.True(File.Exists(own));
    }

    [Fact]
    public void ASymlinkedWorktreesDir_IsMovedAside()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var s = Started(t, repo, git);
        var evil = Path.Combine(repo, "evil");
        Directory.CreateDirectory(Path.Combine(evil, "x"));
        var link = Path.Combine(git, "worktrees");
        Directory.CreateSymbolicLink(link, evil);

        var check = GitCheck.RunAtSessionEnd(repo, s);

        Assert.Contains(check.Moved, q => q.From == link);
        Assert.False(PathChecks.Exists(link));
    }

    // ---- chmod ----

    [Theory]
    [InlineData(0b101_101_101)] // 555: can't rename inside it
    [InlineData(0b011_001_001)] // 311: can't list it
    [InlineData(0)]             // 000: can't enter it
    public void AnEntrysModeCantSaveIt(int mode)
    {
        using var t = new TempDir();
        var (hook, marker) = Canary(t);
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var s = Started(t, repo, git);
        var (checkout, file) = ForgeWorktree(repo, git, "forged", Fsmonitor(hook));
        var entry = Path.GetDirectoryName(file)!;
        AssertFires(checkout, marker);
        File.SetUnixFileMode(entry, (UnixFileMode)mode);
        try
        {
            var check = GitCheck.RunAtSessionEnd(repo, s);
            Assert.Null(check.Failure);
            Assert.Contains(check.Moved, q => q.From == file);
            Assert.False(File.Exists(file));
            if (mode != 0b011_001_001) Assert.Contains(check.Opened, o => o.Path == entry); // 311 is enough to rename in
        }
        finally { File.SetUnixFileMode(entry, Dir755); }
        AssertDoesntFire(checkout, marker);
    }

    [Theory]
    [InlineData(0b101_101_101)]
    [InlineData(0b011_001_001)]
    [InlineData(0)]
    public void TheWorktreesDirsModeCantSaveIt_AndDoesntCostTheRealWorktrees(int mode)
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        Git.Run(repo, "worktree", "add", "-q", "--detach", "real");
        var real = Path.Combine(git, "worktrees", "real", "config.worktree");
        File.WriteAllText(real, "[user]\n\tname = mine\n");
        var s = Started(t, repo, git);
        var (_, file) = ForgeWorktree(repo, git, "forged", "[core]\n");
        var worktrees = Path.Combine(git, "worktrees");
        File.SetUnixFileMode(worktrees, (UnixFileMode)mode);
        try
        {
            var check = GitCheck.RunAtSessionEnd(repo, s);
            // 000 stops the restore of what's inside until the mode is fixed, and Pen retries.
            if (mode == 0) check = GitCheck.RunAtSessionEnd(repo, s);
            Assert.Null(check.Failure);
            Assert.False(File.Exists(file), "still where git would read it");
            Assert.True(File.Exists(real), "the real worktree's file went");
            Assert.True(Directory.Exists(Path.Combine(worktrees, "real")), "the real worktree's entry went");
        }
        finally { File.SetUnixFileMode(worktrees, Dir755); }
        Git.Run(Path.Combine(repo, "real"), "status");
    }

    [Theory]
    [InlineData(0b101_101_101)]
    [InlineData(0b011_001_001)]
    public void TheGitDirsModeCantSaveOneInIt(int mode)
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var s = Started(t, repo, git);
        var file = Path.Combine(git, "config.worktree");
        File.WriteAllText(file, "[core]\n");
        File.SetUnixFileMode(git, (UnixFileMode)mode);
        try
        {
            var check = GitCheck.RunAtSessionEnd(repo, s);
            Assert.Null(check.Failure);
            Assert.Contains(check.Moved, q => q.From == file);
            Assert.False(File.Exists(file));
        }
        finally { File.SetUnixFileMode(git, Dir755); }
    }

    [Theory]
    [InlineData(0b101_101_101)]
    [InlineData(0b011_001_001)]
    public void AModuleReposModeCantSaveOneInIt(int mode)
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var module = Path.Combine(git, "modules", "lib");
        ModuleLayout(module);
        var s = Started(t, repo, git);
        var file = Path.Combine(module, "config.worktree");
        File.WriteAllText(file, "[core]\n");
        File.SetUnixFileMode(module, (UnixFileMode)mode);
        try
        {
            var check = GitCheck.RunAtSessionEnd(repo, s);
            Assert.False(File.Exists(file), "still where git would read it");
            Assert.NotEmpty(check.Moved);
        }
        finally
        {
            foreach (var d in Directory.GetDirectories(Path.Combine(git, "modules"))) File.SetUnixFileMode(d, Dir755);
        }
    }

    [Fact]
    public void AnEntryPenCantOpen_IsAFailureNotASilentPass()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        ForgeWorktree(repo, git, "forged", "[core]\n");
        var entry = Path.Combine(git, "worktrees", "forged");
        var scan = new PromptHazardScan(git, tryOpen: null, new ProtectedManifest([], []));
        scan.Run();
        var hazards = scan.Hazards;
        Assert.Contains(hazards, h => h.Path == Path.Combine(entry, "config.worktree"));

        File.SetUnixFileMode(entry, UnixFileMode.None);
        try
        {
            var reached = new PromptHazardScan(git, tryOpen: null, new ProtectedManifest([], []));
            reached.Run();
            Assert.Contains(entry, reached.UnreachableDirs);
        }
        finally { File.SetUnixFileMode(entry, Dir755); }
    }

    [Fact]
    public void ACheckThatStopsEarly_StillMovesThem()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", "--detach", wt);
        var s = Started(t, wt, git);
        var (_, file) = ForgeWorktree(repo, git, "forged", "[core]\n");
        Directory.Delete(wt, recursive: true);

        var check = GitCheck.RunAtSessionEnd(wt, s);

        Assert.NotNull(check.Failure);
        Assert.Contains(check.Moved, q => q.From == file);
    }

    // ---- pen sign ----

    [Fact]
    public void PenSignsCheck_LeavesThemAlone()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        TurnOn(repo, git, "directly");
        var file = Path.Combine(git, "config.worktree");
        File.WriteAllText(file, "[user]\n\tname = mine\n");
        var (_, forged) = ForgeWorktree(repo, git, "forged", "[user]\n\tname = mine\n");

        var check = GitCheck.RunWithoutOpening(repo, startedWithGit: true, git, modulesAtStart: null);

        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
        Assert.True(File.Exists(file));
        Assert.True(File.Exists(forged));
    }
}
