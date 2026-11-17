using System.Globalization;
using System.Text;

namespace Pen.Platform;

/// <summary>Writes messages to stderr, and Pen's own stdout, sanitized.</summary>
/// <remarks>
/// Writes never throw: after a hangup the terminal is gone, and the end-of-session path must
/// still run to completion. Everything is sanitized on the way out (<see cref="StripControls"/>),
/// since messages carry names and text the agent chose, and those could otherwise rewrite the
/// terminal, including erasing a warning.
/// </remarks>
internal static class Out
{
    /// <summary>Writes "pen: message" as a line to stderr.</summary>
    public static void WriteLine(string message) => Write("pen: " + message);

    /// <summary>Writes a line to stderr, without the "pen: " prefix.</summary>
    public static void WriteRaw(string message) => Write(message);

    /// <summary>Writes text to stdout, as given: no newline is added.</summary>
    public static void WriteStdout(string text)
    {
        try
        {
            Console.Out.Write(StripControls(text));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Reads the terminal's width when <paramref name="output"/> is the process's stdout and that goes to one,
    /// otherwise null.
    /// </summary>
    public static int? ReadTerminalWidth(TextWriter output)
    {
        if (output != Console.Out || Console.IsOutputRedirected)
            return null;
        try
        {
            return Console.WindowWidth;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks whether a terminal could act on this character: C0, DEL, C1, or one that reorders or
    /// breaks lines of text.
    /// </summary>
    internal static bool IsControl(char character) => character is < ' ' or (>= '\x7f' and <= '\x9f')
        or '\u061c' or '\u200e' or '\u200f' or (>= '\u2028' and <= '\u202e') or (>= '\u2066' and <= '\u2069');

    /// <summary>Removes a whole message's control characters. Its own newlines and tabs stay.</summary>
    internal static string StripControls(string text)
    {
        if (!text.Any(c => IsControl(c) && c is not ('\n' or '\t')))
            return text;
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (!IsControl(character) || character is '\n' or '\t')
                builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats a value the agent can influence (a path, branch, host, subject) for putting in a
    /// message: control characters show as escapes, newlines among them.
    /// </summary>
    /// <remarks>
    /// The escapes mean the value can't forge a line of Pen's output. Not for text a shell will
    /// read, which has to keep the real name.
    /// </remarks>
    public static string Field(string value)
    {
        if (!value.Any(IsControl))
            return value;
        var builder = new StringBuilder(value.Length + 8);
        foreach (var character in value)
        {
            if (!IsControl(character))
                builder.Append(character);
            else if (character == '\n')
                builder.Append("\\n");
            else if (character == '\t')
                builder.Append("\\t");
            else if (character == '\r')
                builder.Append("\\r");
            else if (character <= '\xff')
                builder.Append("\\x").Append(((int)character).ToString("x2", CultureInfo.InvariantCulture));
            else
                builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    /// <summary>Describes an exception for the user.</summary>
    /// <remarks>
    /// The AOT build (UseSystemResourceKeys) has framework exceptions' messages as raw resource
    /// keys, so only Pen's own are shown as they are.
    /// </remarks>
    public static string Describe(Exception exception) => exception switch
    {
        PenException => exception.Message,
        UnauthorizedAccessException denied => DescribePermissionDenied(denied),
        _ => exception.GetType().Name,
    };

    /// <summary>Describes a permission error for the user: the path, when it names one.</summary>
    /// <remarks>
    /// The exception's own text can't be shown, since the AOT build (UseSystemResourceKeys)
    /// has it as "UnauthorizedAccess_IODenied_Path, /the/path".
    /// </remarks>
    public static string DescribePermissionDenied(UnauthorizedAccessException exception)
    {
        var pathStart = exception.Message.IndexOf('/', StringComparison.Ordinal);
        if (pathStart < 0)
            return "permission denied";
        var path = exception.Message[pathStart..].Replace("' is denied.", "", StringComparison.Ordinal);
        return $"permission denied: {Field(path)}";
    }

    /// <summary>Writes a question to stderr, with the cursor left after it.</summary>
    public static void Prompt(string question)
    {
        try
        {
            Console.Error.Write(StripControls(question));
            Console.Error.Flush();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Asks "pen: question [y/N]" and reads the answer from <paramref name="input"/>.</summary>
    /// <returns>True only for y or yes.</returns>
    /// <remarks>Without a terminal the answer is echoed, so the transcript reads like an interactive one.</remarks>
    public static bool Confirm(string question, TextReader input)
    {
        Prompt($"pen: {question} [y/N] ");
        string? answer;
        try
        {
            answer = input.ReadLine();
        }
        catch (IOException)
        {
            answer = null;
        }
        if (!Native.IsATty(0))
            WriteRaw(answer ?? "");
        return IsYes(answer);
    }

    /// <summary>Checks whether an answer is y or yes, ignoring case and surrounding spaces.</summary>
    internal static bool IsYes(string? answer) => answer?.Trim().ToLowerInvariant() is "y" or "yes";

    private static void Write(string text)
    {
        try
        {
            Console.Error.WriteLine(StripControls(text));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
