namespace Pen;

/// <summary>
/// What the .pen/Dockerfile needs approved: a hash, or the Problem that means it
/// can't be. Once a private copy was taken to review: the Dockerfile as text for the
/// prompt, and where the copy is while the prompt waits (see <see cref="Trust"/>).
/// </summary>
internal sealed record LayerRequest(string? Hash, string? Problem, string? Dockerfile = null, string? CopyDir = null);

/// <summary>
/// What a project's .pen/ asks for that isn't approved. ports isn't in it: they
/// only publish to the host's loopback.
/// </summary>
internal sealed record TrustRequest(IReadOnlyList<string> Domains, bool Docker, string? GitHubOwner, string? OriginOwner, LayerRequest? Layer)
{
    /// <summary>How many domains a prompt lists. An agent could add thousands to bury one, or the rest.</summary>
    public const int MaxListedDomains = 20;

    public bool IsEmpty => Domains.Count == 0 && !Docker && GitHubOwner == null && Layer == null;

    private int HiddenDomains => Math.Max(Domains.Count - MaxListedDomains, 0);

    /// <summary>
    /// One line for each item, indented, saying what it grants. The values come
    /// from .pen/, so they're escaped. Domains come last, so a long list can't push
    /// another item out of sight; past the limit there's a count, and how to see them.
    /// </summary>
    public IEnumerable<string> Lines() =>
        Grants().Concat(Domains.Take(MaxListedDomains).Select(DomainLine)).Concat(
            HiddenDomains > 0 ? [$"  ...and {HiddenDomains} more domains: pen trust --list shows them all"] : []);

    /// <summary>Every item, with no limit, for pen trust --list.</summary>
    public IEnumerable<string> AllLines() => Grants().Concat(Domains.Select(DomainLine));

    /// <summary>The question to approve, which says so when it covers items not listed.</summary>
    public string Question => HiddenDomains > 0 ? $"approve all of these, with the {HiddenDomains} domains not listed?" : "approve these?";

    /// <summary>What's being approved, as it was hashed; the live .pen/ may already say something else.</summary>
    private string LayerBody() => Layer!.CopyDir == null
        ? Layer.Dockerfile!
        : $"    The rest of what's being approved is in {Out.Field(Layer.CopyDir)} until you answer, a copy made with this hash. "
          + "Read that, not .pen/, which may have changed since.\n" + Layer.Dockerfile;

    private static string DomainLine(string domain) => $"  allow {Out.Field(domain)}: the sandbox can reach it";

    private IEnumerable<string> Grants()
    {
        if (Layer != null)
            yield return Layer.Hash == null
                ? $"  .pen/Dockerfile can't be approved: {Out.Field(Layer.Problem ?? "")}"
                : "  .pen/Dockerfile (new or changed): sessions run in the image it builds, and its build steps can use "
                  + "everything in .pen/ except config.toml and reach the network directly, past the proxy."
                  + (Layer.Dockerfile == null ? " pen trust shows it." : "\n" + LayerBody());
        if (Docker)
            yield return "  docker = true: sessions get their own Docker daemon, running privileged";
        if (GitHubOwner != null)
            yield return $"  github_owner = {Out.Field(GitHubOwner)}: sessions get this owner's GitHub token ("
                + (OriginOwner != null ? $"origin's owner is {Out.Field(OriginOwner)})" : "origin has no GitHub owner)");
    }
}

/// <summary>Where a yes or no comes from, and whether a person can be there to give it.</summary>
internal sealed record Answers(TextReader Input, bool IsTerminal)
{
    public static Answers FromProcess() => new(Console.In, Native.IsATty(0));
}

/// <summary>
/// The decisions about a project's .pen/: its settings reach the token lookup,
/// the allowlist and docker build only once approved in the <see cref="TrustStore"/>,
/// since the agent can write a .pen/ (in a worktree inside the project) for the
/// next session to run with, and can keep changing it while the user looks.
/// </summary>
internal static class Trust
{
    /// <summary>What's in <paramref name="projectConfig"/> and .pen/ that the defaults, the user's config and the store don't cover.</summary>
    public static TrustRequest Requested(Project project, Config user, Config projectConfig, TrustStore store)
    {
        var allowed = Allowlist.Merge(Allowlist.ParseList(Embedded.Text("proxy/allowlist.txt")), user.Allow).ToHashSet(StringComparer.Ordinal);
        var domains = projectConfig.Allow.Distinct()
            .Where(d => !Allowlist.IsAllowed(allowed, d) && !store.CoversDomain(d))
            .ToList();
        var origin = OriginOwner(project);
        var owner = projectConfig.GitHubOwner is { } o
            && !string.Equals(o, origin, StringComparison.OrdinalIgnoreCase) && !store.CoversOwner(o) ? o : null;
        return new(domains, projectConfig.Docker == true && !store.Docker, owner, origin, UnapprovedLayer(project, store));
    }

