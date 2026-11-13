namespace Pen.Tests;

/// <summary>
/// D2: pen prune's classification of project keys, and removal of the ones it
/// may remove. Runs alone: these retake a project lock straight after releasing
/// it, and a process start in a parallel test can hold a shared lock fd until
/// its exec, so the lock could outlive the release.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public class PruneTests
{
    /// <summary>Records <paramref name="dir"/> as a project the way a pen command does, returning its key.</summary>
    private static string Record(HostEnv host, string dir)
    {
        var p = Project.Resolve(dir, host);
        Session.EnsureRuntimeDir(host, p);
        return p.Key;
    }

    private static Prune.Entry Only(List<Prune.Entry> entries, string key) => Assert.Single(entries, e => e.Key == key);

    [Fact]
    public void ARepoThatsStillThereIsLive()
    {
        using var t = new TempDir();
        var host = t.Host();
        var key = Record(host, Git.Repo(t.Sub("repo")));
        var e = Only(Prune.Find(host, [], []), key);
        Assert.Equal(Prune.Status.Live, e.Status);
        Assert.False(e.Removable);
    }

    [Fact]
    public void APlainDirThatsStillThereIsLive()
    {
        using var t = new TempDir();
        var host = t.Host();
        var key = Record(host, t.Sub("plain"));
        Assert.Equal(Prune.Status.Live, Only(Prune.Find(host, [], []), key).Status);
    }

    [Fact]
    public void ADeletedProjectIsMissing()
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var key = Record(host, repo);
        Directory.Delete(repo, recursive: true);
        var e = Only(Prune.Find(host, [], []), key);
        Assert.Equal(Prune.Status.Missing, e.Status);
        Assert.Equal(repo, e.RecordedDir);
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
        Assert.Equal(Prune.Status.Missing, Only(Prune.Find(host, [], []), key).Status);
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

        var e = Only(Prune.Find(host, [], []), key);
        Assert.Equal(Prune.Status.Moved, e.Status);
        Assert.Equal(Project.KeyFor(other), e.CurrentKey);
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
        var (status, current) = Prune.Classify(key, dir);
        Assert.Equal(Prune.Status.Moved, status);
        Assert.Equal(Project.KeyFor(main), current);
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
        Assert.Equal(Project.KeyFor(repo), key);
        Assert.Equal(Prune.Status.Live, Only(Prune.Find(host, [], []), key).Status);

        // The worktree going doesn't matter; the main checkout going does.
        Git.Run(repo, "worktree", "remove", "--force", wt);
        Assert.Equal(Prune.Status.Live, Only(Prune.Find(host, [], []), key).Status);
        Directory.Delete(repo, recursive: true);
        Assert.Equal(Prune.Status.Missing, Only(Prune.Find(host, [], []), key).Status);
    }

    [Fact]
    public void WithoutAProjectDirFileItsUnknown()
    {
        using var t = new TempDir();
        var host = t.Host();
        var key = Record(host, t.Sub("plain"));
        File.Delete(Path.Combine(host.GetRuntimeDir(key), Session.ProjectDirFile));
        var e = Only(Prune.Find(host, [$"{key}_home"], []), key);
        Assert.Equal(Prune.Status.Unknown, e.Status);
        Assert.False(e.Removable);
        Assert.Equal(Prune.Status.Unknown, Prune.Classify(key, null).Status);
    }

    [Fact]
    public void VolumesWithoutARuntimeDirAreUnknown_AndGroupedByKey()
    {
        using var t = new TempDir();
        var host = t.Host();
        const string key = "pen-gone-0123abcd";
        var e = Only(Prune.Find(host, [$"{key}_nuget", $"{key}_home", $"{key}_dind"], []), key);
        Assert.Equal(Prune.Status.Unknown, e.Status);
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
        var entries = Prune.Find(host,
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
        Assert.Equal(Prune.Status.Live, eb.Status);
    }

    [Fact]
    public void OtherDirsInTheStateRootArentKeys()
    {
        using var t = new TempDir();
        var host = t.Host();
        t.Sub("home", ".local", "state", "pen", "not-a-key");
        t.Sub("home", ".local", "state", "pen", "pen-x-nothex12");
        Assert.Empty(Prune.Find(host, [], []));
    }

    [Theory]
    [InlineData("pen-foo-0123abcd_home", "pen-foo-0123abcd")]
    [InlineData("pen-foo-0123abcd_nuget", "pen-foo-0123abcd")]
    [InlineData("pen-a-b_c-0123abcd_dind", "pen-a-b_c-0123abcd")]
    [InlineData("pen-foo-0123abcd_cache", null)]
    [InlineData("pen-foo_home", null)]
    [InlineData("pen-foo-0123ABCD_home", null)]
    [InlineData("xpen-foo-0123abcd_home", null)]
    public void KeyOfVolume(string volume, string? key) => Assert.Equal(key, Prune.KeyOfVolume(volume));

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
    public void IsImageOf(string image, bool expected) => Assert.Equal(expected, Prune.IsImageOf("pen-foo-0123abcd", image));

    [Fact]
    public void AProjectNamedToolsDoesntClaimTheSharedToolsImages()
    {
        Assert.False(Prune.IsImageOf("pen-tools-0123abcd", "pen-tools:0123456789abcdef"));
        Assert.True(Prune.IsImageOf("pen-tools-0123abcd", "pen-tools-0123abcd:0123456789abcdef"));
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
        var text = Prune.Render(host, Prune.Find(host,
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
        var e = Only(Prune.Find(host, [], []), key);
        Assert.True(Prune.Remove(host, e));
        Assert.False(Directory.Exists(host.GetRuntimeDir(key)));
        Assert.Empty(Prune.Find(host, [], []));
    }

    [Theory]
    [InlineData(false)] // a session (or pen sign) holds it shared
    [InlineData(true)]  // pen reset holds it exclusive
    public void AKeyWhoseProjectLockIsHeldIsSkipped(bool exclusive)
    {
        using var t = new TempDir();
        var host = t.Host();
        var dir = t.Sub("gone");
        var key = Record(host, dir);
        Directory.Delete(dir);
        var e = Only(Prune.Find(host, [], []), key);
        using (FileLock.TryTake(SessionRegistry.ProjectLockFile(host.GetRuntimeDir(key)), exclusive))
            Assert.False(Prune.Remove(host, e));
        Assert.True(Directory.Exists(host.GetRuntimeDir(key)));
        Assert.True(Prune.Remove(host, e));
    }

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
        Assert.Equal([OldTools], Prune.StaleToolsImages(images, CurrentBase, _ => OldBase));
    }

    [Fact]
    public void AToolsImageOnTheCurrentBaseStays() =>
        Assert.Equal([OldTools], Prune.StaleToolsImages([CurrentTools, OldTools], CurrentBase, i => i == OldTools ? OldBase : CurrentBase));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<no value>")]
    public void AToolsImageWithoutAReadableLabelStays(string? label) =>
        Assert.Empty(Prune.StaleToolsImages([OldTools], CurrentBase, _ => label));

    [Fact]
    public void WithoutACurrentBaseNothingIsStale() =>
        Assert.Empty(Prune.StaleToolsImages([OldTools], null, _ => OldBase));

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

        Assert.Equal(1, Prune.Run(host, new StringReader("n\n")));
        Assert.Empty(Removed());
        Assert.Equal(0, Prune.Run(host, new StringReader("y\n")));
        Assert.Equal($"image rm {OldTools}", Assert.Single(Removed()));
    }
}
