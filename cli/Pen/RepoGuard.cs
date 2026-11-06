using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pen;

/// <summary>A path mounted over itself in the agent container.</summary>
internal sealed record Mount(string Path, bool ReadOnly);

/// <summary>
/// The start-of-session repo refusals and the mounts that keep the agent from
/// changing what host git runs (see "Protecting the host" in PLAN.md).
/// Everything here reads files directly and never runs git.
/// </summary>
internal static class RepoGuard
{
    private const int MaxListed = 10;

    /// <summary>Refuses layouts whose config and hooks the sandbox can't protect.</summary>
    public static void CheckAtStart(string dir)
    {
        var git = Path.Combine(dir, ".git");
        if (IsLink(git))
            throw new PenException($"{Rel(dir, git)} is a symlink, which pen doesn't support: run pen in the repo it points to");
        if (File.Exists(git))
            throw new PenException("this is a linked worktree (or a submodule's checkout), which pen doesn't support yet: run pen from the main checkout");
        if (Path.Exists(Path.Combine(dir, ".gitmodules")) || Path.Exists(Path.Combine(git, "modules")))
            throw new PenException("this repo has submodules, which pen doesn't support yet");
        if (Exists(Path.Combine(git, "commondir")))
            throw new PenException(".git/commondir exists, which a normal repo never has: it makes git read config and hooks from another directory. Inspect it and remove it, then rerun");

        var nested = RepoScan.FindNested(dir);
        if (nested.Count == 0) return;
        var sb = new StringBuilder();
        sb.Append(Directory.Exists(git)
            ? $"refusing to run in {dir}: it has other git repos inside it, whose config and hooks pen can't protect:"
            : $"refusing to run in {dir}: it contains git repos, whose config and hooks pen can't protect:");
        foreach (var n in nested.Take(MaxListed))
            sb.Append($"\n  {Rel(dir, n.RepoDir)}{Describe(n.Kind)}");
        if (nested.Count > MaxListed) sb.Append($"\n  … and {nested.Count - MaxListed} more");
        sb.Append("\nrun pen in each repo instead");
        throw new PenException(sb.ToString());
    }

    private static string Describe(NestedKind kind) => kind switch
    {
        NestedKind.Bare => " (a bare repo)",
        NestedKind.GitFile => " (a .git file that isn't one of this repo's worktrees)",
        NestedKind.GitLink => " (a .git symlink)",
        _ => "",
    };

    /// <summary>
    /// The mounts over the project mount, creating what must exist to be
    /// mounted (.pen/, .git/hooks, and a few files inside .git) so that the
    /// agent can't create them instead. Call after <see cref="CheckAtStart"/>.
    /// </summary>
    public static List<Mount> PrepareMounts(string dir, string hostHome)
    {
        var mounts = new List<Mount>();
        var git = Path.Combine(dir, ".git");
        if (Directory.Exists(git))
        {
            // .git itself becomes a mount point, so it can't be renamed away and replaced.
            mounts.Add(new(git, ReadOnly: false));

            var config = Path.Combine(git, "config");
            EnsureFile(dir, config);
            mounts.Add(new(config, ReadOnly: true));

            var hooks = Path.Combine(git, "hooks");
            EnsureDirectory(dir, hooks);
            mounts.Add(new(hooks, ReadOnly: true));

            var entries = GitConfigFile.ParseFile(config);
            // With extensions.worktreeConfig, git also reads config.worktree
            // files, which the agent could otherwise create.
            bool worktreeConfig = entries.Any(e => e.Section == "extensions" && e.Key == "worktreeconfig" && GitConfigFile.IsTrue(e.Value));
            AddWorktreeConfig(dir, Path.Combine(git, "config.worktree"), worktreeConfig, mounts);

            // Each linked worktree: host git in it reads config and hooks from
            // wherever its commondir points.
            var worktrees = Path.Combine(git, "worktrees");
            if (IsLink(worktrees))
                throw new PenException(".git/worktrees is a symlink, which pen doesn't support");
            if (Directory.Exists(worktrees))
            {
                foreach (var wt in Directory.EnumerateDirectories(worktrees).Order(StringComparer.Ordinal))
                {
                    if (IsLink(wt))
                        throw new PenException($"{Rel(dir, wt)} is a symlink, which pen doesn't support");
                    var commondir = Path.Combine(wt, "commondir");
                    if (Exists(commondir)) { RequireNotLink(dir, commondir); mounts.Add(new(commondir, ReadOnly: true)); }
                    AddWorktreeConfig(dir, Path.Combine(wt, "config.worktree"), worktreeConfig, mounts);
                }
            }

            AddIncludes(dir, config, hostHome, mounts, depth: 0);
        }

        // .pen/ sets the next session's policy, so the agent must not change or create it.
        EnsureDirectory(dir, Path.Combine(dir, ".pen"));
        mounts.Add(new(Path.Combine(dir, ".pen"), ReadOnly: true));

        return mounts.DistinctBy(m => m.Path).ToList();
    }

