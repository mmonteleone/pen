using System.Diagnostics;
using Pen.Configuration;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Tools;

namespace Pen.Trust;

/// <summary>Decides what a project's .pen/ asks for that isn't approved, and asks the user before a session.</summary>
/// <param name="context">The project and both configs.</param>
/// <param name="answers">Where the user's approval comes from.</param>
/// <param name="catalog">The recipes that say which domains a tool brings.</param>
/// <param name="clock">The clock that says when an abandoned review copy is old enough to delete.</param>
/// <remarks>
/// Settings from .pen/ reach the token lookup, the allowlist and docker build only once approved in the
/// <see cref="TrustStore"/>, since the agent can write a .pen/ (in a worktree inside the project) for the
/// next session to run with, and can keep changing it while the user looks.
/// </remarks>
internal sealed class TrustGate(ProjectContext context, Answers answers, RecipeCatalog catalog, TimeProvider clock)
{
    /// <summary>The heading above the list of what .pen/ asks for.</summary>
    internal const string UnapprovedHeading = ".pen/ asks for things you haven't approved:";

    /// <summary>How many lines of a .pen/Dockerfile a prompt shows.</summary>
    internal const int MaxShownLines = 60;

    /// <summary>How many characters of each .pen/Dockerfile line a prompt shows.</summary>
    internal const int MaxShownLineLength = 200;

