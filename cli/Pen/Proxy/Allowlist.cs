using System.Net;
using System.Text.RegularExpressions;
using Pen.Configuration;
using Pen.Platform;

namespace Pen.Proxy;

/// <summary>
/// Parses, merges and matches the proxy's allow and deny lists, renders its ACL files, and finds the blocked hosts
/// in a proxy log.
/// </summary>
internal static partial class Allowlist
{
    /// <summary>The allow entry for any domain name. It never covers a raw IP.</summary>
    public const string AnyDomain = "*";

    /// <summary>
    /// Tells whether <paramref name="domain"/> is a plain domain or a .domain wildcard, with at least two labels,
    /// and not an IP address.
    /// </summary>
    public static bool IsValidDomain(string domain)
    {
        if (domain.Length > 254 || !DomainRegex().IsMatch(domain))
            return false;
        var bare = domain.TrimStart('.');
        return !IPAddress.TryParse(bare, out _) && !bare.Split('.').All(label => label.All(char.IsAsciiDigit));
    }

    /// <summary>
    /// Tells whether <paramref name="entry"/> is valid in allow: a domain or <see cref="AnyDomain"/>.
    /// </summary>
    public static bool IsValidAllowEntry(string entry) => entry == AnyDomain || IsValidDomain(entry);

    [GeneratedRegex(@"^\.?(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z")]
    private static partial Regex DomainRegex();

    /// <summary>Parses the domains in an allowlist file: one per line, '#' comments, blank lines ignored.</summary>
    /// <returns>The domains, lowercased, in order.</returns>
    public static IEnumerable<string> ParseList(string text) =>
        text.Split('\n')
            .Select(line =>
            {
                int hash = line.IndexOf('#');
                return (hash >= 0 ? line[..hash] : line).Trim().ToLowerInvariant();
            })
            .Where(domain => domain.Length > 0);

