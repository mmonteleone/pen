using Pen.Platform;

namespace Pen.Trust;

/// <summary>
/// What a project's .pen/ asks for that isn't approved. ports isn't in it: they
/// only publish to the host's loopback.
/// </summary>
internal sealed record TrustRequest(
    IReadOnlyList<string> Domains,
    bool Docker,
    string? GitHubOwner,
    string? OriginOwner,
    LayerRequest? Layer)
{
    /// <summary>How many domains a prompt lists. An agent could add thousands to bury one, or the rest.</summary>
    public const int MaxListedDomains = 20;
    /// <summary>How many tools a prompt lists. A recipe with multiple = true takes any number of versions.</summary>
    public const int MaxListedTools = 20;

    /// <summary>Tools in .pen/ not approved, and not also asked for by the user's config or --tool.</summary>
    public IReadOnlyList<ToolRequest> Tools { get; init; } = [];

    public bool IsEmpty => Domains.Count == 0 && !Docker && GitHubOwner == null && Layer == null && Tools.Count == 0;

    private int HiddenDomains => Math.Max(Domains.Count - MaxListedDomains, 0);
    private int HiddenTools => Math.Max(Tools.Count - MaxListedTools, 0);

    /// <summary>
    /// One line for each item, indented, saying what it grants. The values come
    /// from .pen/, so they're escaped. Tools and domains come last, each with a limit, so a
    /// long list can't push another item out of sight; past it there's a count, and how to see them.
    /// </summary>
    public IEnumerable<string> Lines() =>
        Grants()
            .Concat(Tools.Take(MaxListedTools).Select(ToolLine))
            .Concat(HiddenTools > 0 ? [$"  ...and {HiddenTools} more tools: pen trust --list shows them all"] : [])
            .Concat(Domains.Take(MaxListedDomains).Select(DomainLine))
            .Concat(HiddenDomains > 0
                ? [$"  ...and {HiddenDomains} more domains: pen trust --list shows them all"]
                : []);

    /// <summary>Every item, with no limit, for pen trust --list.</summary>
    public IEnumerable<string> AllLines() => Grants().Concat(Tools.Select(ToolLine)).Concat(Domains.Select(DomainLine));

    /// <summary>The question to approve, which says so when it covers items not listed.</summary>
    public string Question
    {
        get
        {
            string[] hidden =
            [
                .. (HiddenTools > 0 ? new[] { $"{HiddenTools} tools" } : []),
                .. (HiddenDomains > 0 ? new[] { $"{HiddenDomains} domains" } : [])
            ];
            return hidden.Length > 0
                ? $"approve all of these, with the {string.Join(" and ", hidden)} not listed?"
                : "approve these?";
        }
    }

    /// <summary>What's being approved, as it was hashed; the live .pen/ may already say something else.</summary>
    private string LayerBody() => Layer!.CopyDir == null
        ? Layer.Dockerfile!
        : $"    The rest of what's being approved is in {Out.Field(Layer.CopyDir)} " +
            "until you answer, a copy made with this hash. "
          + "Read that, not .pen/, which may have changed since.\n" + Layer.Dockerfile;

    private static string DomainLine(string domain) => $"  allow {Out.Field(domain)}: the sandbox can reach it";

    private static string ToolLine(ToolRequest request)
    {
        var domains = string.Join(", ", request.Domains.Select(Out.Field));
        var name = Out.Field(request.Tool.ToString());
        return !request.IsNew ? $"  tool {name} now brings more domains: the sandbox can reach {domains}"
            : $"  tool {name}: sessions run with it installed"
                + (domains.Length > 0 ? $", and the sandbox can reach {domains}" : "");
    }

    private IEnumerable<string> Grants()
    {
        if (Layer != null)
        {
            yield return Layer.Hash == null
                ? $"  .pen/Dockerfile can't be approved: {Out.Field(Layer.Problem ?? "")}"
                : "  .pen/Dockerfile (new or changed): sessions run in " +
                    "the image it builds, and its build steps can use "
                  + "everything in .pen/ except config.toml and reach the network directly, past the proxy."
                  + (Layer.Dockerfile == null ? " pen trust shows it." : "\n" + LayerBody());
        }

        if (Docker)
            yield return "  docker = true: sessions get their own Docker daemon, running privileged";
        if (GitHubOwner != null)
        {
            yield return $"  github_owner = {Out.Field(GitHubOwner)}: sessions get this owner's GitHub token ("
                + (OriginOwner != null
                    ? $"origin's owner is {Out.Field(OriginOwner)})"
                    : "origin has no GitHub owner)");
        }
    }
}
