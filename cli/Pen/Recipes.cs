using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Tomlyn.Model;

namespace Pen;

/// <summary>
/// A tool's recipe: tools/&lt;name&gt;/tool.toml, its settings, and tools/&lt;name&gt;/install.sh,
/// which runs as root at image build, with the versions as separate arguments.
/// Recipes are embedded and trusted, and can't depend on each other. A hidden one works
/// but isn't offered in listings or messages.
/// </summary>
internal sealed class Recipe
{
    public required string Name { get; init; }
    public required string Default { get; init; }
    /// <summary>Whether several versions can be installed together.</summary>
    public bool Multiple { get; init; }
    public bool Hidden { get; init; }
    /// <summary>Domains the tool needs, in file order.</summary>
    public List<string> Allow { get; } = [];
    /// <summary>Environment for sessions with the tool, in file order: set in its image (<see cref="ToolsImage"/>).</summary>
    public List<KeyValuePair<string, string>> Env { get; } = [];
    public string InstallPath => $"{RecipeCatalog.Root}{Name}/{RecipeCatalog.InstallFile}";
    /// <summary>The text of tool.toml, as loaded.</summary>
    public required string Settings { get; init; }
    /// <summary>The text of install.sh, as loaded.</summary>
    public required string Script { get; init; }

    private Regex versionRegex = null!;

    /// <summary>
    /// Whether the regex matches all of the version. Not just IsMatch: an unanchored
    /// pattern could match part of it, and .NET's $ also matches before a final newline.
    /// </summary>
    public bool AcceptsVersion(string version)
    {
        var m = versionRegex.Match(version);
        return m.Success && m.Index == 0 && m.Length == version.Length;
    }

    /// <summary>Parses a tool.toml, the way <see cref="Config.Parse"/> does a config.toml.</summary>
    public static Recipe Parse(string name, string text, string script)
    {
        var display = $"{RecipeCatalog.Root}{name}/{RecipeCatalog.SettingsFile}";
        PenException Error(string message) => new($"{display}: {Out.Field(message)}");
        var table = Config.ParseToml(text, display);

        foreach (var key in table.Keys)
        {
            if (key is not ("default" or "version" or "multiple" or "hidden" or "allow" or "env"))
                throw Error($"unknown key \"{key}\"");
        }
        object Required(string key) => table.TryGetValue(key, out var v) ? v : throw Error($"{key} is missing");
        bool Flag(string key) => !table.TryGetValue(key, out var v) ? false : v as bool? ?? throw Error($"{key} must be true or false (unquoted)");

        Regex regex;
        try
        {
            // Only ever a recipe's own pattern, on a short version: interpreted, so AOT-safe.
            regex = new Regex(Config.String(Required("version"), "version", Error), RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            throw Error("version isn't a valid regular expression");
        }

        var recipe = new Recipe
        {
            Name = name,
            Default = Config.String(Required("default"), "default", Error),
            Multiple = Flag("multiple"),
            Hidden = Flag("hidden"),
            Settings = text,
            Script = script,
            versionRegex = regex,
        };
        if (!ToolSpec.IsVersionSyntax(recipe.Default) || !recipe.AcceptsVersion(recipe.Default))
            throw Error($"default \"{recipe.Default}\" isn't a valid version");

        if (table.TryGetValue("allow", out var allow))
        {
            if (allow is not TomlArray domains) throw Error("allow must be a list of domains");
            foreach (var d in domains)
            {
                if (d is not string domain || !Allowlist.IsValidDomain(domain))
                    throw Error($"allow: {d ?? "null"} isn't a lowercase domain");
                recipe.Allow.Add(domain);
            }
        }
        if (table.TryGetValue("env", out var env))
        {
            foreach (var (variable, value) in Config.Table(env, "env", Error))
            {
                if (!Config.EnvNameRegex().IsMatch(variable)) throw Error($"env.{variable} isn't a valid environment variable name");
                if (Config.IsReservedEnv(variable)) throw Error($"env.{variable} is set by pen");
                var setting = Config.String(value, $"env.{variable}", Error);
                if (setting.Any(char.IsControl)) throw Error($"env.{variable} can't hold a line break or control character");
                recipe.Env.Add(new(variable, setting));
            }
        }
        return recipe;
    }
}

/// <summary>The recipes a set of tools can come from: normally the embedded ones.</summary>
internal sealed partial class RecipeCatalog
{
    public const string Root = "tools/";
    public const string SettingsFile = "tool.toml";
    public const string InstallFile = "install.sh";
    /// <summary>--tool list means the listing, so no tool can have this name.</summary>
    private const string ReservedName = "list";

