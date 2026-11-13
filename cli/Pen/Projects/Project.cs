using System.Security.Cryptography;
using System.Text;
using Pen.Configuration;
using Pen.GitProtection;
using Pen.Platform;

namespace Pen.Projects;

/// <summary>The project Pen runs in: the current directory, by its real path.</summary>
/// <remarks>
/// <para>
/// Its key, pen-&lt;dir&gt;-&lt;hash&gt;, names the project's volumes, image and runtime dir.
/// It comes from <see cref="RepoDir"/>, so all worktrees of a repo share one
/// key (logins, caches, sessions and locks), while each keeps its own
/// <see cref="Dir"/> (mounted as the project, with its own .pen/).
/// </para>
/// <para>
/// The repo is modeled apart from the dir: <see cref="CommonGitDir"/> is the
/// git dir holding config, hooks and objects (the dir's own .git, or for a
/// linked worktree the main checkout's), and <see cref="RepoDir"/> is the main
/// checkout. For a plain directory both describe the dir itself.
/// </para>
/// </remarks>
internal sealed record Project(string Dir, string Key, string? CommonGitDir, string RepoDir)
{
    /// <summary>The dir's own .git path, which may not exist.</summary>
    public string GitDir => Path.Combine(Dir, ".git");
    /// <summary>Whether the dir is a git repo (a checkout or a linked worktree).</summary>
    public bool IsRepo => CommonGitDir != null;
    /// <summary>Whether the dir is a linked worktree: its .git is a file naming another repo's git dir.</summary>
    public bool IsWorktree => CommonGitDir != null && CommonGitDir != GitDir;
    /// <summary>The dir's .pen/ path.</summary>
    public string PenDir => Path.Combine(Dir, ".pen");
    /// <summary>The dir's .pen/config.toml path.</summary>
    public string ProjectConfigPath => Path.Combine(PenDir, Config.FileName);

    /// <summary>Resolves <paramref name="cwd"/> to the project, applying the directory refusals.</summary>
    /// <returns>The project at <paramref name="cwd"/>'s real path, keyed by its main checkout.</returns>
    public static Project Resolve(string cwd, HostEnv host)
    {
        var dir = Native.RealPath(cwd) ?? throw new PenException("can't resolve the " +
            $"current directory {Out.Field(cwd)}: it was removed, or a parent's " +
            "permissions were changed. Fix that or cd to the project, then rerun");
        // Everything in it would look absent: a repo would pass for a plain directory.
        if (!Native.CanList(dir))
        {
            throw new PenException($"pen can't read {Out.Field(dir)}: fix its " +
                $"permissions (chmod u+rwx {GitCheck.Quote(dir)}), then rerun");
        }

        CheckAllowed(dir, host);
        var (common, repoDir) = FindRepo(dir);
        return new Project(dir, ComputeKey(repoDir), common, repoDir);
    }

    /// <summary>
    /// Finds the common git dir and main checkout of <paramref name="dir"/> (a real path), reading files and never
    /// running git.
    /// </summary>
    /// <returns>
    /// The dir's .git directory and the dir itself; for a linked worktree, its validated common dir
    /// (<see cref="RepoScan.ResolveWorktree"/>) and main checkout; otherwise no git dir and the dir itself.
    /// </returns>
    internal static (string? CommonGitDir, string RepoDir) FindRepo(string dir)
    {
        var git = Path.Combine(dir, ".git");
        if (Directory.Exists(git))
            return (git, dir);
        if (!File.Exists(git))
            return (null, dir);
        var common = RepoScan.ResolveWorktree(git);
        if (common == null)
            return (null, dir);
        // A bare repo's worktree has no main checkout; the dir stands in for it.
        return (common, RepoGuard.IsMainCheckoutGitDir(common) ? Path.GetDirectoryName(common)! : dir);
    }

    /// <summary>
    /// Computes the project key, pen-&lt;clean name&gt;-&lt;hash&gt;, of a main checkout's real path.
    /// </summary>
    public static string ComputeKey(string realDir) => $"pen-{CleanName(realDir)}-{HashPath(realDir)}";

    /// <summary>
    /// Cleans the dir's last component for a key: lowercased and reduced to [a-z0-9_-], which compose accepts.
    /// </summary>
    internal static string CleanName(string realDir)
    {
        var name = Path.GetFileName(realDir.TrimEnd('/')).ToLowerInvariant();
        var builder = new StringBuilder();
        foreach (var character in name)
        {
            if (character is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-')
                builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != '-')
                builder.Append('-');
        }
        var clean = builder.ToString().Trim('-');
        if (clean.Length > 40)
            clean = clean[..40].TrimEnd('-');
        return clean.Length == 0 ? "project" : clean;
    }

    /// <summary>Hashes a real path to the 8 hex digits that keys and per-dir file names use.</summary>
    internal static string HashPath(string realDir) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(realDir)))[..8];

    /// <summary>Refuses a directory Pen mustn't run in.</summary>
    /// <exception cref="PenException"><paramref name="dir"/> is refused.</exception>
    /// <remarks>
    /// The project is mounted read-write, so running in such a directory would hand the agent the host user's home,
    /// Pen's own config and state, or a repo it can't see the root of.
    /// </remarks>
    internal static void CheckAllowed(string dir, HostEnv host)
    {
        if (dir == "/")
            throw new PenException("refusing to run in /: run pen in a project directory");
        if (dir.Contains('\n'))
            throw new PenException("refusing to run in a directory whose path contains a newline");

        var home = Native.RealPath(host.Home) ?? host.Home;
        if (IsSameOrAncestor(dir, home))
        {
            throw new PenException(dir == home
                ? "refusing to run in your home directory: run pen in a project directory"
                : $"refusing to run in {dir}: it contains your home directory");
        }

        foreach (var penPath in new[] { host.ConfigDir, host.StateRoot })
        {
            var real = Native.RealPath(penPath) ?? penPath.Replace(host.Home, home, StringComparison.Ordinal);
            if (IsSameOrAncestor(dir, real) || IsSameOrAncestor(real, dir))
            {
                throw new PenException($"refusing to run in {Out.Field(dir)}: it " +
                    $"overlaps {host.Display(penPath)}, which the sandbox must not reach");
            }
        }

        if (!Path.Exists(Path.Combine(dir, ".git")))
        {
            for (var parent = Path.GetDirectoryName(dir); parent != null; parent = Path.GetDirectoryName(parent))
            {
                if (Path.Exists(Path.Combine(parent, ".git")))
                {
                    throw new PenException($"{Out.Field(dir)} is inside the git " +
                        $"repo at {Out.Field(parent)}: run pen from the repo root");
                }
            }
        }
    }

    private static bool IsSameOrAncestor(string ancestor, string path) =>
        path == ancestor || path.StartsWith(ancestor.TrimEnd('/') + "/", StringComparison.Ordinal);
}
