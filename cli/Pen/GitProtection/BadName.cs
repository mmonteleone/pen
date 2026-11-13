using System.IO.Enumeration;
using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// An entry a scan can't look inside, since its name isn't valid UTF-8: .NET's
/// listing has U+FFFD in place of the bad bytes, so that string names nothing
/// (or another entry), while git on this machine can still enter it. Shown is
/// its path for messages, with the name exact (<see cref="UndecodableEntry.Escaped"/>).
/// EndsInNewline: no command Pen could print would name it.
/// </summary>
internal sealed record BadName(string Shown, bool EndsInNewline)
{
    public static BadName At(string dir, UndecodableEntry entry) => new(Path.Combine(dir, entry.Escaped), entry.EndsInNewline);
}
