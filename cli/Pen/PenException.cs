namespace Pen;

/// <summary>A refusal or error shown to the user as "pen: message".</summary>
/// <param name="message">The message, without the "pen: " prefix.</param>
/// <param name="exitCode">The exit code Pen ends with.</param>
internal sealed class PenException(string message, int exitCode = 1) : Exception(message)
{
    /// <summary>Gets the exit code Pen ends with.</summary>
    public int ExitCode { get; } = exitCode;
}
