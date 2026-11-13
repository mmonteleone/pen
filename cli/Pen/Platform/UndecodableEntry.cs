using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using Microsoft.Win32.SafeHandles;
using Pen.CommandLine;

namespace Pen.Platform;

/// <summary>
/// A directory entry whose name isn't valid UTF-8. CouldHoldRepo: a directory,
/// a symlink, or a type the file system doesn't report; not a plain file or a
/// special file, which git can't take for a repo.
/// </summary>
internal sealed record UndecodableEntry(byte[] Name, bool CouldHoldRepo)
{
    /// <summary>The name as .NET's listing shows it.</summary>
    public string Decoded => Encoding.UTF8.GetString(Name);

    /// <summary>Command substitution drops trailing newlines, so no printf command can name this one.</summary>
    public bool EndsInNewline => Name[^1] == (byte)'\n';

    /// <summary>
    /// The exact name as printf(1) text: octal escapes for all but the plainest
    /// bytes, and for a leading -, which printf would read as an option. ASCII
    /// only, so it's safe to print and to paste.
    /// </summary>
    public string Escaped
    {
        get
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Name.Length; i++)
            {
                var b = Name[i];
                if (char.IsAsciiLetterOrDigit((char)b) || b is (byte)'.' or (byte)'_' or (byte)' ' || (b == (byte)'-' && i > 0)) sb.Append((char)b);
                else sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            }
            return sb.ToString();
        }
    }
}
