using Pen.Configuration;
using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;
using Pen.Tools;
using Pen.Trust;

namespace Pen.Proxy;

/// <summary>
/// Resolves what a session's proxy allows and denies: at session start, for pen allow's listing, and for a live
/// update of a running session.
/// </summary>
/// <remarks>
/// Each entry point takes its tools' domains from its own source, which decides what a tool can add: the tools
/// selected for the session starting, the configured tools approved now, or the domains a running session recorded
/// when it started. The allow lists merge the defaults, the user's allow, the project's allow as
/// <see cref="TrustGate.SelectAllowedDomains"/> takes it and those tool domains; the deny list merges the user's and
/// the project's deny, all of it, since deny needs no approval.
/// </remarks>
internal static class EgressPolicyResolver
{
    /// <summary>Resolves the policy of a session starting now.</summary>
    /// <param name="context">The project and both configs.</param>
    /// <param name="trust">The approvals the trust gate required for the session.</param>
    /// <param name="sessionTools">The tools selected for the session, merged and canonical.</param>
    /// <param name="catalog">The recipes that say which domains a tool brings.</param>
    public static EgressPolicy ForSessionStart(
        ProjectContext context,
        TrustStore trust,
        IReadOnlyList<ToolSpec> sessionTools,
        RecipeCatalog catalog)
    {
        var toolDomains = ToolSpec.ListDomains(sessionTools, catalog);
        // The same project domains pen allow applies live: what the gate let through, never on the user's "*" alone.
        var projectAllow = TrustGate.SelectAllowedDomains(context.User, context.ProjectConfig, trust);
        return Merge(context.User, context.ProjectConfig, projectAllow, toolDomains);
    }

    /// <summary>Resolves what a session started now would be allowed and denied, for pen allow's listing.</summary>
    /// <param name="context">The project and both configs.</param>
    /// <param name="catalog">The recipes that say which domains a tool brings.</param>
    /// <remarks>
    /// The tools are the user's and the project's approved ones, with no --tool or implied agent. Only approved
    /// domains and tools from .pen/ count, so an unapproved domain there stays listed.
    /// </remarks>
    public static EgressPolicy ForListing(ProjectContext context, RecipeCatalog catalog)
    {
        var store = TrustStore.For(context.Host, context.Project);
        var approved = TrustGate.SelectAllowedDomains(context.User, context.ProjectConfig, store);
        var toolDomains = ToolSpec.ListDomains(
            context.User.Tools.Concat(store.SelectApprovedTools(context.ProjectConfig.Tools, catalog)),
            catalog);
        return Merge(context.User, context.ProjectConfig, approved, toolDomains);
    }

    /// <summary>
    /// Resolves what <paramref name="session"/>, started in <paramref name="projectDir"/>, gets now, reading the
    /// config files.
    /// </summary>
    /// <param name="host">The host user's environment.</param>
    /// <param name="session">The running session.</param>
    /// <param name="projectDir">The dir it was started in.</param>
    /// <param name="catalog">The recipes the config files' tools come from.</param>
    /// <remarks>
    /// The tools' domains are the ones it started with (tool-domains), so a tools entry added to either config file
    /// since adds nothing.
    /// </remarks>
    /// <exception cref="PenException">A config file can't be read or parsed.</exception>
    public static EgressPolicy ForRunningSession(
        HostEnv host,
        SessionDir session,
        string projectDir,
        RecipeCatalog catalog)
    {
        var user = Config.Load(host.ConfigDir, host.Display(host.ConfigDir), ConfigKind.User, catalog);
        var projectConfig = Config.Load(Path.Combine(projectDir, ".pen"),
            Out.Field(Path.Combine(projectDir, ".pen")), ConfigKind.Project, catalog);
        var approved = TrustGate.SelectAllowedDomains(user, projectConfig,
            TrustStore.Load(host.GetRuntimeDir(session.ProjectKey), projectDir));
        return Merge(user, projectConfig, approved, session.ReadToolDomains());
    }

    /// <summary>Merges the allow sources and the deny lists.</summary>
    /// <param name="user">The user's config.</param>
    /// <param name="projectConfig">The project's config.</param>
    /// <param name="projectAllow">The project's allow entries a session may take.</param>
    /// <param name="toolDomains">The tools' domains, from the entry point's own source.</param>
    private static EgressPolicy Merge(
        Config user,
        Config projectConfig,
        IReadOnlyList<string> projectAllow,
        IReadOnlyList<string> toolDomains)
    {
        IEnumerable<string>[] sources = [Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt")), user.Allow,
            projectAllow, toolDomains];
        return new([.. Allowlist.Merge(sources)], [.. Allowlist.MergeNamed(sources)],
            [.. Allowlist.MergeDeny(user, projectConfig)], [.. toolDomains]);
    }
}

/// <summary>What a session's proxy allows and denies, merged from its sources.</summary>
/// <param name="Allow">The merged allow list; just <see cref="Allowlist.AnyDomain"/> when a source has it.</param>
/// <param name="NamedAllow">
/// The merged allow list without <see cref="Allowlist.AnyDomain"/>: the domains allowed by name, which reach a private
/// address even in open egress.
/// </param>
/// <param name="Deny">The merged deny list, which beats every allow.</param>
/// <param name="ToolDomains">The tools' domains that were merged into the allow lists.</param>
internal sealed record EgressPolicy(
    IReadOnlyList<string> Allow,
    IReadOnlyList<string> NamedAllow,
    IReadOnlyList<string> Deny,
    IReadOnlyList<string> ToolDomains);
