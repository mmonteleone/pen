namespace Pen.Trust;

/// <summary>What the .pen/Dockerfile needs approved: a hash of .pen/, or the problem that means it can't be.</summary>
/// <param name="Hash">The hash of .pen/ to approve, or null when there's a problem.</param>
/// <param name="Problem">Why .pen/ has no hash, or null.</param>
/// <param name="Dockerfile">The Dockerfile as text for the prompt, once a private copy was taken to review.</param>
/// <param name="CopyDir">Where that copy is while the prompt waits, when the prompt points the user at it.</param>
/// <remarks>See <see cref="TrustGate.WithReviewedLayer"/> for the private copy.</remarks>
internal sealed record LayerRequest(string? Hash, string? Problem, string? Dockerfile = null, string? CopyDir = null);
