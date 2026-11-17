using System.Diagnostics;
using System.Security.Cryptography;
using Pen.Configuration;
using Pen.GitProtection;
using Pen.Images;
using Pen.Masking;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Signing;
using Pen.Tools;
using Pen.Trust;

namespace Pen.Sessions;

/// <summary>Runs a session: prepares its protections, runs the agent, stops what could write and checks Git.</summary>
/// <param name="context">The project and both configs.</param>
/// <param name="answers">Where approvals of .pen/ come from.</param>
/// <param name="catalog">The recipes tools come from.</param>
/// <param name="clock">The clock for the start time, waits and how long ended sessions' files are kept.</param>
internal sealed class Session(ProjectContext context, Answers answers, RecipeCatalog catalog, TimeProvider clock)
{
    /// <summary>How long an ended session's directory (and so its proxy.log) is kept after its last activity.</summary>
    internal static readonly TimeSpan KeepEndedSessions = TimeSpan.FromDays(7);

    /// <summary>The file in a project's runtime dir that records its main checkout.</summary>
    internal const string ProjectDirFile = "project-dir";

    private const UnixFileMode Dir700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private Project Project => context.Project;

    private string RuntimeDir => context.Host.GetRuntimeDir(Project.Key);

    /// <summary>Runs <paramref name="request"/>'s command in the project's sandbox, then ends the session.</summary>
    /// <returns>The command's exit code, or 1 when the agent couldn't be started; a refusal throws.</returns>
    /// <remarks>
    /// The steps are: prepare, write the session dir, run the agent, end. The project and worktree locks, the dind
    /// lock, the signal handling and the session's lock are all held until the end (teardown and the .git check)
    /// has finished, and released in reverse order.
    /// </remarks>
    public int Run(SessionRequest request)
    {
        // Every session holds project.lock and its dir's worktree lock
        // shared; pen clean holds the first exclusive, pen sign the second.
        EnsureRuntimeDir(context.Host, Project);
        using var locks = SessionRegistry.TakeForSession(RuntimeDir, Project);

        // A check that didn't finish vouches for nothing: starting now would take
        // the module repos there as this session's baseline, and its end would
        // run git in the directory just warned about.
        if (!FinishDeadSessions(Project.Key, RuntimeDir, clock))
            throw new PenException("not starting: deal with the warning above first");

        var prepared = Prepare(request);

        // One docker session per project: two daemons can't share the dind
        // volume. A dind still using it (an earlier session's teardown failed)
        // would be corrupted by a second, so that's refused too.
        using var dindLock = request.Docker ? TakeDindLock() : null;

        // Ports already in use on the host: a --port is refused, a config
        // port skipped. (One taken between here and compose run fails it.)
        var forwarded = Port.Choose(request.Ports, Port.IsTakenOnHost, Out.WriteLine);
        CreateVolumes(request.Docker);

        // From here on Ctrl-C belongs to the agent, and a hangup still ends the session cleanly.
        using var signals = new SessionSignals();

        // Past the refusals, so a session that won't start doesn't warn, and with
        // signals held, so Ctrl-C can't end Pen between the probe's create and delete.
        RepoGuard.WarnIfCaseMayFold(Project, Out.WriteLine, PathChecks.MayFoldCase);
        HooksPath.WarnIfWritable(Project, context.Host, Out.WriteLine, context.Host.GetVariable);
        foreach (var warning in ListEgressWarnings(prepared.Tools, prepared.Deny))
            Out.WriteLine(warning);
        // The case probe goes in .pen, so it too waits for the signals to be held.
        if (MaskMessages.FormatSummary(prepared.Masks) is { } summary)
            Out.WriteLine(summary);
        var maskWarnings =
            MaskMessages.ListWarnings(prepared.Masks, Project.PenDir, request.Docker, OperatingSystem.IsMacOS(),
                PathChecks.MayFoldCase);
        foreach (var warning in maskWarnings)
            Out.WriteLine(warning);
        // Last of the messages, past the refusals in Prepare and the locks, so a session that won't start rarely
        // uses up the hint.
        if (InitHint.TryClaim(Project.PenDir, RuntimeDir))
            Out.WriteLine(InitHint.Message);

        var session = CreateSessionDir();
        using var sessionLock = FileLock.TryTake(session.LockFile, exclusive: true)
            ?? throw new InvalidOperationException("new session's lock is taken");
        var env = WriteSessionDirOrDelete(session, () => WriteSessionDir(session, request, prepared, forwarded));
        // An empty command is still recorded as such (pen ps shows "(shell)"); the sandbox runs a login shell
        // matching the host's (InteractiveShell.For).
        var command = request.Command.Count > 0
            ? request.Command
            : InteractiveShell.For(context.Host.GetVariable("SHELL"));
        var startInfo = CreateAgentStartInfo(session, command, prepared.Token, env);
        var exitCode = RunAgent(session, startInfo, env, signals);

        EndAndRemind(session);
        return exitCode;
    }

