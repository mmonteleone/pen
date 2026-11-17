using System.Diagnostics;

namespace Pen.Tests;

/// <summary>
/// Copies of the protected files saved at a session's start, and put back at
/// its end: the case-variant writes that get past single-file mounts.
/// </summary>
public class ProtectedFilesTests
{
    private const string Key = "pen-test-12345678";
    private const UnixFileMode Rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly byte[] Binary = [0xff, 0xfe, 0, (byte)'\r', (byte)'\n', 0x80, (byte)'x'];
    private static readonly string[] AllowFile = ["-c", "protocol.file.allow=always"];

    private static SessionDir NewSession(TempDir t, string id = "abcd1234") =>
        new(Key, t.Sub("runtime", "sessions", id));

    /// <summary>
    /// Starts a session as Session does, with the project's open sessions as the baselines: the module repos chosen,
    /// the choice narrowed with the files and git records saved, then compose.yaml, which opens it for the next one.
    /// </summary>
    private static SessionDir Started(TempDir t, string dir, string common, string id = "abcd1234")
    {
        var runtime = Path.Combine(t.Path, "runtime");
        var mounts = RepoGuard.PrepareMounts(dir, "/nonexistent-home");
        var chosen = ModuleBaseline.ChooseAtStart(Key, runtime, common, dir);
        var s = NewSession(t, id);
        ModuleBaseline.NarrowAndSave(s, mounts, chosen, runtime, common, dir);
        File.WriteAllText(s.ComposeFile, "");
        return s;
    }

