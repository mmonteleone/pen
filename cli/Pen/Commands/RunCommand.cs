using Pen.CommandLine;
using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;
using Pen.Tools;

namespace Pen.Commands;

/// <summary>Runs pen's default command: a session in the project's sandbox.</summary>
internal sealed class RunCommand(ProjectContext context, Answers answers, RecipeCatalog catalog, TimeProvider clock)
{
    /// <summary>Runs <paramref name="run"/>'s command in a session with the options it and .pen/ ask for.</summary>
    /// <returns>The command's exit code, or 1 when the agent couldn't be started; a refusal throws.</returns>
    /// <remarks>
    /// --docker turns Docker-in-Docker on for one session, and docker = true in .pen/config.toml for every session;
    /// nothing turns it off per session. --port adds to ports in .pen/config.toml, and --tool to tools in both config
    /// files.
    /// </remarks>
    public int Run(RunInvocation run)
    {
        var request = new SessionRequest(run.Command)
        {
            Docker = run.Docker || context.ProjectConfig.Docker == true,
            Ports = Port.Merge(context.ProjectConfig.Ports, run.Ports),
            FlagTools = run.Tools,
        };
        return new SessionRunner(context, answers, catalog, clock).Run(request);
    }
}