    /// <summary>What <see cref="Prepare"/> found and built for the session.</summary>
    private sealed record Preparation
    {
        /// <summary>Gets the session's tools, merged and canonical.</summary>
        public required IReadOnlyList<ToolSpec> Tools { get; init; }

        /// <summary>Gets the git-protection mounts over the project mount.</summary>
        public required IReadOnlyList<Mount> Mounts { get; init; }

        /// <summary>Gets what the session hides and replaces, mounted after <see cref="Mounts"/>.</summary>
        public required MaskPlan Masks { get; init; }

        /// <summary>
        /// Gets the submodule repos in the common git dir now, split by what the open sessions listed
        /// (<see cref="ModuleBaseline"/>).
        /// </summary>
        public required ModuleChoice Modules { get; init; }

        /// <summary>Gets the image the agent runs.</summary>
        public required string Image { get; init; }

        /// <summary>Gets whether Docker is rootless.</summary>
        public required bool Rootless { get; init; }

        /// <summary>Gets the GitHub token for the project's owner, if there's one.</summary>
        public required string? Token { get; init; }

        /// <summary>Gets the domains the tools bring.</summary>
        public required IReadOnlyList<string> ToolDomains { get; init; }

        /// <summary>
        /// Gets the merged allowlist: just <see cref="Proxy.Allowlist.AnyDomain"/> when a source has it.
        /// </summary>
        public required IReadOnlyList<string> Allowlist { get; init; }

        /// <summary>
        /// Gets the merged allowlist without <see cref="Proxy.Allowlist.AnyDomain"/>: the domains allowed by name,
        /// which reach a private address even in open egress.
        /// </summary>
        public required IReadOnlyList<string> NamedAllowlist { get; init; }

        /// <summary>Gets the merged deny list, which beats every allow.</summary>
        public required IReadOnlyList<string> Deny { get; init; }
    }

