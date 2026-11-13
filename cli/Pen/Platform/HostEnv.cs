using Pen.Configuration;

namespace Pen.Platform;

/// <summary>Facts about the host user that Pen's logic depends on. Tests build their own.</summary>
internal sealed record HostEnv(string Home, uint Uid, uint Gid, string NuGetPackages)
{
    public string ConfigDir => System.IO.Path.Combine(Home, ".config", "pen");
    public string UserConfigPath => System.IO.Path.Combine(ConfigDir, Config.FileName);
    public string SeedDir => System.IO.Path.Combine(ConfigDir, "home");
    public string StateRoot => System.IO.Path.Combine(Home, ".local", "state", "pen");
    public string RuntimeDir(string projectKey) => System.IO.Path.Combine(StateRoot, projectKey);

    public static HostEnv FromProcess()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrEmpty(home) || !home.StartsWith('/'))
            throw new PenException("HOME isn't set to an absolute path");
        home = home.TrimEnd('/');
        if (home.Length == 0) home = "/";

        var nuget = Environment.GetEnvironmentVariable("NUGET_PACKAGES");
        if (string.IsNullOrEmpty(nuget))
            nuget = System.IO.Path.Combine(home, ".nuget", "packages");
        else if (!nuget.StartsWith('/'))
            throw new PenException($"NUGET_PACKAGES must be an absolute path (it's \"{Out.Field(nuget)}\")");
        nuget = nuget.TrimEnd('/');

        return new HostEnv(home, Native.GetUid(), Native.GetGid(), nuget);
    }

    /// <summary>A path for messages, with the home dir shown as ~.</summary>
    public string Display(string path) =>
        path == Home ? "~" : path.StartsWith(Home + "/", StringComparison.Ordinal) ? "~" + path[Home.Length..] : path;
}
