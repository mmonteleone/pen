using System.Security.Cryptography;
using System.Text;
using Pen.Platform;

namespace Pen.SmokeTests.Harness;

/// <summary>File helpers the checks share: modes, hashes and removal of trees a test may have locked down.</summary>
internal static class Files
{
    /// <summary>Gets a file's permission bits in octal, as stat -c %a prints them (e.g. 700).</summary>
    /// <param name="path">The file.</param>
    /// <returns>The mode.</returns>
    public static string GetMode(string path) =>
        Convert.ToString((int)File.GetUnixFileMode(path) & 0b111_111_111, 8);

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
}
