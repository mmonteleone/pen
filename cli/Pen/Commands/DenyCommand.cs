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
/// <param name="startDir">The directory Pen was started in, where the project is.</param>
/// <param name="catalog">The recipes, whose domains the listing says a deny takes.</param>
/// <param name="clock">The clock waits for another pen allow or pen deny are timed by.</param>
/// <remarks>Deny can only take egress away, so a project's needs no approval, unlike pen allow's.</remarks>
internal sealed class DenyCommand(HostEnv host, StartDir startDir, RecipeCatalog catalog, TimeProvider clock)
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
        var target = EgressTarget.Resolve(host, startDir, global);
        target.PrepareProjectAndRecover(host, clock);
        var addition = AddDenied(host, target, domains, catalog);
        EgressMessages.WriteAdded(addition, domains);
        EgressMessages.WriteGlobHint("deny", startDir, addition);
        var results = target.ApplyToRunningSessions(host, new SessionAclUpdater(host, catalog, clock));
        return results.Any(result => result is AclUpdateResult.Failed) ? 1 : 0;
    }

    /// <summary>Adds <paramref name="domains"/> to deny, as <see cref="EgressTarget.AddToConfig"/> does.</summary>
    /// <returns>The file, as messages name it, and the domains that weren't in its deny yet.</returns>
    /// <remarks>Nothing is approved: deny needs no approval.</remarks>
    internal static ConfigAddition AddDenied(
        HostEnv host,
        EgressTarget target,
        IReadOnlyList<string> domains,
        RecipeCatalog catalog) =>
        target.AddToConfig(host, ConfigListKey.Deny, domains, catalog);

    /// <summary>Runs pen deny without domains: lists what both config files deny.</summary>
    /// <returns>0.</returns>
    public int List(ProjectContext context)
    {
        Out.WriteStdout(FormatListing(context.User, host.Display(host.UserConfigPath), context.ProjectConfig,
            catalog));
        return 0;
    }

    /// <summary>
    /// Formats the listing of what both config files deny, naming the user's config.toml as
    /// <paramref name="userConfigPath"/>.
    /// </summary>
    /// <returns>
    /// One line per entry, user's first: the domain, its file, and which default or recipe domains it takes, in
    /// whole or in part; a note when nothing is denied.
    /// </returns>
    internal static string FormatListing(
        UserConfiguration user,
        string userConfigPath,
        ProjectConfiguration projectConfig,
        RecipeCatalog catalog)
    {
        var entries = user.Deny.Distinct().Select(domain => (Domain: domain, Where: userConfigPath))
            .Concat(projectConfig.Deny.Distinct()
                .Select(domain => (Domain: domain, Where: ".pen/" + ConfigLoader.FileName)))
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
                var taken = Allowlist.FindDenyCoverage(denied, domain) switch
                {
                    DenyCoverage.All => domain,
                    DenyCoverage.Part => "part of " + domain,
                    _ => null,
                };
                if (taken is null)
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
