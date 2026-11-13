namespace Pen;

/// <summary>A refusal or error shown to the user as "pen: message".</summary>
internal sealed class PenException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}
