using Pen.Configuration;
using Pen.GitProtection;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Sessions;
using Pen.Tools;

namespace Pen.Commands;

/// <summary>
/// Where pen allow or pen deny adds its domains, and so which running sessions it applies them to: a project's
/// .pen/config.toml, or the user's config.toml with --global.
/// </summary>
internal abstract record EgressTarget
{
    private EgressTarget(Project? currentProject) => CurrentProject = currentProject;

    /// <summary>
    /// Gets the project in the start directory, also with --global: its sessions whose Pen died are finished, and
    /// pen allow warns of its deny; null when there's none.
    /// </summary>
    public Project? CurrentProject { get; }

    /// <summary>The project's .pen/config.toml, applied to the sessions started in its dir.</summary>
    /// <param name="Project">The project in the start directory.</param>
    public sealed record InProject(Project Project) : EgressTarget(Project);

    /// <summary>The user's config.toml (--global), applied to every project's sessions.</summary>
    public sealed record Global : EgressTarget
    {
        /// <summary>Initializes the target.</summary>
        /// <param name="currentProject">The project in the start directory; null when none resolves there.</param>
        public Global(Project? currentProject) : base(currentProject)
        {
        }
    }

    /// <summary>Resolves the target from the start directory.</summary>
    /// <param name="host">The host user's environment.</param>
    /// <param name="startDir">The directory Pen was started in.</param>
    /// <param name="global">Whether --global was given.</param>
    /// <remarks>--global works from anywhere; without it, a failure to resolve the project throws.</remarks>
    public static EgressTarget Resolve(HostEnv host, StartDir startDir, bool global)
    {
        if (!global)
            return new InProject(Project.Resolve(startDir.Get(), host));
        try
        {
            return new Global(Project.Resolve(startDir.Get(), host));
        }
        catch (PenException)
        {
            return new Global(null);
        }
    }

    /// <summary>
    /// Records the current project's runtime dir (the project-dir marker included) and finishes its sessions whose
    /// Pen died; nothing without one.
    /// </summary>
    public void PrepareCurrentProject(HostEnv host, TimeProvider clock)
    {
        if (CurrentProject is not { } project)
            return;
        ProjectRuntime.EnsureRuntimeDir(host, project);
        Session.FinishDeadSessions(project.Key, host.GetRuntimeDir(project.Key), clock);
    }

    /// <summary>Finds the running sessions a domain added here (allowed or denied) applies to.</summary>
    /// <returns>
    /// For a project's .pen/, only those started in its dir; with --global, every project's.
    /// </returns>
    /// <remarks>.pen/ is per worktree: a session in another worktree of the repo reads its own.</remarks>
    public List<SessionDir> FindRunningSessions(HostEnv host) =>
        this is InProject { Project: var project }
            ? SessionRegistry.FindRunningIn(project.Key, host.GetRuntimeDir(project.Key), project.Dir)
            : SessionRegistry.FindRunningEverywhere(host);

    /// <summary>
    /// Applies the config files to each running session a change here is for, saying how each went and then how
    /// many took it.
    /// </summary>
    /// <returns>Each session's result, in order: one failing doesn't stop the rest.</returns>
    public IReadOnlyList<AclUpdateResult> ApplyToRunningSessions(HostEnv host, SessionAclUpdater updater)
    {
        var results = new List<AclUpdateResult>();
        foreach (var session in FindRunningSessions(host))
        {
            var result = updater.Update(session);
            EgressMessages.WriteUpdate(session, result);
            results.Add(result);
        }
        EgressMessages.WriteApplied(results.Count(result => result is AclUpdateResult.Applied), this);
        return results;
    }

    /// <summary>
    /// Adds <paramref name="domains"/> to <paramref name="key"/> in the target's config file, creating a project's
    /// from <see cref="ProjectTemplate"/>'s all-commented template if it's missing.
    /// </summary>
    /// <returns>The file, as messages name it, and the domains that weren't in its list yet.</returns>
    public ConfigAddition AddToConfig(
        HostEnv host,
        ConfigListKey key,
        IReadOnlyList<string> domains,
        RecipeCatalog catalog)
    {
        if (this is not InProject { Project: var project })
        {
            var userPath = host.Display(host.UserConfigPath);
            var user = new ConfigTarget(host.UserConfigPath, userPath, ConfigKind.User, Stub: "");
            return new(userPath, ConfigFileEditor.AddToList(user, key, domains, catalog));
        }

        var where = ".pen/" + Config.FileName;
        MountPlanner.EnsureDirectory(project.Dir, project.PenDir);
        MountPlanner.RequireNotLink(project.Dir, project.ProjectConfigPath);
        var target = new ConfigTarget(project.ProjectConfigPath, where, ConfigKind.Project,
            ProjectTemplate.Render(ProjectDetection.None));
        return new(where, ConfigFileEditor.AddToList(target, key, domains, catalog));
    }
}

/// <summary>What <see cref="EgressTarget.AddToConfig"/> changed.</summary>
/// <param name="Where">The config.toml, as messages name it.</param>
/// <param name="Added">The domains that weren't in its list yet, lowercased, in order.</param>
internal sealed record ConfigAddition(string Where, IReadOnlyList<string> Added);
