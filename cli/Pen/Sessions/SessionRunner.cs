using Pen.GitProtection;
using Pen.Masking;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Tools;

namespace Pen.Sessions;

/// <summary>
/// Runs a session: takes and keeps its locks, ends abandoned ones, prepares its protections, publishes it, runs the
/// agent, stops what could write and checks Git.
/// </summary>
/// <param name="context">The project and both configs.</param>
/// <param name="answers">Where approvals of .pen/ come from.</param>
/// <param name="catalog">The recipes tools come from.</param>
/// <param name="clock">The clock for the start time, waits and how long ended sessions' files are kept.</param>
internal sealed class SessionRunner(ProjectContext context, Answers answers, RecipeCatalog catalog, TimeProvider clock)
{
    private Project Project => context.Project;

    private string RuntimeDir => context.Host.GetRuntimeDir(Project.Key);

    /// <summary>Runs <paramref name="request"/>'s command in the project's sandbox, then ends the session.</summary>
    /// <returns>The command's exit code, or 1 when the agent couldn't be started; a refusal throws.</returns>
    /// <remarks>
    /// The steps, in this order: ensure the runtime dir; take the project and worktree locks; end abandoned sessions;
    /// prepare and approve; take the dind lock; choose the ports and create the volumes; hold the signals; warn and
    /// claim the init hint; create and lock the session dir; write its files; run the agent; end. The project and
    /// worktree locks, the dind lock, the signal handling and the session's lock are all held until the end
    /// (teardown and the .git check) has finished, and released in reverse order.
    /// </remarks>
    public int Run(SessionRequest request)
    {
        // Every session holds project.lock and its dir's worktree lock shared; pen clean holds the first exclusive, pen
        // sign the second.
        ProjectRuntime.EnsureRuntimeDir(context.Host, Project);
        using var locks = SessionRegistry.TakeForSession(RuntimeDir, Project);

        // A check that didn't finish vouches for nothing: starting now would take the module repos there as this
        // session's baseline, and its end would run git in the directory just warned about.
        if (!SessionRecovery.FinishAbandoned(Project.Key, RuntimeDir, clock).EveryCheckFinished)
            throw new PenException("not starting: deal with the warning above first");

        var prepared = new SessionPreparer(context, answers, catalog, clock).CheckApproveAndBuild(request);

        // One docker session per project: two daemons can't share the dind volume. A dind still using it (an earlier
        // session's teardown failed) would be corrupted by a second, so that's refused too.
        using var dindLock = request.Docker ? TakeDindLock() : null;

        // Ports already in use on the host: a --port is refused, a config port skipped. One taken between here and
        // compose run fails the run.
        var forwarded = Port.Choose(request.Ports, Port.IsTakenOnHost, Out.WriteLine);
        CreateVolumes(request.Docker);

        // From here on Ctrl-C belongs to the agent, and a hangup still ends the session cleanly.
        using var signals = new SessionSignals();

        // Past the refusals, so a session that won't start doesn't warn, and with signals held, so Ctrl-C can't end Pen
        // between the probe's create and delete.
        RepoGuard.WarnIfCaseMayFold(Project, Out.WriteLine, PathChecks.MayFoldCase);
        HooksPath.WarnIfWritable(Project, context.Host, Out.WriteLine, context.Host.GetVariable);
        foreach (var warning in EgressWarnings.ListForSession(context, catalog, prepared.Tools, prepared.Egress.Deny))
            Out.WriteLine(warning);
        // The case probe goes in .pen, so it too waits for the signals to be held.
        if (MaskMessages.FormatSummary(prepared.Masks) is { } summary)
            Out.WriteLine(summary);
        var maskWarnings =
            MaskMessages.ListWarnings(prepared.Masks, Project.PenDir, request.Docker, OperatingSystem.IsMacOS(),
                PathChecks.MayFoldCase);
        foreach (var warning in maskWarnings)
            Out.WriteLine(warning);
        // Last of the messages, past the refusals in the preparation and the locks, so a session that won't start
        // rarely uses up the hint.
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
        var exitCode = new AgentRunner(session, env, signals).Run(command, prepared.Token, context.User.Env);

        EndAndRemind(session);
        return exitCode;
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
