using System.Text;

namespace Pen.Platform;

/// <summary>A directory entry whose name isn't valid UTF-8.</summary>
/// <param name="Name">The name's exact bytes.</param>
/// <param name="CouldHoldRepo">
/// Whether it's a directory, a symlink, or a type the file system doesn't report; not a plain file or a special file,
/// which git can't take for a repo.
/// </param>
internal sealed record UndecodableEntry(byte[] Name, bool CouldHoldRepo)
{
    /// <summary>Gets the name as .NET's listing shows it.</summary>
    public string DecodedName => Encoding.UTF8.GetString(Name);

    /// <summary>
    /// Gets whether the name ends in a newline, which no printf command can name, since command substitution drops
    /// trailing newlines.
    /// </summary>
    public bool EndsInNewline => Name[^1] == (byte)'\n';

    /// <summary>Gets the exact name as printf(1) text.</summary>
    /// <remarks>
    /// Octal escapes for all but the plainest bytes, and for a leading -, which printf would read as an option. ASCII
    /// only, so it's safe to print and to paste.
    /// </remarks>
    public string EscapedName
    {
        get
        {
            var builder = new StringBuilder();
            for (var i = 0; i < Name.Length; i++)
            {
                var value = Name[i];
                if (char.IsAsciiLetterOrDigit((char)value) || value is (byte)'.' or (byte)'_' or (byte)' '
                    || (value == (byte)'-' && i > 0))
                {
                    builder.Append((char)value);
                }
                else
                {
                    builder.Append('\\').Append(Convert.ToString(value, 8).PadLeft(3, '0'));
                }
            }
            return builder.ToString();
        }
    }
}
