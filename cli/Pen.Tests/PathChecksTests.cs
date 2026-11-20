namespace Pen.Tests;

public class PathChecksTests
{
    [Theory]
    [InlineData("/a/b", "/a/b", true)]
    [InlineData("/a/b", "/a/b/c", true)]
    [InlineData("/a/b", "/a/b/c/d", true)]
    [InlineData("/a/b/", "/a/b/c", true)]
    [InlineData("/a/b/", "/a/b", false)] // equality is on the text as given, so the slash makes them differ
    [InlineData("/a/b", "/a/bc", false)]
    [InlineData("/a/b", "/a/bc/d", false)]
    [InlineData("/a/b", "/a", false)]
    [InlineData("/a/b", "/", false)]
    [InlineData("/a/b", "/A/b/c", false)] // names are compared exactly
    public void IsInsideTakesEqualAndChildPathsButNotLookalikesOrParents(string dir, string path, bool expected) =>
        Assert.Equal(expected, PathChecks.IsInside(dir, path));

    [Fact]
    public void IsLinkIsTrueForSymlinksToDirectoriesAndDanglingOnes()
    {
        using var temp = new TempDir();
        var dir = temp.Sub("dir");
        var toDir = Path.Combine(temp.Path, "to-dir");
        var dangling = Path.Combine(temp.Path, "dangling");
        File.CreateSymbolicLink(toDir, dir);
        File.CreateSymbolicLink(dangling, Path.Combine(temp.Path, "nowhere"));

        Assert.True(PathChecks.IsLink(toDir));
        Assert.True(PathChecks.IsLink(dangling));
    }

    [Fact]
    public void IsLinkIsFalseForDirectoriesFilesAndMissingPaths()
    {
        using var temp = new TempDir();
        var file = Path.Combine(temp.Path, "file");
        File.WriteAllText(file, "");

        Assert.False(PathChecks.IsLink(temp.Sub("dir")));
        Assert.False(PathChecks.IsLink(file));
        Assert.False(PathChecks.IsLink(Path.Combine(temp.Path, "missing")));
    }

    [Fact]
    public void ExistsCountsDanglingSymlinksAsDoesPathExists()
    {
        using var temp = new TempDir();
        var dangling = Path.Combine(temp.Path, "dangling");
        var file = Path.Combine(temp.Path, "file");
        File.CreateSymbolicLink(dangling, Path.Combine(temp.Path, "nowhere"));
        File.WriteAllText(file, "");

        Assert.True(Path.Exists(dangling)); // .NET already counts a dangling link
        Assert.True(PathChecks.ExistsOrIsLink(dangling));
        Assert.True(PathChecks.ExistsOrIsLink(file));
        Assert.True(PathChecks.ExistsOrIsLink(temp.Sub("dir")));
        Assert.False(PathChecks.ExistsOrIsLink(Path.Combine(temp.Path, "missing")));
    }

    [Fact]
    public void ResolveExistingGivesTheRealPathOfAnExistingPathThroughSymlinks()
    {
        using var temp = new TempDir();
        var real = temp.Sub("real");
        var link = Path.Combine(temp.Path, "link");
        File.CreateSymbolicLink(link, real);

        Assert.Equal(real, PathChecks.ResolveExisting(link));
        Assert.Equal(real, PathChecks.ResolveExisting(real));
    }

    [Fact]
    public void ResolveExistingKeepsTheMissingTailUnderASymlinkedAncestor()
    {
        using var temp = new TempDir();
        var real = temp.Sub("real");
        var link = Path.Combine(temp.Path, "link");
        File.CreateSymbolicLink(link, real);

        Assert.Equal(Path.Combine(real, "a", "b"), PathChecks.ResolveExisting(Path.Combine(link, "a", "b")));
    }

    [Fact]
    public void ResolveExistingResolvesPastADanglingSymlinkToItsParent()
    {
        using var temp = new TempDir();
        var dangling = Path.Combine(temp.Path, "dangling");
        File.CreateSymbolicLink(dangling, Path.Combine(temp.Path, "nowhere"));

        Assert.Equal(dangling, PathChecks.ResolveExisting(dangling));
        Assert.Equal(Path.Combine(dangling, "x"), PathChecks.ResolveExisting(Path.Combine(dangling, "x")));
    }

    [Fact]
    public void ResolveExistingGivesARelativePathBackAsIsWhenNothingResolves()
    {
        const string path = "pen-no-such-dir/child";

        Assert.Equal(path, PathChecks.ResolveExisting(path));
    }
}