    private static readonly Lazy<RecipeCatalog> EmbeddedCatalog = new(() =>
        FromFiles(Pen.Embedded.Under(Root).Select(path => KeyValuePair.Create(path, Pen.Embedded.Text(path)))));

    /// <summary>Every recipe embedded in the binary. A malformed one throws, which unit tests catch before it ships.</summary>
    public static RecipeCatalog Embedded => EmbeddedCatalog.Value;

    private readonly SortedDictionary<string, Recipe> recipes = new(StringComparer.Ordinal);

    public IEnumerable<Recipe> Recipes => recipes.Values;

    public bool TryGet(string name, [MaybeNullWhen(false)] out Recipe recipe) => recipes.TryGetValue(name, out recipe);

    /// <summary>
    /// Loads recipes from files named by their paths under tools/. A recipe is exactly
    /// its two files, and anything else there is an error.
    /// </summary>
    public static RecipeCatalog FromFiles(IEnumerable<KeyValuePair<string, string>> files)
    {
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            var parts = path.StartsWith(Root, StringComparison.Ordinal) ? path[Root.Length..].Split('/') : [];
            if (parts.Length != 2 || !NameRegex().IsMatch(parts[0]) || parts[0] == ReservedName)
                throw new PenException($"{path}: not part of a recipe (tools/<name>/{SettingsFile} and {InstallFile}, with a lowercase name other than {ReservedName})");
            if (parts[1] == SettingsFile) settings[parts[0]] = text;
            else if (parts[1] == InstallFile) scripts[parts[0]] = text;
            else throw new PenException($"{path}: a recipe has only {SettingsFile} and {InstallFile}");
        }

        var catalog = new RecipeCatalog();
        foreach (var (name, text) in settings)
        {
            if (!scripts.TryGetValue(name, out var script)) throw new PenException($"{Root}{name}/{InstallFile} is missing");
            if (!script.StartsWith("#!", StringComparison.Ordinal)) throw new PenException($"{Root}{name}/{InstallFile} must start with #!");
            catalog.recipes[name] = Recipe.Parse(name, text, script);
        }
        foreach (var name in scripts.Keys)
        {
            if (!settings.ContainsKey(name)) throw new PenException($"{Root}{name}/{SettingsFile} is missing");
        }
        return catalog;
    }

    /// <summary>Whether <paramref name="name"/> is the form a recipe's name has.</summary>
    internal static bool IsName(string name) => NameRegex().IsMatch(name);

    private IEnumerable<Recipe> Offered => Recipes.Where(r => !r.Hidden);

    /// <summary>What can be asked for, for a message.</summary>
    public string Available() => Offered.Any()
        ? $"available: {string.Join(", ", Offered.Select(r => r.Name))}"
        : "none are available yet";

    /// <summary>What pen --tool list prints.</summary>
    public string Listing()
    {
        if (!Offered.Any()) return "no tools are available yet\n";
        var width = Offered.Max(r => r.Name.Length);
        return string.Concat(Offered.Select(r =>
            $"{r.Name.PadRight(width)}  default {r.Default}{(r.Multiple ? ", several versions can be installed together" : "")}\n"));
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*\z")]
    private static partial Regex NameRegex();
}
