namespace Pen.SmokeTests.Harness;

/// <summary>Runs host git with the user's own config.</summary>
internal static class SmokeGit
{
    /// <summary>Gets the settings host git adds: a fixed identity, no signing and file://
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

    /// <summary>Checks whether a commit object, as git cat-file commit prints it, has a gpgsig header.</summary>
    /// <remarks>Only the header lines count: a gpgsig line in the message is not a signature.</remarks>
    /// <param name="commitObject">The commit's text.</param>
    /// <returns>Whether a header line starts with gpgsig.</returns>
    public static bool HasSignatureHeader(string commitObject) =>
        commitObject.Split('\n').TakeWhile(line => line.Length > 0)
            .Any(line => line.StartsWith("gpgsig ", StringComparison.Ordinal));

    /// <summary>Runs git in a directory, failing on a non-zero exit.</summary>
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

    /// <summary>Runs git in a directory with <see cref="HostGitSettings"/> first.</summary>
    /// <param name="dir">The directory.</param>
    /// <param name="args">Git's arguments.</param>
    /// <returns>Git's stdout.</returns>
    /// <exception cref="InvalidOperationException">Git exited non-zero.</exception>
    public static string RunHostGit(string dir, params string[] args) => Run(dir, [.. HostGitSettings, .. args]);

    /// <summary>Creates a repo with one empty commit, for the submodule tests to use.</summary>
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
