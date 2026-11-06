namespace Pen.Tests;

public class DockerTests
{
    [Theory]
    [InlineData("[\"name=seccomp,profile=builtin\",\"name=rootless\",\"name=cgroupns\"]\n", true)]
    [InlineData("[\"name=seccomp,profile=builtin\",\"name=cgroupns\"]\n", false)]
    [InlineData("[\"name=userns\",\"name=seccomp,profile=builtin\"]", false)]
    [InlineData("[\"name=seccomp,profile=name=rootlessish\"]", false)]
    [InlineData("null", false)]
    public void DetectsRootlessFromSecurityOptions(string json, bool rootless) =>
        Assert.Equal(rootless, Docker.IsRootless(json));

    [Fact]
    public void TheOverrideMakesTheRootFilesystemReadOnlyOnlyWhenAsked()
    {
        Assert.Contains("\"read_only\": true,\n      \"volumes\"", RepoGuard.ComposeOverride([], readOnlyRoot: true));
        Assert.DoesNotContain("\"read_only\": true,\n      \"volumes\"", RepoGuard.ComposeOverride([]));
    }
}
