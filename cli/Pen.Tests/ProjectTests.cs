namespace Pen.Tests;

public class ProjectTests
{
    [Fact]
    public void KeyIsCleanNameAndPathHash()
    {
        var key = Project.ComputeKey("/Users/me/source/My Project.v2");
        Assert.Matches("^pen-my-project-v2-[0-9a-f]{8}$", key);
        Assert.NotEqual(key, Project.ComputeKey("/Users/me/other/My Project.v2"));
    }

    [Theory]
    [InlineData("/x/Foo_Bar-baz", "foo_bar-baz")]
    [InlineData("/x/ünïcode", "n-code")]
    [InlineData("/x/...", "project")]
    [InlineData("/x/a  b", "a-b")]
    public void CleanName(string dir, string expected) => Assert.Equal(expected, Project.CleanName(dir));

    [Fact]
    public void CleanNameIsCapped() => Assert.Equal(40, Project.CleanName("/x/" + new string('a', 100)).Length);

    [Fact]
    public void SymlinkedPathGivesTheSameKey()
    {
        using var t = new TempDir();
        var real = t.Sub("work", "proj");
        var link = Path.Combine(t.Path, "link");
        Directory.CreateSymbolicLink(link, Path.Combine(t.Path, "work"));
        var host = t.Host();
        var a = Project.Resolve(real, host);
        var b = Project.Resolve(Path.Combine(link, "proj"), host);
        Assert.Equal(real, b.Dir);
        Assert.Equal(a.Key, b.Key);
    }

    [Fact]
    public void DifferentCasePathGivesTheSameKey_OnACaseInsensitiveFileSystem()
    {
        using var t = new TempDir();
        var real = t.Sub("Work", "Proj");
        var upper = Path.Combine(t.Path, "WORK", "PROJ");
        Assert.SkipUnless(Directory.Exists(upper), "file system is case-sensitive");
        var host = t.Host();
        Assert.Equal(Project.Resolve(real, host).Key, Project.Resolve(upper, host).Key);
        Assert.Equal(real, Project.Resolve(upper, host).Dir);
    }

    private static string Refusal(string dir, HostEnv host) =>
        Assert.Throws<PenException>(() => Project.Resolve(dir, host)).Message;

    [Fact]
    public void RefusesRoot()
    {
        using var t = new TempDir();
        Assert.Contains("refusing to run in /", Refusal("/", t.Host()));
    }

    [Fact]
    public void RefusesHomeAndItsAncestors()
    {
        using var t = new TempDir();
        var home = t.Sub("users", "me");
        var host = t.Host(home);
        Assert.Contains("home directory", Refusal(home, host));
        Assert.Contains("contains your home directory", Refusal(Path.Combine(t.Path, "users"), host));
        Assert.Contains("contains your home directory", Refusal(t.Path, host));
    }

    [Fact]
    public void RefusesAnythingOverlappingPensConfigOrState()
    {
        using var t = new TempDir();
        var home = t.Sub("home");
        var host = t.Host(home);
        Directory.CreateDirectory(host.ConfigDir);
        Directory.CreateDirectory(host.StateRoot);
        Assert.Contains("~/.config/pen", Refusal(Path.Combine(home, ".config"), host));
        Assert.Contains("~/.config/pen", Refusal(host.ConfigDir, host));
        Assert.Contains("~/.config/pen", Refusal(t.Sub("home", ".config", "pen", "home"), host));
        Assert.Contains("~/.local/state/pen", Refusal(Path.Combine(home, ".local"), host));
    }

    [Fact]
    public void RefusesASubdirectoryOfARepo_NamingTheRoot()
    {
        using var t = new TempDir();
        var repo = t.Sub("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var sub = t.Sub("repo", "src", "lib");
        var msg = Refusal(sub, t.Host());
        Assert.Contains($"inside the git repo at {repo}", msg);
        Assert.Contains("repo root", msg);
    }

    [Fact]
    public void AllowsARepoRootAndAPlainDirectory()
    {
        using var t = new TempDir();
        var repo = t.Sub("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var p = Project.Resolve(repo, t.Host());
        Assert.True(p.IsRepo);
        Assert.False(Project.Resolve(t.Sub("plain"), t.Host()).IsRepo);
    }

    [Fact]
    public void ADirectoryThatCantBeRead_IsRefused_NotTakenForAPlainDirectory()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.SetUnixFileMode(repo, UnixFileMode.None);
        try
        {
            var e = Assert.Throws<PenException>(() => Project.Resolve(repo, t.Host()));
            Assert.Contains("chmod u+rwx", e.Message);
            Assert.Contains(repo, e.Message);
        }
        finally { File.SetUnixFileMode(repo, (UnixFileMode)0b111_101_101); }
    }
}
