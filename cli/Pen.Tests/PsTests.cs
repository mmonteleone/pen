namespace Pen.Tests;

// Takes session locks: runs alone, with the other lock tests.
[Collection(nameof(LockAndProcessTests))]
public class PsTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>An open session under <paramref name="key"/>; its lock is held (running) unless dead.</summary>
    private static FileLock? Session(HostEnv host, string key, string id, string projectDir, string[]? command, DateTime? started, bool docker = false, bool dead = false)
    {
        var s = new SessionDir(key, Path.Combine(host.RuntimeDir(key), "sessions", id));
        Directory.CreateDirectory(s.Path);
        File.WriteAllText(s.EnvFile, $"PROJECT_DIR={projectDir}\nPEN_PROJECT={key}\n");
        if (command != null) s.WriteCommand(command);
        if (started is { } t) s.WriteStarted(t);
        if (docker) File.WriteAllText(s.DockerMarker, "");
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
        using var l2 = Session(host, "pen-b-22222222", "aaaa0002", b, [], Now.AddHours(-3).AddMinutes(-7), docker: true);
        using var l3 = Session(host, "pen-a-11111111", "bbbb0001", "/srv/a", ["npm", "run", "dev server"], Now.AddDays(-2).AddHours(-4));
        using var dead = Session(host, "pen-a-11111111", "bbbb0002", "/srv/a", ["dead"], Now, dead: true);

        var sessions = Sessions.RunningEverywhere(host);
        Assert.Equal(3, sessions.Count);
        Assert.Equal("""
            PROJECT  SESSION   DOCKER  AGE    COMMAND
            /srv/a   bbbb0001          2d04h  npm run 'dev server'
            ~/src/b  aaaa0002  docker  3h07m  (shell)
            ~/src/b  aaaa0001          5m     claude

            """, Ps.Render(host, sessions, Now));
    }

    [Fact]
    public void ASessionFromAnOlderPenShowsQuestionMarks()
    {
        using var t = new TempDir();
        var host = t.Host();
        using var l = Session(host, "pen-a-11111111", "cccc0001", "/srv/a", command: null, started: null);
        Assert.EndsWith("/srv/a   cccc0001          ?    ?\n", Ps.Render(host, Sessions.RunningEverywhere(host), Now));
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
        Assert.Equal(expected, Ps.Age(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void DescribeQuotesFlattensAndCuts()
    {
        Assert.Equal("bash -c 'echo it'\\''s; ls'", Ps.Describe(["bash", "-c", "echo it's;\n  ls"]));
        Assert.Equal("echo ''", Ps.Describe(["echo", ""]));
        var cut = Ps.Describe(["echo", new string('x', 200)]);
        Assert.Equal(80, cut.Length);
        Assert.EndsWith("x…", cut);
    }
}
