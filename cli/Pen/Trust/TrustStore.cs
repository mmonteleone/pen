using Pen.Configuration;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Tools;

namespace Pen.Trust;

/// <summary>The approvals of what .pen/ asks for, for one directory.</summary>
/// <remarks>
/// <para>
/// They're in &lt;runtime dir&gt;/trust-&lt;hash(dir)&gt;: out of the sandbox's reach, and per directory, so a
/// worktree the agent made starts with none. A store is immutable; approving returns the store as written.
/// </para>
/// <para>
/// The file is plain text, a line for each approval. Every value is checked to be a strict shape, when
/// written and when read, so none can hold a line break and add an approval. Anything else in the file
/// means it's corrupt, and a corrupt store approves nothing. The file names its directory because
/// hash(dir) is short enough to collide on purpose.
/// </para>
/// </remarks>
internal sealed class TrustStore
{
    private const string Header = "pen-trust 1";

    private readonly string _path;
    private readonly string _dir;
    private readonly HashSet<string> _domains = new(StringComparer.Ordinal);

    /// <summary>Whether allow = ["*"] is approved.</summary>
    /// <remarks>
    /// Kept apart from <see cref="_domains"/>, so it covers only the project's own "*" entry: a domain allowed by
    /// name reaches private addresses, which open egress doesn't.
    /// </remarks>
    private bool _anyDomain;
    private readonly HashSet<string> _owners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tools = new(StringComparer.Ordinal);

    /// <summary>The domains approved for a tool, as "name:version domain".</summary>
    /// <remarks>Kept apart from <see cref="_domains"/>, so neither approves for the other.</remarks>
    private readonly HashSet<string> _toolDomains = new(StringComparer.Ordinal);

    /// <summary>Whether docker = true is approved.</summary>
    public bool Docker { get; private set; }

    /// <summary>The approved hash of .pen/ (see <see cref="PenDirHash"/>), or null.</summary>
    public string? LayerHash { get; private set; }

    private TrustStore(string path, string dir)
    {
        _path = path;
        _dir = dir;
    }

    /// <summary>Gets the path of the store for <paramref name="dir"/> in <paramref name="runtimeDir"/>.</summary>
    public static string GetPath(string runtimeDir, string dir) =>
        Path.Combine(runtimeDir, $"trust-{Project.HashPath(dir)}");

    /// <summary>Loads the approvals for <paramref name="project"/>'s directory.</summary>
    /// <returns>The store; with no approvals if its file is missing, unreadable or corrupt.</returns>
    public static TrustStore For(HostEnv host, Project project) => Load(host.GetRuntimeDir(project.Key), project.Dir);

    /// <summary>Loads the approvals for <paramref name="dir"/> from <paramref name="runtimeDir"/>.</summary>
    /// <returns>The store; with no approvals if its file is missing, unreadable or corrupt.</returns>
    /// <remarks>Doesn't throw for any of those.</remarks>
    public static TrustStore Load(string runtimeDir, string dir) => Read(GetPath(runtimeDir, dir), dir);

    private static TrustStore Read(string path, string dir)
    {
        var text = Native.ReadRegularFileOrNull(path);
        return (text is null ? null : Parse(text, path, dir)) ?? new TrustStore(path, dir);
    }

    /// <summary>Parses the store file's <paramref name="text"/>.</summary>
    /// <returns>The store, or null if the text is corrupt in any way, or names another directory.</returns>
    private static TrustStore? Parse(string text, string path, string dir)
    {
        if (!text.EndsWith('\n'))
            return null;
        var lines = text[..^1].Split('\n');
        if (lines.Length < 2 || lines[0] != Header || lines[1] != "dir " + dir)
            return null;

        var store = new TrustStore(path, dir);
        foreach (var line in lines.Skip(2))
        {
            if (!store.TryAddLine(line))
                return null;
        }
        return store;
    }

