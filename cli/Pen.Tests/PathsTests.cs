namespace Pen.Tests;

public class PathsTests
{
    [Theory]
    [InlineData("/a/b", "/a")]
    [InlineData("/a", "/")]
    public void GetParentGivesTheDirectoryHoldingThePath(string path, string expected) =>
        Assert.Equal(expected, Paths.GetParent(path));

    [Fact]
    public void GetParentRefusesTheRoot() => Assert.Throws<ArgumentException>(() => Paths.GetParent("/"));

    [Theory]
    [InlineData("/a/b/c", new[] { "/a/b", "/a", "/" })]
    [InlineData("/a", new[] { "/" })]
    [InlineData("/a/b/c/", new[] { "/a/b/c", "/a/b", "/a", "/" })] // the text is taken as is: the slash makes c a parent
    [InlineData("/", new string[0])]
    public void EnumerateAncestorsGivesTheNearestParentFirstAndTheRootLast(string path, string[] expected) =>
        Assert.Equal(expected, Paths.EnumerateAncestors(path));
}
