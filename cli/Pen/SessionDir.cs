using System.Globalization;

namespace Pen;

/// <summary>
/// One session's directory under the project's runtime dir: its lock, the
/// generated compose.yaml/squid.conf/allowlist, the env compose needs, and the
/// proxy log, which outlives the session.
/// </summary>
internal sealed class SessionDir(string projectKey, string path)
{
    public string Path { get; } = path;
    public string Id { get; } = System.IO.Path.GetFileName(path);
    public string ProjectKey => projectKey;
    public string ComposeProject => $"{projectKey}-{Id}";
    public string Container => $"{ComposeProject}-agent";
    public string DindContainer => $"{ComposeProject}-dind";
    public string PortsContainer => $"{ComposeProject}-ports";

    public string ComposeFile => File("compose.yaml");
    /// <summary>The compose override: mounts over the project mount, dind's and the ports service's settings.</summary>
    public string OverrideFile => File("session.yaml");
    public string ProxyLog => File("proxy.log");
    public string LockFile => File("session.lock");
    public string EnvFile => File("session.env");
    /// <summary>Present when the session started in a repo; holds its common git dir (empty from older pens: PROJECT_DIR/.git).</summary>
    public string StartedWithGit => File("started-with-git");
    /// <summary>
    /// The submodule repos in the common git dir at the start, each path
    /// NUL-terminated: the session-end check quarantines any others.
    /// </summary>
    public string ModulesFile => File("modules");
    /// <summary>The copies of the protected files, and their manifest (<see cref="ProtectedFiles"/>).</summary>
    public string ProtectedDir => File("protected");
    public string ProtectedManifest => System.IO.Path.Combine(ProtectedDir, "manifest.json");
    /// <summary>The manifest, once the files in it have been put back and their copies deleted.</summary>
    public string RestoredManifest => File("restored-manifest.json");
    /// <summary>The command run in the sandbox, each argument NUL-terminated (empty: a shell), for pen ps.</summary>
    public string CommandFile => File("command");
    /// <summary>When the session started, in UTC, for pen ps.</summary>
    public string StartedFile => File("started");
    /// <summary>The ports forwarded from the host's 127.0.0.1, one per line, for pen ps.</summary>
    public string PortsFile => File("ports");
    /// <summary>Marks a docker session: its compose calls all pass --profile docker.</summary>
    public string DockerMarker => File("docker");

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>A session whose files are still there has a stack that may be up.</summary>
    public bool IsOpen => System.IO.File.Exists(ComposeFile);

    /// <summary>docker compose for this session's stack, with the env it was started with.</summary>
    public string[] ComposeArgs(params string[] args)
    {
        var a = new List<string> { "compose", "--progress", "quiet" };
        if (System.IO.File.Exists(DockerMarker)) a.AddRange(["--profile", "docker"]);
        a.AddRange(["-f", ComposeFile]);
        if (System.IO.File.Exists(OverrideFile)) a.AddRange(["-f", OverrideFile]);
        a.AddRange(["-p", ComposeProject]);
        a.AddRange(args);
        return [.. a];
    }

    public void WriteCommand(IReadOnlyList<string> command) =>
        System.IO.File.WriteAllText(CommandFile, string.Concat(command.Select(a => a + "\0")));

    /// <summary>The command, or null for a session started by a pen that didn't record it.</summary>
    public string[]? ReadCommand() =>
        System.IO.File.Exists(CommandFile) ? System.IO.File.ReadAllText(CommandFile).Split('\0')[..^1] : null;

    public void WriteModules(IEnumerable<string> repos) =>
        System.IO.File.WriteAllText(ModulesFile, string.Concat(repos.Select(r => r + "\0")));

    /// <summary>The module repos at the start, or null for a session started by a pen that didn't record them.</summary>
    public string[]? ReadModules() =>
        System.IO.File.Exists(ModulesFile) ? System.IO.File.ReadAllText(ModulesFile).Split('\0')[..^1] : null;

    public void WriteStarted(DateTime utc) =>
        System.IO.File.WriteAllText(StartedFile, utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

    public void WritePorts(IReadOnlyList<int> ports) =>
        System.IO.File.WriteAllText(PortsFile, string.Concat(ports.Select(p => p.ToString(CultureInfo.InvariantCulture) + "\n")));

    /// <summary>The forwarded ports; none for a session started by a pen that didn't record them.</summary>
    public List<int> ReadPorts() =>
        System.IO.File.Exists(PortsFile)
            ? System.IO.File.ReadAllLines(PortsFile).Select(l => int.TryParse(l, CultureInfo.InvariantCulture, out var p) ? p : 0).Where(p => p > 0).ToList()
            : [];

    /// <summary>The common git dir the session started with, or null when it didn't start in a repo.</summary>
    public (bool StartedWithGit, string? CommonGitDir) ReadGit()
    {
        if (!System.IO.File.Exists(StartedWithGit)) return (false, null);
        var common = System.IO.File.ReadAllText(StartedWithGit).TrimEnd('\n');
        return (true, common.Length == 0 ? null : common);
    }

    /// <summary>The project's main checkout, as its runtime dir records it, or null if that can't be read.</summary>
    public string? ReadRepoDir()
    {
        var sessions = System.IO.Path.GetDirectoryName(Path);
        var runtime = sessions == null ? null : System.IO.Path.GetDirectoryName(sessions);
        if (runtime == null) return null;
        try { return System.IO.File.ReadAllText(System.IO.Path.Combine(runtime, Session.ProjectDirFile)).TrimEnd('\n'); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public DateTime? ReadStarted() =>
        System.IO.File.Exists(StartedFile)
        && DateTime.TryParse(System.IO.File.ReadAllText(StartedFile).Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public Dictionary<string, string> ReadEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!System.IO.File.Exists(EnvFile)) return env;
        foreach (var line in System.IO.File.ReadAllLines(EnvFile))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) env[line[..eq]] = line[(eq + 1)..];
        }
        return env;
    }
}
