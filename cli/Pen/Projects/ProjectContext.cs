using Pen.Configuration;
using Pen.Platform;
using Pen.Tools;

namespace Pen.Projects;

/// <summary>What a command that works on a project needs: the host, the project and both configs.</summary>
/// <param name="Host">The host user's environment.</param>
/// <param name="Project">The resolved project.</param>
/// <param name="User">The user's ~/.config/pen/config.toml.</param>
/// <param name="ProjectConfig">The project's .pen/config.toml.</param>
internal sealed record ProjectContext(HostEnv Host, Project Project, UserConfiguration User,
    ProjectConfiguration ProjectConfig)
{
    /// <summary>Loads the project's .pen/config.toml and builds the context with it.</summary>
    /// <remarks>
    /// .pen/ is agent-writable, so only the commands that use the project config build a context: pen sign and
    /// pen clean never read it, and a broken or hostile one can't get in their way.
    /// </remarks>
    public static ProjectContext Load(
        HostEnv host,
        Project project,
        UserConfiguration user,
        RecipeCatalog catalog) =>
        new(host, project, user, ConfigLoader.LoadProject(project.PenDir, ".pen", catalog));
}