    private static void AssertManifestMatchesMounts(SessionDir s, IEnumerable<Mount> mounts, params string[] expected)
    {
        var manifest = ProtectedFiles.Read(s)!;
        var paths = manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected.Order(StringComparer.Ordinal), paths);
        Assert.Equal(mounts.Where(m => m.Verify).Select(m => m.Path).Order(StringComparer.Ordinal), paths);
        foreach (var f in manifest.Files)
        {
            Assert.Equal(File.ReadAllBytes(f.Path), File.ReadAllBytes(Path.Combine(s.ProtectedDir, f.Copy)));
            Assert.Equal(File.GetUnixFileMode(f.Path), f.Mode);
            Assert.Equal(Rw, File.GetUnixFileMode(Path.Combine(s.ProtectedDir, f.Copy)));
        }
    }

    private static (string Repo, string Git) Repo(TempDir t)
    {
        var repo = Git.Repo(t.Sub("repo"));
        return (repo, Path.Combine(repo, ".git"));
    }

    private static CheckResult End(string dir, SessionDir s) => GitCheck.RunAtSessionEnd(dir, s);

    private static void AssertIn10s(Action a) => Assert.True(Task.Run(a).Wait(TimeSpan.FromSeconds(10)), "hung");

    private static void Mkfifo(string path)
    {
        var r = Process.RunAndCaptureText(new ProcessStartInfo("mkfifo", [path]) { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, r.ExitStatus.ExitCode);
    }

    private static void Ln(string from, string to)
    {
        var r = Process.RunAndCaptureText(new ProcessStartInfo("ln", [from, to]) { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, r.ExitStatus.ExitCode);
    }

    // ---- The manifest ----

    [Fact]
    public void AMainCheckout_SavesItsConfig()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var mounts = RepoGuard.PrepareMounts(repo, "/nonexistent-home");
        var s = NewSession(t);
        ProtectedFiles.Save(s, mounts, repo, [], []);
        AssertManifestMatchesMounts(s, mounts, Path.Combine(git, "config"));
        Assert.Contains(Path.Combine(git, "hooks"), ProtectedFiles.Read(s)!.ReadOnlyDirs);
        Assert.Contains(Path.Combine(repo, ".pen"), ProtectedFiles.Read(s)!.ReadOnlyDirs);
    }

    [Fact]
    public void ALinkedWorktree_SavesItsGitFileAndCommondir_ButNotHeadOrIndex()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var wt = Path.Combine(repo, "in");
        Git.Run(repo, "worktree", "add", "-q", wt);
        File.WriteAllText(Path.Combine(wt, "f"), "x");
        Git.Run(wt, "add", "f");
        var mounts = RepoGuard.PrepareMounts(wt, "/nonexistent-home");
        Assert.Contains(mounts, m => m.Path == Path.Combine(git, "HEAD") && m.ReadOnly && !m.Verify);
        var s = NewSession(t);
        ProtectedFiles.Save(s, mounts, wt, [], []);
        AssertManifestMatchesMounts(s, mounts,
            Path.Combine(git, "config"), Path.Combine(git, "worktrees", "in", "commondir"), Path.Combine(wt, ".git"));
    }

    [Fact]
    public void AWorktreeInTheProject_HasItsCommondirSaved()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        Git.Run(repo, "worktree", "add", "-q", "in");
        var mounts = RepoGuard.PrepareMounts(repo, "/nonexistent-home");
        var s = NewSession(t);
        ProtectedFiles.Save(s, mounts, repo, [], []);
        AssertManifestMatchesMounts(s, mounts,
            Path.Combine(git, "config"), Path.Combine(git, "worktrees", "in", "commondir"));
    }

    [Fact]
    public void AWorktreeOutsideTheProject_IsListedAsReadOnly_WithItsConfigWorktreeNotCopied()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "other"));
        var entry = Path.Combine(git, "worktrees", "other");
        File.WriteAllText(Path.Combine(entry, "config.worktree"), "");
        var mounts = RepoGuard.PrepareMounts(repo, "/nonexistent-home");
        var s = NewSession(t);
        ProtectedFiles.Save(s, mounts, repo, [], []);
        AssertManifestMatchesMounts(s, mounts, Path.Combine(git, "config"));
        Assert.Contains(entry, ProtectedFiles.Read(s)!.ReadOnlyDirs);
    }

    [Fact]
    public void ASubmodule_SavesItsConfigAndConfigWorktree()
    {
        using var t = new TempDir();
        var lib = Git.Repo(t.Sub("lib"));
        var (repo, git) = Repo(t);
        Git.Run(repo, [.. AllowFile, "submodule", "add", "-q", lib, "libs/lib"]);
        var module = Path.Combine(git, "modules", "libs", "lib");
        File.WriteAllText(Path.Combine(module, "config.worktree"), "[core]\n");
        var mounts = RepoGuard.PrepareMounts(repo, "/nonexistent-home");
        var s = NewSession(t);
        ProtectedFiles.Save(s, mounts, repo, [], []);
        AssertManifestMatchesMounts(s, mounts,
            Path.Combine(git, "config"), Path.Combine(module, "config"), Path.Combine(module, "config.worktree"));
    }

    [Fact]
    public void Includes_AreSaved_ThoseOutsideTheSandboxAreNot()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var outside = Path.Combine(t.Path, "host.cfg");
        File.WriteAllText(outside, "");
        File.WriteAllText(Path.Combine(repo, "a.cfg"), "[include]\n path = b.cfg\n");
        File.WriteAllText(Path.Combine(repo, "b.cfg"), "");
        File.AppendAllText(Path.Combine(git, "config"), $"[include]\n path = ../a.cfg\n path = extra.cfg\n path = {outside}\n");
        var mounts = RepoGuard.PrepareMounts(repo, "/nonexistent-home");
        var s = NewSession(t);
        ProtectedFiles.Save(s, mounts, repo, [], []);
        AssertManifestMatchesMounts(s, mounts,
            Path.Combine(git, "config"), Path.Combine(git, "extra.cfg"), Path.Combine(repo, "a.cfg"), Path.Combine(repo, "b.cfg"));
    }

    [Fact]
    public void AFileAlsoIncludedAsConfig_IsVerifiedHoweverElseItsMounted()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        File.WriteAllText(Path.Combine(git, "HEAD"), "[a]\n\tk = v\n"); // what git reads as config has to be config
        File.AppendAllText(Path.Combine(git, "config"), "[include]\n path = HEAD\n");
        var mounts = RepoGuard.PrepareMounts(wt, "/nonexistent-home");
        Assert.True(Assert.Single(mounts, m => m.Path == Path.Combine(git, "HEAD")).Verify);
    }

    [Fact]
    public void NoRepo_HasAnEmptyManifest()
    {
        using var t = new TempDir();
        var dir = t.Sub("plain");
        var s = NewSession(t);
        ProtectedFiles.Save(s, RepoGuard.PrepareMounts(dir, "/nonexistent-home"), dir, [], []);
        Assert.Empty(ProtectedFiles.Read(s)!.Files);
    }

    [Fact]
    public void Copies_AreExactBytes()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var inc = Path.Combine(git, "inc.cfg");
        // Valid config, as an include has to be, with bytes a text reading would change.
        byte[] exact = [.. "[a]\r\n\tk = "u8, 0xFF, 0xFE, (byte)'\r', (byte)'\n', (byte)'#', 0x00, 0xC0, (byte)'\r'];
        File.WriteAllBytes(inc, exact);
        File.AppendAllText(Path.Combine(git, "config"), "[include]\n path = inc.cfg\n");
        File.SetUnixFileMode(inc, UnixFileMode.UserRead);
        var s = Started(t, repo, git);
        var f = Assert.Single(ProtectedFiles.Read(s)!.Files, f => f.Path == inc);
        Assert.Equal(exact, File.ReadAllBytes(Path.Combine(s.ProtectedDir, f.Copy)));
        Assert.Equal(UnixFileMode.UserRead, f.Mode);
    }

    [Theory]
    [InlineData("/p/a\nb/config")]
    [InlineData("/p/a\r\n\t\"\\'b/é\u202e\u2028😀/\x7f/config")]
    [InlineData("/p/\ufffd/config")]
    [InlineData("/p/\u0001\u0000/config")]
    public void TheManifest_RoundTripsAnyPath(string path)
    {
        var parsed = ProtectedFiles.Parse(ProtectedFiles.Serialize(new([new(path, "0", (UnixFileMode)0b110_100_100, Adopted: false)], [path])));
        Assert.Equal(path, Assert.Single(parsed.Files).Path);
        Assert.Equal((UnixFileMode)0b110_100_100, parsed.Files[0].Mode);
        Assert.Equal(path, Assert.Single(parsed.ReadOnlyDirs));
    }

    [Fact]
    public void TheManifest_RefusesAPathItCouldntReadBack()
    {
        var e = Assert.Throws<PenException>(() => ProtectedFiles.Serialize(new([new("/p/\ud800/config", "0", Rw, Adopted: false)], [])));
        Assert.Contains("isn't valid text", e.Message);
    }

    [Fact]
    public void ANewlineInAModulesName_RoundTripsThroughARealSession()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var module = t.Sub("repo", ".git", "modules", "a\nb");
        Git.Run(module, "init", "-q", "--bare");
        var s = Started(t, repo, git);
        var config = Path.Combine(module, "config");
        Assert.Contains(ProtectedFiles.Read(s)!.Files, f => f.Path == config);
        File.AppendAllText(config, "[core]\n fsmonitor = /evil\n");
        var check = End(repo, s);
        Assert.Null(check.Failure);
        Assert.DoesNotContain("evil", File.ReadAllText(config));
    }

    // ---- Hostile files at save time ----

    private static (string Repo, string Commondir, IReadOnlyList<Mount> Mounts) WorktreeInProject(TempDir t)
    {
        var (repo, git) = Repo(t);
        Git.Run(repo, "worktree", "add", "-q", "in");
        var commondir = Path.Combine(git, "worktrees", "in", "commondir");
        return (repo, commondir, RepoGuard.PrepareMounts(repo, "/nonexistent-home"));
    }

    private static void AssertRefusedWithNoCopiesLeft(TempDir t, string repo, IReadOnlyList<Mount> mounts, string expected)
    {
        var s = NewSession(t);
        PenException? e = null;
        AssertIn10s(() => e = Assert.Throws<PenException>(() => ProtectedFiles.Save(s, mounts, repo, [], [])));
        Assert.Contains(expected, e!.Message);
        Assert.False(Directory.Exists(s.ProtectedDir));
    }

    [Fact]
    public void Save_RefusesAFifo_WithoutHanging()
    {
        using var t = new TempDir();
        var (repo, commondir, mounts) = WorktreeInProject(t);
        File.Delete(commondir);
        Mkfifo(commondir);
        AssertRefusedWithNoCopiesLeft(t, repo, mounts, "isn't a regular file of at most 1 MB");
    }

    [Fact]
    public void Save_RefusesAFifoPlantedBeforePrepareMounts_WithoutHanging()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        Git.Run(repo, "worktree", "add", "-q", "in");
        var commondir = Path.Combine(git, "worktrees", "in", "commondir");
        File.Delete(commondir);
        Mkfifo(commondir);
        AssertRefusedWithNoCopiesLeft(t, repo, RepoGuard.PrepareMounts(repo, "/nonexistent-home"), "isn't a regular file");
    }

    [Fact]
    public void Save_RefusesAnOversizedFile_RatherThanSavingPartOfIt()
    {
        using var t = new TempDir();
        var (repo, commondir, mounts) = WorktreeInProject(t);
        using (var f = File.OpenWrite(commondir)) f.SetLength(1024 * 1024 + 1);
        AssertRefusedWithNoCopiesLeft(t, repo, mounts, "at most 1 MB");
    }

    [Fact]
    public void Save_TakesAFileAtTheCap()
    {
        using var t = new TempDir();
        var (repo, commondir, mounts) = WorktreeInProject(t);
        using (var f = File.OpenWrite(commondir)) f.SetLength(1024 * 1024);
        var s = NewSession(t);
        ProtectedFiles.Save(s, mounts, repo, [], []);
        var saved = Assert.Single(ProtectedFiles.Read(s)!.Files, f => f.Path == commondir);
        Assert.Equal(1024 * 1024, new FileInfo(Path.Combine(s.ProtectedDir, saved.Copy)).Length);
    }

    [Fact]
    public void Save_RefusesAnUnreadableFile()
    {
        using var t = new TempDir();
        var (repo, commondir, mounts) = WorktreeInProject(t);
        File.SetUnixFileMode(commondir, UnixFileMode.None);
        try { AssertRefusedWithNoCopiesLeft(t, repo, mounts, "fix its permissions"); }
        finally { File.SetUnixFileMode(commondir, Rw); }
    }

    [Fact]
    public void Save_RefusesASymlinkSwappedInAfterTheChecks()
    {
        using var t = new TempDir();
        var (repo, commondir, mounts) = WorktreeInProject(t);
        var target = Path.Combine(t.Path, "elsewhere");
        File.WriteAllText(target, "../..\n");
        File.Delete(commondir);
        File.CreateSymbolicLink(commondir, target);
        AssertRefusedWithNoCopiesLeft(t, repo, mounts, "isn't where pen expected it");
    }

    [Fact]
    public void Save_RefusesAFileThatsGone()
    {
        using var t = new TempDir();
        var (repo, commondir, mounts) = WorktreeInProject(t);
        File.Delete(commondir);
        AssertRefusedWithNoCopiesLeft(t, repo, mounts, "isn't where pen expected it");
    }

    [Fact]
    public void Save_RefusesASessionDirInsideAMount()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        // The common git dir is mounted, though the project dir (all CheckAllowed looks at) doesn't hold it.
        var inGit = new SessionDir(Key, t.Sub("repo", ".git", "state", "sessions", "abcd1234"));
        var e = Assert.Throws<PenException>(() => ProtectedFiles.Save(inGit, RepoGuard.PrepareMounts(wt, "/nonexistent-home"), wt, [], []));
        Assert.Contains("would be inside the sandbox", e.Message);
        Assert.False(Directory.Exists(inGit.ProtectedDir));
    }

    [Fact]
    public void TheSessionDir_IsNotInTheSandbox()
    {
        var compose = Embedded.ReadText("compose.yaml");
        var agent = compose[compose.IndexOf("  agent:", StringComparison.Ordinal)..compose.IndexOf("  proxy:", StringComparison.Ordinal)];
        var dind = compose[compose.IndexOf("  dind:\n", StringComparison.Ordinal)..compose.IndexOf("# Shared by every", StringComparison.Ordinal)];
        // Relative sources resolve in the session dir: only the proxy has any, and only its three files.
        Assert.DoesNotContain("source: ./", agent);
        Assert.DoesNotContain("source: ./", dind);
        Assert.DoesNotContain("source: .\n", compose);
        Assert.DoesNotContain("protected", compose);
    }

    // ---- Comparing and restoring ----

    [Fact]
    public void IdenticalFiles_AreUntouched()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var s = Started(t, repo, git);
        var before = new DateTime(2020, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(config, before);
        var check = End(repo, s);
        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
        Assert.Empty(check.Restored);
        Assert.Equal(before, File.GetLastWriteTimeUtc(config));
    }

    [Fact]
    public void ASessionWithNoManifest_ComparesNothing()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var s = NewSession(t);
        File.WriteAllText(s.StartedWithGit, git + "\n");
        File.WriteAllText(s.ModulesFile, "");
        var config = Path.Combine(git, "config");
        File.AppendAllText(config, "# changed\n");
        var check = End(repo, s);
        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
        Assert.Contains("# changed", File.ReadAllText(config));
    }

    [Fact]
    public void AChangedFile_IsWrittenBackInPlace_AndTheChangedVersionKept()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var mode = File.GetUnixFileMode(config);
        // A hard link sees the same inode, as another session's mount would.
        var pinned = Path.Combine(t.Path, "pinned");
        Ln(config, pinned);
        var s = Started(t, repo, git);
        File.WriteAllBytes(config, Binary);
        File.SetUnixFileMode(config, (UnixFileMode)0b111_111_111);

        var check = End(repo, s);

        Assert.Null(check.Failure);
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.Equal(original, File.ReadAllBytes(pinned));
        Assert.Equal(mode, File.GetUnixFileMode(config));
        var q = Assert.IsType<Quarantined.Moved>(Assert.Single(check.Moved));
        Assert.Equal(config, q.From);
        Assert.Equal(config + GitQuarantine.Suffix, q.To);
        Assert.Equal(Binary, File.ReadAllBytes(q.To));
        Assert.Equal(Rw, File.GetUnixFileMode(q.To));
        Assert.Contains("mv ", GitCheckWording.Warning(repo, check.Moved));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    public void ALongerOrShorterChange_LeavesExactlyTheOriginal(int factor)
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);
        File.WriteAllBytes(config, new byte[original.Length * factor + 1]);
        End(repo, s);
        Assert.Equal(original, File.ReadAllBytes(config));
    }

    [Fact]
    public void ADeletedFile_IsRecreated_WithItsMode()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        File.SetUnixFileMode(config, (UnixFileMode)0b110_110_100);
        var s = Started(t, repo, git);
        File.Delete(config);
        var check = End(repo, s);
        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
        Assert.Equal(config, Assert.Single(check.Restored).Path);
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.Equal((UnixFileMode)0b110_110_100, File.GetUnixFileMode(config));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASymlink_IsMovedAside_WithoutWritingThroughIt(bool dangling)
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);
        var target = Path.Combine(t.Path, "target");
        if (!dangling) File.WriteAllText(target, "the agent's target");
        File.Delete(config);
        File.CreateSymbolicLink(config, target);

        var check = End(repo, s);

        Assert.Null(check.Failure);
        Assert.False(PathChecks.IsLink(config));
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.True(PathChecks.IsLink(config + GitQuarantine.Suffix));
        if (dangling) Assert.False(File.Exists(target));
        else Assert.Equal("the agent's target", File.ReadAllText(target));
    }

    [Fact]
    public void ASymlinkToAnIdenticalFile_IsStillMovedAside()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var s = Started(t, repo, git);
        var target = Path.Combine(repo, "same");
        File.Copy(config, target);
        File.Delete(config);
        File.CreateSymbolicLink(config, target);
        var check = End(repo, s);
        Assert.False(PathChecks.IsLink(config));
        Assert.Single(check.Moved);
    }

    [Fact]
    public void ADirectory_IsMovedAside()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);
        File.Delete(config);
        Directory.CreateDirectory(Path.Combine(config, "inside"));
        var check = End(repo, s);
        Assert.Null(check.Failure);
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.True(Directory.Exists(Path.Combine(config + GitQuarantine.Suffix, "inside")));
    }

    [Fact]
    public void AFifo_IsMovedAside_WithoutHanging()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);
        File.Delete(config);
        Mkfifo(config);
        CheckResult? check = null;
        AssertIn10s(() => check = End(repo, s));
        Assert.Null(check!.Failure);
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.True(File.Exists(config + GitQuarantine.Suffix));
    }

    [Fact]
    public void AnOversizedFile_IsMovedAside_NotRead()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);
        using (var f = File.OpenWrite(config)) f.SetLength(1L << 40); // sparse: reading it would take forever
        CheckResult? check = null;
        AssertIn10s(() => check = End(repo, s));
        Assert.Null(check!.Failure);
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.Equal(1L << 40, new FileInfo(config + GitQuarantine.Suffix).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0b100_000_000)]
    public void AFileMadeUnreadableOrUnwritable_IsMovedAsideAndRecreated(int mode)
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var was = File.GetUnixFileMode(config);
        var s = Started(t, repo, git);
        File.AppendAllText(config, "[core]\n fsmonitor = /evil\n");
        File.SetUnixFileMode(config, (UnixFileMode)mode);
        var check = End(repo, s);
        File.SetUnixFileMode(config + GitQuarantine.Suffix, Rw); // for clean-up
        Assert.Null(check.Failure);
        Assert.Equal(original, File.ReadAllBytes(config));
        Assert.Equal(was, File.GetUnixFileMode(config));
    }

    [Fact]
    public void OnlyAModeChange_IsPutBack_AndNothingQuarantined()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var was = File.GetUnixFileMode(config);
        var s = Started(t, repo, git);
        File.SetUnixFileMode(config, (UnixFileMode)0b111_111_111);
        var check = End(repo, s);
        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
        Assert.Contains("mode", Assert.Single(check.Restored).What);
        Assert.Equal(was, File.GetUnixFileMode(config));
    }

    [Fact]
    public void ADirectorySwappedForASymlink_IsNotWrittenThrough()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var sub = t.Sub("repo", "sub");
        var inc = Path.Combine(sub, "authorized_keys");
        File.WriteAllText(inc, "[user]\n");
        File.AppendAllText(Path.Combine(git, "config"), "[include]\n path = ../sub/authorized_keys\n");
        var s = Started(t, repo, git);
        // The way an agent could aim the restore at a file of the user's.
        var ssh = t.Sub("ssh");
        var victim = Path.Combine(ssh, "authorized_keys");
        File.WriteAllText(victim, "the user's keys");
        Directory.Move(sub, sub + "2");
        File.CreateSymbolicLink(sub, ssh);

        var check = End(repo, s);

        Assert.Null(check.Failure);
        Assert.Equal("the user's keys", File.ReadAllText(victim));
        Assert.False(File.Exists(sub));
        Assert.Equal(sub, Assert.Single(check.Moved, q => q.From == sub).From);
        Assert.True(PathChecks.IsLink(sub + GitQuarantine.Suffix));
    }

    [Fact]
    public void AFileInADirectoryThatsGone_IsLeftGone()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        Git.Run(repo, "worktree", "add", "-q", "in");
        var s = Started(t, repo, git);
        Git.Run(repo, "worktree", "remove", "in");
        var entry = Path.Combine(git, "worktrees", "in");
        Assert.False(Directory.Exists(entry));
        var check = End(repo, s);
        Assert.Null(check.Failure);
        Assert.Empty(check.Moved);
        Assert.False(Directory.Exists(entry));
    }

    [Fact]
    public void ADirectoryThatCantBeEntered_FailsTheCheck()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var sub = t.Sub("repo", "sub");
        File.WriteAllText(Path.Combine(sub, "i.cfg"), "");
        File.AppendAllText(Path.Combine(git, "config"), "[include]\n path = ../sub/i.cfg\n");
        var s = Started(t, repo, git);
        File.AppendAllText(Path.Combine(sub, "i.cfg"), "[core]\n fsmonitor = /evil\n");
        File.SetUnixFileMode(sub, UnixFileMode.None);
        try
        {
            var check = End(repo, s);
            Assert.NotNull(check.Failure);
            Assert.Contains(check.Moved, q => q is Quarantined.Failed && q.Why.Contains("COULD NOT RESTORE", StringComparison.Ordinal));
        }
        finally { File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void WhenItCantRestore_TheCheckFails_AndTheRestStillRuns()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var s = Started(t, repo, git);
        var config = Path.Combine(git, "config");
        File.AppendAllText(config, "[core]\n fsmonitor = /evil\n");
        File.WriteAllText(Path.Combine(git, "commondir"), "planted\n");
        var copy = Path.Combine(s.ProtectedDir, ProtectedFiles.Read(s)!.Files[0].Copy);
        File.Delete(copy);

        var check = End(repo, s);

        Assert.NotNull(check.Failure);
        Assert.Contains(check.Moved, q => q.From == config && q is Quarantined.Failed);
        Assert.True(File.Exists(Path.Combine(git, "commondir" + GitQuarantine.Suffix)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"version":99,"files":[],"readOnlyDirs":[]}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"files":[{"path":"/x","copy":"../x","mode":420,"adopted":false}],"readOnlyDirs":[]}""")]
    public void ADamagedManifest_FailsTheCheck_AndTheRestStillRuns(string manifest)
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var s = Started(t, repo, git);
        File.WriteAllText(s.ProtectedManifest, manifest);
        File.WriteAllText(Path.Combine(git, "commondir"), "planted\n");
        var check = End(repo, s);
        Assert.NotNull(check.Failure);
        Assert.True(File.Exists(Path.Combine(git, "commondir" + GitQuarantine.Suffix)));
    }

    [Fact]
    public void ARetriedEnd_DoesntWriteStaleCopiesOverLaterHostEdits()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var s = Started(t, repo, git);
        File.AppendAllText(config, "# changed\n");
        Assert.Single(End(repo, s).Moved);
        Assert.False(Directory.Exists(s.ProtectedDir));

        // The stack didn't come down, so the end runs again later; meanwhile the user edits on the host.
        File.AppendAllText(config, "# the user's edit\n");
        var again = End(repo, s);
        Assert.Empty(again.Moved);
        Assert.Null(again.Failure);
        Assert.Contains("# the user's edit", File.ReadAllText(config));
    }

    [Fact]
    public void AnEndWhoseRestoreFailed_IsRetryable_AndItsCopiesStayUntilItWorks()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);
        File.AppendAllText(config, "[core]\n fsmonitor = /evil\n");
        File.SetUnixFileMode(git, UnixFileMode.None);
        try
        {
            var check = End(repo, s);
            Assert.True(check.RestoreIncomplete);
            Assert.NotNull(check.Failure);
            Assert.True(File.Exists(s.ProtectedManifest));
        }
        finally { File.SetUnixFileMode(git, (UnixFileMode)0b111_101_101); }

        var retry = End(repo, s);
        Assert.False(retry.RestoreIncomplete);
        Assert.Null(retry.Failure);
        Assert.Equal(original, File.ReadAllBytes(config));
    }

    [Fact]
    public void WhenTheRestoreStopsAfterPuttingFilesBack_WhatItDidIsReported_AndHazardsAreStillSwept()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root writes to any directory");
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);
        File.AppendAllText(config, "[core]\n fsmonitor = /evil\n");
        var msgnum = Path.Combine(t.Sub("repo", ".git", "rebase-merge"), "msgnum");
        File.WriteAllText(msgnum, "$(evil)\n");
        var commondir = Path.Combine(git, "commondir");
        File.WriteAllText(commondir, "planted\n");
        File.SetUnixFileMode(git, (UnixFileMode)0b101_101_101);
        // The manifest can't be renamed, so the restore throws once every file is back.
        File.SetUnixFileMode(s.Path, (UnixFileMode)0b101_101_101);
        try
        {
            var check = End(repo, s);

            Assert.NotNull(check.Failure);
            Assert.True(check.RestoreIncomplete);
            Assert.Equal(original, File.ReadAllBytes(config));
            // The restore's entry first, then the sweep's; the rest of the check didn't run.
            Assert.Equal([config, msgnum], check.Moved.Select(q => q.From));
            Assert.Equal(config + GitQuarantine.Suffix, ((Quarantined.Moved)check.Moved[0]).To);
            Assert.Equal(msgnum + GitQuarantine.Suffix, ((Quarantined.Moved)check.Moved[1]).To);
            Assert.Equal(git, Assert.Single(check.ModeChanges).Path);
            Assert.Empty(check.Restored);
            Assert.Empty(check.RestoreBlocked);
            Assert.True(File.Exists(commondir));
            Assert.True(File.Exists(s.ProtectedManifest));
        }
        finally
        {
            File.SetUnixFileMode(s.Path, (UnixFileMode)0b111_101_101);
            File.SetUnixFileMode(git, (UnixFileMode)0b111_101_101);
        }
    }

    [Fact]
    public void WhenWhereTheSessionStartedCantBeRead_NothingIsRestored_AndItsAFailure()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads any file");
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var s = Started(t, repo, git);
        File.AppendAllText(config, "# changed\n");
        File.WriteAllText(Path.Combine(t.Sub("repo", ".git", "rebase-merge"), "msgnum"), "$(evil)\n");
        File.SetUnixFileMode(s.StartedWithGit, UnixFileMode.None);
        try
        {
            var check = End(repo, s);

            Assert.NotNull(check.Failure);
            Assert.True(check.RestoreIncomplete);
            Assert.Empty(check.Moved);
            Assert.Contains("# changed", File.ReadAllText(config));
            Assert.True(File.Exists(s.ProtectedManifest));
        }
        finally { File.SetUnixFileMode(s.StartedWithGit, Rw); }
    }

    [Fact]
    public void WhenTheModuleListCantBeRead_TheFilesAreBack_AndHazardsAreStillSwept()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads any file");
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);
        File.AppendAllText(config, "# changed\n");
        var msgnum = Path.Combine(t.Sub("repo", ".git", "rebase-merge"), "msgnum");
        File.WriteAllText(msgnum, "$(evil)\n");
        var commondir = Path.Combine(git, "commondir");
        File.WriteAllText(commondir, "planted\n");
        File.SetUnixFileMode(s.ModulesFile, UnixFileMode.None);
        try
        {
            var check = End(repo, s);

            Assert.NotNull(check.Failure);
            Assert.False(check.RestoreIncomplete);
            Assert.Equal(original, File.ReadAllBytes(config));
            Assert.Equal([config, msgnum], check.Moved.Select(q => q.From));
            Assert.True(File.Exists(commondir)); // the rest of the check didn't run
        }
        finally { File.SetUnixFileMode(s.ModulesFile, Rw); }
    }

    /// <summary>The review's replay: the plant can't stop the write-back by denying the quarantine file.</summary>
    [Fact]
    public void Replay_Chmod555OnTheGitDir_DoesntStopTheRestore()
    {
        using var t = new TempDir();
        var (hook, marker) = PlantableFsmonitor(t);
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);
        File.AppendAllText(config, $"[core]\n fsmonitor = {hook}\n");
        AssertRuns(repo, marker);
        File.SetUnixFileMode(git, (UnixFileMode)0b101_101_101);

        var check = End(repo, s);

        Assert.Null(check.Failure);
        Assert.False(check.RestoreIncomplete);
        Assert.Equal(original, File.ReadAllBytes(config));
        var q = Assert.IsType<Quarantined.Moved>(Assert.Single(check.Moved));
        Assert.True(File.Exists(q.To));
        Assert.Contains(check.ModeChanges, o => o.Path == git);
        AssertDoesntRun(repo, marker);
    }

    [Fact]
    public void WhenTheQuarantineCantBeWritten_TheOriginalIsStillBack_AndItsAFailure()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        // At the longest name, the quarantine file's name is too long to create.
        var inc = Path.Combine(repo, new string('b', 255));
        File.WriteAllText(inc, "");
        File.AppendAllText(Path.Combine(git, "config"), $"[include]\n path = ../{Path.GetFileName(inc)}\n");
        var s = Started(t, repo, git);
        File.AppendAllText(inc, "# changed\n");

        var check = End(repo, s);

        Assert.NotNull(check.Failure);
        Assert.True(check.RestoreIncomplete);
        Assert.Empty(File.ReadAllText(inc));
        Assert.True(File.Exists(s.ProtectedManifest));
        Assert.False(End(repo, s).RestoreIncomplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AProtectedFilesDirectoryGoneOrReplaced_IsReported(bool replacedByFile)
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var sub = t.Sub("repo", "sub");
        var inc = Path.Combine(sub, "i.cfg");
        File.WriteAllText(inc, "");
        File.AppendAllText(Path.Combine(git, "config"), "[include]\n path = ../sub/i.cfg\n");
        var s = Started(t, repo, git);
        Directory.Delete(sub, recursive: true);
        if (replacedByFile) File.WriteAllText(sub, "");

        var check = End(repo, s);

        Assert.Null(check.Failure);
        var r = Assert.Single(check.Restored);
        Assert.Equal(inc, r.Path);
        Assert.Contains(replacedByFile ? "replaced by a file" : "was removed", r.What);
        Assert.Equal(replacedByFile, File.Exists(sub));
    }

    /// <summary>The review's replay: a rename means the plant has the name in another case, which a plain path match misses.</summary>
    [Fact]
    public void Replay_ACaseRename_DoesntDefeatInheritingTheBaseline()
    {
        using var t = new TempDir();
        var (hook, marker) = PlantableFsmonitor(t);
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var upper = Path.Combine(git, "CONFIG");
        Assert.SkipUnless(File.Exists(upper), "the file system is case-sensitive");
        var original = File.ReadAllBytes(config);
        var first = Started(t, repo, git, "aaaaaaaa");

        // A's agent, past the mount: the on-disk name of the file becomes CONFIG.
        File.Move(upper, Path.Combine(git, "t"));
        File.WriteAllText(upper, File.ReadAllText(Path.Combine(git, "t")) + $"[core]\n fsmonitor = {hook}\n");
        Assert.Equal(upper, Native.RealPath(config));
        AssertRuns(repo, marker);

        var second = Started(t, repo, git, "bbbbbbbb");

        var a = End(repo, first);
        Assert.Null(a.Failure);
        Assert.Equal(original, File.ReadAllBytes(config));

        var b = End(repo, second);
        Assert.Null(b.Failure);
        Assert.Empty(b.Moved);
        Assert.Equal(original, File.ReadAllBytes(config));
        AssertDoesntRun(repo, marker);
    }

    [Fact]
    public void ANewConfigWorktreePlantedWhileASessionRuns_IsNeverWrittenBack()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var first = Started(t, repo, git, "aaaaaaaa");
        var planted = Path.Combine(git, "config.worktree");
        File.WriteAllText(planted, "[core]\n fsmonitor = /evil\n");
        var second = Started(t, repo, git, "bbbbbbbb");
        Assert.True(Assert.Single(ProtectedFiles.Read(second)!.Files, f => f.Path == planted).Adopted);

        // Gone: not recreated from what the agent wrote.
        File.Delete(planted);
        var check = End(repo, second);
        Assert.Null(check.Failure);
        Assert.Empty(check.Restored);
        Assert.False(File.Exists(planted));
    }

    [Fact]
    public void AnAdoptedConfigWorktree_ThatChanged_IsMovedAside_NotRestored()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var first = Started(t, repo, git, "aaaaaaaa");
        var planted = Path.Combine(git, "config.worktree");
        File.WriteAllText(planted, "# first\n");
        var second = Started(t, repo, git, "bbbbbbbb");
        File.WriteAllText(planted, "# second\n");
        var check = End(repo, second);
        Assert.Null(check.Failure);
        Assert.Equal(planted, Assert.Single(check.Moved).From);
        Assert.False(File.Exists(planted));
    }

    [Fact]
    public void AConfigWorktreeTheOtherSessionCantWrite_IsNotAdopted()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        var file = Path.Combine(git, "worktrees", "wt", "config.worktree");
        File.WriteAllText(file, "");
        var first = Started(t, repo, git, "aaaaaaaa"); // the worktree's entry is read-only as a whole there
        var second = Started(t, wt, git, "bbbbbbbb");
        Assert.False(Assert.Single(ProtectedFiles.Read(second)!.Files, f => f.Path == file).Adopted);
    }

    // ---- Module repos an open session didn't list ----

    /// <summary>A repo with the submodules m and m2, and a session's files saved with m unlisted.</summary>
    private static (string Repo, string Git, SessionDir Session) SavedWithMUnlisted(TempDir t)
    {
        var (repo, git) = Repo(t);
        var lib = Git.Repo(t.Sub("lib"));
        Git.Run(repo, [.. AllowFile, "submodule", "add", "-q", lib, "m"]);
        Git.Run(repo, [.. AllowFile, "submodule", "add", "-q", lib, "m2"]);
        Git.Run(Path.Combine(repo, "m"), "config", "extensions.worktreeConfig", "true");
        Git.Run(Path.Combine(repo, "m"), "config", "--worktree", "user.name", "m");
        var s = NewSession(t);
        File.WriteAllText(s.StartedWithGit, git + "\n");
        s.WriteModules([Path.Combine(git, "modules", "m2")]);
        ProtectedFiles.Save(s, RepoGuard.PrepareMounts(repo, "/nonexistent-home"), repo, [],
            [Path.Combine(git, "modules", "m")]);
        return (repo, git, s);
    }

    [Fact]
    public void EveryFileInAnUnlistedModuleRepo_IsAdopted_AndNothingElse()
    {
        using var t = new TempDir();
        var (_, git, s) = SavedWithMUnlisted(t);
        var files = ProtectedFiles.Read(s)!.Files;
        var m = Path.Combine(git, "modules", "m");

        Assert.Equal(
            [Path.Combine(m, "config"), Path.Combine(m, "config.worktree")],
            files.Where(f => f.Adopted).Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.False(Assert.Single(files, f => f.Path == Path.Combine(git, "modules", "m2", "config")).Adopted);
        Assert.False(Assert.Single(files, f => f.Path == Path.Combine(git, "config")).Adopted);
    }

    [Fact]
    public void AFileInAnUnlistedModuleRepo_IsNeverWrittenBack()
    {
        using var t = new TempDir();
        var (repo, git, s) = SavedWithMUnlisted(t);
        var config = Path.Combine(git, "modules", "m", "config");
        File.WriteAllText(config, "[core]\n\tbare = false\n");
        var otherConfig = Path.Combine(git, "modules", "m2", "config");
        var original = File.ReadAllBytes(otherConfig);
        File.AppendAllText(otherConfig, "[core]\n\tfsmonitor = /evil\n");

        var check = End(repo, s);

        Assert.DoesNotContain(check.Moved, q => q.From == config);
        Assert.DoesNotContain(check.Restored, r => r.Path == config);
        Assert.Contains(check.Moved, q => q.From == otherConfig);
        Assert.Equal(original, File.ReadAllBytes(otherConfig));
    }

    [Fact]
    public void AFileAnOpenSessionAdopted_IsAdoptedAgain_WhateverItsName()
    {
        using var t = new TempDir();
        var (repo, git, older) = SavedWithMUnlisted(t);
        File.WriteAllText(older.ComposeFile, "");
        var s = NewSession(t, "bbbbbbbb");

        ProtectedFiles.Save(s, RepoGuard.PrepareMounts(repo, "/nonexistent-home"), repo, [older], []);

        Assert.True(Assert.Single(ProtectedFiles.Read(s)!.Files,
            f => f.Path == Path.Combine(git, "modules", "m", "config")).Adopted);
        Assert.False(Assert.Single(ProtectedFiles.Read(s)!.Files,
            f => f.Path == Path.Combine(git, "modules", "m2", "config")).Adopted);
    }

    // ---- Includes Pen must not miss ----

    /// <summary>The review's replay: running in a worktree, the main checkout's submodule includes a file the sandbox can write.</summary>
    [Fact]
    public void Replay_AModuleIncludeInTheCommonGitDir_FromALinkedWorktreeSession()
    {
        using var t = new TempDir();
        var (hook, marker) = PlantableFsmonitor(t);
        var lib = Git.Repo(t.Sub("lib"));
        var (repo, git) = Repo(t);
        Git.Run(repo, [.. AllowFile, "submodule", "add", "-q", lib, "lib"]);
        var moduleConfig = Path.Combine(git, "modules", "lib", "config");
        var inc = Path.Combine(git, "x.cfg");
        File.WriteAllText(inc, "");
        File.AppendAllText(moduleConfig, "[include]\n path = ../../x.cfg\n");
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);

        var mounts = RepoGuard.PrepareMounts(wt, "/nonexistent-home");
        Assert.Contains(mounts, m => m.Path == inc && m.ReadOnly && m.Verify);
        var s = Started(t, wt, git);

        File.WriteAllText(inc, $"[core]\n fsmonitor = {hook}\n");
        var checkout = Path.Combine(repo, "lib");
        AssertRuns(checkout, marker);

        Assert.Null(End(wt, s).Failure);
        AssertDoesntRun(checkout, marker);
    }

    [Fact]
    public void AMissingModuleIncludeInTheCommonGitDir_IsCreated_FromALinkedWorktreeSession()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var module = t.Sub("repo", ".git", "modules", "lib");
        Git.Run(module, "init", "-q", "--bare");
        File.AppendAllText(Path.Combine(module, "config"), "[include]\n path = ../../x.cfg\n");
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        var inc = Path.Combine(git, "x.cfg");
        Assert.Contains(RepoGuard.PrepareMounts(wt, "/nonexistent-home"), m => m.Path == inc && m.ReadOnly);
        Assert.True(File.Exists(inc));
    }

    [Fact]
    public void AnIncludeFromAModuleUnderAReadOnlyEntry_IsStillProtected()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var ext = Path.Combine(t.Path, "ext");
        Git.Run(repo, "worktree", "add", "-q", ext);
        var module = t.Sub("repo", ".git", "worktrees", "ext", "modules", "lib");
        Git.Run(module, "init", "-q", "--bare");
        var inc = Path.Combine(git, "y.cfg");
        File.WriteAllText(inc, "");
        File.AppendAllText(Path.Combine(module, "config"), "[include]\n path = ../../../../y.cfg\n");
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        Assert.Contains(RepoGuard.PrepareMounts(wt, "/nonexistent-home"), m => m.Path == inc && m.ReadOnly && m.Verify);
    }

    [Fact]
    public void ASecondSession_TakesTheFirstOnesBaseline_NotWhatItsAgentWrote()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var first = Started(t, repo, git, "aaaaaaaa");
        File.AppendAllText(config, "[core]\n fsmonitor = /evil\n"); // first's agent, past its mount
        var second = Started(t, repo, git, "bbbbbbbb");

        End(repo, first);
        Assert.Equal(original, File.ReadAllBytes(config));

        // Left alone, this session's end would put the planted config back.
        var check = End(repo, second);
        Assert.Empty(check.Moved);
        Assert.Equal(original, File.ReadAllBytes(config));
    }

    [Fact]
    public void ASecondSession_FallsBackToTheFile_WhenTheFirstHasJustEnded()
    {
        using var t = new TempDir();
        var (repo, git) = Repo(t);
        var first = Started(t, repo, git, "aaaaaaaa");
        Directory.Delete(first.ProtectedDir, recursive: true);
        var second = Started(t, repo, git, "bbbbbbbb");
        Assert.NotEmpty(ProtectedFiles.Read(second)!.Files);
    }

    [Fact]
    public void TheWarningAndReport_NameWhatWasRestored()
    {
        var evil = "/p/.git/\u001b[2Jconfig";
        var w = GitCheckWording.Warning("/p", [new Quarantined.Moved(evil, evil + GitQuarantine.Suffix, "it changed")]);
        Assert.Contains("moved out of git's way", w);
        Assert.DoesNotContain('\u001b', w);
    }

    // ---- The review's replays: a write past the mount that really runs code on host git ----

    private static (string Hook, string Marker) PlantableFsmonitor(TempDir t)
    {
        var marker = Path.Combine(t.Path, "ran");
        var hook = Path.Combine(t.Path, "hook.sh");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
        return (hook, marker);
    }

    /// <summary>Writes through a case variant of the name, as the agent can. A case-sensitive filesystem needs the real name for the write to arrive at all.</summary>
    private static void WriteThroughVariant(string real, string variant, string text)
    {
        File.WriteAllText(variant, text);
        if (File.ReadAllText(real) != text)
        {
            File.Delete(variant);
            File.WriteAllText(real, text);
        }
    }

    private static string FsmonitorRepo(TempDir t, string hook)
    {
        var planted = Git.Repo(t.Sub("planted"));
        Git.Run(planted, "config", "core.fsmonitor", hook);
        return Path.Combine(planted, ".git");
    }

    private static void AssertRuns(string dir, string marker)
    {
        Git.Run(dir, "status");
        Assert.True(File.Exists(marker), "the attack didn't run: the replay proves nothing");
        File.Delete(marker);
    }

    private static void AssertDoesntRun(string dir, string marker)
    {
        Git.Run(dir, "status");
        Assert.False(File.Exists(marker), "the planted fsmonitor ran");
    }

    [Fact]
    public void Replay_ConfigThroughAnUpperCaseName()
    {
        using var t = new TempDir();
        var (hook, marker) = PlantableFsmonitor(t);
        var (repo, git) = Repo(t);
        var config = Path.Combine(git, "config");
        var original = File.ReadAllBytes(config);
        var s = Started(t, repo, git);

        WriteThroughVariant(config, Path.Combine(git, "CONFIG"), File.ReadAllText(config) + $"[core]\n fsmonitor = {hook}\n");
        AssertRuns(repo, marker);

        var check = End(repo, s);

        Assert.Null(check.Failure);
        Assert.Equal(config, Assert.Single(check.Moved).From);
        Assert.Equal(original, File.ReadAllBytes(config));
        AssertDoesntRun(repo, marker);
    }

    [Fact]
    public void Replay_AWorktreesGitFileThroughAnUpperCaseName()
    {
        using var t = new TempDir();
        var (hook, marker) = PlantableFsmonitor(t);
        var (repo, git) = Repo(t);
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        var gitFile = Path.Combine(wt, ".git");
        var original = File.ReadAllBytes(gitFile);
        var s = Started(t, wt, git);
        var planted = FsmonitorRepo(t, hook);

        WriteThroughVariant(gitFile, Path.Combine(wt, ".GIT"), $"gitdir: {planted}\n");
        AssertRuns(wt, marker);

        var check = End(wt, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == gitFile);
        Assert.Equal(original, File.ReadAllBytes(gitFile));
        AssertDoesntRun(wt, marker);
    }

    [Fact]
    public void Replay_AWorktreesCommondirThroughAnUpperCaseName()
    {
        using var t = new TempDir();
        var (hook, marker) = PlantableFsmonitor(t);
        var (repo, git) = Repo(t);
        Git.Run(repo, "worktree", "add", "-q", "in");
        var wt = Path.Combine(repo, "in");
        var commondir = Path.Combine(git, "worktrees", "in", "commondir");
        var original = File.ReadAllBytes(commondir);
        var s = Started(t, repo, git);
        var planted = FsmonitorRepo(t, hook);

        WriteThroughVariant(commondir, Path.Combine(git, "worktrees", "in", "COMMONDIR"), planted + "\n");
        AssertRuns(wt, marker);

        var check = End(repo, s);

        Assert.Null(check.Failure);
        Assert.Contains(check.Moved, q => q.From == commondir);
        Assert.Equal(original, File.ReadAllBytes(commondir));
        AssertDoesntRun(wt, marker);
    }

    [Fact]
    public void Replay_ASubmodulesConfig()
    {
        using var t = new TempDir();
        var (hook, marker) = PlantableFsmonitor(t);
        var lib = Git.Repo(t.Sub("lib"));
        var (repo, git) = Repo(t);
        Git.Run(repo, [.. AllowFile, "submodule", "add", "-q", lib, "lib"]);
        var config = Path.Combine(git, "modules", "lib", "config");
        var checkout = Path.Combine(repo, "lib");
        var s = Started(t, repo, git);

        WriteThroughVariant(config, Path.Combine(git, "modules", "lib", "CONFIG"), File.ReadAllText(config) + $"[core]\n fsmonitor = {hook}\n");
        AssertRuns(checkout, marker);

        var check = End(repo, s);

        Assert.Null(check.Failure);
        AssertDoesntRun(checkout, marker);
    }
}
