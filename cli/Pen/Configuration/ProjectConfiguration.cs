using Pen.Masking;
using Pen.Tools;

namespace Pen.Configuration;

/// <summary>The settings in a project's .pen/config.toml, which may be missing.</summary>
/// <remarks>
/// <see cref="ConfigParser.ParseProject"/> makes one. Every collection is the configuration's own read-only copy, so
/// no consumer can change what another sees. .pen/ is agent-writable, so these are requests: <c>TrustGate</c>
/// decides which a session takes. The file can't hold secrets, so there's no place here for them.
/// </remarks>
internal sealed class ProjectConfiguration
{
    /// <summary>The settings of a missing or empty file.</summary>
    public static ProjectConfiguration Empty { get; } = new([], [], [], [], [], gitHubOwner: null, docker: null, []);

    /// <summary>Creates the settings, taking its own copies of the collections.</summary>
    /// <param name="allow">Extra allowed domains, or "*", lowercased, in file order.</param>
    /// <param name="deny">Denied domains, lowercased, in file order.</param>
    /// <param name="tools">The tools, validated against the recipes and canonical.</param>
    /// <param name="hide">The hide patterns, in file order.</param>
    /// <param name="replacements">The [replace] entries, in file order.</param>
    /// <param name="gitHubOwner">github_owner, if set.</param>
    /// <param name="docker">docker, if set.</param>
    /// <param name="ports">The ports, in file order without duplicates.</param>
    public ProjectConfiguration(
        IEnumerable<string> allow,
        IEnumerable<string> deny,
        IEnumerable<ToolSpec> tools,
        IEnumerable<HidePattern> hide,
        IEnumerable<FileReplacement> replacements,
        string? gitHubOwner,
        bool? docker,
        IEnumerable<int> ports)
    {
        Allow = [.. allow];
        Deny = [.. deny];
        Tools = [.. tools];
        Hide = [.. hide];
        Replacements = [.. replacements];
        GitHubOwner = gitHubOwner;
        Docker = docker;
        Ports = [.. ports];
    }

    /// <summary>Gets the extra allowed domains, or "*" for any domain name, in lowercase.</summary>
    public IReadOnlyList<string> Allow { get; }
    /// <summary>Gets the denied domains, in lowercase. A deny beats every allow.</summary>
    public IReadOnlyList<string> Deny { get; }
    /// <summary>Gets tools = ["name", "name:version", …], validated against the recipes and canonical.</summary>
    public IReadOnlyList<ToolSpec> Tools { get; }
    /// <summary>Gets hide = [patterns]: what the sandbox sees as empty, in file order.</summary>
    public IReadOnlyList<HidePattern> Hide { get; }
    /// <summary>
    /// Gets the [replace] entries, in file order. The sandbox sees a copy of each source over its target.
    /// </summary>
    public IReadOnlyList<FileReplacement> Replacements { get; }
    /// <summary>Gets which owner's token to use, or null when unset.</summary>
    public string? GitHubOwner { get; }
    /// <summary>Gets docker = true|false: Docker-in-Docker for every session; null when unset.</summary>
    public bool? Docker { get; }
    /// <summary>
    /// Gets ports = [3000, …]: ports to forward from the host's 127.0.0.1, in file order without duplicates.
    /// </summary>
    public IReadOnlyList<int> Ports { get; }
}

/// <summary>
/// One [replace] entry: the sandbox sees a copy of <paramref name="Source"/> over <paramref name="Target"/>.
/// </summary>
/// <param name="Target">The project-relative path of the file replaced.</param>
/// <param name="Source">The project-relative path of the file whose copy replaces it.</param>
internal sealed record FileReplacement(string Target, string Source);
