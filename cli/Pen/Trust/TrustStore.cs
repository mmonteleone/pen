using Pen.Configuration;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Tools;

namespace Pen.Trust;

/// <summary>
/// The approvals for one directory, in &lt;runtime dir&gt;/trust-&lt;hash(dir)&gt;:
/// out of the sandbox's reach, and per directory, so a worktree the agent made
/// starts with none. Immutable; approving returns the store as written.
/// <para>
/// Plain text, a line for each approval. Every value is checked to be a strict
/// shape, when written and when read, so none can hold a line break and add an
/// approval. Anything else in the file means it's corrupt, and a corrupt store
/// approves nothing. The file names its directory because hash(dir) is short
/// enough to collide on purpose.
/// </para>
/// </summary>
internal sealed class TrustStore
{
    private const string Header = "pen-trust 1";

    private readonly string _path;
    private readonly string _dir;
    private readonly HashSet<string> _domains = new(StringComparer.Ordinal);
    private readonly HashSet<string> _owners = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tools = new(StringComparer.Ordinal);
    /// <summary>
    /// "name:version domain": kept apart from <see cref="_domains"/>, so neither approves for the other.
    /// </summary>
    private readonly HashSet<string> _toolDomains = new(StringComparer.Ordinal);

    public bool Docker { get; private set; }
    public string? LayerHash { get; private set; }

    private TrustStore(string path, string dir)
    {
        _path = path;
        _dir = dir;
    }

    public static string PathFor(string runtimeDir, string dir) =>
        Path.Combine(runtimeDir, $"trust-{Project.Hash(dir)}");

    public static TrustStore For(HostEnv host, Project project) => Load(host.GetRuntimeDir(project.Key), project.Dir);

    /// <summary>
    /// The approvals for <paramref name="dir"/>: none if the file is
    /// missing, unreadable or corrupt. Doesn't throw for any of those.
    /// </summary>
    public static TrustStore Load(string runtimeDir, string dir) => Read(PathFor(runtimeDir, dir), dir);

