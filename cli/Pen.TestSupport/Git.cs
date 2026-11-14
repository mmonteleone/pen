using System.Diagnostics;

namespace Pen.TestSupport;

/// <summary>Runs git for test fixtures, with no user or system config in play.</summary>
internal static class Git
{
    /// <summary>Runs git in <paramref name="dir"/> with a fixed identity, branch name and no maintenance.</summary>
    /// <param name="dir">The directory git runs in (-C).</param>
    /// <param name="args">Git's arguments.</param>
    /// <returns>Git's standard output.</returns>
    /// <exception cref="InvalidOperationException">Git exited with a non-zero code.</exception>
    public static string Run(string dir, params string[] args)
    {
        string[] fixedConfig =
        [
            "-c", "user.name=t", "-c", "user.email=t@example.invalid",
            "-c", "init.defaultBranch=main", "-c", "maintenance.auto=false",
        ];
        var startInfo = new ProcessStartInfo("git", ["-C", dir, .. fixedConfig, .. args])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        var result = Process.RunAndCaptureText(startInfo);
        if (result.ExitStatus.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StandardError}");
        return result.StandardOutput;
    }

    /// <summary>Creates a repo with one empty commit at <paramref name="dir"/>.</summary>
    /// <param name="dir">The repo's directory, created if missing.</param>
    /// <returns>The directory.</returns>
    public static string Repo(string dir)
    {
        Directory.CreateDirectory(dir);
        Run(dir, "init", "-q");
        Run(dir, "commit", "-q", "--allow-empty", "-m", "init");
        return dir;
    }
}
