using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>An entry a scan can't look inside, since its name isn't valid UTF-8.</summary>
/// <param name="DisplayPath">
/// The entry's path for messages, with its name exact (<see cref="UndecodableEntry.EscapedName"/>).
/// </param>
/// <param name="EndsInNewline">Whether its name ends in a newline, so no command Pen could print would name it.</param>
/// <remarks>
/// .NET's listing has U+FFFD in place of the bad bytes, so that string names nothing (or another entry), while git
/// on this machine can still enter it.
/// </remarks>
internal sealed record BadName(string DisplayPath, bool EndsInNewline)
{
    /// <summary>Creates the bad name for <paramref name="entry"/>, listed in <paramref name="dir"/>.</summary>
    public static BadName FromEntry(string dir, UndecodableEntry entry) =>
        new(Path.Combine(dir, entry.EscapedName), entry.EndsInNewline);
}