    /// <summary>Adds the approval on one <paramref name="line"/> of the file.</summary>
    /// <returns>False if the line isn't an approval in its strict shape, or a second layer hash.</returns>
    private bool TryAddLine(string line)
    {
        var space = line.IndexOf(' ');
        var kind = space < 0 ? line : line[..space];
        var value = space < 0 ? null : line[(space + 1)..];
        switch (kind)
        {
            case "allow" when value is not null && IsAllowEntry(value):
                AddAllowEntry(value);
                return true;
            case "github_owner" when value is not null && IsOwner(value):
                _owners.Add(value);
                return true;
            case "tool" when value is not null && ToolSpec.IsCanonicalText(value):
                _tools.Add(value);
                return true;
            case "tool-allow" when value is not null && IsToolDomain(value):
                _toolDomains.Add(value);
                return true;
            case "docker" when value is null:
                Docker = true;
                return true;
            case "layer" when value is not null && IsHash(value) && LayerHash is null:
                LayerHash = value;
                return true;
            default:
                return false;
        }
    }

    private static bool IsDomain(string value) => Allowlist.IsValidDomain(value);

    /// <summary>
    /// Whether <paramref name="value"/> may be an allow line's: a domain, or <see cref="Allowlist.AnyDomain"/>.
    /// </summary>
    /// <remarks>Only allow lines take "*": a tool-allow line's domain is always a domain.</remarks>
    private static bool IsAllowEntry(string value) => Allowlist.IsValidAllowEntry(value);

    private void AddAllowEntry(string entry)
    {
        if (entry == Allowlist.AnyDomain)
            _anyDomain = true;
        else
            _domains.Add(entry);
    }

    private static bool IsOwner(string value) =>
        TomlReader.OwnerRegex().IsMatch(value) && value == value.ToLowerInvariant();

    private static bool IsToolDomain(string value) =>
        value.Split(' ') is [var tool, var domain] && ToolSpec.IsCanonicalText(tool) && IsDomain(domain);

    private static bool IsHash(string value) => value.Length == 64 && value.All(c => char.IsAsciiHexDigitLower(c));

    /// <summary>
    /// Whether the allow entry <paramref name="domain"/> is approved as written, or under an approved wildcard.
    /// </summary>
    /// <remarks>
    /// example.com doesn't cover .example.com. An approved <see cref="Allowlist.AnyDomain"/> covers that entry and
    /// nothing else, so it approves no domain by name.
    /// </remarks>
    public bool CoversDomain(string domain) =>
        domain == Allowlist.AnyDomain ? _anyDomain : Allowlist.IsAllowed(_domains, domain);

    /// <summary>Whether <paramref name="owner"/> is approved as a github_owner, ignoring case.</summary>
    public bool CoversOwner(string owner) => _owners.Contains(owner.ToLowerInvariant());

    /// <summary>Whether <paramref name="tool"/> is approved at exactly its version.</summary>
    /// <remarks>24.04 doesn't cover 24.4.</remarks>
    public bool CoversTool(ToolSpec tool) => _tools.Contains(tool.ToString());

    /// <summary>Whether <paramref name="domain"/> is approved for <paramref name="tool"/>, as written.</summary>
    /// <remarks>An allow domain or a wildcard doesn't count.</remarks>
    public bool CoversToolDomain(ToolSpec tool, string domain) => _toolDomains.Contains($"{tool} {domain}");

    /// <summary>Selects the project tools approved along with every domain they bring.</summary>
    public IReadOnlyList<ToolSpec> SelectApprovedTools(IEnumerable<ToolSpec> tools, RecipeCatalog catalog) =>
        [.. tools.Where(tool => CoversTool(tool)
                && ToolSpec.ListDomains([tool], catalog).All(domain => CoversToolDomain(tool, domain)))];

    /// <summary>Records everything in <paramref name="request"/>.</summary>
    /// <returns>The store as written.</returns>
    /// <exception cref="PenException">When its layer has a problem, so it can't be approved.</exception>
    public TrustStore Approve(TrustRequest request)
    {
        if (request.Layer is LayerRequest.Uncheckable(var problem))
            throw new PenException($"can't approve .pen/: {problem}");
        return SaveWith(
            domains: request.Domains,
            owner: request.GitHubOwner,
            docker: request.Docker,
            layerHash: (request.Layer as LayerRequest.Unapproved)?.Hash,
            tools: request.Tools);
    }

