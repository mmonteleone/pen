namespace Pen.Tests;

/// <summary>
/// D2: pen clean --stale's classification of project keys, and removal of the ones it
/// may remove. Runs alone: these retake a project lock straight after releasing
/// it, and a process start in a parallel test can hold a shared lock fd until
/// its exec, so the lock could outlive the release.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public class CleanStaleTests
{
    /// <summary>Records <paramref name="dir"/> as a project the way a pen command does, returning its key.</summary>
    private static string Record(HostEnv host, string dir)
    {
        var p = Project.Resolve(dir, host);
        ProjectRuntime.EnsureRuntimeDir(host, p);
        return p.Key;
    }

    private static CleanStaleCommand.Entry Only(IReadOnlyList<CleanStaleCommand.Entry> entries, string key) => Assert.Single(entries, e => e.Key == key);

    [Fact]
    public void ARepoThatsStillThereIsLive()
    {
        using var t = new TempDir();
        var host = t.Host();
        var key = Record(host, Git.Repo(t.Sub("repo")));
        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.Equal(CleanStaleCommand.Status.Live, e.Status);
        Assert.False(e.Removable);
    }

    [Fact]
    public void APlainDirThatsStillThereIsLive()
    {
        using var t = new TempDir();
        var host = t.Host();
        var key = Record(host, t.Sub("plain"));
        Assert.Equal(CleanStaleCommand.Status.Live, Only(CleanStaleCommand.Find(host, [], []), key).Status);
    }

    [Fact]
    public void ADeletedProjectIsMissing()
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var key = Record(host, repo);
        Directory.Delete(repo, recursive: true);
        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.Equal(CleanStaleCommand.Status.Missing, e.Status);
        Assert.Equal(repo, Assert.IsType<CleanStaleCommand.Fate.Missing>(e.Fate).Dir);
        Assert.True(e.Removable);
    }

    [Fact]
    public void AFileWhereTheDirWasIsMissing()
    {
        using var t = new TempDir();
        var host = t.Host();
        var dir = t.Sub("plain");
        var key = Record(host, dir);
        Directory.Delete(dir);
        File.WriteAllText(dir, "");
        Assert.Equal(CleanStaleCommand.Status.Missing, Only(CleanStaleCommand.Find(host, [], []), key).Status);
    }

    [Fact]
    public void ADirThatNowResolvesElsewhereIsMoved()
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var key = Record(host, repo);
        // The project moved away, and its old path is now a symlink to another repo.
        Directory.Move(repo, Path.Combine(t.Path, "moved"));
        var other = Git.Repo(t.Sub("other"));
        File.CreateSymbolicLink(repo, other);

        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.Equal(CleanStaleCommand.Status.Moved, e.Status);
        Assert.Equal(Project.ComputeKey(other), Assert.IsType<CleanStaleCommand.Fate.Moved>(e.Fate).CurrentKey);
        Assert.True(e.Removable);
    }

    [Fact]
    public void AMainCheckoutThatBecameAnotherReposWorktreeIsMoved()
    {
        using var t = new TempDir();
        var host = t.Host();
        var dir = t.Sub("x");
        var key = Record(host, dir);
        Directory.Delete(dir);
        var main = Git.Repo(t.Sub("main"));
        Git.Run(main, "worktree", "add", "-q", dir);
        var moved = Assert.IsType<CleanStaleCommand.Fate.Moved>(CleanStaleCommand.Classify(key, dir));
        Assert.Equal(Project.ComputeKey(main), moved.CurrentKey);
    }

    [Fact]
    public void AWorktreesProjectIsLiveByItsMainCheckout_AndMissingWithIt()
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        var key = Record(host, wt);
        Assert.Equal(Project.ComputeKey(repo), key);
        Assert.Equal(CleanStaleCommand.Status.Live, Only(CleanStaleCommand.Find(host, [], []), key).Status);

        // The worktree going doesn't matter; the main checkout going does.
        Git.Run(repo, "worktree", "remove", "--force", wt);
        Assert.Equal(CleanStaleCommand.Status.Live, Only(CleanStaleCommand.Find(host, [], []), key).Status);
        Directory.Delete(repo, recursive: true);
        Assert.Equal(CleanStaleCommand.Status.Missing, Only(CleanStaleCommand.Find(host, [], []), key).Status);
    }

    [Fact]
    public void WithoutAProjectDirFileItsUnknown()
    {
        using var t = new TempDir();
        var host = t.Host();
        var key = Record(host, t.Sub("plain"));
        File.Delete(Path.Combine(host.GetRuntimeDir(key), ProjectRuntime.ProjectDirFile));
        var e = Only(CleanStaleCommand.Find(host, [$"{key}_home"], []), key);
        Assert.Equal(CleanStaleCommand.Status.Unknown, e.Status);
        Assert.False(e.Removable);
        Assert.Equal(CleanStaleCommand.Status.Unknown, CleanStaleCommand.Classify(key, null).Status);
    }

    [Fact]
    public void VolumesWithoutARuntimeDirAreUnknown_AndGroupedByKey()
    {
        using var t = new TempDir();
        var host = t.Host();
        const string key = "pen-gone-0123abcd";
        var e = Only(CleanStaleCommand.Find(host, [$"{key}_nuget", $"{key}_home", $"{key}_dind"], []), key);
        Assert.Equal(CleanStaleCommand.Status.Unknown, e.Status);
        Assert.Null(e.RuntimeDir);
        Assert.Equal([$"{key}_dind", $"{key}_home", $"{key}_nuget"], e.Volumes);
    }

    [Fact]
    public void VolumesImagesAndRuntimeDirsJoinUnderTheirKey()
    {
        using var t = new TempDir();
        var host = t.Host();
        var a = Record(host, Git.Repo(t.Sub("a")));
        var b = Record(host, t.Sub("b"));
        var entries = CleanStaleCommand.Find(host,
            [$"{a}_home", $"{a}_nuget", "pen-c-89abcdef_home", "unrelated_home", $"{a}_other", "pen-c-89abcdef", "pen_home"],
            [$"{a}:latest", $"{a}-0011aabb:latest", $"{a}:old", $"{a}-x-0011aabb:latest", "pen:latest", $"{b}x:latest"]);

        Assert.Equal([a, b, "pen-c-89abcdef"], entries.Select(e => e.Key).Order(StringComparer.Ordinal));
        var ea = Only(entries, a);
        Assert.Equal([$"{a}_home", $"{a}_nuget"], ea.Volumes);
        Assert.Equal([$"{a}-0011aabb:latest", $"{a}:latest"], ea.Images);
        Assert.Equal(host.GetRuntimeDir(a), ea.RuntimeDir);
        var eb = Only(entries, b);
        Assert.Empty(eb.Volumes);
        Assert.Empty(eb.Images);
        Assert.Equal(CleanStaleCommand.Status.Live, eb.Status);
    }

    [Fact]
    public void OtherDirsInTheStateRootArentKeys()
    {
        using var t = new TempDir();
        var host = t.Host();
        t.Sub("home", ".local", "state", "pen", "not-a-key");
        t.Sub("home", ".local", "state", "pen", "pen-x-nothex12");
        Assert.Empty(CleanStaleCommand.Find(host, [], []));
    }

    [Theory]
    [InlineData("pen-foo-0123abcd_home", "pen-foo-0123abcd")]
    [InlineData("pen-foo-0123abcd_nuget", "pen-foo-0123abcd")]
    [InlineData("pen-a-b_c-0123abcd_dind", "pen-a-b_c-0123abcd")]
    [InlineData("pen-foo-0123abcd_cache", null)]
    [InlineData("pen-foo_home", null)]
    [InlineData("pen-foo-0123ABCD_home", null)]
    [InlineData("xpen-foo-0123abcd_home", null)]
    public void KeyOfVolume(string volume, string? key) => Assert.Equal(key, CleanStaleCommand.FindKeyOfVolume(volume));

    [Theory]
    [InlineData("pen-foo-0123abcd:latest", true)]
    [InlineData("pen-foo-0123abcd-89abcdef:latest", true)]
    [InlineData("pen-foo-0123abcd:v1", false)]
    [InlineData("pen-foo-0123abcd-89abcdef-89abcdef:latest", false)]
    [InlineData("pen-foo-0123abcd-bar-89abcdef:latest", false)]
    [InlineData("pen-foo-0123abcdx:latest", false)]
    [InlineData("pen-foo-0123abcd:0123456789abcdef", true)]
    [InlineData("pen-foo-0123abcd-89abcdef:0123456789abcdef", true)]
    [InlineData("pen-foo-0123abcd:0123456789abcde", false)]
    [InlineData("pen-foo-0123abcd:0123456789abcdef0", false)]
    [InlineData("pen-foo-0123abcd:0123456789ABCDEF", false)]
    [InlineData("pen-foo-0123abcd-89abcdef-89abcdef:0123456789abcdef", false)]
    [InlineData("pen-foo-0123abcdx:0123456789abcdef", false)]
    [InlineData("pen-tools:0123456789abcdef", false)]
    public void IsImageOf(string image, bool expected) => Assert.Equal(expected, CleanStaleCommand.IsImageOf("pen-foo-0123abcd", image));

    [Fact]
    public void AProjectNamedToolsDoesntClaimTheSharedToolsImages()
    {
        Assert.False(CleanStaleCommand.IsImageOf("pen-tools-0123abcd", "pen-tools:0123456789abcdef"));
        Assert.True(CleanStaleCommand.IsImageOf("pen-tools-0123abcd", "pen-tools-0123abcd:0123456789abcdef"));
    }

    [Fact]
    public void TheListingGroupsByStatus_WithManualCommandsForUnknownKeys()
    {
        using var t = new TempDir();
        var host = t.Host();
        var live = Git.Repo(t.Sub("live"));
        var liveKey = Record(host, live);
        var gone = Git.Repo(t.Sub("gone"));
        var goneKey = Record(host, gone);
        Directory.Delete(gone, recursive: true);
        var text = CleanStaleCommand.Render(host, CleanStaleCommand.Find(host,
            [$"{liveKey}_home", $"{goneKey}_home", $"{goneKey}_nuget", "pen-old-0123abcd_home"], [$"{goneKey}:latest"]));

        Assert.Contains($"kept, in use:\n  {live}  ({liveKey})", text);
        Assert.Contains("kept, project unknown", text);
        Assert.Contains("    docker volume rm pen-old-0123abcd_home", text);
        Assert.Contains("to remove, missing", text);
        Assert.Contains("isn't mounted", text);
        Assert.Contains($"  {gone}  ({goneKey})\n    {goneKey}_home, {goneKey}_nuget; {goneKey}:latest", text);
        Assert.DoesNotContain("moved", text);
        Assert.True(text.IndexOf("kept, in use", StringComparison.Ordinal) < text.IndexOf("to remove", StringComparison.Ordinal));
    }

    [Fact]
    public void RemovingAKeyDeletesItsRuntimeDir()
    {
        using var t = new TempDir();
        var host = t.Host();
        var dir = t.Sub("gone");
        var key = Record(host, dir);
        Directory.Delete(dir);
        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.True(CleanStaleCommand.Remove(host, e));
        Assert.False(Directory.Exists(host.GetRuntimeDir(key)));
        Assert.Empty(CleanStaleCommand.Find(host, [], []));
    }

    [Theory]
    [InlineData(false)] // a session (or pen sign) holds it shared
    [InlineData(true)]  // pen clean holds it exclusive
    public void AKeyWhoseProjectLockIsHeldIsSkipped(bool exclusive)
    {
        using var t = new TempDir();
        var host = t.Host();
        var dir = t.Sub("gone");
        var key = Record(host, dir);
        Directory.Delete(dir);
        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        using (FileLock.TryTake(SessionRegistry.GetProjectLockFile(host.GetRuntimeDir(key)), exclusive))
            Assert.False(CleanStaleCommand.Remove(host, e));
        Assert.True(Directory.Exists(host.GetRuntimeDir(key)));
        Assert.True(CleanStaleCommand.Remove(host, e));
    }

    /// <summary>
    /// Opens a session in <paramref name="key"/>'s runtime dir as a Pen that died would leave it: its compose.yaml and
    /// its protected copies' manifest there, its session.lock free.
    /// </summary>
    private static SessionDir OpenSession(HostEnv host, string key, string id)
    {
        var session = new SessionDir(key, Path.Combine(host.GetRuntimeDir(key), "sessions", id));
        Directory.CreateDirectory(session.ProtectedDir);
        File.WriteAllText(session.ProtectedManifest, "{}");
        File.WriteAllText(session.ComposeFile, "");
        return session;
    }

    /// <summary>Records a project, then deletes it, returning its dir and key.</summary>
    private static (string Dir, string Key) RecordMissing(TempDir t, HostEnv host, string name)
    {
        var dir = Git.Repo(t.Sub(name));
        var key = Record(host, dir);
        Directory.Delete(dir, recursive: true);
        return (dir, key);
    }

    private const string KeptHeading = "kept, sessions that haven't ended (their protected copies may be the only good ones of the\n"
        + "project's git files):";

    private static string CapturingStderr(Action action)
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { action(); }
        finally { Console.SetError(stderr); }
        return captured.ToString();
    }

    /// <summary>A docker that lists <paramref name="volumes"/> and no images, and has no pen:latest.</summary>
    private static string VolumesDocker(params string[] volumes) => $"""
        case "$*" in
          "volume ls"*) printf '%s\n' {string.Join(' ', volumes)} ;;
          "image inspect"*) exit 1 ;;
        esac
        exit 0

        """;

    private static string[] Calls(TempDir t, string prefix)
    {
        var calls = Path.Combine(t.Path, "calls");
        return File.Exists(calls) ? File.ReadAllLines(calls).Where(c => c.StartsWith(prefix, StringComparison.Ordinal)).ToArray() : [];
    }

    private const string MissingAdvice =
        "\n    if you moved the project, move it back (or if it's on a volume that isn't mounted, mount it) and"
        + "\n    run pen clean there, which ends its sessions and restores the files (answer n to keep its"
        + "\n    sandbox); if it's really gone, remove the sessions' containers and files (copy what you"
        + "\n    need from any protected copies first; this skips Pen's check of the project's .git) and"
        + "\n    run pen clean --stale again, as one command:";

    /// <summary>The advice's line that removes an abandoned session's containers, before its files go.</summary>
    private static string StackLine(string key, string id) =>
        $"\n    docker rm -f {key}-{id}-agent && docker compose -p {key}-{id} down -t 1 --remove-orphans &&";

    [Fact]
    public void AMissingKeyWithAnOpenSessionIsKept_AndListedWithItsProtectedCopies()
    {
        using var t = new TempDir();
        var host = t.Host();
        var (dir, key) = RecordMissing(t, host, "gone");
        var session = OpenSession(host, key, "s1");

        var e = Only(CleanStaleCommand.Find(host, [$"{key}_home"], []), key);
        Assert.Equal(CleanStaleCommand.Status.Missing, e.Status);
        var open = Assert.Single(e.OpenSessions);
        Assert.Equal(session.Path, open.Session.Path);
        Assert.False(open.Running);
        Assert.True(open.HasProtectedCopies);
        Assert.False(e.Removable);

        var text = CleanStaleCommand.Render(host, [e]);
        Assert.Equal(
            KeptHeading + $"\n  {dir}  ({key})"
                + $"\n    ~/.local/state/pen/{key}/sessions/s1/protected (manifest.json lists them)"
                + MissingAdvice
                + StackLine(key, "s1")
                + $"\n    rm -rf ~/.local/state/pen/{key}/sessions/s1 && pen clean --stale",
            text);
        Assert.DoesNotContain("to remove", text);
    }

    [Fact]
    public void TheMissingAdviceSaysToMoveTheProjectBack()
    {
        // A plain mv leaves the recorded dir gone, so a moved project usually shows as missing.
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var key = Record(host, repo);
        Directory.Move(repo, Path.Combine(t.Path, "renamed"));
        OpenSession(host, key, "s1");

        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.Equal(CleanStaleCommand.Status.Missing, e.Status);
        Assert.Contains("if you moved the project, move it back", CleanStaleCommand.Render(host, [e]));
    }

    [Fact]
    public void ARunningSessionIsMarked_AndOnlyTheAbandonedOnesAreOfferedForRemoval()
    {
        using var t = new TempDir();
        var host = t.Host();
        var (dir, key) = RecordMissing(t, host, "gone");
        var running = OpenSession(host, key, "s1");
        OpenSession(host, key, "s2");
        using var held = FileLock.TryTake(running.LockFile, exclusive: true);
        Assert.NotNull(held);

        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.Equal([true, false], e.OpenSessions.Select(open => open.Running));
        Assert.Equal(
            KeptHeading + $"\n  {dir}  ({key})"
                + $"\n    {key}-s1-agent is still running: exit it first"
                + $"\n    ~/.local/state/pen/{key}/sessions/s2/protected (manifest.json lists them)"
                + MissingAdvice
                + StackLine(key, "s2")
                + $"\n    rm -rf ~/.local/state/pen/{key}/sessions/s2 && pen clean --stale",
            CleanStaleCommand.Render(host, [e]));
    }

    [Fact]
    public void AKeyWhoseSessionsAllRunGetsNoRemovalAdvice()
    {
        using var t = new TempDir();
        var host = t.Host();
        var (dir, key) = RecordMissing(t, host, "gone");
        var running = OpenSession(host, key, "s1");
        using var held = FileLock.TryTake(running.LockFile, exclusive: true);
        Assert.NotNull(held);

        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.False(e.Removable);
        var text = CleanStaleCommand.Render(host, [e]);
        Assert.Equal(
            KeptHeading + $"\n  {dir}  ({key})"
                + $"\n    {key}-s1-agent is still running: exit it first"
                + "\n    then run pen clean --stale again",
            text);
        Assert.DoesNotContain("rm -r", text);
        Assert.DoesNotContain("docker", text);
        Assert.DoesNotContain("protected", text.Replace(KeptHeading, ""));
    }

    [Fact]
    public void ASessionWithoutProtectedCopiesKeepsTheKey_WithoutAProtectedPath()
    {
        // A session outside a git repo saves no copies, and an end check that restored them deletes them.
        using var t = new TempDir();
        var host = t.Host();
        var (dir, key) = RecordMissing(t, host, "gone");
        var session = OpenSession(host, key, "s1");
        Directory.Delete(session.ProtectedDir, recursive: true);

        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.False(Assert.Single(e.OpenSessions).HasProtectedCopies);
        Assert.False(e.Removable);
        Assert.Equal(
            KeptHeading + $"\n  {dir}  ({key})"
                + MissingAdvice
                + StackLine(key, "s1")
                + $"\n    rm -rf ~/.local/state/pen/{key}/sessions/s1 && pen clean --stale",
            CleanStaleCommand.Render(host, [e]));
    }

    [Fact]
    public void LiveAndUnknownKeysSessionsArentRead_SoOneThatCantBeReadChangesNothing()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads any directory");
        using var t = new TempDir();
        var host = t.Host();
        var liveKey = Record(host, Git.Repo(t.Sub("live")));
        var unknownKey = Record(host, t.Sub("unknown"));
        File.Delete(Path.Combine(host.GetRuntimeDir(unknownKey), ProjectRuntime.ProjectDirFile));
        var (_, goneKey) = RecordMissing(t, host, "gone");
        var shut = new List<string>();
        foreach (var key in new[] { liveKey, unknownKey })
        {
            OpenSession(host, key, "s1");
            var sessions = Path.Combine(host.GetRuntimeDir(key), "sessions");
            File.SetUnixFileMode(sessions, UnixFileMode.None);
            shut.Add(sessions);
        }
        try
        {
            var entries = CleanStaleCommand.Find(host, [], []);
            Assert.Equal(CleanStaleCommand.Status.Live, Only(entries, liveKey).Status);
            Assert.Equal(CleanStaleCommand.Status.Unknown, Only(entries, unknownKey).Status);
            Assert.All(entries, e => Assert.Empty(e.OpenSessions));
            Assert.True(Only(entries, goneKey).Removable);
        }
        finally
        {
            foreach (var sessions in shut)
                File.SetUnixFileMode(sessions, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void AMissingKeyWhoseSessionsCantBeReadStopsTheCommand()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads any directory");
        using var t = new TempDir();
        var host = t.Host();
        var (_, key) = RecordMissing(t, host, "gone");
        OpenSession(host, key, "s1");
        var sessions = Path.Combine(host.GetRuntimeDir(key), "sessions");
        File.SetUnixFileMode(sessions, UnixFileMode.None);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => CleanStaleCommand.Find(host, [], []));
        }
        finally
        {
            File.SetUnixFileMode(sessions, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void RunWithOnlyAKeptKeyFindsNothingStale_AndRemovesNothing()
    {
        using var t = new TempDir();
        var host = t.Host();
        var (_, key) = RecordMissing(t, host, "gone");
        var session = OpenSession(host, key, "s1");
        using var fake = new FakeDocker(t, VolumesDocker($"{key}_home"));

        var code = 1;
        var stderr = CapturingStderr(() => code = new CleanStaleCommand(host, new Answers(new StringReader("y\n"), IsTerminal: false)).Run());
        Assert.Equal(0, code);
        Assert.Contains(KeptHeading, stderr);
        Assert.Contains("pen: nothing stale to clean", stderr);
        Assert.DoesNotContain("[y/N]", stderr);
        Assert.DoesNotContain("Removing deletes", stderr);
        Assert.Empty(Calls(t, "volume rm"));
        Assert.True(File.Exists(session.ProtectedManifest));
        Assert.True(File.Exists(Path.Combine(host.GetRuntimeDir(key), ProjectRuntime.ProjectDirFile)));
    }

    [Fact]
    public void AMovedKeyWithAnOpenSessionGetsTheMoveBackAdvice()
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var key = Record(host, repo);
        Directory.Move(repo, Path.Combine(t.Path, "moved"));
        var other = Git.Repo(t.Sub("other"));
        File.CreateSymbolicLink(repo, other);
        OpenSession(host, key, "s1");
        OpenSession(host, key, "s2");

        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.Equal(CleanStaleCommand.Status.Moved, e.Status);
        Assert.False(e.Removable);
        Assert.Equal(
            KeptHeading + $"\n  {repo}  ({key}, now {Project.ComputeKey(other)})"
                + $"\n    ~/.local/state/pen/{key}/sessions/s1/protected (manifest.json lists them)"
                + $"\n    ~/.local/state/pen/{key}/sessions/s2/protected (manifest.json lists them)"
                + "\n    make its old dir the project again (move what's there now aside, and the project back) and run"
                + "\n    pen clean there, which ends its sessions and restores the files (answer n to keep its"
                + "\n    sandbox); or remove the sessions' containers and files (copy what you need from any"
                + "\n    protected copies first; this skips Pen's check of the project's .git) and run pen clean"
                + "\n    --stale again, as one command:"
                + StackLine(key, "s1")
                + StackLine(key, "s2")
                + $"\n    rm -rf ~/.local/state/pen/{key}/sessions/s1 ~/.local/state/pen/{key}/sessions/s2 && pen clean --stale",
            CleanStaleCommand.Render(host, [e]));
    }

    [Fact]
    public void TheKeptGroupGoesAfterTheUnknownOne_AndBeforeTheOnesToRemove()
    {
        using var t = new TempDir();
        var host = t.Host();
        var live = Git.Repo(t.Sub("live"));
        var liveKey = Record(host, live);
        OpenSession(host, liveKey, "s1"); // a live key's open sessions change nothing
        var (_, keptKey) = RecordMissing(t, host, "kept");
        OpenSession(host, keptKey, "s1");
        var (goneDir, goneKey) = RecordMissing(t, host, "gone");
        var text = CleanStaleCommand.Render(host, CleanStaleCommand.Find(host, ["pen-old-0123abcd_home"], []));

        Assert.StartsWith($"kept, in use:\n  {live}  ({liveKey})\n", text);
        var unknown = text.IndexOf("kept, project unknown", StringComparison.Ordinal);
        var kept = text.IndexOf(KeptHeading, StringComparison.Ordinal);
        var keptAt = text.IndexOf($"({keptKey})", StringComparison.Ordinal);
        var missing = text.IndexOf("to remove, missing", StringComparison.Ordinal);
        Assert.True(unknown > 0 && kept > unknown && keptAt > kept && missing > keptAt, text);
        Assert.True(text.IndexOf($"\n  {goneDir}  ({goneKey})", StringComparison.Ordinal) > missing, text);
        Assert.Equal(keptAt + 1, text.LastIndexOf(keptKey + ")", StringComparison.Ordinal)); // listed once only
        Assert.DoesNotContain($"{liveKey}/sessions", text);
    }

    [Fact]
    public void WithAKeptKeyAndOneToRemove_OnlyTheSecondGoes()
    {
        using var t = new TempDir();
        var host = t.Host();
        var (_, keptKey) = RecordMissing(t, host, "kept");
        var session = OpenSession(host, keptKey, "s1");
        var (_, goneKey) = RecordMissing(t, host, "gone");
        using var fake = new FakeDocker(t, VolumesDocker($"{keptKey}_home", $"{goneKey}_home"));

        var code = 1;
        var stderr = CapturingStderr(() => code = new CleanStaleCommand(host, new Answers(new StringReader("y\n"), IsTerminal: false)).Run());
        Assert.Equal(0, code);
        Assert.Contains("Removing deletes that project's", stderr);
        Assert.Contains("pen: remove it? [y/N] y", stderr);
        Assert.Contains($"pen: removed {goneKey}", stderr);
        Assert.DoesNotContain($"removed {keptKey}", stderr);
        Assert.Equal($"volume rm {goneKey}_home", Assert.Single(Calls(t, "volume rm")));
        Assert.False(Directory.Exists(host.GetRuntimeDir(goneKey)));
        Assert.True(File.Exists(session.ProtectedManifest));
    }

    [Fact]
    public void AnEndedSessionDoesntKeepTheKey()
    {
        using var t = new TempDir();
        var host = t.Host();
        var (_, key) = RecordMissing(t, host, "gone");
        // What SessionFiles.Delete leaves: the session's dir, with its proxy logs and without compose.yaml.
        var ended = Path.Combine(host.GetRuntimeDir(key), "sessions", "s1");
        Directory.CreateDirectory(ended);
        File.WriteAllText(Path.Combine(ended, "proxy.log"), "");

        var e = Only(CleanStaleCommand.Find(host, [], []), key);
        Assert.Empty(e.OpenSessions);
        Assert.True(e.Removable);
        Assert.DoesNotContain("haven't ended", CleanStaleCommand.Render(host, [e]));
        Assert.True(CleanStaleCommand.Remove(host, e));
        Assert.False(Directory.Exists(host.GetRuntimeDir(key)));
    }

    [Fact]
    public void AKeyThatGainsAnOpenSessionBeforeRemovalIsSkipped()
    {
        using var t = new TempDir();
        var host = t.Host();
        var (_, key) = RecordMissing(t, host, "gone");
        var e = Only(CleanStaleCommand.Find(host, [$"{key}_home"], [$"{key}:latest"]), key);
        Assert.True(e.Removable);
        Assert.Equal([$"{key}_home"], e.Volumes);
        Assert.Equal([$"{key}:latest"], e.Images);
        var session = OpenSession(host, key, "s1");
        using var fake = new FakeDocker(t, "exit 0\n");

        var removed = true;
        var stderr = CapturingStderr(() => removed = CleanStaleCommand.Remove(host, e));
        Assert.False(removed);
        Assert.Equal($"pen: skipping {key}: it has a session that hasn't ended\n", stderr);
        // The re-check comes before anything goes: the volumes and images stay too.
        Assert.Empty(Calls(t, "volume rm"));
        Assert.Empty(Calls(t, "image rm"));
        Assert.True(File.Exists(session.ProtectedManifest));
        Assert.True(File.Exists(Path.Combine(host.GetRuntimeDir(key), ProjectRuntime.ProjectDirFile)));
    }

    [Fact]
    public void TheAdviceRemovesEachAbandonedSessionsContainers_BeforeItsFiles()
    {
        // rm -r alone would leave an agent that outlived its Pen running, with nothing left to end it from.
        using var t = new TempDir();
        var host = t.Host();
        var (_, key) = RecordMissing(t, host, "gone");
        OpenSession(host, key, "s1");
        OpenSession(host, key, "s2");

        var text = CleanStaleCommand.Render(host, [Only(CleanStaleCommand.Find(host, [], []), key)]);
        var first = text.IndexOf($"docker rm -f {key}-s1-agent && docker compose -p {key}-s1 down", StringComparison.Ordinal);
        var second = text.IndexOf($"docker rm -f {key}-s2-agent && docker compose -p {key}-s2 down", StringComparison.Ordinal);
        var files = text.IndexOf("\n    rm -rf ", StringComparison.Ordinal);
        Assert.True(first > 0 && second > first && files > second, text);
        // Every line before the last goes on with &&, so pasted they run as one command.
        var command = text[first..].Split("\n    ");
        Assert.All(command[..^1], line => Assert.EndsWith(" &&", line));
        Assert.EndsWith("&& pen clean --stale", command[^1]);
    }

    [Fact]
    public void ASessionDirThatCantBeSearchedKeepsTheKey_AndIsntOfferedForRemoval()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root searches any directory");
        using var t = new TempDir();
        var host = t.Host();
        var (_, key) = RecordMissing(t, host, "gone");
        var session = OpenSession(host, key, "s1");
        File.SetUnixFileMode(session.Path, UnixFileMode.None);
        try
        {
            var e = Only(CleanStaleCommand.Find(host, [$"{key}_home"], []), key);
            var open = Assert.Single(e.OpenSessions);
            Assert.True(open.Running);
            Assert.False(e.Removable);
            var text = CleanStaleCommand.Render(host, [e]);
            Assert.True(open.Unreadable);
            Assert.Contains(
                $"~/.local/state/pen/{key}/sessions/s1 can't be read: check its permissions, then run "
                    + "pen clean --stale again",
                text);
            Assert.DoesNotContain("still running", text);
            Assert.DoesNotContain("rm -r", text);
        }
        finally
        {
            File.SetUnixFileMode(session.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ASessionDirThatCantBeSearchedByTheRemovalStopsIt()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root searches any directory");
        using var t = new TempDir();
        var host = t.Host();
        var (_, key) = RecordMissing(t, host, "gone");
        var e = Only(CleanStaleCommand.Find(host, [$"{key}_home"], []), key);
        Assert.True(e.Removable);
        var session = OpenSession(host, key, "s1");
        File.SetUnixFileMode(session.Path, UnixFileMode.None);
        using var fake = new FakeDocker(t, "exit 0\n");
        try
        {
            var removed = true;
            var stderr = CapturingStderr(() => removed = CleanStaleCommand.Remove(host, e));
            Assert.False(removed);
            Assert.StartsWith($"pen: skipping {key}: ", stderr);
            Assert.Empty(Calls(t, "volume rm"));
            Assert.True(File.Exists(Path.Combine(host.GetRuntimeDir(key), ProjectRuntime.ProjectDirFile)));
        }
        finally
        {
            File.SetUnixFileMode(session.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Assert.True(File.Exists(session.ProtectedManifest));
    }

    [Fact]
    public void ASessionWhoseLockCantBeProbedCountsAsRunning()
    {
        // A read-only session dir without a session.lock: the probe can't create one.
        Assert.SkipWhen(Native.GetUid() == 0, "root writes any directory");
        using var t = new TempDir();
        var host = t.Host();
        var (_, key) = RecordMissing(t, host, "gone");
        var session = OpenSession(host, key, "s1");
        Assert.False(File.Exists(session.LockFile));
        File.SetUnixFileMode(session.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var e = Only(CleanStaleCommand.Find(host, [], []), key);
            var open = Assert.Single(e.OpenSessions);
            Assert.True(open.Running);
            Assert.False(e.Removable);
            var text = CleanStaleCommand.Render(host, [e]);
            Assert.True(open.Unreadable);
            Assert.Contains(
                $"~/.local/state/pen/{key}/sessions/s1 can't be read: check its permissions, then run "
                    + "pen clean --stale again",
                text);
            Assert.DoesNotContain("still running", text);
            Assert.DoesNotContain("rm -r", text);
        }
        finally
        {
            File.SetUnixFileMode(session.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void APathThatNeedsQuotingKeepsTheTildeOutsideTheQuotes()
    {
        using var t = new TempDir();
        var host = t.Host();
        var (_, key) = RecordMissing(t, host, "gone");
        OpenSession(host, key, "a b");

        var text = CleanStaleCommand.Render(host, [Only(CleanStaleCommand.Find(host, [], []), key)]);
        Assert.Contains($"\n    rm -rf ~/'.local/state/pen/{key}/sessions/a b' && pen clean --stale", text);
        Assert.Contains($"docker rm -f '{key}-a b-agent' && docker compose -p '{key}-a b' down", text);
    }

    [Theory]
    [InlineData("~/.local/state/pen/pen-x-0123abcd", "~/.local/state/pen/pen-x-0123abcd")]
    [InlineData("~/a b/c", "~/'a b/c'")]
    [InlineData("~/it's", "~/'it'\\''s'")]
    [InlineData("/a b", "'/a b'")]
    [InlineData("~", "~")]
    public void Quote(string text, string quoted) => Assert.Equal(quoted, CleanStaleCommand.Quote(text));

    private const string CurrentBase = "sha256:current", OldBase = "sha256:old";
    private const string OldTools = "pen-tools:0123456789abcdef", CurrentTools = "pen-tools:fedcba9876543210";

    [Fact]
    public void OnlyToolsImagesOnAnotherBaseAreStale()
    {
        // A stacked layer inherits its tools image's label, so every image here has the old one.
        string[] images =
        [
            OldTools,
            "pen-foo-0123abcd:0123456789abcdef",
            "pen-foo-0123abcd-89abcdef:0123456789abcdef",
            "pen-tools-0123abcd:0123456789abcdef", // a project named tools
            "pen-tools-0123abcd:latest",
            "pen-tools:latest", // not a name Pen gives
            "other/pen-tools:0123456789abcdef",
        ];
        Assert.Equal([OldTools], CleanStaleCommand.FindStaleToolsImages(images, CurrentBase, _ => OldBase));
    }

    [Fact]
    public void AToolsImageOnTheCurrentBaseStays() =>
        Assert.Equal([OldTools], CleanStaleCommand.FindStaleToolsImages([CurrentTools, OldTools], CurrentBase, i => i == OldTools ? OldBase : CurrentBase));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<no value>")]
    public void AToolsImageWithoutAReadableLabelStays(string? label) =>
        Assert.Empty(CleanStaleCommand.FindStaleToolsImages([OldTools], CurrentBase, _ => label));

    [Fact]
    public void WithoutACurrentBaseNothingIsStale() =>
        Assert.Empty(CleanStaleCommand.FindStaleToolsImages([OldTools], null, _ => OldBase));

    /// <summary>A docker with images on each base, and a stacked layer that inherited the old label.</summary>
    private const string ImagesDocker = """
        case "$*" in
          "image ls"*) printf '%s\n' pen-tools:0123456789abcdef pen-tools:fedcba9876543210 pen-foo-0123abcd:0123456789abcdef ;;
          "image inspect --format {{.Id}} pen:latest") echo sha256:current ;;
          "image inspect"*fedcba9876543210) echo sha256:current ;;
          "image inspect"*) echo sha256:old ;;
        esac
        exit 0

        """;

    [Fact]
    public void RunAsksFirst_AndThenRemovesOnlyTheStaleToolsImage()
    {
        using var t = new TempDir();
        var host = t.Host();
        using var fake = new FakeDocker(t, ImagesDocker);
        string[] Removed() => File.ReadAllLines(Path.Combine(t.Path, "calls")).Where(c => c.StartsWith("image rm", StringComparison.Ordinal)).ToArray();

        Assert.Equal(1, new CleanStaleCommand(host, new Answers(new StringReader("n\n"), IsTerminal: false)).Run());
        Assert.Empty(Removed());
        Assert.Equal(0, new CleanStaleCommand(host, new Answers(new StringReader("y\n"), IsTerminal: false)).Run());
        Assert.Equal($"image rm {OldTools}", Assert.Single(Removed()));
    }
}
