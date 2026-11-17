using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;
using Pen.Tools;
using Pen.Trust;

namespace Pen.Commands;

/// <summary>Runs pen trust: reviews and approves what the project's .pen/ asks for, or lists it.</summary>
/// <param name="context">The project and both configs.</param>
/// <param name="answers">Where the user's approval comes from.</param>
/// <param name="catalog">The recipes that say which domains a tool brings.</param>
/// <param name="clock">The clock that says when an abandoned review copy is old enough to delete.</param>
internal sealed class TrustCommand(ProjectContext context, Answers answers, RecipeCatalog catalog, TimeProvider clock)
{
    private const string NothingNew = "nothing new to approve in .pen/";

    private readonly TrustGate _gate = new(context, answers, catalog, clock);

    /// <summary>Lists (<paramref name="list"/>) or approves what .pen/ asks for.</summary>
    /// <param name="yes">Approve without asking (--yes).</param>
    /// <param name="list">List everything asked for and approve nothing (--list).</param>
    /// <returns>0, or 1 when the user declines; a refusal throws.</returns>
    public int Run(bool yes, bool list)
    {
        Session.EnsureRuntimeDir(context.Host, context.Project);
        var store = TrustStore.For(context.Host, context.Project);
        var request = _gate.FindUnapproved(store, []);
        if (request.IsEmpty)
        {
            Out.WriteLine(NothingNew);
            return 0;
        }
        return list ? ListAll(request, store) : Approve(request, store, yes);
    }

    /// <summary>Writes every item in <paramref name="request"/> to stdout, with no limit.</summary>
    /// <returns>0.</returns>
    /// <remarks>
    /// It approves nothing, so it needs no copy kept for an answer: only the text of one, when there's a
    /// hash to show the Dockerfile for.
    /// </remarks>
    private int ListAll(TrustRequest request, TrustStore store)
    {
        using var look = request.Layer is { Hash: not null }
            ? LayerContext.Review(context.Host, context.Project, clock)
            : null;
        if (look is not null)
            request = TrustGate.WithReviewedLayer(request, look, store, showCopy: false);
        foreach (var line in request.FormatAllLines())
            Out.WriteStdout(line + "\n");
        return 0;
    }

    /// <summary>Shows <paramref name="request"/> and approves it if the user agrees, or with --yes.</summary>
    /// <returns>0 once approved, or when the private copy needs nothing; 1 when the user declines.</returns>
    /// <remarks>
    /// What's approved is exactly what was shown: the layer comes from a private copy, kept until the
    /// answer, and the request is checked again on it.
    /// </remarks>
    private int Approve(TrustRequest request, TrustStore store, bool yes)
    {
        TrustGate.RequireCheckable(request);
        if (!yes && !answers.IsTerminal)
        {
            throw new PenException(TrustGate.DescribeUnapproved(request, "no " +
                "terminal to ask on: use pen trust --yes to approve them"));
        }

        using var review = request.Layer is not null
            ? LayerContext.Review(context.Host, context.Project, clock)
            : null;
        if (review is not null)
            request = TrustGate.WithReviewedLayer(request, review, store, showCopy: !yes);
        if (request.IsEmpty)
        {
            Out.WriteLine(NothingNew);
            return 0;
        }
        TrustGate.RequireCheckable(request);

        TrustGate.WriteRequest(request);
        if (!yes && !Out.Confirm(request.Question, answers))
        {
            Out.WriteLine("nothing approved");
            return 1;
        }
        store.Approve(request);
        Out.WriteLine("approved");
        return 0;
    }
}
