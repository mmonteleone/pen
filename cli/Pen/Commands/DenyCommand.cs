using System.Text;
using Pen.Configuration;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Tools;

namespace Pen.Commands;

/// <summary>
/// Runs pen deny: adds domains to deny in a config.toml and applies them to running sessions, or lists what's
/// denied.
/// </summary>
/// <param name="host">The host user's environment.</param>
/// <param name="catalog">The recipes, whose domains the listing says a deny takes.</param>
/// <param name="clock">The clock waits for another pen allow or pen deny are timed by.</param>
/// <remarks>Deny can only take egress away, so a project's needs no approval, unlike pen allow's.</remarks>
internal sealed class DenyCommand(HostEnv host, RecipeCatalog catalog, TimeProvider clock)
{
    /// <summary>Adds <paramref name="domains"/> to deny and applies them to running sessions.</summary>
    /// <param name="domains">The domains to deny (lowercased).</param>
    /// <param name="global">
    /// Add to the user's config.toml and apply to every project's sessions (--global), rather than to .pen/ and
    /// the sessions started in this directory.
    /// </param>
    /// <returns>0, or 1 when applying to a running session failed; a failure to add throws.</returns>
    public int Run(IReadOnlyList<string> domains, bool global)
    {
        var project = AllowCommand.PrepareProject(host, global, clock);
        var addition = AllowCommand.AddToConfig(host, project, global, ConfigEdit.DenyKey, domains, catalog);
        AllowCommand.WriteAdded(addition, domains);
        AllowCommand.WriteGlobHint("deny", addition);
        return AllowCommand.ApplyToRunning(host, project, global, catalog, clock) ? 0 : 1;
    }

    /// <summary>Runs pen deny without domains: lists what both config files deny.</summary>
    /// <param name="context">The project and both configs.</param>
    /// <returns>0.</returns>
    public int List(ProjectContext context)
    {
        Out.WriteStdout(FormatListing(context.User, host.Display(host.UserConfigPath), context.ProjectConfig,
            catalog));
        return 0;
    }

    /// <summary>Formats the listing of what both config files deny.</summary>
    /// <param name="user">The user's config.</param>
    /// <param name="userConfigPath">The user's config.toml, as messages name it.</param>
    /// <param name="projectConfig">The project's config.</param>
    /// <param name="catalog">The recipes, whose domains a deny may take.</param>
    /// <returns>
    /// One line per entry, user's first: the domain, its file, and which default or recipe domains it takes, in
    /// whole or in part; a note when nothing is denied.
    /// </returns>
    internal static string FormatListing(
        Config user,
        string userConfigPath,
        Config projectConfig,
        RecipeCatalog catalog)
    {
        var entries = user.Deny.Distinct().Select(domain => (Domain: domain, Where: userConfigPath))
            .Concat(projectConfig.Deny.Distinct().Select(domain => (Domain: domain, Where: ".pen/" + Config.FileName)))
            .ToList();
        if (entries.Count == 0)
            return "nothing denied; pen deny <domain…> [--global] adds to deny\n";

        IEnumerable<(string Source, IEnumerable<string> Domains)> sources =
        [
            ("default", Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt"))),
            .. catalog.Recipes.Where(recipe => !recipe.Hidden)
                .OrderBy(recipe => recipe.Name, StringComparer.Ordinal)
                .Select(recipe => (recipe.Name, (IEnumerable<string>)recipe.Allow)),
        ];
        var domainWidth = entries.Max(entry => Out.Field(entry.Domain).Length);
        var whereWidth = entries.Max(entry => entry.Where.Length);
        var listing = new StringBuilder("denied (deny wins over every allow):\n");
        foreach (var (domain, where) in entries)
        {
            var line = $"  {Out.Field(domain).PadRight(domainWidth)}  {where.PadRight(whereWidth)}";
            var taken = DescribeTaken(domain, sources);
            listing.Append((taken.Length > 0 ? $"{line}  takes {taken}" : line).TrimEnd()).Append('\n');
        }
        return listing.ToString();
    }

    /// <summary>Describes what the deny entry <paramref name="denied"/> takes from the allow sources.</summary>
    /// <returns>
    /// Each allow domain it covers, or "part of" one whose wildcard it's under, with the sources that bring it, as
    /// "part of .github.com (default)"; empty when it takes none.
    /// </returns>
    private static string DescribeTaken(
        string denied,
        IEnumerable<(string Source, IEnumerable<string> Domains)> sources)
    {
        var bringers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var (source, domains) in sources)
        {
            foreach (var domain in domains)
            {
                string taken;
                if (Allowlist.IsDenied([denied], domain))
                    taken = domain;
                else if (Allowlist.IsAllowed([domain], denied))
                    taken = "part of " + domain;
                else
                    continue;
                if (!bringers.TryGetValue(taken, out var who))
                {
                    bringers[taken] = who = [];
                    order.Add(taken);
                }
                if (!who.Contains(source))
                    who.Add(source);
            }
        }
        return string.Join(", ", order.Select(taken => $"{taken} ({string.Join(", ", bringers[taken])})"));
    }
}
