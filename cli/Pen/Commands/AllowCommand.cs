using System.Globalization;
using Pen.Configuration;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Sessions;
using Pen.Tools;
using Pen.Trust;

namespace Pen.Commands;

/// <summary>
/// Runs pen allow: adds domains to allow in a config.toml and applies them to running sessions, or lists the
/// recently blocked domains.
/// </summary>
/// <param name="host">The host user's environment.</param>
/// <param name="startDir">The directory Pen was started in, where the project is.</param>
/// <param name="catalog">The recipes that say which domains a tool brings.</param>
/// <param name="clock">The clock waits for another pen allow or pen deny are timed by.</param>
internal sealed class AllowCommand(HostEnv host, StartDir startDir, RecipeCatalog catalog, TimeProvider clock)
{
    /// <summary>Adds <paramref name="domains"/> to allow and applies them to running sessions.</summary>
    /// <param name="domains">The domains to allow (lowercased), or <see cref="Allowlist.AnyDomain"/>.</param>
    /// <param name="global">
    /// Add to the user's config.toml and apply to every project's sessions (--global), rather than to .pen/ and
    /// the sessions started in this directory.
    /// </param>
    /// <returns>0, or 1 when applying to a running session failed; a failure to add throws.</returns>
    public int Run(IReadOnlyList<string> domains, bool global)
    {
        var target = EgressTarget.Resolve(host, startDir, global);
        target.PrepareProjectAndRecover(host, clock);
        var addition = AddAllowed(host, target, domains, catalog);
        EgressMessages.WriteAdded(addition, domains);
        EgressMessages.WriteGlobHint("allow", startDir, addition);
        WarnIfDenied(target.CurrentProject, domains);

        var results = target.ApplyToRunningSessions(host, new SessionAclUpdater(host, catalog, clock));
        return results.Any(result => result is AclUpdateResult.Failed) ? 1 : 0;
    }

    /// <summary>
    /// Adds <paramref name="domains"/> to allow, as <see cref="EgressTarget.AddToConfig"/> does, approving them for
    /// the project when <paramref name="target"/> is its .pen/.
    /// </summary>
    /// <returns>The file, as messages name it, and the domains that weren't in its allow yet.</returns>
    /// <remarks>
    /// The user typed them, so they're approved, also if they were already in the file;
    /// <see cref="Allowlist.AnyDomain"/> too.
    /// </remarks>
    internal static ConfigAddition AddAllowed(
        HostEnv host,
        EgressTarget target,
        IReadOnlyList<string> domains,
        RecipeCatalog catalog)
    {
        var addition = target.AddToConfig(host, ConfigListKey.Allow, domains, catalog);
        if (target is EgressTarget.InProject { Project: var project })
            TrustStore.For(host, project).ApproveDomains(domains);
        return addition;
    }

    /// <summary>Warns about each of <paramref name="domains"/> a deny in either config file still covers.</summary>
    /// <remarks>A config file that can't be read now is left out: applying to a running session reports it.</remarks>
    private void WarnIfDenied(Project? project, IReadOnlyList<string> domains)
    {
        var denies = new List<ConfigDenyList>();
        try
        {
            denies.Add(new(host.Display(host.UserConfigPath),
                ConfigLoader.LoadUser(host.ConfigDir, host.Display(host.ConfigDir), catalog).Deny));
            if (project is not null)
            {
                denies.Add(new(".pen/" + ConfigLoader.FileName,
                    ConfigLoader.LoadProject(project.PenDir, ".pen", catalog).Deny));
            }
        }
        catch (PenException)
        {
            // The loaders' refusals are the only statement of a readable config, so a refusal here means "warn from
            // what could be read": the warning is advisory, and applying to a running session reports the failure.
        }

        foreach (var line in FormatDenyWins(denies, domains))
            Out.WriteLine(line);
    }

    /// <summary>Formats the warning for each of <paramref name="domains"/> a deny list covers.</summary>
    /// <param name="denies">Each config file's deny list, with the file as messages name it.</param>
    /// <param name="domains">The domains just allowed.</param>
    /// <returns>One line per covered domain, naming the files whose deny covers it.</returns>
    /// <remarks><see cref="Allowlist.AnyDomain"/> is left out: deny is meant to narrow it.</remarks>
    internal static IReadOnlyList<string> FormatDenyWins(
        IReadOnlyList<ConfigDenyList> denies,
        IReadOnlyList<string> domains)
    {
        var lines = new List<string>();
        foreach (var domain in domains.Where(domain => domain != Allowlist.AnyDomain))
        {
            var by = denies.Where(file => Allowlist.IsDenied(file.Deny, domain)).Select(file => file.Where).ToList();
            if (by.Count > 0)
                lines.Add($"{domain} is also denied by {string.Join(" and ", by)}; deny wins");
        }
        return lines;
    }

