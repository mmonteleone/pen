using System.Text;
using Pen.Platform;

namespace Pen.Tests;

/// <summary>
/// What each command sets up before it runs, through the built pen in a child process: help, version and the
/// listings need no host, pen ps no user config, and pen sign and pen clean never read .pen/config.toml.
/// </summary>
public class DispatchTierTests
{
    /// <summary>The pen apphost built next to the tests.</summary>
    private static readonly string PenPath = Path.Combine(AppContext.BaseDirectory, "pen");

    /// <summary>A docker that records its calls and finds no volume, so nothing reaches the real daemon.</summary>
    private const string FakeDocker = """
        #!/bin/sh
        printf '%s\n' "$*" >> "$(dirname -- "$0")/calls"
        case "$1 $2" in "volume inspect") exit 1;; esac
        """;

    [Theory]
    [InlineData("--help")]
    [InlineData("--version")]
    [InlineData("allow", "--help")]
    [InlineData("tools")]
    [InlineData("allow", "--defaults")]
    public void HelpVersionAndListingsTouchNothingInHome_AndNeedNoHome(params string[] args)
    {
        using var t = new TempDir();
        var home = t.Sub("home");

        var withHome = RunPen(t, home, t.Sub("cwd"), args);
        var withoutHome = RunPen(t, home: null, t.Sub("cwd"), args);

        Assert.True(withHome.ExitCode == 0, withHome.Stderr);
        Assert.Empty(Directory.EnumerateFileSystemEntries(home));
        Assert.True(withoutHome.ExitCode == 0, withoutHome.Stderr);
        Assert.False(File.Exists(Path.Combine(t.Path, "bin", "calls")));
    }

    [Theory]
    [InlineData("ps")]
    [InlineData("ls")]
    public void PsRunsWithoutTheUserConfig(params string[] args)
    {
        using var t = new TempDir();
        var home = t.Sub("home");

        var result = RunPen(t, home, t.Sub("cwd"), args);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal("pen: no sessions running\n", result.Stderr);
        Assert.False(Directory.Exists(Path.Combine(home, ".config", "pen")));
    }

    public static TheoryData<string, string> BrokenProjectConfigs => new()
    {
        { "allow = [\n", "pen: .pen/config.toml:2: " },
        { "[github]\nme = \"github_pat_x\"\n", "pen: .pen/config.toml: \"github\" belongs in ~/.config/pen/config.toml" },
        { "docker = \"yes\"\n", "pen: .pen/config.toml: docker must be true or false" },
    };

    [Theory]
    [MemberData(nameof(BrokenProjectConfigs))]
    public void SignAndCleanIgnoreABrokenProjectConfig(string config, string error)
    {
        using var t = new TempDir();
        var home = t.Sub("home");
        var project = Git.Repo(t.Sub("proj"));
        Directory.CreateDirectory(Path.Combine(project, ".pen"));
        File.WriteAllText(Path.Combine(project, ".pen", "config.toml"), config);

        // The config is one Pen refuses wherever it's read.
        var trust = RunPen(t, home, project, "trust");
        Assert.Equal(1, trust.ExitCode);
        Assert.Contains("\n" + error, "\n" + trust.Stderr);

        var sign = RunPen(t, home, project, "sign", "HEAD");
        var clean = RunPen(t, home, project, "clean");
        var stale = RunPen(t, home, project, "clean", "--stale");

        Assert.True(sign.ExitCode == 0, sign.Stderr);
        Assert.EndsWith("pen: nothing to sign: no commits in HEAD..HEAD\n", sign.Stderr);
        Assert.True(clean.ExitCode == 0, clean.Stderr);
        Assert.Matches("^pen: nothing to clean: pen-proj-[0-9a-f]+ has no volumes\n$", clean.Stderr);
        Assert.True(stale.ExitCode == 0, stale.Stderr);
        Assert.EndsWith("pen: nothing stale to clean\n", stale.Stderr);
        foreach (var result in new[] { sign, clean, stale })
            Assert.DoesNotContain(".pen/config.toml", result.Stderr + result.Stdout);
    }

    private sealed record PenResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Runs pen with <paramref name="args"/> in <paramref name="dir"/>, stdin from /dev/null, HOME at
    /// <paramref name="home"/> (unset when null) and the fake docker first on PATH.
    /// </summary>
    private static PenResult RunPen(TempDir t, string? home, string dir, params string[] args)
    {
        var bin = t.Sub("bin");
        var docker = Path.Combine(bin, "docker");
        if (!File.Exists(docker))
        {
            File.WriteAllText(docker, FakeDocker);
            File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var startInfo = Proc.CreateStartInfo("/bin/sh", ["-c", "exec \"$0\" \"$@\" </dev/null", PenPath, .. args]);
        startInfo.WorkingDirectory = dir;
        if (home is null)
            startInfo.Environment.Remove("HOME");
        else
            startInfo.Environment["HOME"] = home;
        startInfo.Environment["PATH"] = bin + ":" + Environment.GetEnvironmentVariable("PATH");
        startInfo.Environment.Remove("NUGET_PACKAGES");
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        var result = Proc.CaptureBytes(startInfo, timeout: TimeSpan.FromSeconds(60));
        Assert.True(result.End != CaptureEnd.TimedOut, $"pen {string.Join(' ', args)} didn't finish in 60 s");
        return new(result.ExitCode, Encoding.UTF8.GetString(result.Stdout), Encoding.UTF8.GetString(result.Stderr));
    }
}
