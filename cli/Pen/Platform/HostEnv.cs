using Pen.Configuration;

namespace Pen.Platform;

/// <summary>Facts about the host user that Pen's logic depends on.</summary>
/// <param name="Home">The home directory, absolute, without a trailing slash.</param>
/// <param name="Uid">The user's UID.</param>
/// <param name="Gid">The user's primary GID.</param>
/// <param name="NuGetPackages">The host's NuGet package folder, absolute.</param>
/// <remarks>
/// Production code gets one from <see cref="FromProcess"/>; tests build their own. It also owns the process's
/// working directory (<see cref="TryReadWorkingDirectory"/>, <see cref="LeaveWorkingDirectory"/>).
/// </remarks>
internal sealed record HostEnv(string Home, uint Uid, uint Gid, string NuGetPackages)
{
    /// <summary>Gets the environment Pen was started with; empty unless set.</summary>
    /// <remarks>
    /// The default is empty: only <see cref="FromProcess"/> captures the real environment. Pen never changes its own
    /// environment, so what it read at startup is what it has later.
    /// </remarks>
    public IReadOnlyDictionary<string, string> Variables { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the user's config dir, ~/.config/pen.</summary>
    public string ConfigDir => System.IO.Path.Combine(Home, ".config", "pen");

    public string UserConfigPath => System.IO.Path.Combine(ConfigDir, ConfigLoader.FileName);

    /// <summary>Gets the home seed dir, ~/.config/pen/home.</summary>
    public string SeedDir => System.IO.Path.Combine(ConfigDir, "home");

    /// <summary>Gets the dir holding every project's runtime dir, ~/.local/state/pen.</summary>
    public string StateRoot => System.IO.Path.Combine(Home, ".local", "state", "pen");

    public string GetRuntimeDir(string projectKey) => System.IO.Path.Combine(StateRoot, projectKey);

    /// <summary>Gets an environment variable Pen was started with, or null if it isn't set.</summary>
    public string? GetVariable(string name) => Variables.GetValueOrDefault(name);

    /// <summary>
    /// Reads the host user's facts from the process: HOME, NUGET_PACKAGES, the UID and GID, and the environment.
    /// </summary>
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

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry is { Key: string name, Value: string value })
                variables[name] = value;
        }

        return new HostEnv(home, Native.GetUid(), Native.GetGid(), nuget) { Variables = variables };
    }

    /// <summary>Reads the process's working directory.</summary>
    /// <returns>The directory, or null if it was removed or can't be reached.</returns>
    public static string? TryReadWorkingDirectory()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Changes the process's working directory to Pen's state dir, or / if that can't be used.</summary>
    /// <returns>The directory it left, as <see cref="TryReadWorkingDirectory"/> read it just before.</returns>
    /// <remarks>
    /// The project dir is the agent's to chmod or the user's to remove while Pen runs, and Pen's children (git,
    /// docker) need a working directory they can use, so the end of a session mustn't depend on it. Nothing uses
    /// the process's working directory after this.
    /// </remarks>
    public string? LeaveWorkingDirectory()
    {
        var left = TryReadWorkingDirectory();
        try
        {
            Directory.CreateDirectory(StateRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.CurrentDirectory = StateRoot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Environment.CurrentDirectory = "/";
        }

        return left;
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
