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
        Assert.Contains("\"read_only\": true,\n      \"volumes\"", SessionCompose.GenerateOverride([], readOnlyRoot: true, dindContainer: null, ports: null));
        Assert.DoesNotContain("\"read_only\": true,\n      \"volumes\"", SessionCompose.GenerateOverride([], readOnlyRoot: false, dindContainer: null, ports: null));
    }

    private static System.Text.Json.JsonElement Services(string json) =>
        System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("services");

    [Fact]
    public void APlainSessionsOverrideLeavesDindAlone()
    {
        var s = Services(SessionCompose.GenerateOverride([new("/p/.git", false), new("/p/.pen", true)], readOnlyRoot: false, dindContainer: null, ports: null));
        Assert.False(s.TryGetProperty("dind", out _));
        var agent = s.GetProperty("agent");
        Assert.False(agent.TryGetProperty("environment", out _));
        Assert.False(agent.TryGetProperty("depends_on", out _));
    }

    [Fact]
    public void ADockerSessionsOverrideGivesDindTheAgentsMountsAndWaitsForIt()
    {
        Mount[] mounts = [new("/p/.git", false), new("/p/.git/config", true), new("/p/.pen", true)];
        var s = Services(SessionCompose.GenerateOverride(mounts, readOnlyRoot: true, dindContainer: "pen-p-1234abcd-0011aabb-dind", ports: null));

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

    [Fact]
    public void WithoutPortsThereIsNoPortsServiceOrNetwork()
    {
        foreach (var ports in new PortForward?[] { null, new([], "p-ports", "p-agent") })
        {
            var root = System.Text.Json.JsonDocument.Parse(SessionCompose.GenerateOverride([new("/p/.pen", true)], readOnlyRoot: false, dindContainer: null, ports: ports)).RootElement;
            Assert.False(root.GetProperty("services").TryGetProperty("ports", out _));
            Assert.False(root.TryGetProperty("networks", out _));
            Assert.False(root.GetProperty("services").GetProperty("agent").TryGetProperty("depends_on", out _));
        }
    }

    [Fact]
    public void PortsAddAHardenedForwarderOnTheSandboxAndANonInternalNetwork()
    {
        var json = SessionCompose.GenerateOverride([new("/p/.pen", true)], readOnlyRoot: false, dindContainer: null,
            ports: new([8765, 3000], "pen-p-1234abcd-0011aabb-ports", "pen-p-1234abcd-0011aabb-agent"));
        var root = System.Text.Json.JsonDocument.Parse(json).RootElement;

        // A non-internal network of its own: Docker publishes no ports on an internal one.
        var networks = root.GetProperty("networks");
        Assert.Equal(System.Text.Json.JsonValueKind.Object, networks.GetProperty("ports").ValueKind);
        Assert.False(networks.GetProperty("ports").TryGetProperty("internal", out _));

        var ports = root.GetProperty("services").GetProperty("ports");
        Assert.Equal("pen:latest", ports.GetProperty("image").GetString());
        Assert.Equal("pen-p-1234abcd-0011aabb-ports", ports.GetProperty("container_name").GetString());
        Assert.Equal(0, ports.GetProperty("entrypoint").GetArrayLength());
        Assert.Equal("nobody", ports.GetProperty("user").GetString());
        Assert.Equal(["ALL"], ports.GetProperty("cap_drop").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["no-new-privileges:true"], ports.GetProperty("security_opt").EnumerateArray().Select(e => e.GetString()));
        Assert.True(ports.GetProperty("read_only").GetBoolean());
        Assert.True(ports.GetProperty("init").GetBoolean());
        Assert.Equal(["127.0.0.1:8765:8765", "127.0.0.1:3000:3000"], ports.GetProperty("ports").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(["sandbox", "ports"], ports.GetProperty("networks").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(
            ["sh", "-c",
             "socat TCP-LISTEN:8765,fork,reuseaddr TCP:pen-p-1234abcd-0011aabb-agent:8765 & "
             + "socat TCP-LISTEN:3000,fork,reuseaddr TCP:pen-p-1234abcd-0011aabb-agent:3000 & wait"],
            ports.GetProperty("command").EnumerateArray().Select(e => e.GetString()));

        // compose run starts only the agent's dependencies; the agent stays on the sandbox alone.
        var agent = root.GetProperty("services").GetProperty("agent");
        Assert.Equal("service_started", agent.GetProperty("depends_on").GetProperty("ports").GetProperty("condition").GetString());
        Assert.False(agent.TryGetProperty("networks", out _));
    }

    [Fact]
    public void MountsWithoutASourceWriteTheSameOverrideAsBefore()
    {
        // The exact bytes written before mounts could have a separate source: a path over itself.
        const string expected = """
            {
              "x-pen": "this session's mounts over the project mount and its services, written by pen",
              "services": {
                "agent": {
                  "read_only": true,
                  "environment": {
                    "DOCKER_HOST": "tcp://dind:2375",
                    "PEN_DOCKER": "1"
                  },
                  "depends_on": {
                    "dind": {
                      "condition": "service_healthy"
                    }
                  },
                  "volumes": [
                    {
                      "type": "bind",
                      "source": "/p/.git",
                      "target": "/p/.git",
                      "read_only": false,
                      "bind": {
                        "create_host_path": false
                      }
                    },
                    {
                      "type": "bind",
                      "source": "/p/a$$b/config",
                      "target": "/p/a$$b/config",
                      "read_only": true,
                      "bind": {
                        "create_host_path": false
                      }
                    },
                    {
                      "type": "bind",
                      "source": "/p/.pen",
                      "target": "/p/.pen",
                      "read_only": true,
                      "bind": {
                        "create_host_path": false
                      }
                    }
                  ]
                },
                "dind": {
                  "container_name": "x-dind",
                  "volumes": [
                    {
                      "type": "bind",
                      "source": "/p/.git",
                      "target": "/p/.git",
                      "read_only": false,
                      "bind": {
                        "create_host_path": false
                      }
                    },
                    {
                      "type": "bind",
                      "source": "/p/a$$b/config",
                      "target": "/p/a$$b/config",
                      "read_only": true,
                      "bind": {
                        "create_host_path": false
                      }
                    },
                    {
                      "type": "bind",
                      "source": "/p/.pen",
                      "target": "/p/.pen",
                      "read_only": true,
                      "bind": {
                        "create_host_path": false
                      }
                    }
                  ]
                }
              }
            }
            """;
        Assert.Equal(expected + "\n", SessionCompose.GenerateOverride(
            [new("/p/.git", false), new("/p/a$b/config", true, Verify: true), new("/p/.pen", true)],
            readOnlyRoot: true, dindContainer: "x-dind", ports: null));
    }

    [Fact]
    public void AMountWithASourceMountsItAtItsPathAndEscapesDollarsInBoth()
    {
        Mount[] mounts = [new("/p/a$b/.env", true, Source: "/run/s$1/masks/replace-0"), new("/p/.pen", true)];
        var s = Services(SessionCompose.GenerateOverride(mounts, readOnlyRoot: false, dindContainer: "x-dind", ports: null));
        foreach (var service in new[] { s.GetProperty("agent"), s.GetProperty("dind") })
        {
            var volumes = service.GetProperty("volumes").EnumerateArray().ToList();
            Assert.Equal("/run/s$$1/masks/replace-0", volumes[0].GetProperty("source").GetString());
            Assert.Equal("/p/a$$b/.env", volumes[0].GetProperty("target").GetString());
            Assert.True(volumes[0].GetProperty("read_only").GetBoolean());
            Assert.False(volumes[0].GetProperty("bind").GetProperty("create_host_path").GetBoolean());
            Assert.Equal("/p/.pen", volumes[1].GetProperty("source").GetString());
            Assert.Equal("/p/.pen", volumes[1].GetProperty("target").GetString());
        }
    }

    [Fact]
    public void ADockerSessionWithPortsWaitsForBoth()
    {
        var agent = Services(SessionCompose.GenerateOverride([], readOnlyRoot: false, dindContainer: "x-dind", ports: new([80], "x-ports", "x-agent"))).GetProperty("agent");
        var deps = agent.GetProperty("depends_on");
        Assert.Equal("service_healthy", deps.GetProperty("dind").GetProperty("condition").GetString());
        Assert.Equal("service_started", deps.GetProperty("ports").GetProperty("condition").GetString());
        Assert.Equal("tcp://dind:2375", agent.GetProperty("environment").GetProperty("DOCKER_HOST").GetString());
    }
}
