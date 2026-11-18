namespace Pen.Tests;

/// <summary>A session's start suggests pen init once per project, only while it has no .pen/config.toml.</summary>
public class InitHintTests
{
    private static (string PenDir, string RuntimeDir) Setup(TempDir t) =>
        (Path.Combine(t.Sub("repo"), ".pen"), t.Sub("runtime"));

    [Fact]
    public void ShowsOnce_AndLeavesAMarker()
    {
        using var t = new TempDir();
        var (pen, runtime) = Setup(t);
        Assert.True(InitHint.TryClaim(pen, runtime));
        var marker = Path.Combine(runtime, InitHint.MarkerName);
        Assert.True(File.Exists(marker));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(marker));
        Assert.False(InitHint.TryClaim(pen, runtime));
    }

    [Fact]
    public void AnEmptyPenDirStillShowsIt()
    {
        using var t = new TempDir();
        var (pen, runtime) = Setup(t);
        Directory.CreateDirectory(pen);
        Assert.True(InitHint.TryClaim(pen, runtime));
    }

    [Fact]
    public void NotShown_WhenTheConfigExists_AndNoMarkerIsLeft()
    {
        using var t = new TempDir();
        var (pen, runtime) = Setup(t);
        Directory.CreateDirectory(pen);
        File.WriteAllText(Path.Combine(pen, Config.FileName), "");
        Assert.False(InitHint.TryClaim(pen, runtime));
        Assert.False(File.Exists(Path.Combine(runtime, InitHint.MarkerName)));
        // Once it's gone, the hint is still to come.
        File.Delete(Path.Combine(pen, Config.FileName));
        Assert.True(InitHint.TryClaim(pen, runtime));
    }

    [Fact]
    public void NotShown_WithALegacyConfig()
    {
        using var t = new TempDir();
        var (pen, runtime) = Setup(t);
        Directory.CreateDirectory(pen);
        File.WriteAllText(Path.Combine(pen, Config.LegacyName), "");
        Assert.False(InitHint.TryClaim(pen, runtime));
    }

    [Theory]
    [InlineData("dangling")]
    [InlineData("file")]
    [InlineData("dir")]
    public void NotShown_WhenTheConfigIsALinkOrADirectory(string kind)
    {
        using var t = new TempDir();
        var (pen, runtime) = Setup(t);
        Directory.CreateDirectory(pen);
        var config = Path.Combine(pen, Config.FileName);
        switch (kind)
        {
            case "dangling":
                File.CreateSymbolicLink(config, Path.Combine(t.Path, "nowhere"));
                break;
            case "file":
                File.WriteAllText(Path.Combine(t.Path, "elsewhere.toml"), "");
                File.CreateSymbolicLink(config, Path.Combine(t.Path, "elsewhere.toml"));
                break;
            default:
                Directory.CreateDirectory(config);
                break;
        }
        Assert.False(InitHint.TryClaim(pen, runtime));
    }

    [Fact]
    public void ADanglingMarkerLinkCountsAsShown()
    {
        using var t = new TempDir();
        var (pen, runtime) = Setup(t);
        File.CreateSymbolicLink(Path.Combine(runtime, InitHint.MarkerName), Path.Combine(t.Path, "nowhere"));
        Assert.False(InitHint.TryClaim(pen, runtime));
        Assert.False(File.Exists(Path.Combine(t.Path, "nowhere")));
    }

    [Fact]
    public void AnUnwritableRuntimeDir_DoesNotThrow_AndShowsEachTime()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root writes everything");
        using var t = new TempDir();
        var (pen, runtime) = Setup(t);
        File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            Assert.True(InitHint.TryClaim(pen, runtime));
            Assert.True(InitHint.TryClaim(pen, runtime));
            Assert.False(File.Exists(Path.Combine(runtime, InitHint.MarkerName)));
        }
        finally
        {
            File.SetUnixFileMode(runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void AMissingRuntimeDir_DoesNotThrow()
    {
        using var t = new TempDir();
        var (pen, _) = Setup(t);
        Assert.True(InitHint.TryClaim(pen, Path.Combine(t.Path, "missing")));
    }

    [Fact]
    public void LinkedWorktreesShareTheMarker()
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var worktree = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", worktree);
        var main = Project.Resolve(repo, host);
        var linked = Project.Resolve(worktree, host);
        ProjectRuntime.EnsureRuntimeDir(host, main);
        Assert.True(InitHint.TryClaim(main.PenDir, host.GetRuntimeDir(main.Key)));
        Assert.False(InitHint.TryClaim(linked.PenDir, host.GetRuntimeDir(linked.Key)));
    }
}
