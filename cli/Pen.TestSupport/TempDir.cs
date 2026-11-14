using Pen.Platform;

namespace Pen.TestSupport;

/// <summary>A temp directory, by its real path (macOS's /var → /private/var), removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    /// <summary>Creates a temp directory named pen-test.*.</summary>
    public TempDir()
        : this("pen-test.")
    {
    }

    /// <summary>Creates a temp directory whose name starts with <paramref name="prefix"/>.</summary>
    /// <param name="prefix">The start of the directory's name.</param>
    public TempDir(string prefix)
    {
        var created = Directory.CreateTempSubdirectory(prefix).FullName;
        Path = Native.RealPath(created)!;
    }

    /// <summary>Gets the directory's real path.</summary>
    public string Path { get; }

    /// <summary>Creates a subdirectory, with any missing parents.</summary>
    /// <param name="parts">The path components under the temp directory.</param>
    /// <returns>The subdirectory's path.</returns>
    public string Sub(params string[] parts)
    {
        var path = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a host environment for a home directory, by default home/ under the temp directory.</summary>
    /// <param name="home">The home directory, or null for home/ under the temp directory.</param>
    /// <returns>The host environment, with UID 501, GID 20 and the home's NuGet folder.</returns>
    public HostEnv Host(string? home = null)
    {
        var nuget = System.IO.Path.Combine(home ?? System.IO.Path.Combine(Path, "home"), ".nuget", "packages");
        return new HostEnv(home ?? Sub("home"), 501, 20, nuget);
    }

    /// <summary>Removes the directory and everything in it, ignoring IO errors.</summary>
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
