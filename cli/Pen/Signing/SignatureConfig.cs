namespace Pen.Signing;

/// <summary>
/// Builds command-line settings for verifying and making signatures, from the user's global git config.
/// </summary>
/// <remarks>
/// Settings given with <c>-c</c> beat the repo's own config, so only the user's own settings decide which program
/// signs and what verifies: the repo can't substitute its own signing program or make its own key verify.
/// </remarks>
internal static class SignatureConfig
{
    /// <summary>Builds the settings for reading %G?, so that only the user's settings decide what verifies.</summary>
    /// <returns>The settings as "-c key=value" pairs, already split.</returns>
    /// <remarks>
    /// <c>/dev/null</c> stands in for a file that isn't set or that the agent could write: no signers are trusted
    /// and no keys are revoked.
    /// </remarks>
    public static List<string> BuildForVerifying(HostGit git)
    {
        var config = BuildPrograms(git, git.ReadGlobalValue("gpg.format") ?? "openpgp");
        config.AddRange(FormatSetting("gpg.ssh.allowedSignersFile",
            KeepIfOutsideTheRepo(git.ReadGlobalValue("gpg.ssh.allowedSignersFile")) ?? "/dev/null"));
        config.AddRange(FormatSetting("gpg.ssh.revocationFile",
            KeepIfOutsideTheRepo(git.ReadGlobalValue("gpg.ssh.revocationFile")) ?? "/dev/null"));
        config.AddRange(FormatSetting("gpg.minTrustLevel", ReadMinTrust(git)));
        config.AddRange(FormatSetting("gpg.ssh.defaultKeyCommand",
            git.ReadGlobalValue("gpg.ssh.defaultKeyCommand") ?? ""));
        config.AddRange(FormatSetting("log.showSignature", "false"));
        return config;
    }

    /// <summary>Builds the signing settings from the user's global config.</summary>
    /// <returns>The settings as "-c key=value" pairs, already split.</returns>
    /// <exception cref="PenException">The format is ssh, but no key or key command is set globally.</exception>
    /// <remarks>They go on the command line so the repo can't substitute its own signing program.</remarks>
    public static List<string> BuildForSigning(HostGit git)
    {
        var format = git.ReadGlobalValue("gpg.format") ?? "openpgp";
        var config = BuildPrograms(git, format);
        var key = git.ReadGlobalValue("user.signingkey");
        var keyCommand = git.ReadGlobalValue("gpg.ssh.defaultKeyCommand");
        if (key is not null)
            config.AddRange(FormatSetting("user.signingkey", key));
        if (keyCommand is not null)
            config.AddRange(FormatSetting("gpg.ssh.defaultKeyCommand", keyCommand));
        // With ssh and no key, git runs gpg.ssh.defaultKeyCommand, which a
        // repo could set: so one of the two has to come from global config.
        if (format == "ssh" && key is null && keyCommand is null)
        {
            throw new PenException("gpg.format is ssh but user.signingkey isn't set in your global " +
                "git config: set it (git config --global user.signingkey ~/.ssh/id_ed25519.pub)");
        }

        return config;
    }

    /// <summary>Reads the global gpg.minTrustLevel.</summary>
    /// <returns>The level, or "marginal" if it isn't set globally.</returns>
    public static string ReadMinTrust(HostGit git) => git.ReadGlobalValue("gpg.minTrustLevel") ?? "marginal";

    /// <summary>Keeps a configured file path unless git would resolve it against the working tree.</summary>
    /// <returns><paramref name="path"/> if it's absolute or starts with ~ or %(prefix)/, otherwise null.</returns>
    /// <remarks>The agent writes in the working tree, so it could list its own key in such a file.</remarks>
    public static string? KeepIfOutsideTheRepo(string? path)
    {
        if (path is null)
            return null;
        var name = path.StartsWith(":(optional)", StringComparison.Ordinal) ? path[":(optional)".Length..] : path;
        return name.StartsWith('/') || name.StartsWith('~') || name.StartsWith("%(prefix)/", StringComparison.Ordinal)
            ? path
            : null;
    }

    /// <summary>Builds the settings for the format and every program git could run for it.</summary>
    /// <param name="git">The git whose global config is read.</param>
    /// <param name="format">The global gpg.format, or "openpgp" if it isn't set.</param>
    /// <returns>The settings as "-c key=value" pairs, already split.</returns>
    /// <remarks>
    /// All of them, whatever the format: to verify, git picks the program by the type of the signature, which the
    /// agent chooses. Where one isn't set globally, git's default is passed instead, since a missing override
    /// overrides nothing.
    /// </remarks>
    private static List<string> BuildPrograms(HostGit git, string format)
    {
        var program = git.ReadGlobalValue("gpg.program");
        var openpgpProgram = git.ReadGlobalValue("gpg.openpgp.program");
        var config = new List<string>();
        config.AddRange(FormatSetting("gpg.format", format));
        config.AddRange(FormatSetting("gpg.program", program ?? openpgpProgram ?? "gpg"));
        config.AddRange(FormatSetting("gpg.openpgp.program", openpgpProgram ?? program ?? "gpg"));
        config.AddRange(FormatSetting("gpg.x509.program", git.ReadGlobalValue("gpg.x509.program") ?? "gpgsm"));
        config.AddRange(FormatSetting("gpg.ssh.program", git.ReadGlobalValue("gpg.ssh.program") ?? "ssh-keygen"));
        return config;
    }

    /// <summary>Formats one setting for the command line.</summary>
    /// <returns><c>-c</c> and <c>key=value</c>, as two arguments.</returns>
    private static string[] FormatSetting(string key, string value) => ["-c", $"{key}={value}"];
}
