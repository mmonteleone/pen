using System.Security.Cryptography;
using System.Text;
using Pen.Platform;

namespace Pen.SmokeTests.Harness;

/// <summary>File helpers the checks share: modes, hashes and removal of trees a test may have locked down.</summary>
internal static class Files
{
    /// <summary>Gets the mode of a normal directory or script, rwxr-xr-x.</summary>
    public const UnixFileMode Mode755 =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead
        | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>Gets a file's permission bits in octal, as stat -c %a prints them (e.g. 700).</summary>
    /// <param name="path">The file.</param>
    /// <returns>The mode.</returns>
    public static string GetMode(string path) =>
        Convert.ToString((int)File.GetUnixFileMode(path) & 0b111_111_111, 8);

    /// <summary>Gets the UID that owns a file, as stat -c %u (or BSD stat -f %u) prints it.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The UID, or null if stat failed, e.g. because the file is missing.</returns>
    public static uint? GetOwnerUid(string path)
    {
        var stat = new HostCommand("/bin/sh", ["-c", "stat -c %u \"$1\" 2>/dev/null || stat -f %u \"$1\"", "_", path])
            .Run();
        return stat.ExitCode == 0 && uint.TryParse(stat.CapturedStdout, out var uid) ? uid : null;
    }

    /// <summary>Checks whether a path exists, following symlinks, as test -e does.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True if it does.</returns>
    public static bool Exists(string path) => Test("-e", path);

    /// <summary>Checks whether a path is a regular file, following symlinks, as test -f does.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True if it is.</returns>
    public static bool IsRegularFile(string path) => Test("-f", path);

    /// <summary>Checks whether a path is a directory, following symlinks, as test -d does.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True if it is.</returns>
    public static bool IsDirectory(string path) => Test("-d", path);

    /// <summary>Checks whether a path is a file that isn't empty, following symlinks, as test -s does.</summary>
    /// <param name="path">The path.</param>
    /// <returns>True if it is.</returns>
    public static bool IsNonEmpty(string path) => Test("-s", path);

    /// <summary>Hashes a file's bytes with SHA-256, as cksum &lt; file compares contents.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The hash in hex.</returns>
    public static string HashFile(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Lists a directory's entries by name, sorted, as ls -A does (without its locale's order).</summary>
    /// <param name="dir">The directory.</param>
    /// <returns>The names, or an empty list if it can't be listed.</returns>
    public static IReadOnlyList<string> ListNames(string dir)
    {
        try
        {
            return [.. Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).OfType<string>()
                .Order(StringComparer.Ordinal)];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Hashes a path as pen names project keys: the first 8 hex digits of its SHA-256.</summary>
    /// <param name="path">The path, as a real path.</param>
    /// <returns>The hash.</returns>
    public static string HashPath(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..8];

    /// <summary>Creates a new directory under <paramref name="parent"/> with mktemp -d.</summary>
    /// <param name="parent">The directory it goes in.</param>
    /// <param name="prefix">The start of its name, e.g. .pen-smoke.</param>
    /// <returns>Its real path.</returns>
    /// <exception cref="InvalidOperationException">mktemp failed.</exception>
    public static string CreateUniqueDir(string parent, string prefix)
    {
        var made = new HostCommand("mktemp", ["-d", Path.Combine(parent, prefix + "XXXXXX")]).Run();
        if (made.ExitCode != 0)
            throw new InvalidOperationException($"mktemp -d in {parent} failed: {made.Output}");
        var path = made.CapturedStdout;
        return Native.RealPath(path) ?? path;
    }

    /// <summary>Removes a tree, first giving its owner back the permissions a test may have taken away.</summary>
    /// <param name="path">The tree, which may be missing.</param>
    public static void DeleteTree(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget != null)
        {
            info.Delete();
            return;
        }
        if (!Directory.Exists(path) && !File.Exists(path))
            return;
        new HostCommand("chmod", ["-R", "u+rwx", path]).Run();
        new HostCommand("rm", ["-rf", path]).Run();
    }

    private static bool Test(string flag, string path) =>
        new HostCommand("/bin/sh", ["-c", "test \"$1\" \"$2\"", "_", flag, path]).Run().ExitCode == 0;
}
