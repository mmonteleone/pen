[assembly: System.Runtime.Versioning.UnsupportedOSPlatform("windows")]

namespace Pen.Tests;

/// <summary>A temp directory, by its real path (macOS's /var → /private/var), removed on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        var p = Directory.CreateTempSubdirectory("pen-test.").FullName;
        Path = Native.RealPath(p)!;
    }

    public string Sub(params string[] parts)
    {
        var p = System.IO.Path.Combine([Path, .. parts]);
        Directory.CreateDirectory(p);
        return p;
    }

    public HostEnv Host(string? home = null) =>
        new(home ?? Sub("home"), 501, 20, System.IO.Path.Combine(home ?? System.IO.Path.Combine(Path, "home"), ".nuget", "packages"));

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