    /// <summary>Records domains the user typed.</summary>
    /// <returns>The store as written.</returns>
    public TrustStore ApproveDomains(IEnumerable<string> domains) =>
        SaveWith(domains: domains, owner: null, docker: false, layerHash: null, tools: []);

    /// <summary>Saves the file's current approvals together with these.</summary>
    /// <returns>The store as written.</returns>
    /// <exception cref="PenException">When a value isn't its strict shape: then nothing is written.</exception>
    /// <remarks>
    /// It adds to what the file holds now, since another Pen may have approved something since this store
    /// was read (losing that would only ask again), and replaces it by rename, so it's never seen
    /// half-written.
    /// </remarks>
    private TrustStore SaveWith(
        IEnumerable<string> domains,
        string? owner,
        bool docker,
        string? layerHash,
        IEnumerable<ToolRequest> tools)
    {
        var next = Read(_path, _dir);
        next.AddDomains(domains);
        if (owner is not null)
            next.AddOwner(owner);
        if (layerHash is not null)
            next.AddLayerHash(layerHash);
        foreach (var request in tools)
            next.AddTool(request);
        next.Docker |= docker;
        if (_dir.Contains('\n'))
            throw new PenException("can't record approvals for a directory whose path contains a newline");
        next.WriteFile();
        return next;
    }

    private void AddDomains(IEnumerable<string> domains)
    {
        foreach (var domain in domains)
        {
            if (!IsAllowEntry(domain))
                throw new PenException($"can't approve {Out.Field(domain)}: it isn't a domain");
            AddAllowEntry(domain);
        }
    }

    private void AddOwner(string owner)
    {
        var lower = owner.ToLowerInvariant();
        if (!IsOwner(lower))
            throw new PenException($"can't approve {Out.Field(owner)}: it isn't a GitHub owner name");
        _owners.Add(lower);
    }

    private void AddLayerHash(string layerHash)
    {
        if (!IsHash(layerHash))
            throw new PenException($"can't approve {Out.Field(layerHash)}: it isn't a hash");
        LayerHash = layerHash;
    }

    private void AddTool(ToolRequest request)
    {
        var text = request.Tool.ToString();
        if (!ToolSpec.IsCanonicalText(text))
            throw new PenException($"can't approve {Out.Field(text)}: it isn't a tool");
        _tools.Add(text);
        foreach (var domain in request.Domains)
        {
            if (!IsDomain(domain))
                throw new PenException($"can't approve {Out.Field(domain)}: it isn't a domain");
            _toolDomains.Add($"{text} {domain}");
        }
    }

    /// <summary>Writes the store's file, with mode 600, through a temporary file renamed over it.</summary>
    private void WriteFile()
    {
        var lines = new List<string> { Header, "dir " + _dir };
        if (_anyDomain)
            lines.Add("allow " + Allowlist.AnyDomain);
        lines.AddRange(_domains.Order(StringComparer.Ordinal).Select(domain => "allow " + domain));
        lines.AddRange(_owners.Order(StringComparer.Ordinal).Select(owner => "github_owner " + owner));
        lines.AddRange(_tools.Order(StringComparer.Ordinal).Select(tool => "tool " + tool));
        lines.AddRange(_toolDomains.Order(StringComparer.Ordinal).Select(domain => "tool-allow " + domain));
        if (Docker)
            lines.Add("docker");
        if (LayerHash is not null)
            lines.Add("layer " + LayerHash);

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var temporary = $"{_path}.{Environment.ProcessId}.tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = mode,
            };
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
                stream.Flush(flushToDisk: true);
            }
            File.SetUnixFileMode(temporary, mode); // in case a stale tmp existed with another mode
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
