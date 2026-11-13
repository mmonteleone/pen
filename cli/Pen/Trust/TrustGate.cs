using Pen.Configuration;
using Pen.GitProtection;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Sessions;
using Pen.Tools;

namespace Pen.Trust;

/// <summary>
/// The decisions about a project's .pen/: its settings reach the token lookup,
/// the allowlist and docker build only once approved in the <see cref="TrustStore"/>,
/// since the agent can write a .pen/ (in a worktree inside the project) for the
/// next session to run with, and can keep changing it while the user looks.
/// </summary>
internal static class TrustGate
{
    /// <summary>
    /// What's in <paramref name="projectConfig"/> and .pen/ that the defaults, the user's config,
    /// <paramref name="flagTools"/> (pen --tool) and the store don't cover.
    /// </summary>
    public static TrustRequest Requested(
        Project project,
        Config user,
        Config projectConfig,
        TrustStore store,
        IReadOnlyList<ToolSpec>? flagTools = null,
        RecipeCatalog? catalog = null)
    {
        var allowed = Allowlist.Merge(Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt")), user.Allow)
            .ToHashSet(StringComparer.Ordinal);
        var domains = projectConfig.Allow.Distinct()
            .Where(d => !Allowlist.IsAllowed(allowed, d) && !store.CoversDomain(d))
            .ToList();
        var origin = OriginOwner(project);
        var owner = projectConfig.GitHubOwner is { } o
            && !string.Equals(o, origin, StringComparison.OrdinalIgnoreCase) && !store.CoversOwner(o) ? o : null;
        return new(domains, projectConfig.Docker == true && !store.Docker, owner, origin,
            UnapprovedLayer(project, store))
        {
            Tools = UnapprovedTools(projectConfig, user.Tools.Concat(flagTools ?? []), store,
                catalog ?? RecipeCatalog.Embedded),
        };
    }

    /// <summary>
    /// The project's tools that need an approval. One the user also asked for isn't among
    /// them, since this session has it anyway. Nor is it approved: without the flag, or
    /// taken out of the user's config, the entry in .pen/ asks as any other. An approval
    /// covers the tool at its version and the domains shown for it, each recorded apart from
    /// the store's allow domains, so a recipe that later brings another domain asks again,
    /// and one that drops a domain doesn't.
    /// </summary>
    public static List<ToolRequest> UnapprovedTools(
        Config projectConfig,
        IEnumerable<ToolSpec> theUsers,
        TrustStore store,
        RecipeCatalog catalog)
    {
        var asked = theUsers.ToHashSet();
        var requests = new List<ToolRequest>();
        foreach (var tool in projectConfig.Tools.Where(t => !asked.Contains(t)))
        {
            var isNew = !store.CoversTool(tool);
            var domains = ToolSpec.DomainsOf([tool], catalog).Distinct(StringComparer.Ordinal)
                .Where(d => isNew || !store.CoversToolDomain(tool, d)).ToList();
            if (isNew || domains.Count > 0)
                requests.Add(new(tool, isNew, domains));
        }
        return requests;
    }

    /// <summary>
    /// The same check as for building: when there's a Dockerfile, whether its .pen/ is the approved one.
    /// </summary>
    public static LayerRequest? UnapprovedLayer(Project project, TrustStore store)
    {
        if (!HasDockerfile(project))
            return null;
        var (hash, problem) = PenDirHash.Compute(project.PenDir);
        if (hash == null)
            return new(null, problem);
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
        try
        {
            return GitHub.OriginOwner(project);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private const string Asks = ".pen/ asks for things you haven't approved:";

    /// <summary>
    /// Before a session: returns if nothing new is asked for or the user approves
    /// it now. Otherwise throws, and the session doesn't start.
    /// </summary>
    public static void RequireForSession(
        HostEnv host,
        Project project,
        Config user,
        Config projectConfig,
        Answers answers,
        IReadOnlyList<ToolSpec>? flagTools = null,
        RecipeCatalog? catalog = null)
    {
        var store = TrustStore.For(host, project);
        var request = Requested(project, user, projectConfig, store, flagTools, catalog);
        if (request.IsEmpty)
            return;
        RequireCheckable(request);
        if (!answers.IsTerminal)
        {
            throw new PenException($"{Asks}\n{string.Join('\n', request.Lines())}\nnot starting without " +
                "a terminal to ask on: review them with pen trust, or approve them with pen trust --yes");
        }

        using var review = request.Layer != null ? LayerContext.Review(host, project) : null;
        if (review != null)
            request = Reviewed(request, review, store, showCopy: true);
        if (request.IsEmpty)
            return;
        RequireCheckable(request);

        Out.WriteLine(Asks);
        Out.WriteRaw(string.Join('\n', request.Lines()));
        if (!Out.Confirm(request.Question, answers.Input))
            throw new PenException("not starting: nothing approved");
        store.Approve(request);
    }

    /// <summary>pen trust [--yes | --list].</summary>
    public static int Command(
        HostEnv host,
        Project project,
        Config user,
        Config projectConfig,
        bool yes,
        Answers answers,
        bool list = false,
        RecipeCatalog? catalog = null)
    {
        Session.EnsureRuntimeDir(host, project);
        var store = TrustStore.For(host, project);
        var request = Requested(project, user, projectConfig, store, catalog: catalog);
        if (request.IsEmpty)
        {
            Out.WriteLine("nothing new to approve in .pen/");
            return 0;
        }
        if (list)
        {
            // Approves nothing, so it needs no copy kept for an answer: only the text of one.
            using var look = request.Layer is { Hash: not null } ? LayerContext.Review(host, project) : null;
            if (look != null)
                request = Reviewed(request, look, store, showCopy: false);
            foreach (var line in request.AllLines())
                Out.WriteStdout(line + "\n");
            return 0;
        }
        RequireCheckable(request);
        if (!yes && !answers.IsTerminal)
        {
            throw new PenException($"{Asks}\n{string.Join('\n', request.Lines())}\nno " +
                "terminal to ask on: use pen trust --yes to approve them");
        }

        using var review = request.Layer != null ? LayerContext.Review(host, project) : null;
        if (review != null)
            request = Reviewed(request, review, store, showCopy: !yes);
        if (request.IsEmpty)
        {
            Out.WriteLine("nothing new to approve in .pen/");
            return 0;
        }
        RequireCheckable(request);

        Out.WriteLine(Asks);
        Out.WriteRaw(string.Join('\n', request.Lines()));
        if (!yes && !Out.Confirm(request.Question, answers.Input))
        {
            Out.WriteLine("nothing approved");
            return 1;
        }
        store.Approve(request);
        Out.WriteLine("approved");
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
                : new(hash, null, ShownDockerfile(Path.Combine(review.Dir, "Dockerfile")),
                    showCopy ? review.Dir : null),
        };
    }

    internal const int MaxShownLines = 60, MaxShownLineLength = 200;

    /// <summary>A Dockerfile for the prompt: escaped, as it's the agent's, and cut short, saying what's cut.</summary>
    private static string ShownDockerfile(string path)
    {
        string? text = null;
        try
        {
            text = Native.ReadRegularFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (text == null)
            return "    (too large to show here)";

        var lines = text.TrimEnd('\n').Split('\n');
        var shown = lines.Take(MaxShownLines).Select(l => "    | " + (l.Length <= MaxShownLineLength
            ? Out.Field(l)
            : $"{Out.Field(l[..MaxShownLineLength])} ...cut: " +
                $"{l.Length - MaxShownLineLength} more characters on this line"));
        var cut = lines.Length > MaxShownLines
            ? [$"    ...cut: {lines.Length - MaxShownLines} more lines"]
            : Array.Empty<string>();
        return string.Join('\n', shown.Concat(cut));
    }

    private static void RequireCheckable(TrustRequest request)
    {
        if (request.Layer is { Problem: { } problem })
        {
            throw new PenException("can't approve .pen/, which a .pen/Dockerfile " +
                $"would build from: {problem}. Fix that, then rerun");
        }
    }
}
