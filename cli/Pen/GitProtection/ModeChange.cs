using System.Text;

namespace Pen.GitProtection;

/// <summary>A directory the check made listable and left that way, with the mode it had.</summary>
internal sealed record ModeChange(string Path, UnixFileMode Was, UnixFileMode Now);
