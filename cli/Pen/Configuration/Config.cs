using Pen.Tools;

namespace Pen.Configuration;

/// <summary>
/// A read-only view of the settings either config.toml may hold, for code that still takes the user's and the
/// project's as one type.
/// </summary>
/// <remarks>
/// A <see cref="UserConfiguration"/> or <see cref="ProjectConfiguration"/> converts to it implicitly, sharing its
/// read-only collections; what only the other file holds is empty or null. A view of a project's settings never
/// has tokens.
/// </remarks>
internal sealed class Config
{
    private static readonly IReadOnlyDictionary<string, string> NoTokens =
        new Dictionary<string, string>(StringComparer.Ordinal).AsReadOnly();

    private Config(
        IReadOnlyList<string> allow,
        IReadOnlyList<string> deny,
        IReadOnlyList<ToolSpec> tools,
        string? gitHubOwner,
        IReadOnlyDictionary<string, string> gitHubTokens)
    {
        Allow = allow;
        Deny = deny;
        Tools = tools;
        GitHubOwner = gitHubOwner;
        GitHubTokens = gitHubTokens;
    }

    /// <summary>Gets the extra allowed domains, or "*" for any domain name, in lowercase.</summary>
    public IReadOnlyList<string> Allow { get; }
    /// <summary>Gets the denied domains, in lowercase.</summary>
    public IReadOnlyList<string> Deny { get; }
    /// <summary>Gets the tools, validated against the recipes and canonical.</summary>
    public IReadOnlyList<ToolSpec> Tools { get; }
    /// <summary>Gets the project's github_owner; null for the user's settings.</summary>
    public string? GitHubOwner { get; }
    /// <summary>Gets the user's [github] tokens, keyed by lowercased owner; none for a project's settings.</summary>
    public IReadOnlyDictionary<string, string> GitHubTokens { get; }

    public static implicit operator Config(UserConfiguration user) =>
        new(user.Allow, user.Deny, user.Tools, gitHubOwner: null, user.GitHubTokens);

    public static implicit operator Config(ProjectConfiguration project) =>
        new(project.Allow, project.Deny, project.Tools, project.GitHubOwner, NoTokens);
}