    /// <summary>
    /// Checks the repo, settles the tools and what's approved, prepares the mounts, plans what's hidden and replaced,
    /// ensures the image and finds the token and the allow and deny lists.
    /// </summary>
    /// <returns>What the later steps need.</returns>
    private Preparation Prepare(SessionRequest request)
    {
        RepoGuard.CheckAtStart(Project.Dir);
        var flagTools = AddImpliedAgent(request);
        // Tools that can't go together stop the session, whatever's approved.
        var tools = ToolSpec.Merge(catalog, context.User.Tools, context.ProjectConfig.Tools, flagTools);
        // Before anything below uses a setting from .pen/.
        var trust = new TrustGate(context, answers, catalog, clock).RequireForSession(flagTools);
        var mounts = RepoGuard.PrepareMounts(Project.Dir, context.Host.Home);
        // A hide or [replace] that can't be used stops the session here, before anything is made for it.
        var masks = new MaskPlanner(Project.Dir, Project.CommonGitDir, [.. mounts.Select(mount => mount.Path)],
            [.. context.User.Hide, .. context.ProjectConfig.Hide], context.ProjectConfig.Replace).Plan();
        // PrepareMounts protects every module repo; the session-end check takes as the submodules' own only those
        // listed.
        var modules = Project.IsRepo
            ? ModuleBaseline.ChooseAtStart(Project.Key, RuntimeDir, Project.CommonGitDir!, Project.Dir)
            : ModuleChoice.None;
        var image = EnsureImage(tools);
        // Under rootless Docker, the host user is container root: the agent
        // runs as UID 0 (with no capabilities) on a read-only root filesystem.
        var rootless = Docker.IsRootless();
        var owner = GitHub.FindProjectOwner(Project, context.ProjectConfig);
        var token = GitHub.SelectToken(owner, context.User, RuntimeDir, context.Host);
        var toolDomains = ToolSpec.ListDomains(tools, catalog);
        // The same project domains pen allow applies live: what the gate let through, never on the user's "*" alone.
        var projectAllow = TrustGate.SelectAllowedDomains(context.User, context.ProjectConfig, trust);
        IEnumerable<string>[] allowSources = [Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt")),
            context.User.Allow, projectAllow, toolDomains];
        var deny = Allowlist.MergeDeny(context.User, context.ProjectConfig);
        return new()
        {
            Tools = tools,
            Mounts = mounts,
            Masks = masks,
            Modules = modules,
            Image = image,
            Rootless = rootless,
            Token = token,
            ToolDomains = toolDomains,
            Allowlist = Allowlist.Merge(allowSources),
            NamedAllowlist = Allowlist.MergeNamed(allowSources),
            Deny = deny,
        };
    }

