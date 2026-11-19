using System.Security.Cryptography;
using Pen.GitProtection;
using Pen.Masking;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;

namespace Pen.Sessions;

/// <summary>
/// Creates a new session's directory and writes its files, compose.yaml last; deletes them if that fails, and when
/// the session has ended.
/// </summary>
/// <param name="host">The host user, whose IDs and paths go in the env.</param>
/// <param name="project">The project the session runs in.</param>
/// <param name="clock">The clock for the start time.</param>
/// <remarks>
/// An instance writes one project's sessions, so a write takes only what differs per session. Deleting needs only
/// the session's dir, and teardown and recovery have no project to hand, so those members are static.
/// </remarks>
internal sealed class SessionFiles(HostEnv host, Project project, TimeProvider clock)
{
    private const UnixFileMode Dir700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private string RuntimeDir => host.GetRuntimeDir(project.Key);

    /// <summary>Creates a new session's directory, with a random ID no other session has.</summary>
    public SessionDir CreateDir()
    {
        var sessions = Path.Combine(RuntimeDir, "sessions");
        while (true)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            var path = Path.Combine(sessions, id);
            if (Directory.Exists(path))
                continue;
            Directory.CreateDirectory(path, Dir700);
            return new SessionDir(project.Key, path);
        }
    }

    /// <summary>Takes a new session's lock, which the caller holds until the session has ended.</summary>
    /// <exception cref="InvalidOperationException">The lock is taken: nothing else takes a new session's.</exception>
    public static FileLock TakeLock(SessionDir session) =>
        FileLock.TryTake(session.LockFile, exclusive: true)
            ?? throw new InvalidOperationException("new session's lock is taken");

    /// <summary>
    /// Writes a new session's files: the protected files' copies, the env, the records pen ps and the session-end
    /// check read, and the stack's files, compose.yaml last. If that fails, they're deleted
    /// (<see cref="WriteOrDelete"/>).
    /// </summary>
    /// <param name="session">The new session, whose lock the caller holds.</param>
    /// <param name="request">The session's command and whether it has Docker.</param>
    /// <param name="prepared">What the session's preparation found and built.</param>
    /// <param name="forwarded">The ports forwarded from the host.</param>
    /// <returns>The env compose runs with.</returns>
    public Dictionary<string, string> Write(
        SessionDir session,
        SessionRequest request,
        Preparation prepared,
        IReadOnlyList<int> forwarded) =>
        WriteOrDelete(session, () => WriteAll(session, request, prepared, forwarded));

    /// <summary>
    /// Writes a new session's files with <paramref name="write"/>, deleting them if that fails: without compose.yaml
    /// the session never opened and nothing ran in it.
    /// </summary>
    /// <param name="session">The new session, whose lock this holds.</param>
    /// <param name="write">Writes the session's files, compose.yaml last.</param>
    /// <returns>What <paramref name="write"/> returns.</returns>
    internal static T WriteOrDelete<T>(SessionDir session, Func<T> write)
    {
        try
        {
            return write();
        }
        catch
        {
            try
            {
                Delete(session);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not open, so the next run here deletes what's left once it's old (SessionRecovery).
            }
            throw;
        }
    }

    /// <summary>Deletes an ended session's files, except proxy.log and proxy.log.1.</summary>
    internal static void Delete(SessionDir session)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(session.Path))
        {
            if (entry == session.ProxyLog || entry == session.PreviousProxyLog || entry == session.LockFile
                || entry == session.ComposeFile || entry == session.RestoredManifest
                || entry == session.StartedWithGit || entry == session.ModulesFile)
            {
                continue;
            }

            if (Directory.Exists(entry))
                Directory.Delete(entry, recursive: true);
            else
                File.Delete(entry);
        }
        // compose.yaml marks the session open, so it goes last but for the lock and the records of what it vouched
        // for and the module repos it listed: an open session always has those records.
        File.Delete(session.ComposeFile);
        File.Delete(session.RestoredManifest);
        File.Delete(session.StartedWithGit);
        File.Delete(session.ModulesFile);
        File.Delete(session.LockFile);
    }

    /// <summary>
    /// Writes the files mounted over what's hidden and replaced, and lists every mount over the project.
    /// </summary>
    /// <param name="session">The session, whose masks dir gets the files.</param>
    /// <param name="protection">The git-protection mounts.</param>
    /// <param name="masks">What the session hides and replaces.</param>
    /// <returns><paramref name="protection"/>, then the mask mounts (<see cref="MaskFiles.Write"/>).</returns>
    internal static IReadOnlyList<Mount> WriteMasks(
        SessionDir session,
        IReadOnlyList<Mount> protection,
        MaskPlan masks) =>
        [.. protection, .. MaskFiles.Write(session.MasksDir, masks)];

    /// <summary>
    /// Writes the session's files, compose.yaml last; <see cref="Write"/> deletes them if this throws.
    /// </summary>
    /// <returns>The env compose runs with.</returns>
    private Dictionary<string, string> WriteAll(
        SessionDir session,
        SessionRequest request,
        Preparation prepared,
        IReadOnlyList<int> forwarded)
    {
        // The protected files and the module list, by the open sessions found again and their lists read again: one
        // that opened between the preparation's read and its scan could have made a repo the scan found.
        ModuleBaseline.NarrowAndSave(new(
            Session: session,
            Mounts: prepared.Mounts,
            Chosen: prepared.Modules,
            Open: SessionRegistry.FindOpen(session.ProjectKey, RuntimeDir),
            CommonGitDir: project.CommonGitDir,
            Dir: project.Dir));
        var env = BuildEnv(prepared);
        File.WriteAllLines(session.EnvFile, env.Select(variable => $"{variable.Key}={variable.Value}"));
        WriteRecords(session, request, prepared, forwarded);
        WriteStackFiles(session, request.Docker, prepared, forwarded);
        return env;
    }

    /// <summary>Builds the env compose runs with, which compose.yaml interpolates.</summary>
    private Dictionary<string, string> BuildEnv(Preparation prepared) =>
        new(StringComparer.Ordinal)
        {
            ["PROJECT_DIR"] = project.Dir,
            ["PEN_PROJECT"] = project.Key,
            ["PEN_UID"] = prepared.Rootless ? "0" : host.Uid.ToString(),
            ["PEN_GID"] = prepared.Rootless ? "0" : host.Gid.ToString(),
            ["NUGET_PACKAGES"] = host.NuGetPackages,
            ["PEN_IMAGE"] = prepared.Image,
            ["PEN_SEED"] = host.SeedDir,
        };

    /// <summary>
    /// Writes the session's records for pen ps and the stack: the docker marker, and its ports, tools, command and
    /// start time.
    /// </summary>
    private void WriteRecords(
        SessionDir session,
        SessionRequest request,
        Preparation prepared,
        IReadOnlyList<int> forwarded)
    {
        if (request.Docker)
            File.WriteAllText(session.DockerMarker, "");
        session.WritePorts(forwarded);
        session.WriteTools(prepared.Tools, prepared.Egress.ToolDomains);
        session.WriteCommand(request.Command);
        session.WriteStarted(clock.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// Writes the stack's files: squid.conf, the ACL files, the mask files, the override, proxy.log and compose.yaml.
    /// </summary>
    private static void WriteStackFiles(
        SessionDir session,
        bool docker,
        Preparation prepared,
        IReadOnlyList<int> forwarded)
    {
        File.WriteAllText(session.GetPath("squid.conf"), Embedded.ReadText("proxy/squid.conf"));
        var acls = Allowlist.RenderFiles(prepared.Egress.NamedAllow,
            prepared.Egress.Allow.Contains(Allowlist.AnyDomain), prepared.Egress.Deny);
        File.WriteAllText(session.AllowlistFile, acls.AllowlistText);
        File.WriteAllText(session.DenylistFile, acls.DenylistText);
        File.WriteAllText(session.OpenEgressFile, acls.OpenEgressText);
        var dindContainer = docker ? session.DindContainer : null;
        var ports = new PortForward(forwarded, session.PortsContainer, session.Container);
        // The plan was made before the image was ensured, and Docker would create a mount point that's gone since.
        prepared.Masks.ThrowIfChanged();
        var mounts = WriteMasks(session, prepared.Mounts, prepared.Masks);
        File.WriteAllText(session.OverrideFile,
            SessionCompose.GenerateOverride(mounts, prepared.Rootless, dindContainer, ports));
        File.WriteAllText(session.ProxyLog, "");
        // Squid's own user writes the log on Linux; the session dir itself is 700.
        File.SetUnixFileMode(session.ProxyLog, (UnixFileMode)0b110_110_110);
        // compose.yaml goes last: its presence is what marks a session as open.
        File.WriteAllText(session.ComposeFile, Embedded.ReadText("compose.yaml"));
    }
}