    private static TrustStore Read(string path, string dir)
    {
        string? text = null;
        try
        {
            text = Native.ReadRegularFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (text == null ? null : Parse(text, path, dir)) ?? new TrustStore(path, dir);
    }

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
            var space = line.IndexOf(' ');
            var kind = space < 0 ? line : line[..space];
            var value = space < 0 ? null : line[(space + 1)..];
            switch (kind)
            {
                case "allow" when value != null && IsDomain(value):
                    store._domains.Add(value);
                    break;
                case "github_owner" when value != null && IsOwner(value):
                    store._owners.Add(value);
                    break;
                case "tool" when value != null && ToolSpec.IsCanonicalText(value):
                    store._tools.Add(value);
                    break;
                case "tool-allow" when value != null && IsToolDomain(value):
                    store._toolDomains.Add(value);
                    break;
                case "docker" when value == null:
                    store.Docker = true;
                    break;
                case "layer" when value != null && IsHash(value) && store.LayerHash == null:
                    store.LayerHash = value;
                    break;
                default:
                    return null;
            }
        }
        return store;
    }

    private static bool IsDomain(string value) => Allowlist.IsValidDomain(value);
    private static bool IsOwner(string value) =>
        Config.OwnerRegex().IsMatch(value) && value == value.ToLowerInvariant();
    private static bool IsToolDomain(string value) =>
        value.Split(' ') is [var tool, var domain] && ToolSpec.IsCanonicalText(tool) && IsDomain(domain);
    private static bool IsHash(string value) => value.Length == 64 && value.All(c => char.IsAsciiHexDigitLower(c));

    /// <summary>Approved as written, or under an approved wildcard. example.com doesn't cover .example.com.</summary>
    public bool CoversDomain(string domain) => Allowlist.IsAllowed(_domains, domain);

    public bool CoversOwner(string owner) => _owners.Contains(owner.ToLowerInvariant());

    /// <summary>Approved at exactly this version: 24.04 doesn't cover 24.4.</summary>
    public bool CoversTool(ToolSpec tool) => _tools.Contains(tool.ToString());

    /// <summary>Approved for this tool, as written: an allow domain or a wildcard doesn't count.</summary>
    public bool CoversToolDomain(ToolSpec tool, string domain) => _toolDomains.Contains($"{tool} {domain}");

    /// <summary>The ones among a project's tools approved along with every domain they bring.</summary>
    public List<ToolSpec> ApprovedToolsOf(IEnumerable<ToolSpec> tools, RecipeCatalog catalog) =>
        tools.Where(t => CoversTool(t) && ToolSpec.DomainsOf([t], catalog).All(d => CoversToolDomain(t, d))).ToList();

    /// <summary>The approved ones among a project's allow list: all of it that may reach an allowlist.</summary>
    public List<string> ApprovedOf(IEnumerable<string> allow) => allow.Where(CoversDomain).ToList();

    /// <summary>
    /// Records everything in <paramref name="request"/>. One with a Layer that has a Problem can't be approved.
    /// </summary>
    public TrustStore Approve(TrustRequest request)
    {
        if (request.Layer is { Hash: null, Problem: var problem })
            throw new PenException($"can't approve .pen/: {problem}");
        return Written(request.Domains, request.GitHubOwner, request.Docker, request.Layer?.Hash, request.Tools);
    }

    /// <summary>Records domains the user typed.</summary>
    public TrustStore ApproveDomains(IEnumerable<string> domains) => Written(domains, null, false, null);

    /// <summary>
    /// Adds to what the file holds now, since another Pen may have approved
    /// something since this store was read (losing that would only ask again), and
    /// replaces it by rename, so it's never seen half-written. A value that isn't
    /// a strict shape refuses the whole write.
    /// </summary>
    private TrustStore Written(
        IEnumerable<string> domains,
        string? owner,
        bool docker,
        string? layerHash,
        IEnumerable<ToolRequest>? tools = null)
    {
        var next = Read(_path, _dir);
        foreach (var domain in domains)
        {
            if (!IsDomain(domain))
                throw new PenException($"can't approve {Out.Field(domain)}: it isn't a domain");
            next._domains.Add(domain);
        }
        if (owner != null)
        {
            var lower = owner.ToLowerInvariant();
            if (!IsOwner(lower))
                throw new PenException($"can't approve {Out.Field(owner)}: it isn't a GitHub owner name");
            next._owners.Add(lower);
        }
        if (layerHash != null)
        {
            if (!IsHash(layerHash))
                throw new PenException($"can't approve {Out.Field(layerHash)}: it isn't a hash");
            next.LayerHash = layerHash;
        }
        foreach (var request in tools ?? [])
        {
            var text = request.Tool.ToString();
            if (!ToolSpec.IsCanonicalText(text))
                throw new PenException($"can't approve {Out.Field(text)}: it isn't a tool");
            next._tools.Add(text);
            foreach (var domain in request.Domains)
            {
                if (!IsDomain(domain))
                    throw new PenException($"can't approve {Out.Field(domain)}: it isn't a domain");
                next._toolDomains.Add($"{text} {domain}");
            }
        }
        next.Docker |= docker;
        if (_dir.Contains('\n'))
            throw new PenException("can't record approvals for a directory whose path contains a newline");
        next.Save();
        return next;
    }

    private void Save()
    {
        var lines = new List<string> { Header, "dir " + _dir };
        lines.AddRange(_domains.Order(StringComparer.Ordinal).Select(d => "allow " + d));
        lines.AddRange(_owners.Order(StringComparer.Ordinal).Select(o => "github_owner " + o));
        lines.AddRange(_tools.Order(StringComparer.Ordinal).Select(t => "tool " + t));
        lines.AddRange(_toolDomains.Order(StringComparer.Ordinal).Select(d => "tool-allow " + d));
        if (Docker)
            lines.Add("docker");
        if (LayerHash != null)
            lines.Add("layer " + LayerHash);

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var tmp = $"{_path}.{Environment.ProcessId}.tmp";
        try
        {
            using (var f = new FileStream(tmp,
            new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = mode }))
            {
                f.Write(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
                f.Flush(flushToDisk: true);
            }
            File.SetUnixFileMode(tmp, mode); // in case a stale tmp existed with another mode
            File.Move(tmp, _path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
