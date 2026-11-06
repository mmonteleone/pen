namespace Pen;

/// <summary>A refusal or error shown to the user as "pen: message".</summary>
internal sealed class PenException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Messages to stderr. Writes never throw: after a hangup the terminal is gone,
/// and the end-of-session path must still run to completion.
/// </summary>
internal static class Out
{
    public static void Line(string message) => Write("pen: " + message);

    public static void Raw(string message) => Write(message);

    private static void Write(string text)
    {
        try { Console.Error.WriteLine(text); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
