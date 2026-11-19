using Pen.Configuration;
using Pen.Projects;
using Pen.Tools;

namespace Pen.Proxy;

/// <summary>Words the warnings about a session's egress: open egress, and deny taking what tools need.</summary>
internal static class EgressWarnings
{
    /// <summary>
    /// Lists the warnings about the egress of a session with <paramref name="tools"/> and the merged
    /// <paramref name="deny"/> list.
    /// </summary>
    public static IReadOnlyList<string> ListForSession(
        ProjectContext context, RecipeCatalog catalog, IReadOnlyList<ToolSpec> tools, IReadOnlyList<string> deny)
    {
        var needs = tools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal)
            .Select(name => (name, catalog.TryGet(name, out var recipe) ? recipe.Allow : []));
        return Format(context.User, context.Host.Display(context.Host.UserConfigPath),
            context.ProjectConfig, deny, needs);
    }

    /// <summary>Formats the warnings about a session's egress.</summary>
    /// <param name="user">The user's config.</param>
    /// <param name="userConfigPath">Names the user's config.toml.</param>
    /// <param name="projectConfig">The project's config.</param>
    /// <param name="deny">The merged deny list.</param>
    /// <param name="toolDomains">Each of the session's tools, by name, with the domains its recipe brings.</param>
    /// <returns>
    /// One line when allow has <see cref="Allowlist.AnyDomain"/>, naming the files; then one per domain a tool
    /// needs that deny covers, in part or whole, naming the narrower of the two and the tools.
    /// </returns>
    internal static IReadOnlyList<string> Format(
        UserConfiguration user,
        string userConfigPath,
        ProjectConfiguration projectConfig,
        IReadOnlyList<string> deny,
        IEnumerable<(string Tool, IReadOnlyList<string> Domains)> toolDomains)
    {
        var warnings = new List<string>();
        var openIn = new List<string>();
        if (user.Allow.Contains(Allowlist.AnyDomain))
            openIn.Add(userConfigPath);
        if (projectConfig.Allow.Contains(Allowlist.AnyDomain))
            openIn.Add(".pen/" + ConfigLoader.FileName);
        if (openIn.Count > 0)
            warnings.Add($"egress is open: any domain is allowed (allow = [\"*\"] in {string.Join(" and ", openIn)})");

        // A deny entry under a tool's wildcard takes part of what it needs, and one over a tool's domain all of it.
        var covered = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (tool, domains) in toolDomains)
        {
            foreach (var domain in domains)
            {
                foreach (var entry in deny)
                {
                    var taken = Allowlist.FindDenyCoverage(entry, domain) switch
                    {
                        DenyCoverage.All => domain,
                        DenyCoverage.Part => entry,
                        _ => null,
                    };
                    if (taken is null)
                        continue;
                    if (!covered.TryGetValue(taken, out var needers))
                    {
                        covered[taken] = needers = [];
                        order.Add(taken);
                    }
                    if (!needers.Contains(tool))
                        needers.Add(tool);
                }
            }
        }
        foreach (var taken in order)
        {
            var needers = covered[taken];
            var who = needers.Count == 1
                ? needers[0] + " needs"
                : string.Join(", ", needers[..^1]) + " and " + needers[^1] + " need";
            warnings.Add($"deny covers {taken}, which {who}");
        }
        return warnings;
    }
}
