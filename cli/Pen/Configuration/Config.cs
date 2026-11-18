using Pen.Masking;
using Pen.Tools;

namespace Pen.Configuration;

/// <summary>
/// The settings in one config.toml: ~/.config/pen/'s or a project's .pen/'s, which may be missing.
/// </summary>
/// <remarks>
/// <see cref="ConfigLoader"/> reads one from disk and <see cref="ConfigParser"/> fills it in. Keys are either
/// user-only or project-only, except allow, deny, tools and hide, whose lists are appended (allow: defaults,
/// user, project; deny: user, project; tools: user, project, --tool; hide: user, project).
/// </remarks>
internal sealed class Config
{
    /// <summary>[github] tokens, keyed by lowercased owner (user only).</summary>
    public Dictionary<string, string> GitHubTokens { get; } = new(StringComparer.Ordinal);
    /// <summary>[env] values for the agent service, in file order (user only).</summary>
    public List<KeyValuePair<string, string>> Env { get; } = [];
    /// <summary>Extra allowed domains, or "*" for any domain name, in lowercase (both).</summary>
    public List<string> Allow { get; } = [];
    /// <summary>Denied domains, normalized to lowercase (both). A deny beats every allow.</summary>
    public List<string> Deny { get; } = [];
    /// <summary>Which owner's token to use (project only).</summary>
    public string? GitHubOwner { get; set; }
    /// <summary>docker = true|false: Docker-in-Docker for every session (project only); null when unset.</summary>
    public bool? Docker { get; set; }
    /// <summary>
    /// ports = [3000, …]: ports to forward from the host's 127.0.0.1, in file order without duplicates (project only).
    /// </summary>
    public List<int> Ports { get; } = [];
    /// <summary>tools = ["name", "name:version", …], validated against the recipes and canonical (both).</summary>
    public IReadOnlyList<ToolSpec> Tools { get; set; } = [];
    /// <summary>hide = [patterns]: what the sandbox sees as empty, in file order (both).</summary>
    public List<HidePattern> Hide { get; } = [];
    /// <summary>
    /// [replace] target = source: project-relative paths, in file order (project only). The sandbox sees a copy
    /// of each source over its target.
    /// </summary>
    public List<KeyValuePair<string, string>> Replace { get; } = [];

    public static Config Empty { get; } = new();
}
