using System.Diagnostics;
using Pen.Platform;
using Pen.Proxy;

namespace Pen.Trust;

/// <summary>What a project's .pen/ asks for that isn't approved.</summary>
/// <param name="Domains">
/// The allow domains nothing approves yet, and <see cref="Allowlist.AnyDomain"/> when it's unapproved.
/// </param>
/// <param name="Docker">Whether docker = true needs approving.</param>
/// <param name="GitHubOwner">The github_owner that needs approving, or null.</param>
/// <param name="OriginOwner">The owner of origin, shown next to that github_owner, or null when it has none.</param>
/// <param name="Layer">The .pen/ that a .pen/Dockerfile builds from, when it needs approving, or null.</param>
/// <remarks>ports isn't in it: forwarded ports only publish to the host's loopback.</remarks>
internal sealed record TrustRequest(
    IReadOnlyList<string> Domains,
    bool Docker,
    string? GitHubOwner,
    string? OriginOwner,
    LayerRequest? Layer)
{
    /// <summary>How many domains a prompt lists.</summary>
    /// <remarks>An agent could add thousands to bury one, or the rest.</remarks>
    public const int MaxListedDomains = 20;

    /// <summary>How many tools a prompt lists.</summary>
    /// <remarks>A recipe with multiple = true takes any number of versions.</remarks>
    public const int MaxListedTools = 20;

    /// <summary>Tools in .pen/ not approved, and not also named by the user's config or --tool.</summary>
    public IReadOnlyList<ToolRequest> Tools { get; init; } = [];

    /// <summary>The allow domains nothing approves yet.</summary>
    /// <remarks>
    /// Without an init accessor, so <see cref="NamedDomains"/>, taken from it once, can't fall out of step.
    /// </remarks>
    public IReadOnlyList<string> Domains { get; } = Domains;

    /// <summary>Whether nothing needs approving.</summary>
    public bool IsEmpty => Domains.Count == 0 && !Docker && GitHubOwner is null && Layer is null && Tools.Count == 0;

    /// <summary>Whether <see cref="Allowlist.AnyDomain"/> needs approving: open egress.</summary>
    public bool AnyDomain => Domains.Contains(Allowlist.AnyDomain);

    /// <summary>The domains listed a line each: all but <see cref="Allowlist.AnyDomain"/>, which has its own.</summary>
    private IReadOnlyList<string> NamedDomains { get; } = [.. Domains.Where(domain => domain != Allowlist.AnyDomain)];

    private int HiddenDomainCount => Math.Max(NamedDomains.Count - MaxListedDomains, 0);
    private int HiddenToolCount => Math.Max(Tools.Count - MaxListedTools, 0);

    /// <summary>Formats one indented line for each item, saying what it grants, for a prompt.</summary>
    /// <remarks>
    /// The values come from .pen/, so they're escaped. Tools and domains come last, each with a limit, so a
    /// long list can't push another item out of sight; past it there's a count, and how to see them.
    /// </remarks>
    public IEnumerable<string> FormatLines()
    {
        foreach (var grant in DescribeGrants())
            yield return grant;
        foreach (var tool in Tools.Take(MaxListedTools))
            yield return FormatToolLine(tool);
        if (HiddenToolCount > 0)
            yield return $"  ...and {HiddenToolCount} more tools: pen trust --list shows them all";
        foreach (var domain in NamedDomains.Take(MaxListedDomains))
            yield return FormatDomainLine(domain);
        if (HiddenDomainCount > 0)
            yield return $"  ...and {HiddenDomainCount} more domains: pen trust --list shows them all";
        if (HasNamedDomainBesideAny)
            yield return NamedBesideAnyLine;
    }

    /// <summary>Formats every item, with no limit, for pen trust --list.</summary>
    public IEnumerable<string> FormatAllLines()
    {
        foreach (var line in DescribeGrants())
            yield return line;
        foreach (var tool in Tools)
            yield return FormatToolLine(tool);
        foreach (var domain in NamedDomains)
            yield return FormatDomainLine(domain);
        if (HasNamedDomainBesideAny)
            yield return NamedBesideAnyLine;
    }

    /// <summary>The question to approve, which says so when it covers items not listed.</summary>
    public string Question
    {
        get
        {
            var hidden = new List<string>();
            if (HiddenToolCount > 0)
                hidden.Add($"{HiddenToolCount} tools");
            if (HiddenDomainCount > 0)
                hidden.Add($"{HiddenDomainCount} domains");
            if (hidden.Count == 0)
                return "approve these?";
            return $"approve all of these, with the {string.Join(" and ", hidden)} not listed?";
        }
    }

    private bool HasNamedDomainBesideAny => AnyDomain && NamedDomains.Count > 0;

    /// <summary>Said after the domains when <see cref="Allowlist.AnyDomain"/> is requested beside named ones.</summary>
    internal const string NamedBesideAnyLine =
        "  domains allowed by name reach private addresses too, which * alone doesn't";

    private static string FormatDomainLine(string domain) => $"  allow {Out.Field(domain)}: the sandbox can reach it";

    /// <summary>The line for <see cref="Allowlist.AnyDomain"/>, which isn't a domain the sandbox can reach.</summary>
    internal const string AnyDomainLine = "  allow *: open egress. * allows ANY domain for this project's sessions "
        + "(raw IPs and names that resolve to private addresses stay blocked)";

    private static string FormatToolLine(ToolRequest request)
    {
        var domains = string.Join(", ", request.Domains.Select(Out.Field));
        var name = Out.Field(request.Tool.ToString());
        if (!request.IsNew)
            return $"  tool {name} now brings more domains: the sandbox can reach {domains}";
        if (domains.Length == 0)
            return $"  tool {name}: sessions run with it installed";
        return $"  tool {name}: sessions run with it installed, and the sandbox can reach {domains}";
    }

    /// <summary>
    /// Describes <see cref="Allowlist.AnyDomain"/> and the items other than tools and domains, one line each (the
    /// layer's has its text).
    /// </summary>
    /// <remarks>"*" comes first, never among the domains a long list cuts.</remarks>
    private IEnumerable<string> DescribeGrants()
    {
        if (AnyDomain)
            yield return AnyDomainLine;
        if (Layer is not null)
            yield return DescribeLayer(Layer);
        if (Docker)
            yield return "  docker = true: sessions get their own Docker daemon, running privileged";
        if (GitHubOwner is not null)
        {
            var origin = OriginOwner is not null
                ? $"origin's owner is {Out.Field(OriginOwner)})"
                : "origin has no GitHub owner)";
            yield return $"  github_owner = {Out.Field(GitHubOwner)}: sessions get this owner's GitHub token ("
                + origin;
        }
    }

    /// <summary>Describes what approving <paramref name="layer"/> grants, with its Dockerfile if known.</summary>
    private static string DescribeLayer(LayerRequest layer)
    {
        switch (layer)
        {
            case LayerRequest.Uncheckable(var problem):
                return $"  .pen/Dockerfile can't be approved: {Out.Field(problem)}";
            case LayerRequest.Unapproved unapproved:
                var grant = "  .pen/Dockerfile (new or changed): sessions run in " +
                    "the image it builds, and its build steps can use "
                    + "everything in .pen/ except config.toml and reach the network directly, past the proxy.";
                if (unapproved.Dockerfile is null)
                    return grant + " pen trust shows it.";
                return grant + "\n" + FormatLayerBody(unapproved.Dockerfile, unapproved.CopyDir);
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Formats what's being approved, as it was hashed: the Dockerfile, after where its copy is.</summary>
    /// <remarks>The live .pen/ may already say something else.</remarks>
    private static string FormatLayerBody(string dockerfile, string? copyDir)
    {
        if (copyDir is null)
            return dockerfile;
        return $"    The rest of what's being approved is in {Out.Field(copyDir)} " +
            "until you answer, a copy made with this hash. "
            + "Read that, not .pen/, which may have changed since.\n" + dockerfile;
    }
}
