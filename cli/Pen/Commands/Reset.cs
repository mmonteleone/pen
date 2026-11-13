using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;

namespace Pen.Commands;

/// <summary>pen reset: removes the project's volumes, after asking.</summary>
internal static class Reset
{
    public static readonly string[] VolumeSuffixes = ["home", "nuget", "dind"];

    public static int Run(HostEnv host, Project project, TextReader input)
    {
        Session.EnsureRuntimeDir(host, project);
        using var projectLock = SessionRegistry.TakeForReset(host, project);
        Session.FinishDeadSessions(project.Key, host.GetRuntimeDir(project.Key));

        var volumes = VolumeSuffixes.Select(s => $"{project.Key}_{s}")
            .Where(v => Proc.Capture("docker", ["volume", "inspect", v]).ExitStatus.ExitCode == 0)
            .ToList();
        if (volumes.Count == 0)
        {
            Out.WriteLine($"nothing to reset: {project.Key} has no volumes");
            return 0;
        }

        Out.WriteLine($"this removes {string.Join(", ", volumes)}:");
        Out.WriteRaw("  the project's sandbox home (agent logins and config, shell history), its NuGet cache"
                + (volumes.Any(v => v.EndsWith("_dind", StringComparison.Ordinal)) ? " and its Docker images" : "")
                + ".\n  The project's files and your ~/.config/pen are untouched.");
        if (!Out.Confirm("remove them?", input))
        {
            Out.WriteLine("nothing removed");
            return 1;
        }

        var r = Proc.Capture("docker", ["volume", "rm", .. volumes]);
        if (r.ExitStatus.ExitCode != 0)
            throw new PenException($"docker volume rm failed: {r.StandardError.Trim()}");
        Out.WriteLine($"removed {string.Join(", ", volumes)}");
        return 0;
    }
}
