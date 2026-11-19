using System.Text;
using Pen.Configuration;
using Pen.GitProtection;
using Pen.Platform;
using Pen.Projects;
using Pen.Tools;

namespace Pen.Commands;

/// <summary>
/// Runs pen init: writes a starting .pen/config.toml from <see cref="ProjectTemplate"/>, with the tools and hide
/// patterns <see cref="ProjectDetection"/> finds turned on.
/// </summary>
/// <remarks>
/// It never reads the project's .pen/config.toml, never overwrites and never approves anything: what it writes comes
/// from repo files, so its tools go through <see cref="Trust.TrustGate"/> like any other change to .pen/.
/// </remarks>
internal sealed class InitCommand(HostEnv host, Project project, RecipeCatalog catalog)
{
    private const string Where = ".pen/" + ConfigLoader.FileName;

    /// <summary>The mode .pen/config.toml is created with: 644.</summary>
    private const UnixFileMode CreatedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
        UnixFileMode.OtherRead;

    /// <summary>Writes .pen/config.toml unless it or a legacy .pen/config exists, and says what it turned on.</summary>
    /// <returns>0; a refusal throws.</returns>
    /// <exception cref="PenException">
    /// The file or a legacy one exists, or .pen or the file is a symlink (or .pen is a file); nothing was written.
    /// </exception>
    public int Run()
    {
        ProjectRuntime.EnsureRuntimeDir(host, project);
        // A symlink is refused as such, dangling or not, rather than as a file that exists.
        MountPlanner.RequireNotLink(project.Dir, project.PenDir);
        MountPlanner.RequireNotLink(project.Dir, project.ProjectConfigPath);
        ThrowIfExists();

        var detection = ProjectDetection.Detect(project.Dir, catalog);
        var text = ProjectTemplate.Render(detection);
        Validate(text, catalog);

        MountPlanner.EnsureDirectory(project.Dir, project.PenDir);
        MountPlanner.RequireNotLink(project.Dir, project.ProjectConfigPath);
        Create(project.ProjectConfigPath, text);

        Out.WriteLine($"created {Where}");
        foreach (var line in FormatFound(detection))
            Out.WriteRaw(line);
        Out.WriteLine(FormatNextStep(detection));
        return 0;
    }

    /// <summary>Refuses when .pen/config.toml or the legacy .pen/config exists.</summary>
    private void ThrowIfExists()
    {
        if (File.Exists(Path.Combine(project.PenDir, ConfigLoader.LegacyName)))
        {
            throw new PenException($".pen/{ConfigLoader.LegacyName}, pen's old config format, already exists: " +
                $"move its settings to {Where} (docs/usage.md#project-settings), then delete it");
        }
        if (Path.Exists(project.ProjectConfigPath))
            throw CreateExistsError();
    }

    private static PenException CreateExistsError() =>
        new($"{Where} already exists: edit it (docs/usage.md#project-settings)");

    /// <summary>Checks that the rendered text is a valid project config.</summary>
    /// <exception cref="InvalidOperationException">It isn't: the template or its rendering is wrong.</exception>
    private static void Validate(string text, RecipeCatalog catalog)
    {
        try
        {
            ConfigParser.ParseProject(text, Where, catalog);
        }
        catch (PenException exception)
        {
            throw new InvalidOperationException($"the rendered {Where} isn't valid: {exception.Message}", exception);
        }
    }

    /// <summary>Creates the file at <paramref name="path"/> with <paramref name="text"/>, in mode 644.</summary>
    /// <exception cref="PenException">Something is at <paramref name="path"/> already, as in a race.</exception>
    private static void Create(string path, string text)
    {
        using var file = CreateNew(path);
        try
        {
            // The umask may have narrowed the mode it was created with.
            File.SetUnixFileMode(file.SafeFileHandle, CreatedMode);
            file.Write(Encoding.UTF8.GetBytes(text));
        }
        catch
        {
            // A partial file would be refused as already existing on the next try.
            DeleteAfterFailedWrite(path);
            throw;
        }
    }

    /// <summary>Creates the file at <paramref name="path"/>, which mustn't exist, for writing.</summary>
    /// <exception cref="PenException">Something is at <paramref name="path"/> already, as in a race.</exception>
    private static FileStream CreateNew(string path)
    {
        try
        {
            return new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = CreatedMode,
            });
        }
        catch (IOException) when (Path.Exists(path) || PathChecks.IsLink(path))
        {
            throw CreateExistsError();
        }
    }

    /// <summary>Deletes the file at <paramref name="path"/> that a write failed on, leaving it if it can't.</summary>
    private static void DeleteAfterFailedWrite(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The write error is the one to report.
        }
    }

    /// <summary>Formats the lines listing the tools and hide patterns turned on, with their sources.</summary>
    /// <returns>A tools line and a hide line, each left out when there's nothing; indented, with no prefix.</returns>
    /// <remarks>Every path and pattern comes from the repo, so each is shown through <see cref="Out.Field"/>.</remarks>
    internal static IReadOnlyList<string> FormatFound(ProjectDetection detection)
    {
        var lines = new List<string>();
        if (detection.Tools.Count > 0)
        {
            var tools = detection.Tools.Select(tool => tool.Sources.Count == 0
                ? ProjectTemplate.FormatTool(tool)
                : $"{ProjectTemplate.FormatTool(tool)} ({ListFields(tool.Sources)})");
            lines.Add($"  tools: {string.Join(", ", tools)}");
        }
        if (detection.Hide.Count > 0)
        {
            var line = $"  hide: {ListFields([.. detection.Hide.Select(pattern => pattern.Pattern)])}";
            var found = detection.Hide.SelectMany(pattern => pattern.Sources).Distinct(StringComparer.Ordinal).ToList();
            if (found.Count > 0)
                line += $" (found {ListFields(found)})";
            lines.Add(line);
        }
        return lines;
    }

    /// <summary>Formats the closing line: review and commit, and approve the tools when any were turned on.</summary>
    internal static string FormatNextStep(ProjectDetection detection) => detection.Tools.Count > 0
        ? "review it, then commit it; the first session asks you to approve its tools (or run pen trust)"
        : "review it, then commit it";

    /// <summary>Lists repo-derived items through <see cref="Out.Field"/>, capped as the template caps them.</summary>
    private static string ListFields(IReadOnlyList<string> items) =>
        CappedList.Join(items, ProjectTemplate.MaxListed, Out.Field);
}
