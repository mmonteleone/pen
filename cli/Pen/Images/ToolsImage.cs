using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pen.Platform;
using Pen.Tools;

namespace Pen.Images;

/// <summary>Builds pen-tools:&lt;hash&gt; images: pen:latest with a set of tools installed.</summary>
/// <remarks>
/// The hash depends only on what goes into the image, never on a project, so projects with the same tools
/// share it. If pen:latest moves during a build, the image gets the name of the old base, which nothing asks
/// for once the new base is current.
/// </remarks>
internal static class ToolsImage
{
    public const string Repository = "pen-tools";
    public const string BaseIdLabel = "pen.base-id";
    private const string MountPoint = "/pen-recipe";

    /// <summary>One recipe to run, with the versions to pass its install.sh.</summary>
    private readonly record struct Install(Recipe Recipe, List<string> Versions);

    /// <summary>Plans the installs for <paramref name="tools"/>.</summary>
    /// <returns>The recipes to run, in canonical order, each with its versions.</returns>
    /// <remarks>
    /// The tools may have come from .pen/, and their versions reach a Dockerfile and a root script, so they're checked
    /// again here rather than trusted to have been parsed.
    /// </remarks>
    private static List<Install> PlanInstalls(IEnumerable<ToolSpec> tools, RecipeCatalog catalog)
    {
        var plan = new List<Install>();
        foreach (var versions in ToolSpec.Merge(catalog, tools).GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!catalog.TryGet(versions.Key, out var recipe))
                throw new PenException($"unknown tool \"{Out.Field(versions.Key)}\" ({catalog.DescribeAvailable()})");
            foreach (var tool in versions)
            {
                if (!ToolSpec.IsVersionSyntax(tool.Version) || !recipe.AcceptsVersion(tool.Version))
                    throw new PenException($"\"{Out.Field(tool.Version)}\" isn't a valid version of {recipe.Name}");
            }
            plan.Add(new(recipe, [.. versions.Select(tool => tool.Version)]));
        }
        return plan;
    }

    /// <summary>Generates the Dockerfile that installs <paramref name="plan"/> on pen:latest.</summary>
    /// <remarks>
    /// One RUN per recipe, in JSON (exec) form: no shell parses it, each version is one JSON string and so one
    /// argument, and Dockerfile variables aren't expanded in it. Only that recipe's directory is mounted, read-only, so
    /// a script can't see another's and none stays in the image. pen:latest has no USER, but this states the root the
    /// scripts rely on. Each recipe's [env] is ENV after all the RUNs, so installs don't depend on each other, sessions
    /// and a .pen/Dockerfile's RUNs get it, and that Dockerfile or the user's [env] can still override it. A value is
    /// literal.
    /// </remarks>
    private static string GenerateDockerfile(List<Install> plan)
    {
        static string Quoted(string text) =>
            $"\"{JsonEncodedText.Encode(text, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)}\"";
        static string EnvValue(string value) => $"\"{value
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", @"\$", StringComparison.Ordinal)}\"";
        var text = new StringBuilder($"FROM {Image.Name}\nUSER root\n");
        foreach (var (recipe, versions) in plan)
        {
            var argv = string.Join(',', versions.Prepend($"{MountPoint}/{RecipeCatalog.InstallFile}").Select(Quoted));
            text.Append(
                $"RUN --mount=type=bind,source={RecipeCatalog.Root}{recipe.Name},target={MountPoint} [{argv}]\n");
        }
        foreach (var (recipe, _) in plan)
        {
            foreach (var (name, value) in recipe.Env)
                text.Append($"ENV {name}={EnvValue(value)}\n");
        }
        return text.ToString();
    }

    /// <summary>Generates the Dockerfile that installs <paramref name="tools"/> on pen:latest.</summary>
    internal static string GenerateDockerfile(IEnumerable<ToolSpec> tools, RecipeCatalog catalog) =>
        GenerateDockerfile(PlanInstalls(tools, catalog));

    /// <summary>Computes the name of the image for <paramref name="tools"/> on <paramref name="baseId"/>.</summary>
    /// <returns>pen-tools:&lt;hash&gt;, the same whatever order the tools are in.</returns>
    /// <remarks>
    /// The hash covers the base image's ID, the canonical list, the recipes' files and the Dockerfile generated from
    /// them.
    /// </remarks>
    internal static string ComputeTag(string baseId, IEnumerable<ToolSpec> tools, RecipeCatalog catalog)
    {
        var plan = PlanInstalls(tools, catalog);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string kind, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(Encoding.UTF8.GetBytes($"{kind}\0{bytes.Length}\0"));
            hash.AppendData(bytes);
        }
        Add("base", baseId);
        foreach (var (recipe, versions) in plan)
        {
            Add("recipe", recipe.Name);
            foreach (var version in versions)
                Add("version", version);
            Add(RecipeCatalog.SettingsFile, recipe.Settings);
            Add(RecipeCatalog.InstallFile, recipe.Script);
        }
        Add("Dockerfile", GenerateDockerfile(plan));
        return $"{Repository}:{Convert.ToHexStringLower(hash.GetHashAndReset())[..16]}";
    }

    /// <summary>Writes the tar build context: the Dockerfile and each recipe's script.</summary>
    internal static void WriteContext(Stream output, IEnumerable<ToolSpec> tools, RecipeCatalog catalog)
    {
        var plan = PlanInstalls(tools, catalog);
        using var tar = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true);
        void Write(string path, string text, UnixFileMode mode) =>
            tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, path)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
                Mode = mode,
                ModificationTime = DateTimeOffset.UnixEpoch,
            });
        Write("Dockerfile", GenerateDockerfile(plan), (UnixFileMode)0b110_100_100);
        foreach (var (recipe, _) in plan)
            Write(recipe.InstallPath, recipe.Script, (UnixFileMode)0b111_101_101);
    }

    /// <summary>Reads the image ID of the current pen:latest.</summary>
    /// <returns>The ID, or null if it can't be read.</returns>
    internal static string? ReadCurrentBaseId()
    {
        var ids = Proc.Capture("docker", ["image", "inspect", "--format", "{{.Id}}", Image.Name]);
        var id = ids.StandardOutput.Trim();
        return ids.ExitStatus.ExitCode != 0 || id.Length == 0 ? null : id;
    }

    /// <summary>
    /// Builds the tools image for <paramref name="tools"/> if its tag doesn't exist, or always, without the
    /// build cache, with <paramref name="noCache"/> (pen upgrade --no-cache).
    /// </summary>
    /// <returns>The image to run: the tools image, or pen:latest with no tools.</returns>
    /// <remarks>pen:latest has to be up to date already (<see cref="Image.EnsureCurrent"/>).</remarks>
    public static string Ensure(IReadOnlyList<ToolSpec> tools, RecipeCatalog catalog, bool noCache = false)
    {
        if (tools.Count == 0)
            return Image.Name;
        var baseId = ReadCurrentBaseId() ?? throw new PenException("can't " +
            $"read the ID of {Image.Name}: is Docker running?");

        var name = ComputeTag(baseId, tools, catalog);
        if (!noCache
            && Proc.Capture("docker", ["image", "inspect", "--format", "{{.Id}}", name]).ExitStatus.ExitCode == 0)
        {
            return name;
        }

        var args = new List<string>
        {
            "build", "--progress", "plain", "-t", name, "--label", $"{BaseIdLabel}={baseId}"
        };
        if (noCache)
            args.Add("--no-cache");
        args.Add("-");
        Image.RunBuild(
            args,
            stdin => WriteContext(stdin, tools, catalog),
            $"building the tools image {name} for {string.Join(' ', ToolSpec.Merge(catalog, tools))}",
            "building the tools image failed");
        return name;
    }
}
