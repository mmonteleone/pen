namespace Pen.Trust;

/// <summary>What the .pen/Dockerfile needs approved: a hash of .pen/, or the problem that means it can't be.</summary>
/// <remarks>See <see cref="TrustGate.WithReviewedLayer"/> for the private copy.</remarks>
internal abstract record LayerRequest
{
    private LayerRequest()
    {
    }

    /// <summary>A hash of .pen/ to approve.</summary>
    /// <param name="Hash">The hash of .pen/.</param>
    /// <param name="Dockerfile">The Dockerfile as text for the prompt, once a private copy was taken to review.</param>
    /// <param name="CopyDir">Where that copy is while the prompt waits, when the prompt points the user at it.</param>
    internal sealed record Unapproved(string Hash, string? Dockerfile = null, string? CopyDir = null) : LayerRequest;

    /// <summary>Why .pen/ has no hash, so it can't be approved.</summary>
    internal sealed record Uncheckable(string Problem) : LayerRequest;
}