    /// <summary>Merges the sources in order, dropping duplicates and entries a wildcard already covers.</summary>
    /// <returns>The merged list, lowercased; just <see cref="AnyDomain"/> when any source has it.</returns>
    /// <remarks>
    /// ".github.com" covers "github.com" and "api.github.com"; Squid complains about overlapping dstdomain entries.
    /// <see cref="AnyDomain"/> covers every other entry.
    /// </remarks>
    public static List<string> Merge(params IEnumerable<string>[] sources)
    {
        var all = sources.SelectMany(source => source)
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Where(domain => domain.Length > 0)
            .ToList();
        if (all.Contains(AnyDomain))
            return [AnyDomain];
        var wildcards = all.Where(domain => domain.StartsWith('.')).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var domain in all)
        {
            if (!seen.Add(domain))
                continue;
            if (ListWildcardsOver(domain).Any(wildcard => wildcard != domain && wildcards.Contains(wildcard)))
                continue;
            result.Add(domain);
        }
        return result;
    }

    /// <summary>Merges the sources as <see cref="Merge"/> does, leaving out <see cref="AnyDomain"/>.</summary>
    /// <returns>The domains the sources allow by name, which the proxy allows in open egress too.</returns>
    public static List<string> MergeNamed(params IEnumerable<string>[] sources) =>
        Merge(sources.Select(source => source.Where(domain => domain.Trim() != AnyDomain)).ToArray());

    /// <summary>Lists the wildcards that match <paramref name="domain"/>, a domain or a wildcard.</summary>
    /// <remarks>
    /// a.x.com is under .a.x.com, .x.com and .com. A few per name, so a list of any length is checked in time
    /// proportional to it.
    /// </remarks>
    private static IEnumerable<string> ListWildcardsOver(string domain)
    {
        var bare = domain.TrimStart('.');
        for (int at = 0; ; at = bare.IndexOf('.', at) + 1)
        {
            yield return "." + bare[at..];
            if (bare.IndexOf('.', at) < 0)
                yield break;
        }
    }

    /// <summary>Merges the deny lists of the user's and the project's config.</summary>
    /// <returns>The merged list, lowercased, as <see cref="Merge"/> gives it.</returns>
    /// <remarks>The project's deny needs no approval: it can only take egress away.</remarks>
    public static List<string> MergeDeny(Config user, Config project) => Merge(user.Deny, project.Deny);

    /// <summary>
    /// Tells whether <paramref name="host"/> is allowed by a merged list (exact, under a wildcard, or by
    /// <see cref="AnyDomain"/> when it isn't an IP).
    /// </summary>
    public static bool IsAllowed(IEnumerable<string> list, string host) =>
        IsAllowed(list.ToHashSet(StringComparer.Ordinal), host);

    /// <inheritdoc cref="IsAllowed(IEnumerable{string}, string)"/>
    public static bool IsAllowed(HashSet<string> list, string host) => IsCovered(list, host);

    /// <summary>Tells whether <paramref name="host"/> is denied by a merged list (exact or under a wildcard).</summary>
    public static bool IsDenied(IEnumerable<string> list, string host) =>
        IsDenied(list.ToHashSet(StringComparer.Ordinal), host);

    /// <inheritdoc cref="IsDenied(IEnumerable{string}, string)"/>
    public static bool IsDenied(HashSet<string> list, string host) => IsCovered(list, host);

    /// <summary>Tells whether an entry in <paramref name="list"/> covers <paramref name="host"/>.</summary>
    private static bool IsCovered(HashSet<string> list, string host) =>
        list.Contains(host)
            || ListWildcardsOver(host).Any(list.Contains)
            || (list.Contains(AnyDomain) && !IsIpLiteral(host));

    /// <summary>Tells whether <paramref name="host"/> is an IP address, in brackets or not, not a name.</summary>
    /// <remarks>
    /// Broad on purpose, since <see cref="AnyDomain"/> covers whatever isn't an IP: all-numeric labels (1.2.3.4.,
    /// 2130706433) and anything with a colon or a bracket count too, as no domain name has one.
    /// </remarks>
    internal static bool IsIpLiteral(string host)
    {
        if (host.AsSpan().IndexOfAny(":[]") >= 0)
            return true;
        return IPAddress.TryParse(host, out _) || host.Split('.').All(label => label.All(char.IsAsciiDigit));
    }

    /// <summary>Renders <paramref name="domains"/> as an allowlist file, one per line.</summary>
    public static string Render(IEnumerable<string> domains) => string.Join('\n', domains) + "\n";

    /// <summary>What a dstdomain ACL file (allowlist.txt, denylist.txt) with no entries holds.</summary>
    /// <remarks>
    /// Squid takes an empty ACL file with a warning. If it refused one, this would need an entry that matches
    /// nothing, such as .invalid (RFC 6761), which listings would then hide.
    /// </remarks>
    private const string NoDomains = "";
    /// <summary>What open-egress.txt holds when egress isn't open.</summary>
    /// <remarks>Empty, as <see cref="NoDomains"/> is; the regex that matches nothing would be a^.</remarks>
    private const string ClosedEgress = "";
    /// <summary>The dstdom_regex open-egress.txt holds for <see cref="AnyDomain"/>, which matches every name.</summary>
    private const string AnyNamePattern = ".";
    /// <summary>What open-egress.txt holds for <see cref="AnyDomain"/>.</summary>
    private const string OpenEgress = AnyNamePattern + "\n";

    /// <summary>Renders the proxy's three ACL files.</summary>
    /// <param name="namedAllow">The domains allowed by name, merged by <see cref="MergeNamed"/>.</param>
    /// <param name="open">Whether allow has <see cref="AnyDomain"/>.</param>
    /// <param name="deny">The merged deny list.</param>
    /// <returns>
    /// allowlist.txt with the domains allowed by name, open or not, since the proxy lets them reach what open egress
    /// doesn't (a private address); open-egress.txt matching any name when <paramref name="open"/>, otherwise
    /// nothing; denylist.txt with the denied domains.
    /// </returns>
    public static AclFiles RenderFiles(IReadOnlyList<string> namedAllow, bool open, IReadOnlyList<string> deny)
    {
        // A "*" in either would render as a domain to squid. Config rejects it in deny, and a merged allow list
        // that has it holds nothing else, so passing one here is a caller's mistake, not a list to cut down.
        if (deny.Any(domain => domain.Trim() == AnyDomain))
            throw new ArgumentException($"deny can't contain {AnyDomain}.", nameof(deny));
        if (namedAllow.Any(domain => domain.Trim() == AnyDomain))
            throw new ArgumentException($"the named allow list can't contain {AnyDomain}.", nameof(namedAllow));
        return new(
            AllowlistText: namedAllow.Count == 0 ? NoDomains : Render(namedAllow),
            DenylistText: deny.Count == 0 ? NoDomains : Render(deny),
            OpenEgressText: open ? OpenEgress : ClosedEgress);
    }

    /// <summary>Tells whether an open-egress.txt holding <paramref name="openEgressText"/> opens egress.</summary>
    /// <remarks>Only the line <see cref="RenderFiles"/> writes for <see cref="AnyDomain"/> does.</remarks>
    public static bool IsOpenEgress(string openEgressText) =>
        openEgressText.Split('\n').Any(line => line.Trim() == AnyNamePattern);

    /// <summary>Finds the hosts the proxy denied in <paramref name="logLines"/>.</summary>
    /// <returns>Each host once, in first-seen order.</returns>
    /// <remarks>The log is in squid.conf's "pen" format: time client TCP_DENIED/403 METHOD URL server.</remarks>
    public static List<string> FindBlockedHosts(IEnumerable<string> logLines)
    {
        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // the agent decides how many hosts there are
        foreach (var line in logLines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || !fields[2].StartsWith("TCP_DENIED", StringComparison.Ordinal))
                continue;
            var host = ParseHost(fields[4]);
            if (host.Length > 0 && seen.Add(host))
                hosts.Add(host);
        }
        return hosts;
    }

    /// <summary>Parses the host in "example.com:443" (CONNECT) or "http://example.com/x" (plain HTTP).</summary>
    /// <returns>The host, lowercased unless it's in brackets; empty when there's none.</returns>
    internal static string ParseHost(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
            url = url[(scheme + 3)..];
        int slash = url.IndexOf('/');
        if (slash >= 0)
            url = url[..slash];
        int at = url.LastIndexOf('@');
        if (at >= 0)
            url = url[(at + 1)..];
        if (url.StartsWith('['))
        {
            int close = url.IndexOf(']');
            return close > 0 ? url[1..close] : url;
        }
        int colon = url.LastIndexOf(':');
        if (colon >= 0)
            url = url[..colon];
        return url.ToLowerInvariant();
    }

    /// <summary>How many blocked hosts a listing shows.</summary>
    /// <remarks>
    /// The agent decides how many there are, and a listing that long would push earlier warnings out of sight.
    /// </remarks>
    internal const int MaxListed = 20;
    private const int MaxHostShown = 80;

    /// <summary>Formats a blocked host as listed: sanitized, and cut short if it's long (the agent chose it).</summary>
    internal static string FormatShownHost(string host) =>
        host.Length <= MaxHostShown ? Out.Field(host) : Out.Field(host[..(MaxHostShown - 1)]) + "…";

    /// <summary>Formats the session-end line for <paramref name="hosts"/>, first blocked first.</summary>
    /// <returns>
    /// The <see cref="MaxListed"/> most recent ones, how many more there were, and the pen allow line for the ones
    /// shown; null when nothing was blocked.
    /// </returns>
    public static string? FormatBlockedLine(IReadOnlyList<string> hosts)
    {
        if (hosts.Count == 0)
            return null;
        var (line, shown) = FormatHostList("blocked: ", hosts, " (pen allow lists the latest)");
        var allowable = shown.Where(IsValidDomain).ToList();
        if (allowable.Count > 0)
            line += $" → pen allow {string.Join(' ', allowable)} (add --global for all projects)";
        return line;
    }

    /// <summary>
    /// Formats the session-end lines for <paramref name="hosts"/>, first blocked first, as a session's ACL files
    /// say why each was blocked.
    /// </summary>
    /// <param name="hosts">The blocked hosts, in the order they were first blocked.</param>
    /// <param name="allow">
    /// What the session allows now: allowlist.txt's domains, allowed by name, plus <see cref="AnyDomain"/> when open.
    /// </param>
    /// <param name="deny">What the session denies now: denylist.txt.</param>
    /// <returns>
    /// With no deny and no <see cref="AnyDomain"/>, <see cref="FormatBlockedLine"/>'s line for the hosts not allowed
    /// now. Otherwise up to three lines: the denied hosts; the raw IPs, and in open egress every other name not
    /// allowed by name (it resolved privately or not at all, asked for another port, or was blocked before egress
    /// opened); and the hosts not allowed, which alone get the pen allow line. Hosts allowed by name now are left
    /// out, as they are with no deny. None when nothing is left to report.
    /// </returns>
    public static List<string> FormatBlockedLines(
        IReadOnlyList<string> hosts,
        HashSet<string> allow,
        HashSet<string> deny)
    {
        bool open = allow.Contains(AnyDomain);
        if (deny.Count == 0 && !open)
            return FormatBlockedLine(hosts.Where(host => !IsAllowed(allow, host)).ToList()) is { } only ? [only] : [];

        var named = open ? allow.Where(entry => entry != AnyDomain).ToHashSet(StringComparer.Ordinal) : allow;
        var denied = new List<string>();
        var unreachable = new List<string>();
        var notAllowed = new List<string>();
        foreach (var host in hosts)
        {
            switch (ClassifyBlocked(host, named, open, deny))
            {
                case BlockedAs.Denied:
                    denied.Add(host);
                    break;
                case BlockedAs.Unreachable:
                    unreachable.Add(host);
                    break;
                case BlockedAs.NotAllowed:
                    notAllowed.Add(host);
                    break;
            }
        }

        var lines = new List<string>();
        if (denied.Count > 0)
            lines.Add(FormatHostList("denied by config: ", denied, " (pen allow lists the latest)").Line);
        if (unreachable.Count > 0)
            lines.Add(FormatHostList($"blocked ({DescribeUnreachable(open)}): ", unreachable, "").Line);
        if (FormatBlockedLine(notAllowed) is { } line)
            lines.Add(line);
        return lines;
    }

    /// <summary>Says why a blocked host is in the group of <see cref="ClassifyBlocked"/>'s Unreachable.</summary>
    /// <param name="open">Whether egress is open.</param>
    /// <remarks>
    /// The log doesn't say which; a name blocked before egress opened is among them too. Of the rest, pen allow
    /// would let through only a name that resolves privately, which open egress keeps out on purpose, so it isn't
    /// offered.
    /// </remarks>
    internal static string DescribeUnreachable(bool open) =>
        open ? "raw IP, private address, unresolvable name, another port or blocked earlier" : "raw IP";

    /// <summary>
    /// Tells why <paramref name="host"/>, which a proxy blocked, is blocked now, as session-end lines and the pen
    /// allow listing group it.
    /// </summary>
    /// <param name="host">The blocked host.</param>
    /// <param name="named">What's allowed by name, without <see cref="AnyDomain"/>.</param>
    /// <param name="open">Whether allow has <see cref="AnyDomain"/>.</param>
    /// <param name="deny">What's denied.</param>
    /// <returns>
    /// Denied when deny covers it, whatever allows it; AllowedNow when it's allowed by name; with neither deny nor
    /// open egress, NotAllowed for the rest. Otherwise Unreachable for a raw IP, and in open egress for every name
    /// left, and NotAllowed for the rest, which alone pen allow would let through.
    /// </returns>
    internal static BlockedAs ClassifyBlocked(string host, HashSet<string> named, bool open, HashSet<string> deny)
    {
        if (IsDenied(deny, host))
            return BlockedAs.Denied;
        if (IsAllowed(named, host))
            return BlockedAs.AllowedNow;
        if (deny.Count == 0 && !open)
            return BlockedAs.NotAllowed;
        return open || IsIpLiteral(host) ? BlockedAs.Unreachable : BlockedAs.NotAllowed;
    }

    /// <summary>
    /// Formats <paramref name="prefix"/> and the <see cref="MaxListed"/> most recent of <paramref name="hosts"/>,
    /// with how many more there were and <paramref name="moreNote"/>.
    /// </summary>
    /// <returns>The text, and the hosts shown.</returns>
    private static (string Line, List<string> Shown) FormatHostList(
        string prefix,
        IReadOnlyList<string> hosts,
        string moreNote)
    {
        var shown = hosts.Skip(Math.Max(0, hosts.Count - MaxListed)).ToList();
        var line = prefix + string.Join(", ", shown.Select(FormatShownHost));
        if (hosts.Count > shown.Count)
            line += $" and {hosts.Count - shown.Count} more{moreNote}";
        return (line, shown);
    }
}

/// <summary>Why a host a proxy blocked is blocked now (<see cref="Allowlist.ClassifyBlocked"/>).</summary>
internal enum BlockedAs
{
    /// <summary>Deny covers it.</summary>
    Denied,

    /// <summary>It's allowed by name now, so it isn't reported.</summary>
    AllowedNow,

    /// <summary>It's a raw IP, or in open egress a name it didn't let through: pen allow wouldn't help.</summary>
    Unreachable,

    /// <summary>Nothing allows it: pen allow would let it through.</summary>
    NotAllowed,
}

/// <summary>The contents of the proxy's ACL files: allowlist.txt, denylist.txt and open-egress.txt.</summary>
internal sealed record AclFiles(string AllowlistText, string DenylistText, string OpenEgressText);
