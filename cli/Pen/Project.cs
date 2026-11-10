using System.Security.Cryptography;
using System.Text;

namespace Pen;

/// <summary>
/// The project Pen runs in: the current directory, by its real path. Its key,
/// pen-&lt;dir&gt;-&lt;hash&gt;, names the project's volumes, image and runtime dir.
/// It comes from <see cref="RepoDir"/>, so all worktrees of a repo share one
/// key (logins, caches, sessions and locks), while each keeps its own
/// <see cref="Dir"/> (mounted as the project, with its own .pen/).
/// <para>
/// The repo is modeled apart from the dir: <see cref="CommonGitDir"/> is the
/// git dir holding config, hooks and objects (the dir's own .git, or for a
/// linked worktree the main checkout's), and <see cref="RepoDir"/> is the main
/// checkout. For a plain directory both describe the dir itself.
/// </para>
/// </summary>
/// <summary>
/// Where Pen was started, and moving away from there. The project dir is the
/// agent's to chmod or the user's to remove while Pen runs, and Pen's children
/// (git, docker) need a working directory they can use, so the end of a session
/// mustn't depend on it. Nothing uses the process's cwd after <see cref="Leave"/>.
/// </summary>
internal static class StartDir
{
    private static bool _left;
    private static string? _dir;

    /// <summary>The directory Pen started in, or a refusal saying why it can't be used.</summary>
    public static string Get()
    {
        var dir = _left ? _dir : Read();
        return dir ?? throw new PenException("pen can't read the current directory: it was removed, or its permissions or a parent's were changed. Fix that (chmod u+rwx . if it's only this directory's) or cd to the project, then rerun");
    }

    /// <summary>Changes to Pen's state dir, or / if that can't be used.</summary>
    public static void Leave(HostEnv host)
    {
        _dir = Read();
        _left = true;
        try
        {
            Directory.CreateDirectory(host.StateRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.CurrentDirectory = host.StateRoot;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Environment.CurrentDirectory = "/"; }
    }

    private static string? Read()
    {
        try { return Environment.CurrentDirectory; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

internal sealed record Project(string Dir, string Key, string? CommonGitDir, string RepoDir)
{
    public string GitDir => Path.Combine(Dir, ".git");
    public bool IsRepo => CommonGitDir != null;
    /// <summary>A linked worktree: its .git is a file naming another repo's git dir.</summary>
    public bool IsWorktree => CommonGitDir != null && CommonGitDir != GitDir;
    public string PenDir => Path.Combine(Dir, ".pen");
    public string ProjectConfigPath => Path.Combine(PenDir, Config.FileName);

    /// <summary>Resolves <paramref name="cwd"/> and applies the directory refusals.</summary>
    public static Project Resolve(string cwd, HostEnv host)
    {
        var dir = Native.RealPath(cwd) ?? throw new PenException($"can't resolve the current directory {Out.Field(cwd)}: it was removed, or a parent's permissions were changed. Fix that or cd to the project, then rerun");
        // Everything in it would look absent: a repo would pass for a plain directory.
        if (!Native.CanList(dir))
            throw new PenException($"pen can't read {Out.Field(dir)}: fix its permissions (chmod u+rwx {GitCheck.Quote(dir)}), then rerun");
        CheckAllowed(dir, host);
        var (common, repoDir) = FindRepo(dir);
        return new Project(dir, KeyFor(repoDir), common, repoDir);
    }

    /// <summary>
    /// The common git dir and main checkout of <paramref name="dir"/> (a real
    /// path): its .git directory, or a linked worktree's validated common dir
    /// (<see cref="RepoScan.ResolveWorktree"/>). Reads files; never runs git.
    /// </summary>
    internal static (string? CommonGitDir, string RepoDir) FindRepo(string dir)
    {
        var git = Path.Combine(dir, ".git");
        if (Directory.Exists(git)) return (git, dir);
        if (!File.Exists(git)) return (null, dir);
        var common = RepoScan.ResolveWorktree(git);
        if (common == null) return (null, dir);
        // A bare repo's worktree has no main checkout; the dir stands in for it.
        return (common, RepoGuard.IsMainCheckoutGitDir(common) ? Path.GetDirectoryName(common)! : dir);
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
    /// user's home, Pen's own config and state, or a repo it can't see the root of.
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
                throw new PenException($"refusing to run in {Out.Field(dir)}: it overlaps {host.Display(penPath)}, which the sandbox must not reach");
        }

        if (!Path.Exists(Path.Combine(dir, ".git")))
        {
            for (var parent = Path.GetDirectoryName(dir); parent != null; parent = Path.GetDirectoryName(parent))
            {
                if (Path.Exists(Path.Combine(parent, ".git")))
                    throw new PenException($"{Out.Field(dir)} is inside the git repo at {Out.Field(parent)}: run pen from the repo root");
            }
        }
    }

    private static bool IsSameOrAncestor(string ancestor, string path) =>
        path == ancestor || path.StartsWith(ancestor.TrimEnd('/') + "/", StringComparison.Ordinal);
}
