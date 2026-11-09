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

    /// <summary>
    /// The message for a permission error: the path, when it names one. The
    /// exception's own text can't be shown, since the AOT build (UseSystemResourceKeys)
    /// has it as "UnauthorizedAccess_IODenied_Path, /the/path".
    /// </summary>
    public static string PermissionDenied(UnauthorizedAccessException e)
    {
        var at = e.Message.IndexOf('/', StringComparison.Ordinal);
        return at < 0 ? "permission denied" : $"permission denied: {e.Message[at..].Replace("' is denied.", "", StringComparison.Ordinal)}";
    }

    /// <summary>A question, with the cursor left after it.</summary>
    public static void Prompt(string question)
    {
        try { Console.Error.Write(question); Console.Error.Flush(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Asks "pen: question [y/N]" and reads the answer from <paramref name="input"/>:
    /// true only for y or yes. Without a terminal the answer is echoed, so the
    /// transcript reads like an interactive one.
    /// </summary>
    public static bool Confirm(string question, TextReader input)
    {
        Prompt($"pen: {question} [y/N] ");
        string? answer;
        try { answer = input.ReadLine(); }
        catch (IOException) { answer = null; }
        if (!Native.IsATty(0)) Raw(answer ?? "");
        return IsYes(answer);
    }

    internal static bool IsYes(string? answer) => answer?.Trim().ToLowerInvariant() is "y" or "yes";

    private static void Write(string text)
    {
        try { Console.Error.WriteLine(text); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
