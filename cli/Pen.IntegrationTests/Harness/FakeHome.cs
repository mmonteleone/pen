namespace Pen.IntegrationTests.Harness;

/// <summary>A throwaway HOME, so config checks never touch the real ~/.config/pen.</summary>
/// <remarks>
/// It starts with only a .gitconfig, whose identity pen's first run copies into the seed; that run creates
/// ~/.config/pen from the templates, whose config has every value commented out, so sessions are tool-free.
/// </remarks>
internal sealed class FakeHome
{
    /// <summary>The user.name in the throwaway HOME's global git config.</summary>
    public const string GitName = "Integration Host";

    /// <summary>The user.email in the throwaway HOME's global git config.</summary>
    public const string GitEmail = "integration-host@example.invalid";

    /// <summary>Creates the HOME at <paramref name="path"/> with its global git identity.</summary>
    /// <param name="path">The directory, created if missing.</param>
    public FakeHome(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
        File.WriteAllText(
            System.IO.Path.Combine(path, ".gitconfig"),
            $"[user]\n\tname = {GitName}\n\temail = {GitEmail}\n");
    }

    /// <summary>Gets the HOME's path.</summary>
    public string Path { get; }

    /// <summary>Gets ~/.config/pen.</summary>
    public string ConfigDir => System.IO.Path.Combine(Path, ".config", "pen");

    /// <summary>Gets ~/.config/pen/config.toml.</summary>
    public string ConfigPath => System.IO.Path.Combine(ConfigDir, "config.toml");

    /// <summary>Gets the home seed, ~/.config/pen/home.</summary>
    public string SeedDir => System.IO.Path.Combine(ConfigDir, "home");

    /// <summary>Gets ~/.local/state/pen, which holds the projects' runtime dirs.</summary>
    public string StateRoot => System.IO.Path.Combine(Path, ".local", "state", "pen");

    /// <summary>Gets the runtime dir of a project key under this HOME.</summary>
    /// <param name="key">The project key.</param>
    /// <returns>The path.</returns>
    public string GetRuntimeDir(string key) => System.IO.Path.Combine(StateRoot, key);

    /// <summary>Replaces config.toml with these lines, mode 600.</summary>
    /// <param name="lines">The file's lines.</param>
    public void WriteConfig(params string[] lines)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, string.Concat(lines.Select(line => line + "\n")));
        File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>Gives sessions no tools: a config.toml with nothing in it.</summary>
    public void MakeToolFree() => WriteConfig("# none");

    /// <summary>Sets a value in the HOME's global git config (git config --file ~/.gitconfig name value).</summary>
    /// <param name="name">The setting, e.g. commit.gpgsign.</param>
    /// <param name="value">Its value.</param>
    /// <exception cref="InvalidOperationException">Git failed.</exception>
    public void SetGitConfig(string name, string value) =>
        new HostCommand("git", ["config", "--file", System.IO.Path.Combine(Path, ".gitconfig"), name, value]).Run()
            .RequireSuccess();

    /// <summary>Has git in this HOME sign with an SSH key through ssh-keygen, by setting
    /// gpg.format, user.signingkey and gpg.ssh.program.</summary>
    /// <param name="key">The key.</param>
    public void UseSigningKey(SigningKey key)
    {
        SetGitConfig("gpg.format", "ssh");
        SetGitConfig("user.signingkey", key.Path);
        SetGitConfig("gpg.ssh.program", "ssh-keygen");
    }
}