    /// <summary>The same check as for building: when there's a Dockerfile, whether its .pen/ is the approved one.</summary>
    public static LayerRequest? UnapprovedLayer(Project project, TrustStore store)
    {
        if (!HasDockerfile(project)) return null;
        var (hash, problem) = PenDirHash.Compute(project.PenDir);
        if (hash == null) return new(null, problem);
        return hash == store.LayerHash ? null : new(hash, null);
    }

    public static bool HasDockerfile(Project project)
    {
        var dockerfile = Path.Combine(project.PenDir, "Dockerfile");
        return File.Exists(dockerfile) || RepoGuard.IsLink(dockerfile);
    }

    /// <summary>Unknown, so that a github_owner counts as differing, when the git config can't be read.</summary>
    private static string? OriginOwner(Project project)
    {
        try { return GitHub.OriginOwner(project); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private const string Asks = ".pen/ asks for things you haven't approved:";

    /// <summary>
    /// Before a session: returns if nothing new is asked for or the user approves
    /// it now. Otherwise throws, and the session doesn't start.
    /// </summary>
    public static void RequireForSession(HostEnv host, Project project, Config user, Config projectConfig, Answers answers)
    {
        var store = TrustStore.For(host, project);
        var request = Requested(project, user, projectConfig, store);
        if (request.IsEmpty) return;
        RequireCheckable(request);
        if (!answers.IsTerminal)
            throw new PenException($"{Asks}\n{string.Join('\n', request.Lines())}\nnot starting without a terminal to ask on: review them with pen trust, or approve them with pen trust --yes");

        using var review = request.Layer != null ? LayerContext.Review(host, project) : null;
        if (review != null) request = Reviewed(request, review, store, showCopy: true);
        if (request.IsEmpty) return;
        RequireCheckable(request);

        Out.Line(Asks);
        Out.Raw(string.Join('\n', request.Lines()));
        if (!Out.Confirm(request.Question, answers.Input))
            throw new PenException("not starting: nothing approved");
        store.Approve(request);
    }

    /// <summary>pen trust [--yes | --list].</summary>
    public static int Command(HostEnv host, Project project, Config user, Config projectConfig, bool yes, Answers answers, bool list = false)
    {
        Session.EnsureRuntimeDir(host, project);
        var store = TrustStore.For(host, project);
        var request = Requested(project, user, projectConfig, store);
        if (request.IsEmpty)
        {
            Out.Line("nothing new to approve in .pen/");
            return 0;
        }
        if (list)
        {
            // Approves nothing, so it needs no copy kept for an answer: only the text of one.
            using var look = request.Layer is { Hash: not null } ? LayerContext.Review(host, project) : null;
            if (look != null) request = Reviewed(request, look, store, showCopy: false);
            foreach (var line in request.AllLines()) Out.Stdout(line + "\n");
            return 0;
        }
        RequireCheckable(request);
        if (!yes && !answers.IsTerminal)
            throw new PenException($"{Asks}\n{string.Join('\n', request.Lines())}\nno terminal to ask on: use pen trust --yes to approve them");

        using var review = request.Layer != null ? LayerContext.Review(host, project) : null;
        if (review != null) request = Reviewed(request, review, store, showCopy: !yes);
        if (request.IsEmpty)
        {
            Out.Line("nothing new to approve in .pen/");
            return 0;
        }
        RequireCheckable(request);

        Out.Line(Asks);
        Out.Raw(string.Join('\n', request.Lines()));
        if (!yes && !Out.Confirm(request.Question, answers.Input))
        {
            Out.Line("nothing approved");
            return 1;
        }
        store.Approve(request);
        Out.Line("approved");
        return 0;
    }

    /// <summary>
    /// The request with its layer taken from a private copy: the hash that would be
    /// approved and the text shown are the same bytes, whatever .pen/ turns into
    /// while the user reads, so a swap can't get approved unseen.
    /// </summary>
    private static TrustRequest Reviewed(TrustRequest request, LayerContext review, TrustStore store, bool showCopy)
    {
        var (hash, problem) = review.Result;
        return request with
        {
            Layer = hash == null ? new(null, problem)
                : hash == store.LayerHash ? null
                : new(hash, null, ShownDockerfile(Path.Combine(review.Dir, "Dockerfile")), showCopy ? review.Dir : null),
        };
    }

    internal const int MaxShownLines = 60, MaxShownLineLength = 200;

    /// <summary>A Dockerfile for the prompt: escaped, as it's the agent's, and cut short, saying what's cut.</summary>
    private static string ShownDockerfile(string path)
    {
        string? text = null;
        try { text = Native.ReadRegularFile(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (text == null) return "    (too large to show here)";

        var lines = text.TrimEnd('\n').Split('\n');
        var shown = lines.Take(MaxShownLines).Select(l => "    | " + (l.Length <= MaxShownLineLength
            ? Out.Field(l)
            : $"{Out.Field(l[..MaxShownLineLength])} ...cut: {l.Length - MaxShownLineLength} more characters on this line"));
        var cut = lines.Length > MaxShownLines ? [$"    ...cut: {lines.Length - MaxShownLines} more lines"] : Array.Empty<string>();
        return string.Join('\n', shown.Concat(cut));
    }

    private static void RequireCheckable(TrustRequest request)
    {
        if (request.Layer is { Problem: { } problem })
            throw new PenException($"can't approve .pen/, which a .pen/Dockerfile would build from: {problem}. Fix that, then rerun");
    }
}

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

    public bool Docker { get; private set; }
    public string? LayerHash { get; private set; }

    private TrustStore(string path, string dir)
    {
        _path = path;
        _dir = dir;
    }

    public static string PathFor(string runtimeDir, string dir) => Path.Combine(runtimeDir, $"trust-{Project.Hash(dir)}");

    public static TrustStore For(HostEnv host, Project project) => Load(host.RuntimeDir(project.Key), project.Dir);

    /// <summary>The approvals for <paramref name="dir"/>: none if the file is missing, unreadable or corrupt. Doesn't throw for any of those.</summary>
    public static TrustStore Load(string runtimeDir, string dir) => Read(PathFor(runtimeDir, dir), dir);

    private static TrustStore Read(string path, string dir)
    {
        string? text = null;
        try { text = Native.ReadRegularFile(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return (text == null ? null : Parse(text, path, dir)) ?? new TrustStore(path, dir);
    }

    private static TrustStore? Parse(string text, string path, string dir)
    {
        if (!text.EndsWith('\n')) return null;
        var lines = text[..^1].Split('\n');
        if (lines.Length < 2 || lines[0] != Header || lines[1] != "dir " + dir) return null;

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
    private static bool IsOwner(string value) => Config.OwnerRegex().IsMatch(value) && value == value.ToLowerInvariant();
    private static bool IsHash(string value) => value.Length == 64 && value.All(c => char.IsAsciiHexDigitLower(c));

    /// <summary>Approved as written, or under an approved wildcard. example.com doesn't cover .example.com.</summary>
    public bool CoversDomain(string domain) => Allowlist.IsAllowed(_domains, domain);

    public bool CoversOwner(string owner) => _owners.Contains(owner.ToLowerInvariant());

    /// <summary>The approved ones among a project's allow list: all of it that may reach an allowlist.</summary>
    public List<string> ApprovedOf(IEnumerable<string> allow) => allow.Where(CoversDomain).ToList();

    /// <summary>Records everything in <paramref name="request"/>. One with a Layer that has a Problem can't be approved.</summary>
    public TrustStore Approve(TrustRequest request)
    {
        if (request.Layer is { Hash: null, Problem: var problem })
            throw new PenException($"can't approve .pen/: {problem}");
        return Written(request.Domains, request.GitHubOwner, request.Docker, request.Layer?.Hash);
    }

    /// <summary>Records domains the user typed.</summary>
    public TrustStore ApproveDomains(IEnumerable<string> domains) => Written(domains, null, false, null);

    /// <summary>
    /// Adds to what the file holds now, since another pen may have approved
    /// something since this store was read (losing that would only ask again), and
    /// replaces it by rename, so it's never seen half-written. A value that isn't
    /// a strict shape refuses the whole write.
    /// </summary>
    private TrustStore Written(IEnumerable<string> domains, string? owner, bool docker, string? layerHash)
    {
        var next = Read(_path, _dir);
        foreach (var domain in domains)
        {
            if (!IsDomain(domain)) throw new PenException($"can't approve {Out.Field(domain)}: it isn't a domain");
            next._domains.Add(domain);
        }
        if (owner != null)
        {
            var lower = owner.ToLowerInvariant();
            if (!IsOwner(lower)) throw new PenException($"can't approve {Out.Field(owner)}: it isn't a GitHub owner name");
            next._owners.Add(lower);
        }
        if (layerHash != null)
        {
            if (!IsHash(layerHash)) throw new PenException($"can't approve {Out.Field(layerHash)}: it isn't a hash");
            next.LayerHash = layerHash;
        }
        next.Docker |= docker;
        if (_dir.Contains('\n')) throw new PenException("can't record approvals for a directory whose path contains a newline");
        next.Save();
        return next;
    }

    private void Save()
    {
        var lines = new List<string> { Header, "dir " + _dir };
        lines.AddRange(_domains.Order(StringComparer.Ordinal).Select(d => "allow " + d));
        lines.AddRange(_owners.Order(StringComparer.Ordinal).Select(o => "github_owner " + o));
        if (Docker) lines.Add("docker");
        if (LayerHash != null) lines.Add("layer " + LayerHash);

        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        var tmp = $"{_path}.{Environment.ProcessId}.tmp";
        try
        {
            using (var f = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = mode }))
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
