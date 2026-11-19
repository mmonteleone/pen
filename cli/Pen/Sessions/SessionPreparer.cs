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

/// <summary>
/// Gets a session ready to start: checks the repo, settles its tools and what's approved, prepares the git protection
/// and what's hidden, builds its images and finds its token and egress.
/// </summary>
/// <param name="context">The project and both configs.</param>
/// <param name="answers">Where approvals of .pen/ come from.</param>
/// <param name="catalog">The recipes tools come from.</param>
/// <param name="clock">The clock for old review copies and the wait for another project layer build.</param>
internal sealed class SessionPreparer(
    ProjectContext context,
    Answers answers,
    RecipeCatalog catalog,
    TimeProvider clock)
{
    private Project Project => context.Project;

    private string RuntimeDir => context.Host.GetRuntimeDir(Project.Key);

    /// <summary>
    /// Checks the repo, settles the tools and asks for what .pen/ needs approved, prepares the mounts, plans what's
    /// hidden and replaced, builds the images when they aren't current and finds the token and the allow and deny
    /// lists.
    /// </summary>
    /// <remarks>
    /// This creates what the mounts need in the project and its git dir, may ask for approval and save it, updates
    /// the token notice and may build images. A refusal throws.
    /// </remarks>
    public Preparation CheckApproveAndBuild(SessionRequest request)
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
        // Under rootless Docker, the host user is container root: the agent runs as UID 0 (with no capabilities) on a
        // read-only root filesystem.
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
}
