using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;

namespace Pen.Commands;

/// <summary>Runs pen clean: removes the project's volumes, after asking.</summary>
/// <remarks>
/// It holds project.lock exclusive throughout, so it refuses while any session or pen sign runs in the project,
/// and none starts until it's done.
/// </remarks>
internal sealed class CleanCommand(HostEnv host, Project project, Answers answers, TimeProvider clock)
{
    /// <summary>What a project's volume names end in, after its key and an underscore.</summary>
    public static readonly string[] VolumeSuffixes = ["home", "nuget", "dind"];

    /// <summary>Removes the project's volumes if the user agrees.</summary>
    /// <returns>0 once removed, or when there are none; 1 when the user declines; a failure throws.</returns>
    public int Run()
    {
        ProjectRuntime.EnsureRuntimeDir(host, project);
        using var projectLock = SessionRegistry.TakeForClean(host, project);
        // pen clean runs no git, and a session kept for a retry needs nothing it removes (only containers use the
        // volumes, and one whose writers couldn't be removed throws here), so a check that didn't finish only warns.
        _ = SessionRecovery.FinishAbandoned(project.Key, host.GetRuntimeDir(project.Key), clock);

        var volumes = FindVolumes();
        if (volumes.Count == 0)
        {
            Out.WriteLine($"nothing to clean: {project.Key} has no volumes");
            return 0;
        }

        Out.WriteLine($"this removes {string.Join(", ", volumes)}:");
        var images = volumes.Any(volume => volume.EndsWith("_dind", StringComparison.Ordinal))
            ? " and its Docker images"
            : "";
        Out.WriteRaw("  the project's sandbox home (agent logins and config, shell history), its NuGet cache"
                + images
                + ".\n  The project's files, your ~/.config/pen, and this project's approvals (pen trust)"
                + "\n  and blocked-domain logs (pen allow) are kept.");
        if (!Out.Confirm("remove them?", answers))
        {
            Out.WriteLine("nothing removed");
            return 1;
        }

        var result = Proc.Capture("docker", ["volume", "rm", .. volumes]);
        if (result.ExitStatus.ExitCode != 0)
            throw new PenException($"docker volume rm failed: {result.StandardError.Trim()}");
        Out.WriteLine($"removed {string.Join(", ", volumes)}");
        return 0;
    }

    /// <summary>Finds the project's volumes that exist.</summary>
    private List<string> FindVolumes() =>
        [.. VolumeSuffixes.Select(suffix => $"{project.Key}_{suffix}")
            .Where(volume => Proc.Capture("docker", ["volume", "inspect", volume]).ExitStatus.ExitCode == 0)];
}
