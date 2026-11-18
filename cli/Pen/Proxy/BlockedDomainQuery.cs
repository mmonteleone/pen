using System.Globalization;
using Pen.Sessions;

namespace Pen.Proxy;

/// <summary>
/// Finds the hosts a project's sessions had blocked, in the proxy logs kept in its runtime dir, and groups them by
/// why a policy blocks them now.
/// </summary>
internal static class BlockedDomainQuery
{
    /// <summary>
    /// Finds the hosts blocked in the proxy logs of the sessions in <paramref name="runtimeDir"/>, and groups them
    /// by what <paramref name="policy"/> does with them.
    /// </summary>
    /// <param name="projectKey">The project's key.</param>
    /// <param name="runtimeDir">The project's runtime dir.</param>
    /// <param name="policy">What a session started now would be allowed and denied.</param>
    /// <returns>Each group newest last denial first; hosts the policy allows by name are left out.</returns>
    public static BlockedHosts Find(string projectKey, string runtimeDir, EgressPolicy policy)
    {
        var logs = Directory.EnumerateDirectories(Path.Combine(runtimeDir, "sessions"))
            .Select(dir => new SessionDir(projectKey, dir).ReadProxyLogLines());
        var named = policy.NamedAllow.ToHashSet(StringComparer.Ordinal);
        var open = policy.Allow.Contains(Allowlist.AnyDomain);
        var denied = policy.Deny.ToHashSet(StringComparer.Ordinal);
        // Grouped as the session-end lines are: deny beats every allow, "*" included, and in open egress a name it
        // blocked anyway (one resolving privately, say) is grouped apart, to be listed with no pen allow line.
        var groups = FindBlockedAcross(logs)
            .ToLookup(entry => Allowlist.ClassifyBlocked(entry.Host, named, open, denied));
        return new(
            NotAllowed: [.. groups[BlockedAs.NotAllowed]],
            Unreachable: [.. groups[BlockedAs.Unreachable]],
            Denied: [.. groups[BlockedAs.Denied]],
            Open: open);
    }

    /// <summary>Finds the denied hosts across <paramref name="logs"/>.</summary>
    /// <returns>Each host once, newest last denial first.</returns>
    /// <remarks>Lines are in squid.conf's "pen" format, whose first field is the UTC time.</remarks>
    internal static IReadOnlyList<BlockedHost> FindBlockedAcross(IEnumerable<IEnumerable<string>> logs)
    {
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var line in logs.SelectMany(log => log))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || !fields[2].StartsWith("TCP_DENIED", StringComparison.Ordinal))
                continue;
            var blockedHost = Allowlist.ParseHost(fields[4]);
            if (blockedHost.Length == 0)
                continue;
            if (!DateTimeOffset.TryParse(fields[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                    out var time))
            {
                time = DateTimeOffset.MinValue;
            }

            if (!last.TryGetValue(blockedHost, out var seen) || time > seen)
                last[blockedHost] = time;
        }
        return [.. last.Select(entry => new BlockedHost(entry.Key, entry.Value))
            .OrderByDescending(blocked => blocked.Last)
            .ThenBy(blocked => blocked.Host, StringComparer.Ordinal)];
    }
}

/// <summary>A blocked host and when it was last blocked.</summary>
/// <param name="Host">The host the proxy denied.</param>
/// <param name="Last">When it last denied it; <see cref="DateTimeOffset.MinValue"/> when that can't be read.</param>
internal sealed record BlockedHost(string Host, DateTimeOffset Last);

/// <summary>What <see cref="BlockedDomainQuery.Find"/> found, grouped by <see cref="BlockedAs"/>.</summary>
/// <param name="NotAllowed">What nothing allows: pen allow would let them through.</param>
/// <param name="Unreachable">
/// The raw IPs, and in open egress every other name not allowed by name: pen allow wouldn't let them through.
/// </param>
/// <param name="Denied">What deny covers now.</param>
/// <param name="Open">Whether the policy has <see cref="Allowlist.AnyDomain"/>.</param>
internal sealed record BlockedHosts(
    IReadOnlyList<BlockedHost> NotAllowed,
    IReadOnlyList<BlockedHost> Unreachable,
    IReadOnlyList<BlockedHost> Denied,
    bool Open);
