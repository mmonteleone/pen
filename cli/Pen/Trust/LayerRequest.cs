namespace Pen.Trust;

/// <summary>
/// What the .pen/Dockerfile needs approved: a hash, or the Problem that means it
/// can't be. Once a private copy was taken to review: the Dockerfile as text for the
/// prompt, and where the copy is while the prompt waits (see <see cref="TrustGate"/>).
/// </summary>
internal sealed record LayerRequest(string? Hash, string? Problem, string? Dockerfile = null, string? CopyDir = null);
