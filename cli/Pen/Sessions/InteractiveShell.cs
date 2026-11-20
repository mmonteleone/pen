namespace Pen.Sessions;

/// <summary>Picks the shell a session runs when it's given no command.</summary>
internal static class InteractiveShell
{
    /// <summary>Gets the login shell command matching the host's $SHELL (null when it's unset).</summary>
    /// <returns>
    /// fish -l, zsh -l or bash -l when the last path component of <paramref name="hostShell"/> is exactly fish, zsh
    /// or bash; bash -l for anything else.
    /// </returns>
    /// <remarks>
    /// Only these literals reach the container: the host's value is matched, never passed on, so a shell the image
    /// lacks or a crafted $SHELL can't become the sandbox's command.
    /// </remarks>
    public static IReadOnlyList<string> For(string? hostShell)
    {
        var name = Path.GetFileName(hostShell);
        if (name == "fish")
            return ["fish", "-l"];
        if (name == "zsh")
            return ["zsh", "-l"];
        return ["bash", "-l"];
    }
}
