using Pen.Configuration;

namespace Pen.Platform;

/// <summary>Facts about the host user that Pen's logic depends on.</summary>
/// <param name="Home">The home directory, absolute, without a trailing slash.</param>
/// <param name="Uid">The user's UID.</param>
/// <param name="Gid">The user's primary GID.</param>
/// <param name="NuGetPackages">The host's NuGet package folder, absolute.</param>
/// <remarks>Production code gets one from <see cref="FromProcess"/>; tests build their own.</remarks>
internal sealed record HostEnv(string Home, uint Uid, uint Gid, string NuGetPackages)
{
    /// <summary>Gets the user's config dir, ~/.config/pen.</summary>
    public string ConfigDir => System.IO.Path.Combine(Home, ".config", "pen");

    /// <summary>Gets the path of the user's config.toml.</summary>
    public string UserConfigPath => System.IO.Path.Combine(ConfigDir, Config.FileName);

    /// <summary>Gets the home seed dir, ~/.config/pen/home.</summary>
    public string SeedDir => System.IO.Path.Combine(ConfigDir, "home");

    /// <summary>Gets the dir holding every project's runtime dir, ~/.local/state/pen.</summary>
    public string StateRoot => System.IO.Path.Combine(Home, ".local", "state", "pen");

    /// <summary>Gets the runtime dir of the project with key <paramref name="projectKey"/>.</summary>
    public string GetRuntimeDir(string projectKey) => System.IO.Path.Combine(StateRoot, projectKey);

    /// <summary>Reads the host user's facts from the process: HOME, NUGET_PACKAGES, the UID and GID.</summary>
    /// <exception cref="PenException">HOME or NUGET_PACKAGES isn't an absolute path.</exception>
    public static HostEnv FromProcess()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home) || !home.StartsWith('/'))
            throw new PenException("HOME isn't set to an absolute path");
        home = home.TrimEnd('/');
        if (home.Length == 0)
            home = "/";

        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(nuget))
            nuget = System.IO.Path.Combine(home, ".nuget", "packages");
        else if (!nuget.StartsWith('/'))
            throw new PenException($"NUGET_PACKAGES must be an absolute path (it's \"{Out.Field(nuget)}\")");
        nuget = nuget.TrimEnd('/');

        return new HostEnv(home, Native.GetUid(), Native.GetGid(), nuget);
    }

    /// <summary>Formats a path for messages, with the home dir shown as ~.</summary>
    public string Display(string path)
    {
        if (path == Home)
            return "~";
        if (path.StartsWith(Home + "/", StringComparison.Ordinal))
            return "~" + path[Home.Length..];
        return path;
    }
}
