using System.Text;

namespace Pen.Platform;

/// <summary>Writes paths into commands the user pastes into a shell.</summary>
internal static class Shell
{
    /// <summary>
    /// Quotes a path as one word of a command the user pastes, in sh, bash, zsh or fish 3.4+, on a name the agent
    /// chose.
    /// </summary>
    /// <remarks>
    /// Plain single quotes where they mean the same in all of them. Otherwise "$(printf '…')" with an octal escape
    /// for all but the plainest characters, since: a control character has to stay out of the terminal; fish reads a
    /// backslash in single quotes differently; printf reads % and \; interactive bash and zsh expand ! inside double
    /// quotes. A leading - would be an option, to mv or to printf.
    /// </remarks>
    internal static string Quote(string path)
    {
        if (path.StartsWith('-'))
            path = "./" + path;
        if (!path.Any(c => Out.IsControl(c) || c == '\\'))
            return "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        var quoted = new StringBuilder("\"$(printf '");
        foreach (var character in path)
        {
            if (char.IsAsciiLetterOrDigit(character)
                || character is '/' or '.' or '_' or '-' or ' ' or ',' or ':' or '+' or '=' or '@'
                || (character > '\x7f' && !Out.IsControl(character)))
            {
                quoted.Append(character);
            }
            else
            {
                foreach (var octet in Encoding.UTF8.GetBytes(character.ToString()))
                    quoted.Append('\\').Append(Convert.ToString(octet, 8).PadLeft(3, '0'));
            }
        }
        return quoted.Append("')\"").ToString();
    }
}