    /// <summary>Formats pen allow --defaults: the built-in allowlist from its first section on.</summary>
    /// <remarks>
    /// The section comments stay, since they say why each domain is there; the header about Squid's file format
    /// goes.
    /// </remarks>
    internal static string FormatDefaults()
    {
        var lines = Embedded.ReadText("proxy/allowlist.txt").Split('\n');
        var first = Array.FindIndex(lines, line => line.StartsWith("# ---", StringComparison.Ordinal));
        return string.Join('\n', lines[Math.Max(first, 0)..]).TrimEnd('\n') + "\n";
    }

    /// <summary>
    /// Runs pen allow without domains: lists what the project's sessions had blocked and isn't allowed now, apart
    /// from it what nothing pen allow adds would let through, and what deny covers now.
    /// </summary>
    /// <param name="context">The project and both configs, which say what a session would be allowed.</param>
    /// <returns>0.</returns>
    public int ListBlocked(ProjectContext context)
    {
        var project = context.Project;
        var runtimeDir = host.GetRuntimeDir(project.Key);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        // The listing only reads proxy logs and runs no git, so a check that didn't finish only warns.
        _ = SessionRecovery.FinishAbandoned(project.Key, runtimeDir, clock);

        var policy = EgressPolicyResolver.ForListing(context, catalog);
        var found = BlockedDomainQuery.Find(project.Key, runtimeDir, policy);
        if (found.NotAllowed.Count == 0 && found.Unreachable.Count == 0 && found.Denied.Count == 0)
        {
            Out.WriteStdout("nothing blocked in this project's sessions (last 7 days) that isn't allowed now\n");
            return 0;
        }

        if (found.NotAllowed.Count > 0)
            WriteBlocked(found.NotAllowed);
        if (found.Unreachable.Count > 0)
        {
            WriteHosts(
                $"blocked in this project's sessions ({Allowlist.DescribeUnreachable(found.Open)}), newest first:",
                found.Unreachable);
        }
        if (found.Denied.Count > 0)
            WriteDenied(found.Denied);
        return 0;
    }

    /// <summary>Writes the newest of <paramref name="blocked"/> to stdout, and the pen allow line for them.</summary>
    /// <remarks>Only the newest few: the agent decides how many there are.</remarks>
    private static void WriteBlocked(IReadOnlyList<BlockedHost> blocked)
    {
        var shown = WriteHosts("blocked in this project's sessions, newest first:", blocked);
        var allowable = shown.Select(entry => entry.Host).Where(Allowlist.IsValidDomain).ToList();
        if (allowable.Count > 0)
            Out.WriteStdout($"pen allow {string.Join(' ', allowable)}   (add --global for all projects)\n");
    }

    /// <summary>Writes the newest of <paramref name="denied"/>, which deny covers now, to stdout.</summary>
    /// <remarks>No pen allow line: deny beats every allow, so it wouldn't let them through.</remarks>
    private static void WriteDenied(IReadOnlyList<BlockedHost> denied) =>
        WriteHosts("denied by config, blocked in this project's sessions, newest first:", denied);

    /// <summary>Writes <paramref name="heading"/> and the newest of <paramref name="hosts"/> to stdout.</summary>
    /// <returns>The ones shown.</returns>
    /// <remarks>Only the newest few: the agent decides how many there are.</remarks>
    private static List<BlockedHost> WriteHosts(string heading, IReadOnlyList<BlockedHost> hosts)
    {
        var shown = hosts.Take(Allowlist.MaxListed).ToList();
        Out.WriteStdout(heading + "\n");
        foreach (var entry in shown)
        {
            var when = entry.Last == DateTimeOffset.MinValue
                ? ""
                : entry.Last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Out.WriteStdout($"  {Allowlist.FormatShownHost(entry.Host),-40} {when}\n");
        }
        if (hosts.Count > shown.Count)
            Out.WriteStdout($"  and {hosts.Count - shown.Count} more, blocked earlier\n");
        return shown;
    }
}

/// <summary>One config file's deny list, for <see cref="AllowCommand.FormatDenyWins"/>.</summary>
/// <param name="Where">The config.toml, as messages name it.</param>
/// <param name="Deny">Its deny list.</param>
internal sealed record ConfigDenyList(string Where, IReadOnlyList<string> Deny);
