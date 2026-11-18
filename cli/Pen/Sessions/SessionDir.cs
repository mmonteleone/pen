using System.Globalization;
using Pen.GitProtection;
using Pen.Projects;
using Pen.Proxy;
using Pen.Tools;

namespace Pen.Sessions;

/// <summary>One session's directory under the project's runtime dir, and the files in it.</summary>
/// <param name="projectKey">The key of the session's project.</param>
/// <param name="path">The directory, sessions/&lt;id&gt; under the project's runtime dir.</param>
/// <remarks>
/// It holds the session's lock, the generated compose.yaml, squid.conf and ACL files, the env compose needs, the
/// records pen ps and the session-end check read, and the proxy log, which outlives the session.
/// </remarks>
internal sealed class SessionDir(string projectKey, string path)
{
    /// <summary>Gets the directory.</summary>
    public string Path { get; } = path;

    /// <summary>Gets the session's ID, the directory's name.</summary>
    public string Id { get; } = System.IO.Path.GetFileName(path);

    /// <summary>Gets the key of the session's project.</summary>
    public string ProjectKey => projectKey;

    /// <summary>Gets the name of the session's compose project, &lt;key&gt;-&lt;id&gt;.</summary>
    public string ComposeProject => $"{projectKey}-{Id}";

    /// <summary>Gets the name of the agent's container.</summary>
    public string Container => $"{ComposeProject}-agent";

    /// <summary>Gets the name of the dind container, in a docker session.</summary>
    public string DindContainer => $"{ComposeProject}-dind";

    /// <summary>Gets the name of the ports service's container, when ports are forwarded.</summary>
    public string PortsContainer => $"{ComposeProject}-ports";

    /// <summary>Gets the path of compose.yaml, whose presence marks the session open.</summary>
    public string ComposeFile => GetPath("compose.yaml");

    /// <summary>
    /// Gets the path of the compose override: mounts over the project mount, dind's and the ports service's settings.
    /// </summary>
    public string OverrideFile => GetPath("session.yaml");

    /// <summary>
    /// Gets the path of the proxy's allowlist ACL file: the domains allowed by name, in open egress too.
    /// </summary>
    public string AllowlistFile => GetPath("allowlist.txt");

    /// <summary>Gets the path of the proxy's deny ACL file: the denied domains, which beat every allow.</summary>
    /// <remarks>
    /// A session started by a Pen without deny has none, and no deny or open ACL in its squid.conf either.
    /// </remarks>
    public string DenylistFile => GetPath("denylist.txt");

    /// <summary>Gets the path of the proxy's open-egress ACL file, which matches any name in open egress.</summary>
    public string OpenEgressFile => GetPath("open-egress.txt");

    /// <summary>Gets whether the session's proxy has the deny and open ACLs, as one started by this Pen does.</summary>
    public bool HasDenyAndOpen => File.Exists(DenylistFile);

    /// <summary>Gets whether the session's open-egress.txt opens egress.</summary>
    /// <remarks>False without one, as in a session started by a Pen without open egress.</remarks>
    public bool HasOpenEgress =>
        File.Exists(OpenEgressFile) && Allowlist.IsOpenEgress(File.ReadAllText(OpenEgressFile));

