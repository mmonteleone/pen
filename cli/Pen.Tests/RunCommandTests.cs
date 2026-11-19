using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Pen.Tests;

/// <summary>What pen's run invocation and .pen/config.toml make of a session: Docker, ports and tools.</summary>
[Collection(nameof(LockAndProcessTests))]
public sealed class RunCommandTests : IDisposable
{
    private readonly TempDir _t = new();
    private readonly HostEnv _host;
    private readonly Project _project;
    private readonly FakeDocker _docker;

    public RunCommandTests()
    {
        _host = _t.Host();
        _project = Project.Resolve(Git.Repo(_t.Sub("repo")), _host);
        // Answers the base image's ID, and keeps the tools record a session's compose run sees.
        _docker = new FakeDocker(_t, """
            case "$*" in "image inspect --format {{.Id}} "*) echo sha256:base; exit 0 ;; esac
            case " $* " in *" run --rm "*) find home -path '*/sessions/*/tools' -exec cp {} tools-seen \; ; find home -path '*/sessions/*/ports' -exec cp {} ports-seen \; ;; esac
            exit 0

            """);
    }

    public void Dispose()
    {
        _docker.Dispose();
        _t.Dispose();
    }

    private string[] Calls => File.ReadAllLines(Path.Combine(_t.Path, "calls"));

    private int Run(string projectToml, bool docker = false, int[]? ports = null, ToolSpec[]? tools = null)
    {
        Directory.CreateDirectory(_project.PenDir);
        File.WriteAllText(_project.ProjectConfigPath, projectToml);
        var context = new ProjectContext(_host, _project, UserConfiguration.Empty,
            ConfigLoader.LoadProject(_project.PenDir, ".pen", RecipeCatalog.Embedded));
        // Approves whatever .pen/ asks for.
        var answers = new Answers(new StringReader("y\n"), IsTerminal: true);
        return new RunCommand(context, answers, RecipeCatalog.Embedded, TimeProvider.System)
            .Run(new RunInvocation(["true"], docker, ports ?? [], tools ?? []));
    }

    [Theory]
    [InlineData("", true, true)]
    [InlineData("docker = true\n", false, true)]
    [InlineData("docker = true\n", true, true)]
    [InlineData("docker = false\n", false, false)]
    public void DockerIsOnWithTheFlagOrTheConfig(string toml, bool flag, bool dind)
    {
        Assert.Equal(0, Run(toml, docker: flag));
        Assert.Equal(dind, Calls.Contains($"volume create {_project.Key}_dind"));
    }

    [Fact]
    public void ATakenPortIsRefusedFromTheFlagAndSkippedFromTheConfig()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        var taken = ((IPEndPoint)listener.LocalEndPoint!).Port;

        var error = Assert.Throws<PenException>(() => Run($"ports = [{taken}]\n", ports: [taken]));
        Assert.StartsWith($"--port {taken}: port {taken} is already in use", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Calls, call => call.StartsWith("volume ", StringComparison.Ordinal));

        int free;
        using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            free = ((IPEndPoint)probe.LocalEndPoint!).Port;
        }
        Assert.Equal(0, Run($"ports = [{taken}, {free}]\n"));
        Assert.Equal([free.ToString(CultureInfo.InvariantCulture)], File.ReadAllLines(Path.Combine(_t.Path, "ports-seen")));
    }

    [Fact]
    public void FlagToolsReachTheSession()
    {
        Assert.Equal(0, Run("", tools: [new("go", "1.27")]));
        Assert.Equal(["go:1.27"], File.ReadAllLines(Path.Combine(_t.Path, "tools-seen")));
    }

    [Fact]
    public void ARefusedSessionDoesNotUseUpTheInitHint()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen();
        var taken = ((IPEndPoint)listener.LocalEndPoint!).Port;
        var context = new ProjectContext(_host, _project, UserConfiguration.Empty, ProjectConfiguration.Empty);
        RunCommand Command() => new(context, new Answers(new StringReader(""), IsTerminal: false), RecipeCatalog.Embedded, TimeProvider.System);

        Assert.Throws<PenException>(() => Command().Run(new RunInvocation(["true"], Docker: true, [taken], [])));
        Assert.False(File.Exists(Path.Combine(_host.GetRuntimeDir(_project.Key), InitHint.MarkerName)));
    }
}
