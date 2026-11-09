using System.Net;
using System.Text.RegularExpressions;

namespace Pen;

/// <summary>The proxy's effective allowlist, and the blocked domains in a proxy log.</summary>
internal static partial class Allowlist
{
    /// <summary>A plain domain or a .domain wildcard, with at least two labels, not an IP address.</summary>
    public static bool IsValidDomain(string domain)
    {
        if (domain.Length > 254 || !DomainRegex().IsMatch(domain)) return false;
        var bare = domain.TrimStart('.');
        return !IPAddress.TryParse(bare, out _) && !bare.Split('.').All(l => l.All(char.IsAsciiDigit));
    }

    [GeneratedRegex(@"^\.?(?:[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z")]
    private static partial Regex DomainRegex();

    /// <summary>Domains from an allowlist file: one per line, '#' comments, blank lines ignored.</summary>
    public static IEnumerable<string> ParseList(string text) =>
        text.Split('\n')
            .Select(l => { int h = l.IndexOf('#'); return (h >= 0 ? l[..h] : l).Trim().ToLowerInvariant(); })
            .Where(l => l.Length > 0);

    /// <summary>
    /// Merges the sources in order, dropping duplicates and entries a wildcard
    /// already covers (".github.com" covers "github.com" and "api.github.com"),
    /// since Squid complains about overlapping dstdomain entries.
    /// </summary>
    public static List<string> Merge(params IEnumerable<string>[] sources)
    {
        var all = sources.SelectMany(s => s).Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).ToList();
        var wildcards = all.Where(d => d.StartsWith('.')).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var d in all)
        {
            if (!seen.Add(d)) continue;
            if (WildcardsOver(d).Any(w => w != d && wildcards.Contains(w))) continue;
            result.Add(d);
        }
        return result;
    }

    /// <summary>
    /// The wildcards that match <paramref name="d"/>, a domain or a wildcard:
    /// a.x.com is under .a.x.com, .x.com and .com. A few per name, so a list of any
    /// length is checked in time proportional to it.
    /// </summary>
    private static IEnumerable<string> WildcardsOver(string d)
    {
        var bare = d.TrimStart('.');
        for (int at = 0; ; at = bare.IndexOf('.', at) + 1)
        {
            yield return "." + bare[at..];
            if (bare.IndexOf('.', at) < 0) yield break;
        }
    }

    /// <summary>Whether <paramref name="host"/> is allowed by a merged list (exact, or under a wildcard).</summary>
    public static bool IsAllowed(IEnumerable<string> list, string host) =>
        IsAllowed(list.ToHashSet(StringComparer.Ordinal), host);

    /// <inheritdoc cref="IsAllowed(IEnumerable{string}, string)"/>
    public static bool IsAllowed(HashSet<string> list, string host) =>
        list.Contains(host) || WildcardsOver(host).Any(list.Contains);

    public static string Render(IEnumerable<string> domains) => string.Join('\n', domains) + "\n";

    /// <summary>
    /// Hosts the proxy denied, in first-seen order, from a log in squid.conf's
    /// "pen" format: time client TCP_DENIED/403 METHOD URL server.
    /// </summary>
    public static List<string> BlockedHosts(IEnumerable<string> logLines)
    {
        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal); // the agent decides how many hosts there are
        foreach (var line in logLines)
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 5 || !f[2].StartsWith("TCP_DENIED", StringComparison.Ordinal)) continue;
            var host = HostOf(f[4]);
            if (host.Length > 0 && seen.Add(host)) hosts.Add(host);
        }
        return hosts;
    }

    /// <summary>The host in "example.com:443" (CONNECT) or "http://example.com/x" (plain HTTP).</summary>
    internal static string HostOf(string url)
    {
        int scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) url = url[(scheme + 3)..];
        int slash = url.IndexOf('/');
        if (slash >= 0) url = url[..slash];
        int at = url.LastIndexOf('@');
        if (at >= 0) url = url[(at + 1)..];
        if (url.StartsWith('['))
        {
            int close = url.IndexOf(']');
            return close > 0 ? url[1..close] : url;
        }
        int colon = url.LastIndexOf(':');
        if (colon >= 0) url = url[..colon];
        return url.ToLowerInvariant();
    }

    /// <summary>
    /// How many blocked hosts a listing shows. The agent decides how many there
    /// are, and a listing that long would push earlier warnings out of sight.
    /// </summary>
    internal const int MaxListed = 20;
    private const int MaxHostShown = 80;

    /// <summary>A blocked host as listed: sanitized, and cut short if it's long (the agent chose it).</summary>
    internal static string ShownHost(string host) =>
        host.Length <= MaxHostShown ? Out.Field(host) : Out.Field(host[..(MaxHostShown - 1)]) + "…";

    /// <summary>
    /// The session-end line for <paramref name="hosts"/> (first blocked first), or
    /// null when nothing was blocked: the <see cref="MaxListed"/> most recent ones,
    /// how many more there were, and the pen allow line for the ones shown.
    /// </summary>
    public static string? BlockedLine(IReadOnlyList<string> hosts)
    {
        if (hosts.Count == 0) return null;
        var shown = hosts.Skip(Math.Max(0, hosts.Count - MaxListed)).ToList();
        var line = "blocked: " + string.Join(", ", shown.Select(ShownHost));
        if (hosts.Count > shown.Count) line += $" and {hosts.Count - shown.Count} more (pen allow lists the latest)";
        var allowable = shown.Where(IsValidDomain).ToList();
        if (allowable.Count > 0)
            line += $" → pen allow {string.Join(' ', allowable)} (add --global for all projects)";
        return line;
    }
}
