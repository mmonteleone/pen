using Pen.Platform;

namespace Pen.Configuration;

/// <summary>Creates the user's ~/.config/pen/ on first run, from the embedded templates.</summary>
internal static class UserConfigInitializer
{
    /// <summary>
    /// Creates ~/.config/pen/ and, each only when missing, its config.toml (600) from the template
    /// and the home seed (see <see cref="Seed"/>), and says so in one line.
    /// </summary>
    /// <param name="host">The host user's environment.</param>
    /// <param name="identity">
    /// Gives the git user.name and user.email for the seed's .gitconfig; called only when the seed is created.
    /// Pen passes <see cref="GitIdentity.ReadFromHost"/>.
    /// </param>
    /// <remarks>
    /// Never rewrites an existing file, and adds no config.toml next to a legacy config (<see cref="ConfigLoader"/>
    /// reports that).
    /// </remarks>
    public static void EnsureExists(HostEnv host, Func<GitIdentity> identity)
    {
        var createdDir = EnsureConfigDir(host);
        var createdConfig = EnsureUserConfigFile(host);
        var createdSeed = EnsureSeed(host, identity);
        ReportCreated(host, createdDir, createdConfig, createdSeed);
    }

    private static bool EnsureConfigDir(HostEnv host)
    {
        if (Directory.Exists(host.ConfigDir))
            return false;
        Directory.CreateDirectory(host.ConfigDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return true;
    }

    private static bool EnsureUserConfigFile(HostEnv host)
    {
        var legacy = Path.Combine(host.ConfigDir, ConfigLoader.LegacyName);
        if (File.Exists(host.UserConfigPath) || File.Exists(legacy))
            return false;
        CreateFromTemplate(host.UserConfigPath, "templates/" + ConfigLoader.FileName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return true;
    }

    private static bool EnsureSeed(HostEnv host, Func<GitIdentity> identity)
    {
        if (Path.Exists(host.SeedDir) || PathChecks.IsLink(host.SeedDir))
            return false;
        return Seed.EnsureExists(host, identity());
    }

    /// <summary>Says what was created: only the outermost of the dir, config.toml and seed.</summary>
    private static void ReportCreated(HostEnv host, bool createdDir, bool createdConfig, bool createdSeed)
    {
        if (createdDir)
        {
            Out.WriteLine($"created {host.Display(host.ConfigDir)}/: add " +
                $"GitHub tokens to {host.Display(host.UserConfigPath)}");
        }
        else if (createdConfig)
        {
            Out.WriteLine($"created {host.Display(host.UserConfigPath)}: add GitHub tokens there");
        }
        else if (createdSeed)
        {
            Out.WriteLine($"created {host.Display(host.SeedDir)}/, the home seed: " +
                "sandbox-wide agent config, copied into every project's sandbox home");
        }
    }

    private static void CreateFromTemplate(string path, string template, UnixFileMode mode)
    {
        try
        {
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = mode,
            });
            using var source = Embedded.Open(template);
            source.CopyTo(file);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another Pen created it first.
        }
    }
}
