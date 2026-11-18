using Pen.Masking;
using Pen.Tools;

namespace Pen.Configuration;

/// <summary>The settings in the user's ~/.config/pen/config.toml, which may be missing.</summary>
/// <remarks>
/// <see cref="ConfigParser.ParseUser"/> makes one. Every collection is the configuration's own read-only copy, so
/// no consumer can change what another sees. A class rather than a record: the tokens and env values are secrets,
/// and a generated ToString would print them.
/// </remarks>
internal sealed class UserConfiguration
{
    /// <summary>The settings of a missing or empty file.</summary>
    public static UserConfiguration Empty { get; } = new([], [], [], [], [], []);

    /// <summary>Creates the settings, taking its own copies of the collections.</summary>
    /// <param name="allow">Extra allowed domains, or "*", lowercased, in file order.</param>
    /// <param name="deny">Denied domains, lowercased, in file order.</param>
    /// <param name="tools">The tools, validated against the recipes and canonical.</param>
    /// <param name="hide">The hide patterns, in file order.</param>
    /// <param name="gitHubTokens">The [github] tokens, keyed by lowercased owner, with no key twice.</param>
    /// <param name="env">The [env] values, in file order.</param>
    public UserConfiguration(
        IEnumerable<string> allow,
        IEnumerable<string> deny,
        IEnumerable<ToolSpec> tools,
        IEnumerable<HidePattern> hide,
        IEnumerable<KeyValuePair<string, string>> gitHubTokens,
        IEnumerable<KeyValuePair<string, string>> env)
    {
        Allow = [.. allow];
        Deny = [.. deny];
        Tools = [.. tools];
        Hide = [.. hide];
        GitHubTokens = new Dictionary<string, string>(gitHubTokens, StringComparer.Ordinal).AsReadOnly();
        Env = [.. env];
    }

    /// <summary>Gets the extra allowed domains, or "*" for any domain name, in lowercase.</summary>
    public IReadOnlyList<string> Allow { get; }
    /// <summary>Gets the denied domains, in lowercase. A deny beats every allow.</summary>
    public IReadOnlyList<string> Deny { get; }
    /// <summary>Gets tools = ["name", "name:version", …], validated against the recipes and canonical.</summary>
    public IReadOnlyList<ToolSpec> Tools { get; }
    /// <summary>Gets hide = [patterns]: what the sandbox sees as empty, in file order.</summary>
    public IReadOnlyList<HidePattern> Hide { get; }
    /// <summary>Gets the [github] tokens, keyed by lowercased owner and compared ordinally.</summary>
    public IReadOnlyDictionary<string, string> GitHubTokens { get; }
    /// <summary>Gets the [env] values for the agent service, in file order.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Env { get; }
}
