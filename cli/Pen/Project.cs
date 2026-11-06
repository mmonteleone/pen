using System.Security.Cryptography;
using System.Text;

namespace Pen;

/// <summary>
/// The project pen runs in: the current directory, by its real path. Its key,
/// pen-&lt;dir&gt;-&lt;hash&gt;, names the project's volumes, image and runtime dir.
/// </summary>
internal sealed record Project(string Dir, string Key)
{
    public string GitDir => Path.Combine(Dir, ".git");
    public bool HasGitDir => Directory.Exists(GitDir);
    public string PenDir => Path.Combine(Dir, ".pen");
    public string ProjectAllowlistPath => Path.Combine(PenDir, Config.AllowlistName);

    /// <summary>Resolves <paramref name="cwd"/> and applies the directory refusals.</summary>
    public static Project Resolve(string cwd, HostEnv host)
    {
        var dir = Native.RealPath(cwd) ?? throw new PenException($"can't resolve the current directory {cwd}");
        CheckAllowed(dir, host);
        return new Project(dir, KeyFor(dir));
    }

    public static string KeyFor(string realDir) => $"pen-{CleanName(realDir)}-{Hash(realDir)}";

    /// <summary>The dir's last component, lowercased and reduced to [a-z0-9_-], which compose accepts.</summary>
    internal static string CleanName(string realDir)
    {
        var name = Path.GetFileName(realDir.TrimEnd('/')).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var clean = sb.ToString().Trim('-');
        if (clean.Length > 40) clean = clean[..40].TrimEnd('-');
        return clean.Length == 0 ? "project" : clean;
    }

    internal static string Hash(string realDir) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(realDir)))[..8];

    /// <summary>
    /// Refuses directories whose read-write mount would hand the agent the host
    /// user's home, pen's own config and state, or a repo it can't see the root of.
    /// </summary>
    internal static void CheckAllowed(string dir, HostEnv host)
    {
        if (dir == "/")
            throw new PenException("refusing to run in /: run pen in a project directory");
        if (dir.Contains('\n'))
            throw new PenException("refusing to run in a directory whose path contains a newline");

        var home = Native.RealPath(host.Home) ?? host.Home;
        if (IsSameOrAncestor(dir, home))
            throw new PenException(dir == home
                ? "refusing to run in your home directory: run pen in a project directory"
                : $"refusing to run in {dir}: it contains your home directory");

        foreach (var penPath in new[] { host.ConfigDir, host.StateRoot })
        {
            var real = Native.RealPath(penPath) ?? penPath.Replace(host.Home, home, StringComparison.Ordinal);
            if (IsSameOrAncestor(dir, real) || IsSameOrAncestor(real, dir))
                throw new PenException($"refusing to run in {dir}: it overlaps {host.Display(penPath)}, which the sandbox must not reach");
        }

        if (!Path.Exists(Path.Combine(dir, ".git")))
        {
            for (var parent = Path.GetDirectoryName(dir); parent != null; parent = Path.GetDirectoryName(parent))
            {
                if (Path.Exists(Path.Combine(parent, ".git")))
                    throw new PenException($"{dir} is inside the git repo at {parent}: run pen from the repo root");
            }
        }
    }

    private static bool IsSameOrAncestor(string ancestor, string path) =>
        path == ancestor || path.StartsWith(ancestor.TrimEnd('/') + "/", StringComparison.Ordinal);
}
