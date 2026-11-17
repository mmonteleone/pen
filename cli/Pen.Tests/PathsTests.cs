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
}
