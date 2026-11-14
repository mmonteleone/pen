namespace Pen.SmokeTests.Harness;

/// <summary>Runs host git with the user's own config, as smoke.sh's bare git and its hostgit helper do.</summary>
internal static class SmokeGit
{
    /// <summary>Gets the settings smoke.sh's hostgit adds: a fixed identity, no signing and file://
    /// submodules.</summary>
    public static IReadOnlyList<string> HostGitSettings { get; } =
    [
        "-c", "user.name=smoke", "-c", "user.email=smoke@example.invalid", "-c", "commit.gpgsign=false",
        "-c", "protocol.file.allow=always",
    ];

    /// <summary>Gets the command that runs git in a directory (git -C dir …), for a check that reads its
    /// result.</summary>
    /// <param name="dir">The directory.</param>
    /// <param name="args">Git's arguments.</param>
    /// <returns>The command.</returns>
    public static HostCommand CreateCommand(string dir, params string[] args) => new("git", ["-C", dir, .. args]);

    /// <summary>Runs git in a directory, as a command smoke.sh's set -e stopped on.</summary>
    /// <param name="dir">The directory.</param>
    /// <param name="args">Git's arguments.</param>
    /// <returns>Git's stdout.</returns>
    /// <exception cref="InvalidOperationException">Git exited non-zero.</exception>
    public static string Run(string dir, params string[] args)
    {
        var result = CreateCommand(dir, args).Run();
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git -C {dir} {string.Join(' ', args)} failed: {result.Output}");
        return result.Stdout;
    }

    /// <summary>Runs git in a directory with <see cref="HostGitSettings"/> first, as smoke.sh's hostgit does.</summary>
    /// <param name="dir">The directory.</param>
    /// <param name="args">Git's arguments.</param>
    /// <returns>Git's stdout.</returns>
    /// <exception cref="InvalidOperationException">Git exited non-zero.</exception>
    public static string RunHostGit(string dir, params string[] args) => Run(dir, [.. HostGitSettings, .. args]);

    /// <summary>Creates a repo with one empty commit, as smoke.sh's submodule sections make $SCRATCH/smlib.</summary>
    /// <param name="dir">The repo's directory.</param>
    /// <param name="message">The commit's message.</param>
    public static void CreateRepo(string dir, string message)
    {
        Directory.CreateDirectory(dir);
        Run(dir, "init", "-q");
        Run(dir, "-c", "user.name=smoke", "-c", "user.email=smoke@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "-q", "--allow-empty", "-m", message);
    }
}
