using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

/// <summary>A session's life: exit codes, terminals, sessions side by side, and what's left once they end.</summary>
public sealed class SessionTests(SessionTests.Fixture fixture) : IClassFixture<SessionTests.Fixture>
{
    private const string InitHint =
        "pen: no .pen/config.toml here: pen init writes one with this project's tools and hidden files";

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
        var pen = ShellQuote.Quote(IntegrationEnvironment.PenPath);

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
        var result = new Pty { TimeoutSeconds = 60 }.SendOn("[$#] $", "echo shell-$((6*7)); exit\\r")
            .Run(Cli.WithEnvironment("SHELL", "/bin/bash"));

        Assert.True(result.Text.Contains("shell-42", StringComparison.Ordinal), result.Describe());
    }

    [Fact(DisplayName = "...fish when the host's $SHELL is fish (/opt/homebrew/bin/fish)")]
    public void HostFishIsFish()
    {
        var result = new Pty { TimeoutSeconds = 60 }.SendOn("[>#] ", "echo fish-$FISH_VERSION; exit\\r")
            .Run(Cli.WithEnvironment("SHELL", "/opt/homebrew/bin/fish"));

        Assert.True(Regex.IsMatch(result.Text, "fish-[0-9]+\\.[0-9]+"), result.Describe());
    }

    [Fact(DisplayName = "...zsh when it's zsh (/bin/zsh), with a prompt on a fresh home, not the new-user menu")]
    public void HostZshIsZshWithNoNewUserMenu()
    {
        // The project is new, so its home volume is too: no .zshrc, which zsh-newuser-install would stop for.
        var result = new Pty { TimeoutSeconds = 60 }
            .SendOn("[%#] ", "[ -e ~/.zshrc ] || echo zsh-$((6*7)); exit\\r")
            .Run(Cli.WithWorkingDirectory(fixture.FreshProject.Dir).WithEnvironment("SHELL", "/bin/zsh"));

        Assert.True(
            result.Text.Contains("zsh-42", StringComparison.Ordinal)
                && !result.Text.Contains("zsh-newuser-install", StringComparison.Ordinal)
                && !result.Text.Contains("Z Shell configuration function for new users", StringComparison.Ordinal),
            result.Describe());
    }

    [Fact(DisplayName = "...and bash when it's another shell (/usr/bin/nu)")]
    public void OtherHostShellIsBash()
    {
        var result = new Pty { TimeoutSeconds = 60 }.SendOn("[$#] $", "echo bash-${BASH_VERSINFO[0]}; exit\\r")
            .Run(Cli.WithEnvironment("SHELL", "/usr/bin/nu"));

        Assert.True(Regex.IsMatch(result.Text, "bash-[0-9]"), result.Describe());
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

    [Fact(DisplayName = "a project's first session suggests pen init")]
    public void FirstSessionSuggestsInit() =>
        Assert.True(
            fixture.HintFirst.ExitCode == 0 && fixture.HintFirst.Output.Contains(InitHint, StringComparison.Ordinal),
            fixture.HintFirst.Describe());

    [Fact(DisplayName = "...and its next one doesn't")]
    public void NextSessionDoesNotSuggestInit() =>
        Assert.True(
            fixture.HintSecond.ExitCode == 0
                && fixture.HintSecond.OutputComplete
                && !fixture.HintSecond.Output.Contains(InitHint, StringComparison.Ordinal),
            fixture.HintSecond.Describe());

    [Fact(DisplayName = "...nor does a first session where .pen/config.toml exists")]
    public void ProjectWithConfigIsNotToldToInit() =>
        Assert.True(
            fixture.WithConfig.ExitCode == 0
                && fixture.WithConfig.OutputComplete
                && !fixture.WithConfig.Output.Contains(InitHint, StringComparison.Ordinal),
            fixture.WithConfig.Describe());

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
    /// left once both have ended; then runs two sessions in a project with no config and one in a project with
    /// one, for the pen init hint.</summary>
    public sealed class Fixture : IntegrationFixture
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

        /// <summary>Gets a second project, where no session has run, so its home volume is new.</summary>
        internal IntegrationProject FreshProject { get; private set; } = null!;

        /// <summary>Gets the first session in a project with no .pen/config.toml.</summary>
        internal CommandResult HintFirst { get; private set; } = null!;

        /// <summary>Gets the second session in that project.</summary>
        internal CommandResult HintSecond { get; private set; } = null!;

        /// <summary>Gets the first session in a project whose .pen/config.toml exists.</summary>
        internal CommandResult WithConfig { get; private set; } = null!;

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
            LastSession = IntegrationProject.FindLastSession(RuntimeDir);
            FreshProject = CreateProject();

            var hintCli = Cli.WithWorkingDirectory(CreateProject().Dir);
            HintFirst = hintCli.Run("run", "true");
            HintSecond = hintCli.Run("run", "true");
            var configured = CreateProject();
            Directory.CreateDirectory(Path.Combine(configured.Dir, ".pen"));
            File.WriteAllText(Path.Combine(configured.Dir, ".pen", "config.toml"), "");
            WithConfig = Cli.WithWorkingDirectory(configured.Dir).Run("run", "true");
        }
    }
}
