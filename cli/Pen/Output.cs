using System.Globalization;
using System.Text;

namespace Pen;

/// <summary>A refusal or error shown to the user as "pen: message".</summary>
internal sealed class PenException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Messages to stderr, and pen's own stdout. Writes never throw: after a hangup
/// the terminal is gone, and the end-of-session path must still run to completion.
/// Everything is sanitized on the way out (<see cref="Clean"/>), since messages
/// carry names and text the agent chose, and those could otherwise rewrite the
/// terminal, including erasing a warning.
/// </summary>
internal static class Out
{
    public static void Line(string message) => Write("pen: " + message);

    public static void Raw(string message) => Write(message);

    /// <summary>Text for stdout, as given: no newline is added.</summary>
    public static void Stdout(string text)
    {
        try { Console.Out.Write(Clean(text)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Whether a terminal could act on this character: C0, DEL, C1, or one that reorders or breaks lines of text.</summary>
    internal static bool IsControl(char c) => c is < ' ' or (>= '\x7f' and <= '\x9f')
        or '\u061c' or '\u200e' or '\u200f' or (>= '\u2028' and <= '\u202e') or (>= '\u2066' and <= '\u2069');

    /// <summary>A whole message, without its control characters. Its own newlines and tabs stay.</summary>
    internal static string Clean(string text)
    {
        if (!text.Any(c => IsControl(c) && c is not ('\n' or '\t'))) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (!IsControl(c) || c is '\n' or '\t') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// A value the agent can influence (a path, branch, host, subject), for
    /// putting in a message: control characters show as escapes, newlines among
    /// them, so the value can't forge a line of pen's output. Not for text a
    /// shell will read, which has to keep the real name.
    /// </summary>
    public static string Field(string value)
    {
        if (!value.Any(IsControl)) return value;
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (!IsControl(c)) sb.Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\t') sb.Append("\\t");
            else if (c == '\r') sb.Append("\\r");
            else if (c <= '\xff') sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            else sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>
    /// An exception as text for the user. The AOT build (UseSystemResourceKeys)
    /// has framework exceptions' messages as raw resource keys, so only pen's
    /// own are shown as they are.
    /// </summary>
    public static string Describe(Exception e) => e switch
    {
        PenException => e.Message,
        UnauthorizedAccessException u => PermissionDenied(u),
        _ => e.GetType().Name,
    };

    /// <summary>
    /// The message for a permission error: the path, when it names one. The
    /// exception's own text can't be shown, since the AOT build (UseSystemResourceKeys)
    /// has it as "UnauthorizedAccess_IODenied_Path, /the/path".
    /// </summary>
    public static string PermissionDenied(UnauthorizedAccessException e)
    {
        var at = e.Message.IndexOf('/', StringComparison.Ordinal);
        return at < 0 ? "permission denied" : $"permission denied: {Field(e.Message[at..].Replace("' is denied.", "", StringComparison.Ordinal))}";
    }

    /// <summary>A question, with the cursor left after it.</summary>
    public static void Prompt(string question)
    {
        try { Console.Error.Write(Clean(question)); Console.Error.Flush(); }
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
        try { Console.Error.WriteLine(Clean(text)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
