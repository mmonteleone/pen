using System.Net;
using System.Text.RegularExpressions;
using Pen.Platform;

namespace Pen.Proxy;

/// <summary>Parses, merges and matches the proxy's allowlist, and finds the blocked hosts in a proxy log.</summary>
internal static partial class Allowlist
{
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
    /// <returns>The merged list, lowercased.</returns>
    /// <remarks>
    /// ".github.com" covers "github.com" and "api.github.com"; Squid complains about overlapping dstdomain entries.
    /// </remarks>
    public static List<string> Merge(params IEnumerable<string>[] sources)
    {
        var all = sources.SelectMany(source => source)
            .Select(domain => domain.Trim().ToLowerInvariant())
            .Where(domain => domain.Length > 0)
            .ToList();
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

    /// <summary>
    /// Tells whether <paramref name="host"/> is allowed by a merged list (exact, or under a wildcard).
    /// </summary>
    public static bool IsAllowed(IEnumerable<string> list, string host) =>
        IsAllowed(list.ToHashSet(StringComparer.Ordinal), host);

    /// <inheritdoc cref="IsAllowed(IEnumerable{string}, string)"/>
    public static bool IsAllowed(HashSet<string> list, string host) =>
        list.Contains(host) || ListWildcardsOver(host).Any(list.Contains);

    /// <summary>Renders <paramref name="domains"/> as an allowlist file, one per line.</summary>
    public static string Render(IEnumerable<string> domains) => string.Join('\n', domains) + "\n";

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
        var shown = hosts.Skip(Math.Max(0, hosts.Count - MaxListed)).ToList();
        var line = "blocked: " + string.Join(", ", shown.Select(FormatShownHost));
        if (hosts.Count > shown.Count)
            line += $" and {hosts.Count - shown.Count} more (pen allow lists the latest)";
        var allowable = shown.Where(IsValidDomain).ToList();
        if (allowable.Count > 0)
            line += $" → pen allow {string.Join(' ', allowable)} (add --global for all projects)";
        return line;
    }
}
