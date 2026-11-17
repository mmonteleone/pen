using System.Text.RegularExpressions;
using Pen.Platform;

namespace Pen.Tools;

/// <summary>One tool at one version, always valid for its recipe.</summary>
/// <remarks>
/// A sorted, duplicate-free list of these (<see cref="TryCanonicalize"/>) is a set of tools' identity,
/// so the order they were asked for in never matters.
/// </remarks>
internal readonly partial record struct ToolSpec(string Name, string Version)
{
    /// <summary>Formats the tool as name:version.</summary>
    public override string ToString() => $"{Name}:{Version}";

    /// <summary>Parses "name" (the recipe's default version) or "name:version" into <paramref name="tool"/>.</summary>
    /// <returns>What's wrong with <paramref name="spec"/>, or null when it parsed.</returns>
    /// <remarks>
    /// A version stays as written, since 24.04 and 24.4 are different versions, and a recipe's regex
    /// rejects the forms it doesn't want. The spec may come from .pen/, which an agent can write, and the
    /// version ends up as an argument to a root script. So whatever a recipe's regex says, it's held to
    /// <see cref="IsVersionSyntax"/>: no shell syntax, slash, whitespace or leading dash.
    /// </remarks>
    public static string? TryParse(string spec, RecipeCatalog catalog, out ToolSpec tool)
    {
        tool = default;
        var colon = spec.IndexOf(':');
        var name = colon < 0 ? spec : spec[..colon];
        if (!catalog.TryGet(name, out var recipe))
        {
            var hint = spec == "list" ? "; pen tools lists them" : "";
            return $"unknown tool \"{Out.Field(name)}\" ({catalog.DescribeAvailable()}){hint}";
        }
        if (colon < 0)
        {
            tool = new(name, recipe.Default);
            return null;
        }
        var given = spec[(colon + 1)..];
        if (!IsVersionSyntax(given) || !recipe.AcceptsVersion(given))
            return $"\"{Out.Field(given)}\" isn't a valid version of {name}";
        tool = new(name, given);
        return null;
    }

    /// <summary>Makes <paramref name="canonical"/>: the tools without exact duplicates, sorted ordinally.</summary>
    /// <returns>
    /// What's wrong if it holds two versions of a tool whose recipe doesn't allow that, or null.
    /// </returns>
    public static string? TryCanonicalize(
        IEnumerable<ToolSpec> tools,
        RecipeCatalog catalog,
        out IReadOnlyList<ToolSpec> canonical)
    {
        var sorted = tools.Distinct().OrderBy(t => t.ToString(), StringComparer.Ordinal).ToList();
        canonical = sorted;
        foreach (var versions in sorted.GroupBy(t => t.Name, StringComparer.Ordinal))
        {
            if (versions.Count() > 1 && catalog.TryGet(versions.Key, out var recipe) && !recipe.Multiple)
            {
                return $"{string.Join(" and ", versions)} can't be used together: " +
                    $"only one version of {versions.Key} can be installed";
            }
        }
        return null;
    }

    /// <summary>Merges user config, project config and --tool into the canonical list for a session.</summary>
    /// <returns>The canonical list; tools that can't go together throw.</returns>
    public static IReadOnlyList<ToolSpec> Merge(RecipeCatalog catalog, params IEnumerable<ToolSpec>[] lists)
    {
        if (TryCanonicalize(lists.SelectMany(list => list), catalog, out var canonical) is { } conflict)
            throw new PenException($"tools: {conflict}. Check --tool, ~/.config/pen/config.toml and .pen/config.toml");
        return canonical;
    }

    /// <summary>Finds the agent <paramref name="command"/> implies.</summary>
    /// <returns>
    /// The agent at its default version, when <c>command[0]</c> is exactly the name of a visible agent
    /// recipe and none of <paramref name="named"/> (any version) is that agent; otherwise null.
    /// </returns>
    /// <remarks>Only the command's first word counts.</remarks>
    public static ToolSpec? FindImpliedAgent(
        IReadOnlyList<string> command,
        RecipeCatalog catalog,
        IEnumerable<ToolSpec> named)
    {
        if (command.Count == 0 || !catalog.TryGet(command[0], out var recipe) || !recipe.IsAgent || recipe.Hidden)
            return null;
        return named.Any(t => t.Name == recipe.Name) ? null : new ToolSpec(recipe.Name, recipe.Default);
    }

    /// <summary>Lists the domains the tools' recipes bring, in order.</summary>
    /// <remarks>A tool with no recipe (not one that's parsed) brings none.</remarks>
    public static List<string> ListDomains(IEnumerable<ToolSpec> tools, RecipeCatalog catalog) =>
        tools.Select(t => t.Name).Distinct(StringComparer.Ordinal)
            .SelectMany(name => catalog.TryGet(name, out var recipe) ? recipe.Allow : [])
            .ToList();

    /// <summary>Checks whether <paramref name="text"/> has the form of a parsed tool, name:version.</summary>
    internal static bool IsCanonicalText(string text)
    {
        var colon = text.IndexOf(':');
        return colon > 0 && RecipeCatalog.IsName(text[..colon]) && IsVersionSyntax(text[(colon + 1)..]);
    }

    /// <summary>Checks whether <paramref name="version"/> has only characters any version may have.</summary>
    /// <remarks>A version must pass this fixed check as well as the recipe's own regex, whatever that allows.</remarks>
    internal static bool IsVersionSyntax(string version) => VersionSyntaxRegex().IsMatch(version);

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}\z")]
    private static partial Regex VersionSyntaxRegex();
}
