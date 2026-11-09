using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Pen.Tests;

/// <summary>
/// No git a test starts may start detached maintenance. It outlives the command
/// and runs git worktree prune on the test's repo, which deletes a worktree entry
/// the test is building and hangs forever on a FIFO in one. The environment
/// covers git run from scripts and through HostGit too.
/// </summary>
internal static class NoGitMaintenance
{
    [ModuleInitializer]
    internal static void Disable()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "maintenance.auto");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "false");
    }
}

/// <summary>Runs git for test fixtures, with no user or system config in play.</summary>
internal static class Git
{
    public static string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", dir, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "init.defaultBranch=main", "-c", "maintenance.auto=false", .. args])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        var r = Process.RunAndCaptureText(psi);
        if (r.ExitStatus.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {r.StandardError}");
        return r.StandardOutput;
    }

    /// <summary>A repo with one commit at <paramref name="dir"/>.</summary>
    public static string Repo(string dir)
    {
        Directory.CreateDirectory(dir);
        Run(dir, "init", "-q");
        Run(dir, "commit", "-q", "--allow-empty", "-m", "init");
        return dir;
    }
}