    /// <summary>The domains the embedded default allowlist names, read once: it's part of the binary.</summary>
    private static readonly string[] DefaultDomains =
        [.. Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt"))];

    /// <summary>Finds what's in the project config and .pen/ that nothing approves yet.</summary>
    /// <param name="store">The approvals for the project's directory.</param>
    /// <param name="flagTools">The tools pen --tool names, which need no approval.</param>
    /// <returns>
    /// What the defaults, the user's config, <paramref name="flagTools"/> and <paramref name="store"/>
    /// don't cover; empty when nothing needs approving.
    /// </returns>
    public TrustRequest FindUnapproved(TrustStore store, IReadOnlyList<ToolSpec> flagTools)
    {
        var domains = FindUnapprovedDomains(store);
        var origin = ReadOriginOwner(context.Project);
        var owner = FindUnapprovedOwner(store, origin);
        var docker = context.ProjectConfig.Docker == true && !store.Docker;
        return new(domains, docker, owner, origin, FindUnapprovedLayer(context.Project, store))
        {
            Tools = FindUnapprovedTools(context.ProjectConfig, context.User.Tools.Concat(flagTools), store, catalog),
        };
    }

    /// <summary>Finds the project's allow domains that the defaults, user allow and store don't cover.</summary>
    /// <remarks>What <see cref="SelectAllowedDomains"/> leaves out, each once.</remarks>
    private List<string> FindUnapprovedDomains(TrustStore store)
    {
        var covers = CoversProjectDomain(context.User, store);
        return [.. context.ProjectConfig.Allow.Distinct().Where(domain => !covers(domain))];
    }

    /// <summary>Selects the entries of the project's allow that a session's allowlist may take.</summary>
    /// <param name="user">The user's config.</param>
    /// <param name="projectConfig">The project's config, whose allow is agent-writable.</param>
    /// <param name="store">The approvals for the project's directory.</param>
    /// <returns>
    /// Those approved in <paramref name="store"/>, or covered by the defaults and the user's allow without
    /// asking, in order; the rest are what a prompt asks for. Session start and live apply both take these.
    /// </returns>
    internal static IReadOnlyList<string> SelectAllowedDomains(
        UserConfiguration user,
        ProjectConfiguration projectConfig,
        TrustStore store) =>
        [.. projectConfig.Allow.Where(CoversProjectDomain(user, store))];

    /// <summary>Tells whether a project allow entry needs no approval now.</summary>
    /// <remarks>
    /// <para>A domain counts when the defaults or the user's allow name it, as written or under a wildcard, or the
    /// store approves it. The user's <see cref="Allowlist.AnyDomain"/> doesn't count for it: a domain allowed by
    /// name reaches private addresses, which open egress doesn't, so it covers only the project's own "*".</para>
    /// <para>The project's "*" counts when the user's allow has it too, or it's approved; an approved one covers
    /// no other entry (<see cref="TrustStore.CoversDomain"/>).</para>
    /// </remarks>
    private static Func<string, bool> CoversProjectDomain(UserConfiguration user, TrustStore store)
    {
        var named = Allowlist.MergeNamed(DefaultDomains, user.Allow).ToHashSet(StringComparer.Ordinal);
        var userOpen = Allowlist.Merge(user.Allow).Contains(Allowlist.AnyDomain);
        return domain =>
            (domain == Allowlist.AnyDomain ? userOpen : Allowlist.IsAllowed(named, domain))
            || store.CoversDomain(domain);
    }

    /// <summary>Finds the project's github_owner when it needs approving.</summary>
    /// <param name="store">The approvals for the project's directory.</param>
    /// <param name="origin">The owner of origin, or null when it has none or can't be read.</param>
    /// <returns>
    /// The github_owner, or null when there's none, it's origin's owner (ignoring case) or it's approved.
    /// </returns>
    private string? FindUnapprovedOwner(TrustStore store, string? origin)
    {
        if (context.ProjectConfig.GitHubOwner is not { } owner)
            return null;
        if (string.Equals(owner, origin, StringComparison.OrdinalIgnoreCase) || store.CoversOwner(owner))
            return null;
        return owner;
    }

    /// <summary>Finds the project's tools that need an approval.</summary>
    /// <param name="projectConfig">The project's .pen/config.toml.</param>
    /// <param name="namedByUser">The tools the user's config and pen --tool name.</param>
    /// <param name="store">The approvals for the project's directory.</param>
    /// <param name="catalog">The recipes that say which domains each tool brings.</param>
    /// <returns>Each tool that's new at its version, or brings domains not approved for it.</returns>
    /// <remarks>
    /// One the user also names isn't among them, since this session has it anyway. Nor is it approved:
    /// without the flag, or taken out of the user's config, the entry in .pen/ asks as any other. An
    /// approval covers the tool at its version and the domains shown for it, each recorded apart from the
    /// store's allow domains, so a recipe that later brings another domain asks again, and one that drops a
    /// domain doesn't.
    /// </remarks>
    public static IReadOnlyList<ToolRequest> FindUnapprovedTools(
        ProjectConfiguration projectConfig,
        IEnumerable<ToolSpec> namedByUser,
        TrustStore store,
        RecipeCatalog catalog)
    {
        var named = namedByUser.ToHashSet();
        var requests = new List<ToolRequest>();
        foreach (var tool in projectConfig.Tools.Where(t => !named.Contains(t)))
        {
            var isNew = !store.CoversTool(tool);
            var domains = ToolSpec.ListDomains([tool], catalog).Distinct(StringComparer.Ordinal)
                .Where(domain => isNew || !store.CoversToolDomain(tool, domain))
                .ToList();
            if (isNew || domains.Count > 0)
                requests.Add(new(tool, isNew, domains));
        }
        return requests;
    }

    /// <summary>Finds whether the .pen/ a .pen/Dockerfile builds from needs approving.</summary>
    /// <param name="project">The project whose .pen/ is hashed.</param>
    /// <param name="store">The approvals for the project's directory.</param>
    /// <returns>
    /// Null when there's no Dockerfile or the hash of .pen/ is the approved one; otherwise the new hash,
    /// or the problem that means .pen/ has none.
    /// </returns>
    /// <remarks>It's the same check as before a build.</remarks>
    public static LayerRequest? FindUnapprovedLayer(Project project, TrustStore store)
    {
        if (!HasDockerfile(project))
            return null;
        return PenDirHash.Compute(project.PenDir) switch
        {
            PenDirHashResult.NoHash(var problem) => new LayerRequest.Uncheckable(problem),
            PenDirHashResult.Hashed(var hash) when hash == store.LayerHash => null,
            PenDirHashResult.Hashed(var hash) => new LayerRequest.Unapproved(hash),
            _ => throw new UnreachableException(),
        };
    }

    /// <summary>Whether <paramref name="project"/> has a .pen/Dockerfile, or a symlink by that name.</summary>
    /// <remarks>A dangling symlink counts too.</remarks>
    public static bool HasDockerfile(Project project)
    {
        var dockerfile = Path.Combine(project.PenDir, "Dockerfile");
        return File.Exists(dockerfile) || PathChecks.IsLink(dockerfile);
    }

    /// <summary>Gets the GitHub owner of the project's origin remote.</summary>
    /// <returns>The owner, or null if there's no GitHub origin or git config can't be read.</returns>
    /// <remarks>Null makes any github_owner in .pen/ count as differing from origin, so it needs approval.</remarks>
    private static string? ReadOriginOwner(Project project)
    {
        try
        {
            return GitHub.ReadOriginOwner(project);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Lets a session start once nothing new is asked for, or the user approves it now.</summary>
    /// <param name="flagTools">The tools pen --tool names, and the agent the command implies.</param>
    /// <returns>The approvals the session starts with, including any given now.</returns>
    /// <exception cref="PenException">When it isn't approved: the session doesn't start.</exception>
    public TrustStore RequireForSession(IReadOnlyList<ToolSpec> flagTools)
    {
        var store = TrustStore.For(context.Host, context.Project);
        var request = FindUnapproved(store, flagTools);
        if (request.IsEmpty)
            return store;
        RequireCheckable(request);
        if (!answers.IsTerminal)
        {
            throw new PenException(DescribeUnapproved(request, "not starting without " +
                "a terminal to ask on: review them with pen trust, or approve them with pen trust --yes"));
        }

        using var review = request.Layer is not null
            ? LayerContext.TakeForReviewRemovingAbandoned(context.Host, context.Project, clock)
            : null;
        if (review is not null)
            request = WithReviewedLayer(request, review, store, showCopy: true);
        if (request.IsEmpty)
            return store;
        RequireCheckable(request);

        WriteRequest(request);
        if (!Out.Confirm(request.Question, answers))
            throw new PenException("not starting: nothing approved");
        return store.Approve(request);
    }

    /// <summary>Describes <paramref name="request"/> for an error, ending with <paramref name="ending"/>.</summary>
    internal static string DescribeUnapproved(TrustRequest request, string ending) =>
        $"{UnapprovedHeading}\n{string.Join('\n', request.FormatLines())}\n{ending}";

    /// <summary>Writes <paramref name="request"/> to stderr, ahead of the question that approves it.</summary>
    internal static void WriteRequest(TrustRequest request)
    {
        Out.WriteLine(UnapprovedHeading);
        Out.WriteRaw(string.Join('\n', request.FormatLines()));
    }

    /// <summary>Takes <paramref name="request"/>'s layer from the private copy in <paramref name="review"/>.</summary>
    /// <param name="request">What .pen/ asks for, as found on the live .pen/.</param>
    /// <param name="review">The private copy of .pen/, kept until the user answers.</param>
    /// <param name="store">The approvals for the project's directory.</param>
    /// <param name="showCopy">Whether the prompt points the user at the copy, to read the rest of it there.</param>
    /// <returns>The request with the copy's hash, its Dockerfile to show, or no layer if the copy's approved.</returns>
    /// <remarks>
    /// The hash that would be approved and the text shown are the same bytes, whatever .pen/ turns into
    /// while the user reads, so a swap can't get approved unseen.
    /// </remarks>
    internal static TrustRequest WithReviewedLayer(
        TrustRequest request,
        LayerContext review,
        TrustStore store,
        bool showCopy) =>
        request with { Layer = FindUnapprovedCopy(review, store, showCopy) };

    /// <summary>Finds whether the private copy in <paramref name="review"/> needs approving.</summary>
    /// <returns>Null when its hash is the approved one; otherwise its hash and Dockerfile, or its problem.</returns>
    private static LayerRequest? FindUnapprovedCopy(LayerContext review, TrustStore store, bool showCopy)
    {
        switch (review.Result)
        {
            case PenDirHashResult.NoHash(var problem):
                return new LayerRequest.Uncheckable(problem);
            case PenDirHashResult.Hashed(var hash) when hash == store.LayerHash:
                return null;
            case PenDirHashResult.Hashed(var hash):
                var dockerfile = FormatDockerfile(Path.Combine(review.Dir, "Dockerfile"));
                return new LayerRequest.Unapproved(hash, dockerfile, showCopy ? review.Dir : null);
            default:
                throw new UnreachableException();
        }
    }

    /// <summary>Formats the Dockerfile at <paramref name="path"/> for the prompt, cut short.</summary>
    /// <remarks>
    /// Its lines are escaped, as it's the agent's, and what's cut is counted. Only what's shown depends on
    /// reading it: the hash that's approved was taken already.
    /// </remarks>
    private static string FormatDockerfile(string path)
    {
        string? text;
        try
        {
            text = Native.ReadRegularFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"    (can't be read to show here: {Out.Describe(exception)})";
        }
        if (text is null)
            return "    (too large to show here)";

        var lines = text.TrimEnd('\n').Split('\n');
        var shown = lines.Take(MaxShownLines).Select(FormatDockerfileLine).ToList();
        if (lines.Length > MaxShownLines)
            shown.Add($"    ...cut: {lines.Length - MaxShownLines} more lines");
        return string.Join('\n', shown);
    }

    /// <summary>Formats one Dockerfile line for the prompt, escaped, cut at <see cref="MaxShownLineLength"/>.</summary>
    private static string FormatDockerfileLine(string line)
    {
        if (line.Length <= MaxShownLineLength)
            return "    | " + Out.Field(line);
        return $"    | {Out.Field(line[..MaxShownLineLength])} ...cut: " +
            $"{line.Length - MaxShownLineLength} more characters on this line";
    }

    /// <summary>Throws when <paramref name="request"/>'s layer can't be approved at all.</summary>
    /// <exception cref="PenException">When .pen/ has no hash, saying why.</exception>
    internal static void RequireCheckable(TrustRequest request)
    {
        if (request.Layer is LayerRequest.Uncheckable(var problem))
        {
            throw new PenException("can't approve .pen/, which a .pen/Dockerfile " +
                $"would build from: {problem}. Fix that, then rerun");
        }
    }
}
