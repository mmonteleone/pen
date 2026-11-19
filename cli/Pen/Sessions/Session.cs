using System.Diagnostics;
using Pen.Configuration;
using Pen.GitProtection;
using Pen.Images;
using Pen.Masking;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
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
        ProjectRuntime.EnsureRuntimeDir(context.Host, Project);
        using var locks = SessionRegistry.TakeForSession(RuntimeDir, Project);

        // A check that didn't finish vouches for nothing: starting now would take
        // the module repos there as this session's baseline, and its end would
        // run git in the directory just warned about.
        if (!SessionRecovery.FinishAbandoned(Project.Key, RuntimeDir, clock).EveryCheckFinished)
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
        foreach (var warning in ListEgressWarnings(prepared.Tools, prepared.Egress.Deny))
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

        var files = new SessionFiles(context.Host, Project, clock);
        var session = files.CreateDir();
        using var sessionLock = SessionFiles.TakeLock(session);
        var env = files.Write(session, request, prepared, forwarded);
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
        var mounts = RepoGuard.EnsureMountTargetsAndPlan(Project.Dir, context.Host.Home);
        // A hide or [replace] that can't be used stops the session here, before anything is made for it.
        var masks = new MaskPlanner(Project.Dir, Project.CommonGitDir, [.. mounts.Select(mount => mount.Path)],
            [.. context.User.Hide, .. context.ProjectConfig.Hide],
            context.ProjectConfig.Replacements)
            .Plan();
        // The mounts protect every module repo; the session-end check takes as the submodules' own only those
        // listed. The open sessions are found just before the module scan.
        var modules = Project.IsRepo
            ? ModuleBaseline.ChooseAtStart(SessionRegistry.FindOpen(Project.Key, RuntimeDir), Project.CommonGitDir!,
                Project.Dir)
            : ModuleChoice.None;
        var image = EnsureImage(tools);
        // Under rootless Docker, the host user is container root: the agent
        // runs as UID 0 (with no capabilities) on a read-only root filesystem.
        var rootless = Docker.IsRootless();
        var owner = GitHub.FindProjectOwner(Project, context.ProjectConfig);
        var token = GitHub.SelectTokenAndUpdateNotice(owner, context.User, RuntimeDir, context.Host);
        var egress = EgressPolicyResolver.ForSessionStart(context, trust, tools, catalog);
        return new()
        {
            Tools = tools,
            Mounts = mounts,
            Masks = masks,
            Modules = modules,
            Image = image,
            Rootless = rootless,
            Token = token,
            Egress = egress,
        };
    }

    /// <summary>Lists the warnings about the session's egress: open egress, and deny taking what tools need.</summary>
    private IReadOnlyList<string> ListEgressWarnings(IReadOnlyList<ToolSpec> tools, IReadOnlyList<string> deny)
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
    internal static IReadOnlyList<string> FormatEgressWarnings(
        UserConfiguration user,
        string userConfigPath,
        ProjectConfiguration projectConfig,
        IReadOnlyList<string> deny,
        IEnumerable<(string Tool, IReadOnlyList<string> Domains)> toolDomains)
    {
        var warnings = new List<string>();
        var openIn = new List<string>();
        if (user.Allow.Contains(Allowlist.AnyDomain))
            openIn.Add(userConfigPath);
        if (projectConfig.Allow.Contains(Allowlist.AnyDomain))
            openIn.Add(".pen/" + ConfigLoader.FileName);
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
                    var taken = Allowlist.FindDenyCoverage(entry, domain) switch
                    {
                        DenyCoverage.All => domain,
                        DenyCoverage.Part => entry,
                        _ => null,
                    };
                    if (taken is null)
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

        return new ComposeClient(session, env).CreateStartInfo(runArgs, secrets);
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

    /// <summary>Ends a session that's the only one this command is ending, and gives the signing reminder.</summary>
    /// <returns>How ending it went.</returns>
    /// <remarks>No other session's check is waiting on this one, so its own result decides the reminder.</remarks>
    internal static SessionEndResult EndAndRemind(SessionDir session)
    {
        var result = SessionTeardown.End(session);
        SessionRecovery.WriteSigningReminder(result);
        return result;
    }
}
