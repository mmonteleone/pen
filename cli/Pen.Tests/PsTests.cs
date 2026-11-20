namespace Pen.Tests;

// Takes session locks: runs alone, with the other lock tests.
[Collection(nameof(LockAndProcessTests))]
public class PsTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>An open session under <paramref name="key"/>; its lock is held (running) unless dead.</summary>
    private static FileLock? Session(HostEnv host, string key, string id, string projectDir, string[]? command, DateTime? started, bool docker = false, bool dead = false, int[]? ports = null, ToolSpec[]? tools = null, string? openEgress = null)
    {
        var s = new SessionDir(key, Path.Combine(host.GetRuntimeDir(key), "sessions", id));
        Directory.CreateDirectory(s.Path);
        File.WriteAllText(s.EnvFile, $"PROJECT_DIR={projectDir}\nPEN_PROJECT={key}\n");
        if (command != null) s.WriteCommand(command);
        if (started is { } t) s.WriteStarted(t);
        if (docker) File.WriteAllText(s.DockerMarker, "");
        if (ports != null) s.WritePorts(ports);
        if (tools != null) s.WriteTools(tools, []);
        if (openEgress != null) File.WriteAllText(s.OpenEgressFile, openEgress);
        File.WriteAllText(s.ComposeFile, "");
        return dead ? null : FileLock.TryTake(s.LockFile, exclusive: true);
    }

    [Fact]
    public void CommandAndStartedRoundTrip()
    {
        using var t = new TempDir();
        var s = new SessionDir("k", t.Sub("s"));
        Assert.Null(s.ReadCommand()!);
        Assert.Null(s.ReadStarted());
        s.WriteCommand(["bash", "-c", "echo a\necho b"]);
        s.WriteStarted(Now);
        Assert.Equal(["bash", "-c", "echo a\necho b"], s.ReadCommand()!);
        Assert.Equal(Now, s.ReadStarted());
        Assert.Equal(DateTimeKind.Utc, s.ReadStarted()!.Value.Kind);
        s.WriteCommand([]);
        Assert.Equal([], s.ReadCommand()!);
    }

    [Fact]
    public void ListsRunningSessionsOfEveryProject_ByProjectThenAge()
    {
        using var t = new TempDir();
        var host = t.Host();
        var b = Path.Combine(host.Home, "src", "b");
        using var l1 = Session(host, "pen-b-22222222", "aaaa0001", b, ["claude"], Now.AddMinutes(-5));
        using var l2 = Session(host, "pen-b-22222222", "aaaa0002", b, [], Now.AddHours(-3).AddMinutes(-7), docker: true, tools: [new("a", "1"), new("b", "2")]);
        using var l3 = Session(host, "pen-a-11111111", "bbbb0001", "/srv/a", ["npm", "run", "dev server"], Now.AddDays(-2).AddHours(-4), ports: [5173, 3000], openEgress: ".\n");
        using var dead = Session(host, "pen-a-11111111", "bbbb0002", "/srv/a", ["dead"], Now, dead: true);

        var sessions = SessionRegistry.FindRunningEverywhere(host);
        Assert.Equal(3, sessions.Count);
        Assert.Equal("""
            PROJECT  SESSION   DOCKER  EGRESS  PORTS      TOOLS    AGE    COMMAND
            /srv/a   bbbb0001          open    5173,3000           2d04h  npm run 'dev server'
            ~/src/b  aaaa0002  docker                     a:1,b:2  3h07m  (shell)
            ~/src/b  aaaa0001                                      5m     claude

            """, PsCommand.Render(host, sessions, Now));
    }

    [Fact]
    public void ASessionFromAnOlderPenShowsQuestionMarks()
    {
        using var t = new TempDir();
        var host = t.Host();
        using var l = Session(host, "pen-a-11111111", "cccc0001", "/srv/a", command: null, started: null);
        Assert.EndsWith("/srv/a   cccc0001                                ?    ?\n", PsCommand.Render(host, SessionRegistry.FindRunningEverywhere(host), Now));
    }

    [Fact]
    public void OnlyAnOpenEgressFileThatOpensMarksTheSession()
    {
        using var t = new TempDir();
        var host = t.Host();
        using var closed = Session(host, "pen-a-11111111", "dddd0001", "/srv/a", ["a"], Now, openEgress: "");
        using var open = Session(host, "pen-a-11111111", "dddd0002", "/srv/a", ["b"], Now.AddSeconds(1), openEgress: ".\n");
        using var old = Session(host, "pen-a-11111111", "dddd0003", "/srv/a", ["c"], Now.AddSeconds(2));
        var rows = PsCommand.Render(host, SessionRegistry.FindRunningEverywhere(host), Now.AddSeconds(5)).Split('\n');
        Assert.Equal("PROJECT  SESSION   DOCKER  EGRESS  PORTS  TOOLS  AGE  COMMAND", rows[0]);
        Assert.Equal("/srv/a   dddd0001                                5s   a", rows[1]);
        Assert.Equal("/srv/a   dddd0002          open                  4s   b", rows[2]);
        Assert.Equal("/srv/a   dddd0003                                3s   c", rows[3]);
    }

    [Fact]
    public void PortsRoundTrip()
    {
        using var t = new TempDir();
        var s = new SessionDir("k", t.Sub("s"));
        Assert.Empty(s.ReadPorts()); // a session from an older Pen
        s.WritePorts([8765, 3000]);
        Assert.Equal("8765\n3000\n", File.ReadAllText(s.PortsFile));
        Assert.Equal([8765, 3000], s.ReadPorts());
        s.WritePorts([]);
        Assert.Equal("", File.ReadAllText(s.PortsFile));
        Assert.Empty(s.ReadPorts());
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59, "59s")]
    [InlineData(60, "1m")]
    [InlineData(3599, "59m")]
    [InlineData(3600, "1h00m")]
    [InlineData(86400 + 3 * 3600 + 59, "1d03h")]
    [InlineData(-5, "0s")]
    public void Age(int seconds, string expected) =>
        Assert.Equal(expected, PsCommand.FormatAge(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void DescribeQuotesFlattensAndCuts()
    {
        Assert.Equal("bash -c 'echo it'\\''s; ls'", PsCommand.Describe(["bash", "-c", "echo it's;\n  ls"]));
        Assert.Equal("echo ''", PsCommand.Describe(["echo", ""]));
        var cut = PsCommand.Describe(["echo", new string('x', 200)]);
        Assert.Equal(80, cut.Length);
        Assert.EndsWith("x…", cut);
    }
}
