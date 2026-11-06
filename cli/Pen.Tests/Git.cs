using System.Diagnostics;

namespace Pen.Tests;

/// <summary>Runs git for test fixtures, with no user or system config in play.</summary>
internal static class Git
{
    public static string Run(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git", ["-C", dir, "-c", "user.name=t", "-c", "user.email=t@example.invalid", "-c", "init.defaultBranch=main", .. args])
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
