using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pen;

/// <summary>A path mounted over itself in the agent container.</summary>
internal sealed record Mount(string Path, bool ReadOnly);

/// <summary>
/// Ports forwarded from the host's 127.0.0.1 by the session's ports service
/// (named <paramref name="Container"/>) to the same ports on the agent
/// container (<paramref name="AgentContainer"/>: compose run gives the agent
/// no service-name alias, so it's reached by container name).
/// </summary>
internal sealed record PortForward(IReadOnlyList<int> Ports, string Container, string AgentContainer);

/// <summary>
/// The start-of-session repo refusals and the mounts that keep the agent from
/// changing what host git runs (see "Protecting the host" in PLAN.md).
/// Everything here reads files directly and never runs git.
/// </summary>
internal static class RepoGuard
{
    private const int MaxListed = 10;

    /// <summary>
    /// Refuses layouts whose config and hooks the sandbox can't protect. A
    /// linked worktree of a non-bare main checkout is accepted when its .git
    /// file validates (<see cref="RepoScan.ResolveWorktree"/>); the main
    /// repo's git dir then gets the same checks as a main checkout's own.
    /// </summary>
    public static void CheckAtStart(string dir)
    {
        var git = Path.Combine(dir, ".git");
        if (IsLink(git))
            throw new PenException($"{Rel(dir, git)} is a symlink, which pen doesn't support: run pen in the repo it points to");
        var common = git;
        if (File.Exists(git))
            common = CheckWorktree(dir, git);
        if (Path.Exists(Path.Combine(dir, ".gitmodules")) || Path.Exists(Path.Combine(common, "modules")))
            throw new PenException("this repo has submodules, which pen doesn't support yet");
        if (Exists(Path.Combine(common, "commondir")))
            throw new PenException($"{Rel(dir, Path.Combine(common, "commondir"))} exists, which a normal repo never has: it makes git read config and hooks from another directory. Inspect it and remove it, then rerun");

        var nested = RepoScan.FindNested(dir, common);
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

    /// <summary>
    /// The common git dir of the linked worktree whose .git file is
    /// <paramref name="git"/>, refusing anything else: a .git file that doesn't
    /// validate (a submodule's checkout, or a forged one), or a worktree of a
    /// bare repo, which has no main checkout to share pen's state with.
    /// </summary>
    private static string CheckWorktree(string dir, string git)
    {
        var common = RepoScan.ResolveWorktree(git)
            ?? throw new PenException(".git is a file that isn't a valid linked worktree (a submodule's checkout, or a worktree whose entry in the main repo is missing or moved), which pen doesn't support. For a worktree, git worktree repair in the main checkout may fix it");
        // ResolveWorktree requires the path to be real already; this is belt and braces.
        if (IsLink(common) || !Directory.Exists(common) || Native.RealPath(common) != common)
            throw new PenException($"this worktree's git dir {common} isn't a real directory, which pen doesn't support");
        if (!IsMainCheckoutGitDir(common))
            throw new PenException($"this is a worktree of the bare repo {common}, which pen doesn't support: run pen in a worktree of a repo with a main checkout");
        return common;
    }

    /// <summary>
    /// Whether <paramref name="common"/> is a non-bare main checkout's .git:
    /// named .git, a directory, and not core.bare.
    /// </summary>
    internal static bool IsMainCheckoutGitDir(string common)
    {
        if (Path.GetFileName(common) != ".git" || !Directory.Exists(common)) return false;
        var bare = GitConfigFile.ParseFile(Path.Combine(common, "config"))
            .Where(e => e.Section == "core" && e.Key == "bare").Select(e => e.Value).LastOrDefault();
        return bare == null || !GitConfigFile.IsTrue(bare);
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
    /// <para>
    /// For a linked worktree, the main repo's git dir (the common dir: objects,
    /// refs, the worktree's own entry) is mounted read-write, protected like a
    /// main checkout's .git; the worktree's .git file read-only; and the main
    /// checkout's own HEAD and index read-only, since the main checkout isn't
    /// visible in the sandbox.
    /// </para>
    /// </summary>
    public static List<Mount> PrepareMounts(string dir, string hostHome)
    {
        var mounts = new List<Mount>();
        var git = Path.Combine(dir, ".git");
        if (Directory.Exists(git))
        {
            ProtectGitDir(git, dir, hostHome, mounts);
        }
        else if (File.Exists(git))
        {
            var common = RepoScan.ResolveWorktree(git)
                ?? throw new PenException(".git is a file that isn't a valid linked worktree");
            ProtectGitDir(common, dir, hostHome, mounts);
            foreach (var name in new[] { "HEAD", "index" })
            {
                var f = Path.Combine(common, name);
                if (!File.Exists(f)) continue;
                RequireNotLink(dir, f);
                mounts.Add(new(f, ReadOnly: true));
            }
            // The .git file names the git dir, so the agent must not repoint it.
            mounts.Add(new(git, ReadOnly: true));
        }

        // .pen/ sets the next session's policy, so the agent must not change or create it.
        EnsureDirectory(dir, Path.Combine(dir, ".pen"));
        mounts.Add(new(Path.Combine(dir, ".pen"), ReadOnly: true));

        return mounts.DistinctBy(m => m.Path).ToList();
    }

    /// <summary>
    /// The mounts over a git dir (the project's .git, or the common dir of the
    /// worktree it is) that keep the agent from changing what host git reads
    /// as config or runs as hooks, for this checkout and every other worktree
    /// sharing the dir. <paramref name="dir"/> is the project dir: messages
    /// are relative to it, and the worktrees it doesn't contain aren't visible
    /// in the sandbox, so their entries are read-only as a whole.
    /// </summary>
    private static void ProtectGitDir(string gitDir, string dir, string hostHome, List<Mount> mounts)
    {
        // The git dir itself becomes a mount point, so it can't be renamed away and replaced.
        mounts.Add(new(gitDir, ReadOnly: false));

        var config = Path.Combine(gitDir, "config");
        EnsureFile(dir, config);
        mounts.Add(new(config, ReadOnly: true));

        var hooks = Path.Combine(gitDir, "hooks");
        EnsureDirectory(dir, hooks);
        mounts.Add(new(hooks, ReadOnly: true));

        var entries = GitConfigFile.ParseFile(config);
        // With extensions.worktreeConfig, git also reads config.worktree
        // files, which the agent could otherwise create.
        bool worktreeConfig = entries.Any(e => e.Section == "extensions" && e.Key == "worktreeconfig" && GitConfigFile.IsTrue(e.Value));
        AddWorktreeConfig(dir, Path.Combine(gitDir, "config.worktree"), worktreeConfig, mounts);

        // Each linked worktree: host git in it reads config and hooks from
        // wherever its commondir points.
        var worktrees = Path.Combine(gitDir, "worktrees");
        if (IsLink(worktrees))
            throw new PenException($"{Rel(dir, worktrees)} is a symlink, which pen doesn't support");
        if (Directory.Exists(worktrees))
        {
            // A mount point too, so the entries mounted below can't be moved
            // aside together and replaced with forged ones.
            mounts.Add(new(worktrees, ReadOnly: false));
            foreach (var wt in Directory.EnumerateDirectories(worktrees).Order(StringComparer.Ordinal))
            {
                if (IsLink(wt))
                    throw new PenException($"{Rel(dir, wt)} is a symlink, which pen doesn't support");
                var commondir = Path.Combine(wt, "commondir");
                if (Exists(commondir)) RequireNotLink(dir, commondir);
                if (!IsWorktreeInside(dir, wt))
                {
                    // The worktree isn't visible in the sandbox, so its entry
                    // looks dangling there: `git worktree prune` would delete
                    // it. Read-only keeps it, and its HEAD, index and commondir.
                    mounts.Add(new(wt, ReadOnly: true));
                    continue;
                }
                if (Exists(commondir)) mounts.Add(new(commondir, ReadOnly: true));
                AddWorktreeConfig(dir, Path.Combine(wt, "config.worktree"), worktreeConfig, mounts);
            }
        }

        AddIncludes(dir, gitDir, config, hostHome, mounts, depth: 0);
    }

    /// <summary>
    /// Whether the worktree whose entry is <paramref name="entry"/> (a
    /// &lt;git dir&gt;/worktrees/&lt;name&gt;) is inside <paramref name="dir"/>, by the
    /// path its gitdir file names. An entry without a usable one counts as
    /// outside.
    /// </summary>
    internal static bool IsWorktreeInside(string dir, string entry)
    {
        var gitdirFile = Path.Combine(entry, "gitdir");
        try
        {
            if (!File.Exists(gitdirFile) || IsLink(gitdirFile)) return false;
            var text = File.ReadAllText(gitdirFile).TrimEnd('\n', '\r');
            if (text.Length == 0 || text.Contains('\n') || text.Contains('\0')) return false;
            if (!Path.IsPathRooted(text)) text = Path.Combine(entry, text);
            return IsInside(dir, ResolveExisting(Path.GetFullPath(text)));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void AddWorktreeConfig(string dir, string path, bool enabled, List<Mount> mounts)
    {
        if (!enabled && !Exists(path)) return;
        EnsureFile(dir, path);
        mounts.Add(new(path, ReadOnly: true));
    }

    /// <summary>
    /// include.path and includeIf.*.path files inside the project or the git
    /// dir. The agent could write one of those and host git would read it as
    /// config, so each is mounted read-only too. A missing one inside the git
    /// dir is created empty; a missing one in the working tree is refused,
    /// since creating it would add a file to the project.
    /// </summary>
    private static void AddIncludes(string dir, string gitDir, string configFile, string hostHome, List<Mount> mounts, int depth)
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
            if (!IsInside(dir, real) && !IsInside(gitDir, real)) continue;
            if (Directory.Exists(real)) continue; // git ignores a directory
            if (!File.Exists(real))
            {
                if (!IsInside(gitDir, real))
                    throw new PenException($"{Rel(dir, configFile)} includes {Rel(dir, real)}, which doesn't exist: the agent could create it, and git on this machine would read it as config. Create it (it can be empty) or remove the include, then rerun");
                Directory.CreateDirectory(Path.GetDirectoryName(real)!);
                File.WriteAllText(real, "");
            }
            RequireNotLink(dir, real);
            mounts.Add(new(real, ReadOnly: true));
            AddIncludes(dir, gitDir, real, hostHome, mounts, depth + 1);
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
    /// The session's compose override (session.yaml): it adds <paramref name="mounts"/> to the agent
    /// service. It's JSON, which YAML reads as is, so any path is quoted
    /// correctly; $ is doubled, since compose interpolates it.
    /// <paramref name="readOnlyRoot"/> also makes the agent's root filesystem
    /// read-only (rootless Docker, where the agent is container root and would
    /// otherwise own the image's files).
    /// <paramref name="dindContainer"/>, in docker sessions, names the dind
    /// container, gives dind the same mounts (so containers the agent starts
    /// with -v $PWD:… can't write what the agent can't), and points the agent
    /// at dind once it's healthy.
    /// <paramref name="ports"/>, when it has any, adds the ports service: the
    /// agent's network is internal, and Docker publishes no ports there, so a
    /// socat per port on both the sandbox network and a non-internal ports
    /// network relays 127.0.0.1:&lt;p&gt; on the host to &lt;p&gt; on the agent. It
    /// runs pen:latest as nobody with no capabilities on a read-only root; the
    /// agent can reach it, but only to be relayed back to itself.
    /// </summary>
    public static string ComposeOverride(IReadOnlyList<Mount> mounts, bool readOnlyRoot = false, string? dindContainer = null, PortForward? ports = null)
    {
        if (ports is { Ports.Count: 0 }) ports = null;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("x-pen", "this session's mounts over the project mount and its services, written by pen");
            w.WriteStartObject("services");
            w.WriteStartObject("agent");
            if (readOnlyRoot) w.WriteBoolean("read_only", true);
            if (dindContainer != null)
            {
                w.WriteStartObject("environment");
                w.WriteString("DOCKER_HOST", "tcp://dind:2375");
                w.WriteString("PEN_DOCKER", "1");
                w.WriteEndObject();
            }
            if (dindContainer != null || ports != null)
            {
                // compose run starts only the agent's dependencies.
                w.WriteStartObject("depends_on");
                if (dindContainer != null)
                {
                    w.WriteStartObject("dind");
                    w.WriteString("condition", "service_healthy");
                    w.WriteEndObject();
                }
                if (ports != null)
                {
                    w.WriteStartObject("ports");
                    w.WriteString("condition", "service_started");
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            WriteMounts(w, mounts);
            w.WriteEndObject();
            if (dindContainer != null)
            {
                w.WriteStartObject("dind");
                w.WriteString("container_name", dindContainer);
                WriteMounts(w, mounts);
                w.WriteEndObject();
            }
            if (ports != null) WritePortsService(w, ports);
            w.WriteEndObject();
            if (ports != null)
            {
                w.WriteStartObject("networks");
                w.WriteStartObject("ports");
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void WritePortsService(Utf8JsonWriter w, PortForward ports)
    {
        w.WriteStartObject("ports");
        w.WriteString("image", Image.Name);
        w.WriteString("container_name", ports.Container);
        w.WriteStartArray("entrypoint");
        w.WriteEndArray();
        // A socat per port; tini (init) as PID 1 stops them all at once.
        var script = string.Concat(ports.Ports.Select(p => $"socat TCP-LISTEN:{p},fork,reuseaddr TCP:{ports.AgentContainer}:{p} & ")) + "wait";
        w.WriteStartArray("command");
        w.WriteStringValue("sh");
        w.WriteStringValue("-c");
        w.WriteStringValue(script.Replace("$", "$$", StringComparison.Ordinal));
        w.WriteEndArray();
        w.WriteBoolean("init", true);
        w.WriteString("user", "nobody");
        w.WriteStartArray("cap_drop");
        w.WriteStringValue("ALL");
        w.WriteEndArray();
        w.WriteStartArray("security_opt");
        w.WriteStringValue("no-new-privileges:true");
        w.WriteEndArray();
        w.WriteBoolean("read_only", true);
        w.WriteNumber("pids_limit", 1024);
        w.WriteStartArray("ports");
        foreach (var p in ports.Ports) w.WriteStringValue($"127.0.0.1:{p}:{p}");
        w.WriteEndArray();
        w.WriteStartArray("networks");
        w.WriteStringValue("sandbox");
        w.WriteStringValue("ports");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteMounts(Utf8JsonWriter w, IReadOnlyList<Mount> mounts)
    {
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