    private static void AddWorktreeConfig(string dir, string path, bool enabled, List<Mount> mounts)
    {
        if (!enabled && !Exists(path)) return;
        EnsureFile(dir, path);
        mounts.Add(new(path, ReadOnly: true));
    }

    /// <summary>
    /// include.path and includeIf.*.path files inside the project. The agent
    /// could write one of those and host git would read it as config, so each
    /// is mounted read-only too. A missing one inside .git is created empty; a
    /// missing one in the working tree is refused, since creating it would
    /// add a file to the project.
    /// </summary>
    private static void AddIncludes(string dir, string configFile, string hostHome, List<Mount> mounts, int depth)
    {
        if (depth > 10) return; // git's own limit
        foreach (var e in GitConfigFile.ParseFile(configFile))
        {
            if (e.Section is not ("include" or "includeif") || e.Key != "path" || e.Value.Length == 0) continue;
            var p = e.Value;
            if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal)) p = hostHome + p[1..];
            else if (p.StartsWith('~')) continue; // ~user/: another user's home, never inside a project
            if (!Path.IsPathRooted(p)) p = Path.Combine(Path.GetDirectoryName(configFile)!, p);
            p = Path.GetFullPath(p);

            var real = ResolveExisting(p);
            if (!IsInside(dir, real)) continue;
            if (Directory.Exists(real)) continue; // git ignores a directory
            if (!File.Exists(real))
            {
                if (!IsInside(Path.Combine(dir, ".git"), real))
                    throw new PenException($"{Rel(dir, configFile)} includes {Rel(dir, real)}, which doesn't exist: the agent could create it, and git on this machine would read it as config. Create it (it can be empty) or remove the include, then rerun");
                Directory.CreateDirectory(Path.GetDirectoryName(real)!);
                File.WriteAllText(real, "");
            }
            RequireNotLink(dir, real);
            mounts.Add(new(real, ReadOnly: true));
            AddIncludes(dir, real, hostHome, mounts, depth + 1);
        }
    }

    /// <summary>The real path of <paramref name="path"/>, or of its deepest existing ancestor plus the rest.</summary>
    private static string ResolveExisting(string path)
    {
        var rest = new Stack<string>();
        var p = path;
        while (true)
        {
            var real = Native.RealPath(p);
            if (real != null) return rest.Aggregate(real, Path.Combine);
            var parent = Path.GetDirectoryName(p);
            if (parent == null) return path;
            rest.Push(Path.GetFileName(p));
            p = parent;
        }
    }

    /// <summary>
    /// The compose override that adds <paramref name="mounts"/> to the agent
    /// service. It's JSON, which YAML reads as is, so any path is quoted
    /// correctly; $ is doubled, since compose interpolates it.
    /// <paramref name="readOnlyRoot"/> also makes the agent's root filesystem
    /// read-only (rootless Docker, where the agent is container root and would
    /// otherwise own the image's files).
    /// </summary>
    public static string ComposeOverride(IReadOnlyList<Mount> mounts, bool readOnlyRoot = false)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("x-pen", "mounts over the project mount, written by pen for this session");
            w.WriteStartObject("services");
            w.WriteStartObject("agent");
            if (readOnlyRoot) w.WriteBoolean("read_only", true);
            w.WriteStartArray("volumes");
            foreach (var m in mounts)
            {
                var path = m.Path.Replace("$", "$$", StringComparison.Ordinal);
                w.WriteStartObject();
                w.WriteString("type", "bind");
                w.WriteString("source", path);
                w.WriteString("target", path);
                w.WriteBoolean("read_only", m.ReadOnly);
                // A missing source is an error, never created (as root, on Linux).
                w.WriteStartObject("bind");
                w.WriteBoolean("create_host_path", false);
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    internal static void EnsureDirectory(string dir, string path)
    {
        RequireNotLink(dir, path);
        if (File.Exists(path))
            throw new PenException($"{Rel(dir, path)} is a file, not a directory: pen needs to mount it read-only as a directory");
        Directory.CreateDirectory(path);
    }

    private static void EnsureFile(string dir, string path)
    {
        RequireNotLink(dir, path);
        if (Directory.Exists(path))
            throw new PenException($"{Rel(dir, path)} is a directory, not a file");
        if (!File.Exists(path)) File.WriteAllText(path, "");
    }

    internal static void RequireNotLink(string dir, string path)
    {
        if (IsLink(path))
            throw new PenException($"{Rel(dir, path)} is a symlink, which pen doesn't support here: it has to mount it read-only");
    }

    internal static bool IsLink(string path) => new FileInfo(path).LinkTarget != null;

    /// <summary>Exists, including as a dangling symlink.</summary>
    internal static bool Exists(string path) => Path.Exists(path) || IsLink(path);

    internal static bool IsInside(string dir, string path) =>
        path == dir || path.StartsWith(dir.TrimEnd('/') + "/", StringComparison.Ordinal);

    internal static string Rel(string dir, string path) =>
        path == dir ? "." : IsInside(dir, path) ? path[(dir.TrimEnd('/').Length + 1)..] : path;
}
