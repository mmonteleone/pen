using System.Text.RegularExpressions;

namespace Pen;

/// <summary>
/// One tool at one version, always valid for its recipe. A sorted, duplicate-free
/// list of these (<see cref="TryCanonicalize"/>) is a set of tools' identity, so the
/// order they were asked for in never matters.
/// </summary>
internal readonly partial record struct ToolSpec(string Name, string Version)
{
    public override string ToString() => $"{Name}:{Version}";

    /// <summary>
    /// Parses "name" (the recipe's default version) or "name:version". The spec may
    /// come from .pen/, which an agent can write, and the version ends up as an argument
    /// to a root script. So whatever a recipe's regex says, it's held to
    /// <see cref="IsVersionSyntax"/>: no shell syntax, slash, whitespace or leading dash.
    /// Returns what's wrong, or null.
    /// </summary>
    public static string? TryParse(string spec, RecipeCatalog catalog, out ToolSpec tool)
    {
        tool = default;
        var colon = spec.IndexOf(':');
        var name = colon < 0 ? spec : spec[..colon];
        if (!catalog.TryGet(name, out var recipe))
            return $"unknown tool \"{Out.Field(name)}\" ({catalog.Available()})";
        if (colon < 0)
        {
            tool = new(name, recipe.Default);
            return null;
        }
        var given = spec[(colon + 1)..];
        var version = IsVersionSyntax(given) ? NormalizeVersion(given) : null;
        if (version == null || !recipe.AcceptsVersion(version))
            return $"\"{Out.Field(given)}\" isn't a valid version of {name}";
        tool = new(name, version);
        return null;
    }

    /// <summary>
    /// The canonical list: no duplicates, sorted ordinally. Returns what's wrong
    /// if it holds two versions of a tool whose recipe doesn't allow that, or null.
    /// </summary>
    public static string? TryCanonicalize(IEnumerable<ToolSpec> tools, RecipeCatalog catalog, out IReadOnlyList<ToolSpec> canonical)
    {
        var sorted = tools.Distinct().OrderBy(t => t.ToString(), StringComparer.Ordinal).ToList();
        canonical = sorted;
        foreach (var versions in sorted.GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            if (versions.Count() > 1 && catalog.TryGet(versions.Key, out var recipe) && !recipe.Multiple)
                return $"{string.Join(" and ", versions)} can't be used together: only one version of {versions.Key} can be installed";
        }
        return null;
    }

    /// <summary>The canonical list for a session: user config, project config and --tool appended.</summary>
    public static IReadOnlyList<ToolSpec> Merge(RecipeCatalog catalog, params IEnumerable<ToolSpec>[] lists)
    {
        if (TryCanonicalize(lists.SelectMany(l => l), catalog, out var canonical) is { } conflict)
            throw new PenException($"tools: {conflict}. Check --tool, ~/.config/pen/config.toml and .pen/config.toml");
        return canonical;
    }

    /// <summary>The characters any version may use, whatever its recipe allows.</summary>
    internal static bool IsVersionSyntax(string version) => VersionSyntaxRegex().IsMatch(version);

    /// <summary>
    /// A version's one spelling: leading zeros go from its all-digit dot-separated
    /// parts, so dotnet:010 and dotnet:10 are the same tool. Takes a version
    /// that passed <see cref="IsVersionSyntax"/>.
    /// </summary>
    internal static string NormalizeVersion(string version) =>
        string.Join('.', version.Split('.').Select(part =>
            part.Length > 1 && part.All(char.IsAsciiDigit) && part.TrimStart('0') is var digits ? (digits.Length > 0 ? digits : "0") : part));

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}\z")]
    private static partial Regex VersionSyntaxRegex();
}