    /// <summary>Reads what the session's proxy enforces now, from its ACL files.</summary>
    /// <returns>
    /// The allowed domains, with <see cref="Allowlist.AnyDomain"/> in open egress, and the denied ones; a missing
    /// file adds none.
    /// </returns>
    public (HashSet<string> Allow, HashSet<string> Deny) ReadEgress()
    {
        static IEnumerable<string> ReadList(string file) =>
            File.Exists(file) ? Allowlist.ParseList(File.ReadAllText(file)) : [];
        var allow = ReadList(AllowlistFile).ToHashSet(StringComparer.Ordinal);
        if (HasOpenEgress)
            allow.Add(Allowlist.AnyDomain);
        return (allow, ReadList(DenylistFile).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Gets the path of the proxy's log.</summary>
    public string ProxyLog => GetPath("proxy.log");

    /// <summary>
    /// Gets the path of the tail of proxy.log when it was last trimmed (<see cref="ProxyLogTrimmer"/>).
    /// </summary>
    public string PreviousProxyLog => GetPath("proxy.log.1");

    /// <summary>Reads the proxy's log lines, oldest first: proxy.log.1, then proxy.log.</summary>
    /// <returns>The lines, read as they're enumerated; a missing file adds none.</returns>
    public IEnumerable<string> ReadProxyLogLines()
    {
        foreach (var log in new[] { PreviousProxyLog, ProxyLog })
        {
            if (!File.Exists(log))
                continue;
            foreach (var line in File.ReadLines(log))
                yield return line;
        }
    }

    /// <summary>Gets the path of the session's lock, which its Pen holds exclusive while it runs.</summary>
    public string LockFile => GetPath("session.lock");

    /// <summary>
    /// Gets the path of the lock pen allow and pen deny hold exclusive while they rewrite the session's ACL files and
    /// have its proxy reload them.
    /// </summary>
    /// <remarks>
    /// Nothing else takes it, and a session's presence goes by compose.yaml, not by it. Deleting the session's files
    /// deletes it with the rest.
    /// </remarks>
    public string AclLockFile => GetPath("acl.lock");

    /// <summary>Gets the path of the env compose runs with, one NAME=value per line.</summary>
    public string EnvFile => GetPath("session.env");

    /// <summary>Gets the path of the file that's present when the session started in a repo.</summary>
    /// <remarks>It holds the repo's common git dir (empty from older pens: PROJECT_DIR/.git).</remarks>
    public string StartedWithGit => GetPath("started-with-git");

    /// <summary>
    /// Gets the path of the list of submodule repos in the common git dir at the start that the session takes as the
    /// submodules' own: those every session open then listed too (<see cref="ModuleBaseline"/>).
    /// </summary>
    /// <remarks>
    /// Each path is NUL-terminated; the session-end check quarantines any others. It goes after compose.yaml when the
    /// session's files are deleted, so an open session started in a repo always has it.
    /// </remarks>
    public string ModulesFile => GetPath("modules");

    /// <summary>
    /// Gets the path of the copies of the protected files, and their manifest (<see cref="ProtectedFiles"/>).
    /// </summary>
    public string ProtectedDir => GetPath("protected");

    /// <summary>Gets the path of the manifest of the protected files' copies.</summary>
    public string ProtectedManifest => System.IO.Path.Combine(ProtectedDir, "manifest.json");

    /// <summary>
    /// Gets the path of the manifest, once the files in it have been put back and their copies deleted.
    /// </summary>
    public string RestoredManifest => GetPath("restored-manifest.json");

    /// <summary>
    /// Gets the path of the files mounted over what the session hides and replaces (<see cref="Masking.MaskFiles"/>).
    /// </summary>
    public string MasksDir => GetPath("masks");

    /// <summary>Gets the path of the command run in the sandbox, for pen ps.</summary>
    /// <remarks>Each argument is NUL-terminated; empty means a shell.</remarks>
    public string CommandFile => GetPath("command");

    /// <summary>Gets the path of when the session started, in UTC, for pen ps.</summary>
    public string StartedFile => GetPath("started");

    /// <summary>Gets the path of the ports forwarded from the host's 127.0.0.1, one per line, for pen ps.</summary>
    public string PortsFile => GetPath("ports");

    /// <summary>Gets the path of the session's tools, as name:version, one per line, for pen ps.</summary>
    public string ToolsFile => GetPath("tools");

    /// <summary>Gets the path of the domains the session's tools brought, one per line.</summary>
    /// <remarks>
    /// Rewriting its allowlist keeps them, whether they came from a flag no later command knows about or from a
    /// recipe a later Pen has changed. Only what the session started with, never read again from .pen/.
    /// </remarks>
    public string ToolDomainsFile => GetPath("tool-domains");

    /// <summary>
    /// Gets the path of the marker of a docker session, whose compose calls all pass --profile docker.
    /// </summary>
    public string DockerMarker => GetPath("docker");

    /// <summary>Gets the path of the file <paramref name="name"/> in the session's directory.</summary>
    public string GetPath(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>Gets whether the session's files are still there, so its stack may be up.</summary>
    public bool IsOpen => File.Exists(ComposeFile);

    /// <summary>Builds the arguments of a docker compose call on this session's stack.</summary>
    /// <param name="args">The compose subcommand and its arguments.</param>
    /// <returns>The arguments for docker, which runs with the env the session started with.</returns>
    public string[] BuildComposeArgs(params string[] args)
    {
        var composeArgs = new List<string> { "compose", "--progress", "quiet" };
        if (File.Exists(DockerMarker))
            composeArgs.AddRange(["--profile", "docker"]);
        composeArgs.AddRange(["-f", ComposeFile]);
        if (File.Exists(OverrideFile))
            composeArgs.AddRange(["-f", OverrideFile]);
        composeArgs.AddRange(["-p", ComposeProject]);
        composeArgs.AddRange(args);
        return [.. composeArgs];
    }

    /// <summary>Writes the command run in the sandbox.</summary>
    public void WriteCommand(IReadOnlyList<string> command) =>
        File.WriteAllText(CommandFile, string.Concat(command.Select(argument => argument + "\0")));

    /// <summary>Reads the command run in the sandbox.</summary>
    /// <returns>The command, or null for a session started by a Pen that didn't record it.</returns>
    public string[]? ReadCommand() =>
        File.Exists(CommandFile) ? File.ReadAllText(CommandFile).Split('\0')[..^1] : null;

    /// <summary>Writes the submodule repos the session takes as the submodules' own.</summary>
    public void WriteModules(IEnumerable<string> repos) =>
        File.WriteAllText(ModulesFile, string.Concat(repos.Select(repo => repo + "\0")));

    /// <summary>Reads the submodule repos the session took as the submodules' own at its start.</summary>
    /// <returns>The repos, or null for a session started by a Pen that didn't record them.</returns>
    public string[]? ReadModules() =>
        File.Exists(ModulesFile) ? File.ReadAllText(ModulesFile).Split('\0')[..^1] : null;

    /// <summary>Writes when the session started.</summary>
    public void WriteStarted(DateTime utc) =>
        File.WriteAllText(StartedFile, utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

    /// <summary>Writes the ports forwarded from the host.</summary>
    public void WritePorts(IReadOnlyList<int> ports) =>
        File.WriteAllText(PortsFile,
            string.Concat(ports.Select(port => port.ToString(CultureInfo.InvariantCulture) + "\n")));

    /// <summary>Reads the ports forwarded from the host.</summary>
    /// <returns>The ports; none for a session started by a Pen that didn't record them.</returns>
    public List<int> ReadPorts()
    {
        if (!File.Exists(PortsFile))
            return [];
        return [.. File.ReadAllLines(PortsFile)
            .Select(line => int.TryParse(line, CultureInfo.InvariantCulture, out var port) ? port : 0)
            .Where(port => port > 0)];
    }

    /// <summary>Writes the session's tools and the domains they brought.</summary>
    public void WriteTools(IEnumerable<ToolSpec> tools, IEnumerable<string> domains)
    {
        File.WriteAllText(ToolsFile, string.Concat(tools.Select(tool => tool + "\n")));
        File.WriteAllText(ToolDomainsFile, string.Concat(domains.Select(domain => domain + "\n")));
    }

    /// <summary>Reads the session's tools.</summary>
    /// <returns>The tools; none for a session started by a Pen that didn't record them.</returns>
    public string[] ReadTools() => File.Exists(ToolsFile) ? File.ReadAllLines(ToolsFile) : [];

    /// <summary>Reads the domains the session's tools brought.</summary>
    /// <returns>The domains; none for a session started by a Pen that didn't record them.</returns>
    public string[] ReadToolDomains() => File.Exists(ToolDomainsFile) ? File.ReadAllLines(ToolDomainsFile) : [];

    /// <summary>Reads whether the session started in a repo, and its common git dir.</summary>
    /// <returns>
    /// Whether it started in a repo, and the common git dir it started with, or null when it didn't start in one
    /// or its Pen didn't record the dir.
    /// </returns>
    public (bool StartedWithGit, string? CommonGitDir) ReadGit()
    {
        if (!File.Exists(StartedWithGit))
            return (false, null);
        var common = File.ReadAllText(StartedWithGit).TrimEnd('\n');
        return (true, common.Length == 0 ? null : common);
    }

    /// <summary>Reads the project's main checkout, as its runtime dir records it.</summary>
    /// <returns>The main checkout, or null if that can't be read.</returns>
    public string? ReadRepoDir()
    {
        var sessions = System.IO.Path.GetDirectoryName(Path);
        var runtime = sessions is null ? null : System.IO.Path.GetDirectoryName(sessions);
        if (runtime is null)
            return null;
        try
        {
            return File.ReadAllText(System.IO.Path.Combine(runtime, ProjectRuntime.ProjectDirFile)).TrimEnd('\n');
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads when the session started.</summary>
    /// <returns>The time, in UTC, or null when it wasn't recorded or can't be read.</returns>
    public DateTime? ReadStarted()
    {
        if (!File.Exists(StartedFile))
            return null;
        var styles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
        return DateTime.TryParse(File.ReadAllText(StartedFile).Trim(), CultureInfo.InvariantCulture, styles,
            out var started)
            ? started
            : null;
    }

    /// <summary>Reads the env compose runs with.</summary>
    /// <returns>The variables; none when the file is missing.</returns>
    public Dictionary<string, string> ReadEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(EnvFile))
            return env;
        foreach (var line in File.ReadAllLines(EnvFile))
        {
            var equals = line.IndexOf('=');
            if (equals > 0)
                env[line[..equals]] = line[(equals + 1)..];
        }
        return env;
    }
}
