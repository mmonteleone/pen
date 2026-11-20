using Pen.Platform;

namespace Pen.Projects;

/// <summary>A project's runtime dir under the state root, and the record of its main checkout there.</summary>
internal static class ProjectRuntime
{
    /// <summary>The file in a project's runtime dir that records its main checkout.</summary>
    internal const string ProjectDirFile = "project-dir";

    private const UnixFileMode Dir700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

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
}
