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

    private static System.Text.Json.JsonElement Services(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("services");

    [Fact]
    public void APlainSessionsOverrideLeavesDindAlone()
    {
        var s = Services(RepoGuard.ComposeOverride([new("/p/.git", false), new("/p/.pen", true)]));
        Assert.False(s.TryGetProperty("dind", out _));
        var agent = s.GetProperty("agent");
        Assert.False(agent.TryGetProperty("environment", out _));
        Assert.False(agent.TryGetProperty("depends_on", out _));
    }

    [Fact]
    public void ADockerSessionsOverrideGivesDindTheAgentsMountsAndWaitsForIt()
    {
        Mount[] mounts = [new("/p/.git", false), new("/p/.git/config", true), new("/p/.pen", true)];
        var s = Services(RepoGuard.ComposeOverride(mounts, readOnlyRoot: true, dindContainer: "pen-p-1234abcd-0011aabb-dind"));

        var agent = s.GetProperty("agent");
        Assert.Equal("tcp://dind:2375", agent.GetProperty("environment").GetProperty("DOCKER_HOST").GetString());
        Assert.Equal("1", agent.GetProperty("environment").GetProperty("PEN_DOCKER").GetString());
        Assert.Equal("service_healthy", agent.GetProperty("depends_on").GetProperty("dind").GetProperty("condition").GetString());

        var dind = s.GetProperty("dind");
        Assert.Equal("pen-p-1234abcd-0011aabb-dind", dind.GetProperty("container_name").GetString());
        // dind keeps a writable root: read_only is the agent's alone.
        Assert.False(dind.TryGetProperty("read_only", out _));
        string Mounts(System.Text.Json.JsonElement svc) => string.Join(";", svc.GetProperty("volumes").EnumerateArray()
            .Select(v => $"{v.GetProperty("source").GetString()}>{v.GetProperty("target").GetString()}:{v.GetProperty("read_only").GetBoolean()}"));
        Assert.Equal("/p/.git>/p/.git:False;/p/.git/config>/p/.git/config:True;/p/.pen>/p/.pen:True", Mounts(dind));
        Assert.Equal(Mounts(agent), Mounts(dind));
    }
}
