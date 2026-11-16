namespace Pen.SmokeTests;

/// <summary>A session's life: exit codes, terminals, sessions side by side, and what's left once they end.</summary>
public sealed class SessionTests(SessionTests.Fixture fixture) : IClassFixture<SessionTests.Fixture>
{
    private PenCli Cli => fixture.Cli;

    [Fact(DisplayName = "the command's exit code passes through")]
    public void ExitCodePassesThrough()
    {
        var result = Cli.Run("run", "sh", "-c", "exit 7");

        Assert.True(result.ExitCode == 7, result.Describe());
    }

    [Fact(DisplayName = "pen run ls | head works without a TTY")]
    public void WorksInAPipeline()
    {
        var pen = ShellQuote.Quote(SmokeEnvironment.PenPath);

        var result = Cli.Wrap("/bin/sh", "-c", $"{pen} run ls -a </dev/null 2>/dev/null | head -1").Run();

        Assert.True(result.CapturedStdout == ".", result.Describe());
    }

    [Fact(DisplayName = "with a terminal, the agent gets one too")]
    public void AgentGetsATerminal()
    {
        var result = new Pty().Run(Cli, "run", "tty");

        Assert.True(result.Text.Contains("/dev/pts/", StringComparison.Ordinal), result.Describe());
    }

    [Fact(DisplayName = "pen claude runs claude")]
    public void ClaudeRunsClaude()
    {
        var result = new Pty().Run(Cli, "claude", "--version");

        Assert.Matches("[0-9]+\\.[0-9]+\\.[0-9]+", result.Text);
    }

    [Fact(DisplayName = "pen with no arguments is an interactive shell")]
    public void NoArgumentsIsAShell()
    {
        var result = new Pty { TimeoutSeconds = 60 }.SendOn("[$#] $", "echo shell-$((6*7)); exit\\r").Run(Cli);

        Assert.True(result.Text.Contains("shell-42", StringComparison.Ordinal), result.Describe());
    }

    [Fact(DisplayName = "a second session runs alongside the first")]
    public void SecondSessionRunsAlongside() =>
        Assert.True(fixture.Second.CapturedStdout == "200", fixture.Second.Describe());

    [Fact(DisplayName = "the first still works after the second exits")]
    public void FirstStillWorks() =>
        Assert.True(fixture.First.Output.Contains("after:200", StringComparison.Ordinal), fixture.First.Describe());

    [Fact(DisplayName = "docker ps is clean after the last session exits")]
    public void NoContainersAreLeft() => Assert.Equal(0, fixture.ContainersAfter);

    [Fact(DisplayName = "no session network is left")]
    public void NoNetworkIsLeft() => Assert.Empty(fixture.NetworksAfter);

    [Fact(DisplayName = "an ended session keeps only its proxy.log")]
    public void EndedSessionKeepsOnlyItsProxyLog() =>
        Assert.Equal(["proxy.log"], Directory.EnumerateFileSystemEntries(fixture.LastSession).Select(Path.GetFileName));

    [Fact(DisplayName = "runtime and session dirs are 700")]
    public void RuntimeAndSessionDirsArePrivate() =>
        Assert.Equal(
            ["700", "700", "700"],
            [
                Files.GetMode(fixture.RuntimeDir), Files.GetMode(fixture.LastSession),
                Files.GetMode(Path.Combine(fixture.RuntimeDir, "sessions")),
            ]);

    [Fact(DisplayName = "proxy.log is 666")]
    public void ProxyLogIsWorldWritable() =>
        Assert.Equal("666", Files.GetMode(Path.Combine(fixture.LastSession, "proxy.log")));

    [Fact(DisplayName = "the runtime dir records the project's real path in project-dir (clean --stale D1)")]
    public void RuntimeDirRecordsTheProjectDir() =>
        Assert.Equal(
            fixture.Project.Dir, File.ReadAllText(Path.Combine(fixture.RuntimeDir, "project-dir")).TrimEnd('\n'));

    [Fact(DisplayName = "the project's volumes are named by its key")]
    public void VolumesAreNamedByTheKey()
    {
        var result = Docker.Run("volume", "inspect", $"{fixture.Key}_home", $"{fixture.Key}_nuget");

        Assert.True(result.ExitCode == 0, result.Describe());
    }

    [Fact(DisplayName = "pen:latest carries its image hash")]
    public void ImageCarriesItsHash()
    {
        var hash = Docker.Run("image", "inspect", "--format", "{{ index .Config.Labels \"pen.image-hash\" }}",
            "pen:latest");

        Assert.True(hash.CapturedStdout.Length > 0, hash.Describe());
    }

    /// <summary>Runs a session that makes a request after 8 s, and a second one alongside it, then records what's
    /// left once both have ended.</summary>
    public sealed class Fixture : SmokeFixture
    {
        /// <summary>Gets the first session's result, its stdout and stderr together.</summary>
        internal CommandResult First { get; private set; } = null!;

        /// <summary>Gets the second session's result.</summary>
        internal CommandResult Second { get; private set; } = null!;

        /// <summary>Gets how many of the key's containers there were once both had ended.</summary>
        internal int ContainersAfter { get; private set; }

        /// <summary>Gets the key's networks once both had ended.</summary>
        internal IReadOnlyList<string> NetworksAfter { get; private set; } = [];

        /// <summary>Gets the newest session dir once both had ended.</summary>
        internal string LastSession { get; private set; } = null!;

        protected override void Prepare()
        {
            var first = Track(Cli.Start("run", "bash", "-c", """
                sleep 8; curl -s -o /dev/null -w "after:%{http_code}" -I https://github.com
                """));
            if (!Polling.WaitForContainer($"^{Key}-.*-agent$"))
                throw new InvalidOperationException($"first session didn't start: {first.OutputSoFar}");
            Second = Cli.Run("run", "bash", "-c", "curl -s -o /dev/null -w %{http_code} -I https://github.com");
            First = first.WaitForExit();
            ContainersAfter = Docker.CountContainers(Key);
            NetworksAfter = Docker.ReadLines("network", "ls", "-q", "--filter", $"name=^{Key}-");
            LastSession = SmokeProject.FindLastSession(RuntimeDir);
        }
    }
}