    /// <summary>Lists the warnings about the session's egress: open egress, and deny taking what tools need.</summary>
    private List<string> ListEgressWarnings(IReadOnlyList<ToolSpec> tools, IReadOnlyList<string> deny)
    {
        var needs = tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal)
            .Select(name => (name, catalog.TryGet(name, out var recipe) ? recipe.Allow : []));
        return FormatEgressWarnings(context.User, context.Host.Display(context.Host.UserConfigPath),
            context.ProjectConfig, deny, needs);
    }

    /// <summary>Formats the warnings about a session's egress.</summary>
    /// <param name="user">The user's config.</param>
    /// <param name="userConfigPath">Names the user's config.toml.</param>
    /// <param name="projectConfig">The project's config.</param>
    /// <param name="deny">The merged deny list.</param>
    /// <param name="toolDomains">Each of the session's tools, by name, with the domains its recipe brings.</param>
    /// <returns>
    /// One line when allow has <see cref="Allowlist.AnyDomain"/>, naming the files; then one per domain a tool
    /// needs that deny covers, in part or whole, naming the narrower of the two and the tools.
    /// </returns>
    internal static List<string> FormatEgressWarnings(
        Config user,
        string userConfigPath,
        Config projectConfig,
        IReadOnlyList<string> deny,
        IEnumerable<(string Tool, IReadOnlyList<string> Domains)> toolDomains)
    {
        var warnings = new List<string>();
        var openIn = new List<string>();
        if (user.Allow.Contains(Allowlist.AnyDomain))
            openIn.Add(userConfigPath);
        if (projectConfig.Allow.Contains(Allowlist.AnyDomain))
            openIn.Add(".pen/" + Config.FileName);
        if (openIn.Count > 0)
            warnings.Add($"egress is open: any domain is allowed (allow = [\"*\"] in {string.Join(" and ", openIn)})");

        // A deny entry under a tool's wildcard takes part of what it needs, and one over a tool's domain all of it.
        var covered = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (tool, domains) in toolDomains)
        {
            foreach (var domain in domains)
            {
                foreach (var entry in deny)
                {
                    string taken;
                    if (Allowlist.IsDenied([entry], domain))
                        taken = domain;
                    else if (Allowlist.IsAllowed([domain], entry))
                        taken = entry;
                    else
                        continue;
                    if (!covered.TryGetValue(taken, out var needers))
                    {
                        covered[taken] = needers = [];
                        order.Add(taken);
                    }
                    if (!needers.Contains(tool))
                        needers.Add(tool);
                }
            }
        }
        foreach (var taken in order)
        {
            var needers = covered[taken];
            var who = needers.Count == 1
                ? needers[0] + " needs"
                : string.Join(", ", needers[..^1]) + " and " + needers[^1] + " need";
            warnings.Add($"deny covers {taken}, which {who}");
        }
        return warnings;
    }

    /// <summary>Adds the agent the command's first word names to the --tool list.</summary>
    /// <returns>
    /// The --tool list, with the agent added unless a tool from user config, .pen/ or --tool already names it.
    /// </returns>
    /// <remarks>A command naming an agent brings it, like --tool, so it needs no approval.</remarks>
    private IReadOnlyList<ToolSpec> AddImpliedAgent(SessionRequest request)
    {
        var flagTools = request.FlagTools;
        var implied = ToolSpec.FindImpliedAgent(request.Command, catalog,
            [.. context.User.Tools, .. context.ProjectConfig.Tools, .. flagTools]);
        if (implied is { } agent)
            return [.. flagTools, agent];
        return flagTools;
    }

    /// <summary>Ensures pen:latest, the tools image on it and the project layer on that are built.</summary>
    /// <returns>The project layer, or the tools image when the project has none.</returns>
    private string EnsureImage(IReadOnlyList<ToolSpec> tools)
    {
        Image.EnsureCurrent();
        var toolsImage = ToolsImage.Ensure(tools, catalog);
        return Image.EnsureProjectLayer(context.Host, Project, clock, baseImage: toolsImage) ?? toolsImage;
    }

    /// <summary>Takes the project's dind lock, for a docker session.</summary>
    /// <returns>The lock; another docker session running, or a dind left over, throws.</returns>
    private FileLock TakeDindLock()
    {
        var dindLock = FileLock.TryTake(Path.Combine(RuntimeDir, "dind.lock"), exclusive: true)
            ?? throw new PenException("another --docker session is running in this project, and only one can " +
                "run at a time (they'd share its Docker volume): exit it, or start this one without --docker");
        var volume = $"{Project.Key}_dind";
        var stale = Docker.ListContainersUsing(volume);
        if (stale.Count > 0)
        {
            dindLock.Dispose();
            throw new PenException($"{string.Join(", ", stale)} still uses {volume}, left over from an " +
                $"earlier session: remove it with docker rm -f {string.Join(' ', stale)}, then rerun");
        }
        return dindLock;
    }

    /// <summary>Creates the project's volumes the session uses: home and nuget, and dind with Docker.</summary>
    private void CreateVolumes(bool docker)
    {
        var volumes = new List<string> { "home", "nuget" };
        if (docker)
            volumes.Add("dind");
        foreach (var volume in volumes)
        {
            var result = Proc.Capture("docker", ["volume", "create", $"{Project.Key}_{volume}"]);
            if (result.ExitStatus.ExitCode != 0)
                throw new PenException($"can't create volume {Project.Key}_{volume}: {result.StandardError.Trim()}");
        }
    }

    /// <summary>
    /// Removes the host env that would change what compose loads or starts from <paramref name="startInfo"/>.
    /// </summary>
    internal static void ScrubComposeEnv(ProcessStartInfo startInfo)
    {
        foreach (var name in new[]
            {
                "COMPOSE_FILE", "COMPOSE_PROJECT_NAME", "COMPOSE_PROFILES", "COMPOSE_ENV_FILES",
                "COMPOSE_PATH_SEPARATOR"
            })
        {
            startInfo.Environment.Remove(name);
        }
    }

    /// <summary>
    /// Creates the project's runtime dir and records the main checkout's real path in its project-dir.
    /// </summary>
    /// <remarks>
    /// pen clean --stale uses the record to tell which keys belong to projects that were moved or deleted. Every
    /// command that resolves a project calls this, so active projects fill it in over time.
    /// </remarks>
    internal static void EnsureRuntimeDir(HostEnv host, Project project)
    {
        var runtimeDir = host.GetRuntimeDir(project.Key);
        Directory.CreateDirectory(host.StateRoot, Dir700);
        Directory.CreateDirectory(runtimeDir, Dir700);
        Directory.CreateDirectory(Path.Combine(runtimeDir, "sessions"), Dir700);
        WriteProjectDir(runtimeDir, project.RepoDir);
    }

    /// <summary>
    /// Writes <paramref name="repoDir"/> to project-dir unless it's already there, via temp file + rename.
    /// </summary>
    internal static void WriteProjectDir(string runtimeDir, string repoDir)
    {
        var path = Path.Combine(runtimeDir, ProjectDirFile);
        var content = repoDir + "\n";
        try
        {
            if (File.ReadAllText(path) == content)
                return;
        }
        catch (FileNotFoundException) { }
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Creates a new session's directory, with a random ID no other session has.</summary>
    private SessionDir CreateSessionDir()
    {
        var sessions = Path.Combine(RuntimeDir, "sessions");
        while (true)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            var path = Path.Combine(sessions, id);
            if (Directory.Exists(path))
                continue;
            Directory.CreateDirectory(path, Dir700);
            return new SessionDir(Project.Key, path);
        }
    }

    /// <summary>
    /// Writes the session's files: the protected files' copies, the env, the records pen ps and the session-end
    /// check read, and the stack's files, compose.yaml last.
    /// </summary>
    /// <returns>The env compose runs with.</returns>
    private Dictionary<string, string> WriteSessionDir(
        SessionDir session,
        SessionRequest request,
        Preparation prepared,
        IReadOnlyList<int> forwarded)
    {
        // The protected files and the module list, by the open sessions' lists read again: one that opened between
        // Prepare's read and its scan could have made a repo the scan found.
        ModuleBaseline.NarrowAndSave(session, prepared.Mounts, prepared.Modules, RuntimeDir, Project.CommonGitDir,
            Project.Dir);
        var env = BuildEnv(prepared);
        File.WriteAllLines(session.EnvFile, env.Select(variable => $"{variable.Key}={variable.Value}"));
        WriteRecords(session, request, prepared, forwarded);
        WriteStackFiles(session, request.Docker, prepared, forwarded);
        return env;
    }

    /// <summary>
    /// Writes a new session's files with <paramref name="write"/>, deleting them if that fails: without compose.yaml
    /// the session never opened and nothing ran in it.
    /// </summary>
    /// <param name="session">The new session, whose lock this holds.</param>
    /// <param name="write">Writes the session's files, compose.yaml last.</param>
    /// <returns>What <paramref name="write"/> returns.</returns>
    internal static T WriteSessionDirOrDelete<T>(SessionDir session, Func<T> write)
    {
        try
        {
            return write();
        }
        catch
        {
            try
            {
                DeleteSessionFiles(session);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not open, so the next run here deletes what's left once it's old (FinishDeadSessions).
            }
            throw;
        }
    }

    /// <summary>Builds the env compose runs with, which compose.yaml interpolates.</summary>
    private Dictionary<string, string> BuildEnv(Preparation prepared)
    {
        var host = context.Host;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PROJECT_DIR"] = Project.Dir,
            ["PEN_PROJECT"] = Project.Key,
            ["PEN_UID"] = prepared.Rootless ? "0" : host.Uid.ToString(),
            ["PEN_GID"] = prepared.Rootless ? "0" : host.Gid.ToString(),
            ["NUGET_PACKAGES"] = host.NuGetPackages,
            ["PEN_IMAGE"] = prepared.Image,
            ["PEN_SEED"] = host.SeedDir,
        };
    }

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
        session.WriteTools(prepared.Tools, prepared.ToolDomains);
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
        var acls = Allowlist.RenderFiles(prepared.NamedAllowlist, prepared.Allowlist.Contains(Allowlist.AnyDomain),
            prepared.Deny);
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

    /// <summary>
    /// Writes the files mounted over what's hidden and replaced, and lists every mount over the project.
    /// </summary>
    /// <param name="session">The session, whose masks dir gets the files.</param>
    /// <param name="protection">The git-protection mounts.</param>
    /// <param name="masks">What the session hides and replaces.</param>
    /// <returns><paramref name="protection"/>, then the mask mounts (<see cref="MaskFiles.Write"/>).</returns>
    internal static List<Mount> WriteMasks(SessionDir session, IReadOnlyList<Mount> protection, MaskPlan masks) =>
        [.. protection, .. MaskFiles.Write(session.MasksDir, masks)];

    /// <summary>Creates the docker compose run of the agent, with the secrets in its env.</summary>
    /// <param name="session">The session.</param>
    /// <param name="command">The command to run in the sandbox.</param>
    /// <param name="token">The GitHub token, if there's one.</param>
    /// <param name="env">The env compose runs with.</param>
    /// <remarks>Secrets are passed by name only on the command line: their values are never on one.</remarks>
    private ProcessStartInfo CreateAgentStartInfo(
        SessionDir session,
        IReadOnlyList<string> command,
        string? token,
        Dictionary<string, string> env)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (token is not null)
            secrets["GH_TOKEN"] = token;
        foreach (var (name, value) in context.User.Env)
            secrets[name] = value;

        var runArgs = new List<string> { "run", "--rm", "--name", session.Container };
        if (!Native.IsATty(0) || !Native.IsATty(1))
            runArgs.Add("-T");
        foreach (var name in secrets.Keys)
            runArgs.AddRange(["-e", name]); // by name only: values never on a command line
        runArgs.Add("agent");
        runArgs.AddRange(command);

        var startInfo = Proc.CreateStartInfo("docker", session.BuildComposeArgs([.. runArgs]), env);
        foreach (var (name, value) in secrets)
            startInfo.Environment[name] = value;
        ScrubComposeEnv(startInfo);
        return startInfo;
    }

    /// <summary>Runs the agent and waits for it, trimming the proxy log meanwhile.</summary>
    /// <returns>The agent's exit code, or 1 when it couldn't be started (reported).</returns>
    /// <remarks>A stop asked for before the start refuses it; one asked for after stops the child.</remarks>
    private static int RunAgent(
        SessionDir session,
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string> env,
        SessionSignals signals)
    {
        int exitCode;
        try
        {
            if (signals.Stopping)
                throw new PenException("stopped before the session started");
            using var child = Proc.Start(startInfo);
            signals.Container = session.Container;
            signals.Child = child;
            using var trimmer = ProxyLogTrimmer.Start(session, env);
            exitCode = Proc.ToExitCode(child.WaitForExitStatus());
        }
        catch (PenException error)
        {
            Out.WriteLine(error.Message);
            exitCode = 1;
        }
        finally
        {
            signals.Child = null;
        }
        return exitCode;
    }

    /// <summary>How the session-end .git check went, and whether the session's files must stay.</summary>
    /// <param name="Result">What the end path returns.</param>
    /// <param name="KeepFiles">
    /// Whether the session's files stay, for a retry, since putting files back didn't finish.
    /// </param>
    private sealed record EndCheck(SessionEndResult Result, bool KeepFiles);

    /// <summary>
    /// Ends a live or dead session: stops what could write, checks .git, stops the stack and cleans up.
    /// </summary>
    /// <returns>Whether the check finished, and the dir for the signing reminder.</returns>
    /// <remarks>
    /// <para>The agent container goes first (after a hangup compose may have left it running), and in a docker
    /// session dind with it, since containers inside dind can write to the project until it's gone. If that fails,
    /// the session's files are kept for a retry and this throws, before Git is checked. Then the .git check, then
    /// the stack, then the session's files except proxy.log.</para>
    /// <para>The caller holds the session's lock and releases it after. A check that can't finish doesn't stop
    /// any of this: it warns, and the session ends. End never runs git itself: whether that's safe can depend on
    /// other sessions' checks in the same repo, so the caller decides.</para>
    /// </remarks>
    internal static SessionEndResult End(SessionDir session)
    {
        var env = session.ReadEnv();
        RemoveWriters(session);
        // The .git check, now that the agent is gone and before anything runs git.
        var check = CheckGit(session, env);
        // What the session ended up allowing and denying (pen allow may have
        // changed it), so the blocked line doesn't suggest a domain that's
        // allowed by now, and says which hosts deny blocked.
        var egress = session.ReadEgress();
        StopStack(session, env, check.KeepFiles);
        ReportBlocked(session, egress.Allow, egress.Deny);
        return check.Result;
    }

    /// <summary>Removes the agent's container and, in a docker session, dind's.</summary>
    /// <remarks>
    /// Any failure throws once both were tried: the session is kept for a retry, and Git isn't checked.
    /// </remarks>
    private static void RemoveWriters(SessionDir session)
    {
        var containers = new List<string> { session.Container };
        if (File.Exists(session.DockerMarker))
            containers.Add(session.DindContainer);
        var failures = new List<string>();
        foreach (var container in containers)
        {
            try
            {
                Docker.RemoveWriter(container);
            }
            catch (PenException error)
            {
                failures.Add(error.Message);
            }
        }
        if (failures.Count > 0)
        {
            throw new PenException($"{string.Join("\n", failures)}\nsession {session.Id} is " +
                "retained for retry by the next pen command here; Git has not been checked");
        }
    }

    /// <summary>Runs the session-end .git check and reports it.</summary>
    /// <param name="session">The session.</param>
    /// <param name="env">The env the session started with, which names its project dir.</param>
    /// <returns>
    /// The end result (no reminder unless the check finished, moved nothing and put nothing back), and whether the
    /// session's files must stay.
    /// </returns>
    /// <remarks>
    /// Without a project dir there's nothing to check, unless the session saved protected files: then its record
    /// is damaged, so they weren't compared, and its files stay.
    /// </remarks>
    private static EndCheck CheckGit(SessionDir session, Dictionary<string, string> env)
    {
        if (env.TryGetValue("PROJECT_DIR", out var projectDir))
        {
            var check = GitCheck.RunAtSessionEnd(projectDir, session);
            GitCheckReport.Report(projectDir, check);
            var isClean = check.Failure is null && check.Moved.Count == 0 && check.Restored.Count == 0;
            var result = new SessionEndResult(check.Failure is null, isClean ? projectDir : null);
            if (check.RestoreIncomplete)
                Out.WriteRaw(GitCheckWording.KeptCopiesAdvice(session, check.RestoreBlocked));
            return new(result, check.RestoreIncomplete);
        }

        if (File.Exists(session.ProtectedManifest))
        {
            Out.WriteRaw(GitCheckWording.NotCheckedWarning(session.Path, "the session's record " +
                "of its project is damaged, so its protected files weren't compared"));
            Out.WriteRaw(GitCheckWording.KeptCopiesAdvice(session, null));
            return new(new(false, null), KeepFiles: true);
        }
        return new(new(true, null), KeepFiles: false);
    }

    /// <summary>Stops the session's stack, then deletes its files unless <paramref name="keepFiles"/>.</summary>
    /// <remarks>
    /// A stack that won't stop, or whose compose down can't start, keeps the files, for a retry by the next pen
    /// command.
    /// </remarks>
    private static void StopStack(SessionDir session, Dictionary<string, string> env, bool keepFiles)
    {
        if (env.Count == 0)
        {
            if (!keepFiles)
                DeleteSessionFiles(session);
            return;
        }

        var startInfo = Proc.CreateStartInfo("docker",
            session.BuildComposeArgs("down", "-t", "1", "--remove-orphans"), env);
        startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;
        ScrubComposeEnv(startInfo);
        string? failure;
        try
        {
            var down = Proc.CaptureText(startInfo);
            failure = down.ExitStatus.ExitCode != 0 ? down.StandardError.Trim() : null;
        }
        catch (PenException error)
        {
            failure = error.Message;
        }
        if (failure is not null)
        {
            Out.WriteLine($"stopping session {session.Id}'s stack failed: " +
                $"{failure} (it will be retried by the next pen command here)");
        }
        else if (!keepFiles)
        {
            DeleteSessionFiles(session);
        }
    }

    /// <summary>
    /// Writes the lines about what the proxy blocked and isn't in <paramref name="allowed"/>, if anything, with the
    /// hosts <paramref name="denied"/> covers apart (<see cref="Allowlist.FormatBlockedLines"/>).
    /// </summary>
    private static void ReportBlocked(SessionDir session, HashSet<string> allowed, HashSet<string> denied)
    {
        if (!File.Exists(session.ProxyLog) && !File.Exists(session.PreviousProxyLog))
            return;
        var blocked = Allowlist.FindBlockedHosts(session.ReadProxyLogLines());
        foreach (var line in Allowlist.FormatBlockedLines(blocked, allowed, denied))
            Out.WriteLine(line);
    }

    /// <summary>Ends a session that's the only one this command is ending, and gives the signing reminder.</summary>
    /// <returns>How ending it went.</returns>
    internal static SessionEndResult EndAndRemind(SessionDir session)
    {
        var result = End(session);
        Remind(result);
        return result;
    }

    /// <summary>Writes the signing reminder for <paramref name="result"/>'s dir, if it has one.</summary>
    /// <remarks>It runs Pen's own git call (the unsigned-commit count), so only once no check has failed.</remarks>
    private static void Remind(SessionEndResult result)
    {
        if (result.ReminderDir is null)
            return;
        var reminder = BranchSigner.FormatReminderIn(result.ReminderDir);
        if (reminder is not null)
            Out.WriteLine(reminder);
    }

    /// <summary>Deletes an ended session's files, except proxy.log and proxy.log.1.</summary>
    internal static void DeleteSessionFiles(SessionDir session)
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
    /// Ends every session whose Pen crashed or was killed, and deletes ended sessions' dirs after
    /// <see cref="KeepEndedSessions"/>.
    /// </summary>
    /// <returns>
    /// False if one's .git check didn't finish: the caller must not run git then, and neither does this.
    /// </returns>
    /// <remarks>
    /// A dead session still has a compose.yaml, but nobody holds its lock. These sessions may share a repo, so
    /// their reminders wait until every check has finished.
    /// </remarks>
    internal static bool FinishDeadSessions(string projectKey, string runtimeDir, TimeProvider clock)
    {
        var sessions = Path.Combine(runtimeDir, "sessions");
        if (!Directory.Exists(sessions))
            return true;
        var ended = new List<SessionEndResult>();
        foreach (var path in Directory.EnumerateDirectories(sessions))
        {
            var session = new SessionDir(projectKey, path);
            if (session.IsOpen)
            {
                if (TryEndDead(session) is { } result)
                    ended.Add(result);
            }
            else if (clock.GetUtcNow().UtcDateTime - ReadLastActivity(path) > KeepEndedSessions)
            {
                DeleteEnded(path);
            }
        }
        if (ended.Any(result => !result.CheckFinished))
            return false;
        foreach (var result in ended)
            Remind(result);
        return true;
    }

    /// <summary>Ends <paramref name="session"/> if its Pen is gone, holding its lock meanwhile.</summary>
    /// <returns>How ending it went, or null if it's running or ended while this looked.</returns>
    private static SessionEndResult? TryEndDead(SessionDir session)
    {
        using var sessionLock = FileLock.TryTake(session.LockFile, exclusive: true);
        if (sessionLock is null || !session.IsOpen)
            return null; // running, or ended while we looked
        Out.WriteLine($"ending session {session.Id}, whose pen exited without ending it");
        return End(session);
    }

    /// <summary>Deletes an ended session's directory, leaving it to the next run if that fails.</summary>
    private static void DeleteEnded(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Reads when a session's directory, or its proxy.log, last changed.</summary>
    private static DateTime ReadLastActivity(string dir)
    {
        var latest = Directory.GetLastWriteTimeUtc(dir);
        var log = Path.Combine(dir, "proxy.log");
        if (File.Exists(log))
        {
            var logTime = File.GetLastWriteTimeUtc(log);
            if (logTime > latest)
                latest = logTime;
        }
        return latest;
    }
}
