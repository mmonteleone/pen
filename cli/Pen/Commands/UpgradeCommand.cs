using Pen.Configuration;
using Pen.Images;
using Pen.Platform;
using Pen.Projects;
using Pen.Tools;
using Pen.Trust;

namespace Pen.Commands;

/// <summary>
/// Runs pen upgrade: rebuilds pen:latest, the tools and project layer in use here, and pulls sidecar images.
/// </summary>
/// <param name="host">The host user's environment.</param>
/// <param name="user">The user's ~/.config/pen/config.toml.</param>
/// <param name="startDir">The directory Pen was started in, where a project may be.</param>
/// <param name="catalog">The recipes tools come from.</param>
/// <param name="clock">The clock PEN_REFRESH and the wait for another project layer build are taken from.</param>
/// <remarks>
/// The base always gets a new ID (PEN_REFRESH sets its last layer), so every tools image, the agents'
/// included, rebuilds on it: the ones from config here now, any other on its next use. pen upgrade never
/// asks for approval, so it builds only what's already approved from .pen/.
/// </remarks>
internal sealed class UpgradeCommand(
    HostEnv host,
    UserConfiguration user,
    StartDir startDir,
    RecipeCatalog catalog,
    TimeProvider clock)
{
    /// <summary>Upgrades the images, without the build cache when <paramref name="noCache"/> (--no-cache).</summary>
    /// <returns>0; a failure throws.</returns>
    public int Run(bool noCache)
    {
        Out.WriteLine($"upgrading {Image.Name}{(noCache ? " from scratch (--no-cache)" : "")}");
        var refresh = clock.GetUtcNow().ToUnixTimeSeconds().ToString();
        Image.Build(Embedded.ComputeImageHash(), refresh: refresh, pull: true, noCache: noCache);

        // The project layer too, when run in a project that has one.
        var project = TryResolveProject();
        var tools = ToolsImage.Ensure(SelectTools(project), catalog, noCache: noCache);
        if (project is not null)
            RebuildProjectLayer(project, noCache, tools);

        PullSidecarImages();
        Out.WriteLine("upgrade done");
        return 0;
    }

    /// <summary>Resolves the project in the start directory.</summary>
    /// <returns>The project, or null when there isn't one to resolve there.</returns>
    private Project? TryResolveProject()
    {
        try
        {
            return Project.Resolve(startDir.Get(), host);
        }
        catch (PenException)
        {
            return null;
        }
    }

    /// <summary>Selects the tools to rebuild for <paramref name="project"/>.</summary>
    /// <returns>
    /// What a session started there with no --tool would have, less what isn't approved; outside a project,
    /// or when the project's tools can't be used (with a note), the user's alone.
    /// </returns>
    /// <remarks>pen upgrade doesn't ask, so unapproved tools from .pen/ are skipped rather than built.</remarks>
    internal IReadOnlyList<ToolSpec> SelectTools(Project? project)
    {
        if (project is null)
            return user.Tools;
        ProjectContext context;
        try
        {
            context = ProjectContext.Load(host, project, user, catalog);
        }
        catch (PenException exception)
        {
            Out.WriteLine($"skipping this project's tools: {exception.Message}");
            return user.Tools;
        }
        var store = TrustStore.For(host, project);
        var unapproved = TrustGate.FindUnapprovedTools(context.ProjectConfig, user.Tools, store, catalog)
            .Select(request => request.Tool)
            .ToList();
        if (unapproved.Count > 0)
        {
            Out.WriteLine("skipping tools that .pen/ asks for and aren't approved (pen trust reviews " +
                $"them): {string.Join(' ', unapproved.Select(tool => Out.Field(tool.ToString())))}");
        }

        var approved = user.Tools.Concat(context.ProjectConfig.Tools.Except(unapproved));
        if (!ToolSpec.TryCanonicalize(approved, catalog, out var tools, out var conflict))
        {
            Out.WriteLine($"skipping this project's tools: {conflict}");
            return user.Tools;
        }
        return tools;
    }

    /// <summary>
    /// Rebuilds <paramref name="project"/>'s layer on <paramref name="baseImage"/>, if it has one and it's
    /// approved, without the build cache when <paramref name="noCache"/>.
    /// </summary>
    /// <remarks>pen upgrade doesn't ask, so an unapproved .pen/ is skipped rather than built.</remarks>
    internal void RebuildProjectLayer(Project project, bool noCache, string baseImage)
    {
        if (!TrustGate.HasDockerfile(project))
            return;
        if (TrustGate.FindUnapprovedLayer(project, TrustStore.For(host, project)) is not null)
        {
            Out.WriteLine("skipping the project layer: .pen/ isn't approved (pen trust reviews it)");
            return;
        }
        Out.WriteLine($"rebuilding the project layer {Image.GetProjectLayerName(project, baseImage)}");
        Image.EnsureProjectLayer(host, project, clock, baseImage: baseImage, noCache: noCache);
    }

    /// <summary>Pulls newer proxy and dind images for the embedded compose.yaml.</summary>
    private static void PullSidecarImages()
    {
        Out.WriteLine("pulling sidecar images");
        // compose reads the embedded compose.yaml from stdin. Its required variables only need values for
        // interpolation; nothing is started.
        var env = new Dictionary<string, string>
        {
            ["PROJECT_DIR"] = "/nonexistent",
            ["PEN_PROJECT"] = "pen-upgrade",
            ["PEN_UID"] = "1",
            ["PEN_GID"] = "1",
            ["NUGET_PACKAGES"] = "/nonexistent",
            ["PEN_SEED"] = "/nonexistent",
        };
        var startInfo = Proc.CreateStartInfo("docker",
            ["compose", "--profile", "docker", "-f", "-", "-p", "pen-upgrade", "pull", "proxy", "dind"], env);
        startInfo.RedirectStandardInput = true;
        using var process = Proc.Start(startInfo);
        using (var stdin = process.StandardInput)
            stdin.Write(Embedded.ReadText("compose.yaml"));
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new PenException($"docker compose pull failed (exit {process.ExitCode})");
    }
}
