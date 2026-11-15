namespace Pen.Tests;

/// <summary>D1: the runtime dir records its project's main checkout (for pen prune); the shared [y/N] prompt.</summary>
public class RuntimeDirTests
{
    private static string ProjectDirOf(HostEnv host, Project p) =>
        File.ReadAllText(Path.Combine(host.GetRuntimeDir(p.Key), Session.ProjectDirFile));

    [Fact]
    public void WritesTheRepoDir_WithTheRuntimeDirs()
    {
        using var t = new TempDir();
        var host = t.Host();
        var p = Project.Resolve(Git.Repo(t.Sub("repo")), host);
        Session.EnsureRuntimeDir(host, p);
        Assert.Equal(p.Dir + "\n", ProjectDirOf(host, p));
        Assert.True(Directory.Exists(Path.Combine(host.GetRuntimeDir(p.Key), "sessions")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(host.GetRuntimeDir(p.Key)));
    }

    [Fact]
    public void ALinkedWorktreeRecordsTheMainCheckout()
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(repo, "worktree", "add", "-q", wt);
        var p = Project.Resolve(wt, host);
        Session.EnsureRuntimeDir(host, p);
        Assert.Equal(repo + "\n", ProjectDirOf(host, p));
        // ...and the main checkout, sharing the key, agrees.
        Session.EnsureRuntimeDir(host, Project.Resolve(repo, host));
        Assert.Equal(repo + "\n", ProjectDirOf(host, p));
    }

    [Fact]
    public void TheRealPathIsRecorded()
    {
        using var t = new TempDir();
        var host = t.Host();
        var real = Git.Repo(t.Sub("real"));
        var link = Path.Combine(t.Path, "link");
        File.CreateSymbolicLink(link, real);
        var p = Project.Resolve(link, host);
        Session.EnsureRuntimeDir(host, p);
        Assert.Equal(real + "\n", ProjectDirOf(host, p));
    }

    [Fact]
    public void AStaleFileIsReplaced_AndNoTempFileIsLeft()
    {
        using var t = new TempDir();
        var host = t.Host();
        var p = Project.Resolve(t.Sub("plain"), host);
        var runtime = host.GetRuntimeDir(p.Key);
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, Session.ProjectDirFile), "/somewhere/else\n");
        Session.EnsureRuntimeDir(host, p);
        Assert.Equal(p.Dir + "\n", ProjectDirOf(host, p));
        Session.EnsureRuntimeDir(host, p);
        Assert.Equal(p.Dir + "\n", ProjectDirOf(host, p));
        Assert.Empty(Directory.EnumerateFiles(runtime, "*.tmp"));
    }

    [Theory]
    [InlineData("y", true)]
    [InlineData("Y", true)]
    [InlineData(" yes ", true)]
    [InlineData("YES", true)]
    [InlineData("n", false)]
    [InlineData("", false)]
    [InlineData("yep", false)]
    [InlineData(null, false)]
    public void IsYes(string? answer, bool expected) => Assert.Equal(expected, Out.IsYes(answer));

    [Theory]
    [InlineData("y\n", true)]
    [InlineData("yes\nno\n", true)]
    [InlineData("no\n", false)]
    [InlineData("\n", false)]
    [InlineData("", false)] // end of input
    public void ConfirmReadsOneLine(string input, bool expected) =>
        Assert.Equal(expected, Out.Confirm("remove them?", new StringReader(input)));
}
