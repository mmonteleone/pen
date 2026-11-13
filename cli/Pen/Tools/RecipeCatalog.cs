using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Tomlyn.Model;
using Pen.Platform;

namespace Pen.Tools;

/// <summary>The recipes a set of tools can come from: normally the embedded ones.</summary>
internal sealed partial class RecipeCatalog
{
    public const string Root = "tools/";
    public const string SettingsFile = "tool.toml";
    public const string InstallFile = "install.sh";
    /// <summary>--tool list means the listing, so no tool can have this name.</summary>
    private const string ReservedName = "list";

    private static readonly Lazy<RecipeCatalog> EmbeddedCatalog = new(() =>
        FromFiles(Pen.Platform.Embedded.Under(Root).Select(path => KeyValuePair.Create(path, Pen.Platform.Embedded.Text(path)))));

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
        var agents = Offered.Where(r => r.IsAgent).ToList();
        var tools = Offered.Where(r => !r.IsAgent).ToList();
        string Group(List<Recipe> group)
        {
            var width = group.Max(r => r.Name.Length);
            return string.Concat(group.Select(r =>
                $"{r.Name.PadRight(width)}  default {r.Default}{(r.Multiple ? ", several versions can be installed together" : "")}\n"));
        }
        if (agents.Count == 0) return Group(tools);
        if (tools.Count == 0) return $"agents:\n{Group(agents)}";
        return $"agents:\n{Group(agents)}\ntools:\n{Group(tools)}";
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*\z")]
    private static partial Regex NameRegex();
}
