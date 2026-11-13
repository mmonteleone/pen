using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pen.Platform;
using Pen.Tools;

namespace Pen.Images;

/// <summary>
/// pen-tools:&lt;hash&gt;: pen:latest with a set of tools installed. The hash depends only
/// on what goes into the image, never on a project, so projects with the same tools share it.
/// If pen:latest moves during a build, the image gets the name of the old base, which
/// nothing asks for once the new base is current.
/// </summary>
internal static class ToolsImage
{
    public const string Repository = "pen-tools";
    public const string BaseIdLabel = "pen.base-id";
    private const string MountPoint = "/pen-recipe";

    private readonly record struct Install(Recipe Recipe, List<string> Versions);

    /// <summary>
    /// The recipes to run, in canonical order, each with its versions. The tools may have
    /// come from .pen/, and their versions reach a Dockerfile and a root script, so they're
    /// checked again here rather than trusted to have been parsed.
    /// </summary>
    private static List<Install> Plan(IEnumerable<ToolSpec> tools, RecipeCatalog catalog)
    {
        var plan = new List<Install>();
        foreach (var versions in ToolSpec.Merge(catalog, tools).GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            if (!catalog.TryGet(versions.Key, out var recipe))
                throw new PenException($"unknown tool \"{Out.Field(versions.Key)}\" ({catalog.Available()})");
            foreach (var t in versions)
            {
                if (!ToolSpec.IsVersionSyntax(t.Version) || !recipe.AcceptsVersion(t.Version))
                    throw new PenException($"\"{Out.Field(t.Version)}\" isn't a valid version of {recipe.Name}");
            }
            plan.Add(new(recipe, versions.Select(t => t.Version).ToList()));
        }
        return plan;
    }

    /// <summary>
    /// One RUN per recipe, in JSON (exec) form: no shell parses it, each version is
    /// one JSON string and so one argument, and Dockerfile variables aren't expanded in it.
    /// Only that recipe's directory is mounted, read-only, so a script can't see another's
    /// and none stays in the image. pen:latest has no USER, but this states the root the
    /// scripts rely on. Each recipe's [env] is ENV after all the RUNs, so installs don't
    /// depend on each other, sessions and a .pen/Dockerfile's RUNs get it, and that
    /// Dockerfile or the user's [env] can still override it. A value is literal.
    /// </summary>
    private static string Dockerfile(List<Install> plan)
    {
        static string Quoted(string s) => $"\"{JsonEncodedText.Encode(s, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)}\"";
        static string EnvValue(string s) => $"\"{s.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("$", @"\$", StringComparison.Ordinal)}\"";
        var text = new StringBuilder($"FROM {Image.Name}\nUSER root\n");
        foreach (var (recipe, versions) in plan)
        {
            var argv = string.Join(',', versions.Prepend($"{MountPoint}/{RecipeCatalog.InstallFile}").Select(Quoted));
            text.Append($"RUN --mount=type=bind,source={RecipeCatalog.Root}{recipe.Name},target={MountPoint} [{argv}]\n");
        }
        foreach (var (recipe, _) in plan)
        {
            foreach (var (name, value) in recipe.Env)
                text.Append($"ENV {name}={EnvValue(value)}\n");
        }
        return text.ToString();
    }

    internal static string Dockerfile(IEnumerable<ToolSpec> tools, RecipeCatalog catalog) => Dockerfile(Plan(tools, catalog));

    /// <summary>
    /// The image's name, whatever order the tools are in: from the base image's ID,
    /// the canonical list, the recipes' files and the Dockerfile generated from them.
    /// </summary>
    internal static string Tag(string baseId, IEnumerable<ToolSpec> tools, RecipeCatalog catalog)
    {
        var plan = Plan(tools, catalog);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string kind, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            h.AppendData(Encoding.UTF8.GetBytes($"{kind}\0{bytes.Length}\0"));
            h.AppendData(bytes);
        }
        Add("base", baseId);
        foreach (var (recipe, versions) in plan)
        {
            Add("recipe", recipe.Name);
            foreach (var version in versions) Add("version", version);
            Add(RecipeCatalog.SettingsFile, recipe.Settings);
            Add(RecipeCatalog.InstallFile, recipe.Script);
        }
        Add("Dockerfile", Dockerfile(plan));
        return $"{Repository}:{Convert.ToHexStringLower(h.GetHashAndReset())[..16]}";
    }

    /// <summary>Writes the tar build context: the Dockerfile and each recipe's script.</summary>
    internal static void WriteContext(Stream output, IEnumerable<ToolSpec> tools, RecipeCatalog catalog)
    {
        var plan = Plan(tools, catalog);
        using var tar = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true);
        void Write(string path, string text, UnixFileMode mode) => tar.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, path)
        {
            DataStream = new MemoryStream(Encoding.UTF8.GetBytes(text)),
            Mode = mode,
            ModificationTime = DateTimeOffset.UnixEpoch,
        });
        Write("Dockerfile", Dockerfile(plan), (UnixFileMode)0b110_100_100);
        foreach (var (recipe, _) in plan)
            Write(recipe.InstallPath, recipe.Script, (UnixFileMode)0b111_101_101);
    }

    /// <summary>pen:latest's image ID, or null if it can't be read.</summary>
    internal static string? CurrentBaseId()
    {
        var ids = Proc.Capture("docker", ["image", "inspect", "--format", "{{.Id}}", Image.Name]);
        var id = ids.StandardOutput.Trim();
        return ids.ExitStatus.ExitCode != 0 || id.Length == 0 ? null : id;
    }

    /// <summary>
    /// The image to run for these tools, built if its tag doesn't exist, or always without
    /// the build cache with <paramref name="noCache"/> (pen upgrade --clean). pen:latest with
    /// no tools. pen:latest has to be up to date already (<see cref="Image.EnsureCurrent"/>).
    /// </summary>
    public static string Ensure(IReadOnlyList<ToolSpec> tools, RecipeCatalog catalog, bool noCache = false)
    {
        if (tools.Count == 0) return Image.Name;
        var baseId = CurrentBaseId() ?? throw new PenException($"can't read the ID of {Image.Name}: is Docker running?");

        var name = Tag(baseId, tools, catalog);
        if (!noCache && Proc.Capture("docker", ["image", "inspect", "--format", "{{.Id}}", name]).ExitStatus.ExitCode == 0)
            return name;
        var args = new List<string> { "build", "--progress", "plain", "-t", name, "--label", $"{BaseIdLabel}={baseId}" };
        if (noCache) args.Add("--no-cache");
        args.Add("-");
        Image.RunBuild(
            args,
            stdin => WriteContext(stdin, tools, catalog),
            $"building the tools image {name} for {string.Join(' ', ToolSpec.Merge(catalog, tools))}",
            "building the tools image failed");
        return name;
    }
}
