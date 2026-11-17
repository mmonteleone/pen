using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Pen.Tools;

/// <summary>A set of recipes that tools can come from: in production, the embedded ones.</summary>
internal sealed partial class RecipeCatalog
{
    public const string Root = "tools/";
    public const string SettingsFile = "tool.toml";
    public const string InstallFile = "install.sh";

    private static readonly Lazy<RecipeCatalog> EmbeddedCatalog = new(() =>
        FromFiles(Pen.Platform.Embedded.ListUnder(Root)
            .Select(path => KeyValuePair.Create(path, Pen.Platform.Embedded.ReadText(path)))));

    /// <summary>Gets the catalog of every recipe embedded in the binary.</summary>
    /// <remarks>A malformed recipe throws, which unit tests catch before it ships.</remarks>
    public static RecipeCatalog Embedded => EmbeddedCatalog.Value;

    private readonly SortedDictionary<string, Recipe> _recipes = new(StringComparer.Ordinal);

    /// <summary>Gets the recipes, in name order.</summary>
    public IEnumerable<Recipe> Recipes => _recipes.Values;

    /// <summary>Tries to get the recipe called <paramref name="name"/>.</summary>
    public bool TryGet(string name, [MaybeNullWhen(false)] out Recipe recipe) => _recipes.TryGetValue(name, out recipe);

    /// <summary>Loads a catalog from files named by their paths under tools/.</summary>
    /// <remarks>A recipe is exactly its two files, and anything else there is an error.</remarks>
    public static RecipeCatalog FromFiles(IEnumerable<KeyValuePair<string, string>> files)
    {
        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var scripts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
        {
            var parts = path.StartsWith(Root, StringComparison.Ordinal) ? path[Root.Length..].Split('/') : [];
            if (parts.Length != 2 || !NameRegex().IsMatch(parts[0]))
            {
                throw new PenException($"{path}: not part of a recipe (tools/<name>/{SettingsFile} " +
                    $"and {InstallFile}, with a lowercase name)");
            }

            if (parts[1] == SettingsFile)
                settings[parts[0]] = text;
            else if (parts[1] == InstallFile)
                scripts[parts[0]] = text;
            else
                throw new PenException($"{path}: a recipe has only {SettingsFile} and {InstallFile}");
        }

        var catalog = new RecipeCatalog();
        foreach (var (name, text) in settings)
        {
            if (!scripts.TryGetValue(name, out var script))
                throw new PenException($"{Root}{name}/{InstallFile} is missing");
            if (!script.StartsWith("#!", StringComparison.Ordinal))
                throw new PenException($"{Root}{name}/{InstallFile} must start with #!");
            catalog._recipes[name] = Recipe.Parse(name, text, script);
        }
        foreach (var name in scripts.Keys)
        {
            if (!settings.ContainsKey(name))
                throw new PenException($"{Root}{name}/{SettingsFile} is missing");
        }
        return catalog;
    }

    /// <summary>Checks whether <paramref name="name"/> has the form of a recipe's name.</summary>
    internal static bool IsName(string name) => NameRegex().IsMatch(name);

    /// <summary>Gets the recipes that listings and messages offer: the ones that aren't hidden.</summary>
    private IEnumerable<Recipe> Offered => Recipes.Where(r => !r.Hidden);

    /// <summary>Describes what can be asked for, for a message.</summary>
    public string DescribeAvailable() => Offered.Any()
        ? $"available: {string.Join(", ", Offered.Select(r => r.Name))}"
        : "none are available yet";

    /// <summary>Formats what pen tools prints: the offered agents and tools, each with its default.</summary>
    public string FormatListing()
    {
        if (!Offered.Any())
            return "no tools are available yet\n";
        var agents = Offered.Where(r => r.IsAgent).ToList();
        var tools = Offered.Where(r => !r.IsAgent).ToList();
        if (agents.Count == 0)
            return FormatGroup(tools);
        if (tools.Count == 0)
            return $"agents:\n{FormatGroup(agents)}";
        return $"agents:\n{FormatGroup(agents)}\ntools:\n{FormatGroup(tools)}";
    }

    /// <summary>Formats one line per recipe in <paramref name="group"/>, with the names padded to one width.</summary>
    private static string FormatGroup(List<Recipe> group)
    {
        var width = group.Max(r => r.Name.Length);
        return string.Concat(group.Select(r =>
            $"{r.Name.PadRight(width)}  default " +
                $"{r.Default}{(r.Multiple ? ", several versions can be installed together" : "")}\n"));
    }

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]*\z")]
    private static partial Regex NameRegex();
}
