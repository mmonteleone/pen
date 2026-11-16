using Pen.Configuration;

namespace Pen.Sessions;

/// <summary>Decides when a session's start suggests pen init: once per project, while it has no config.</summary>
internal static class InitHint
{
    /// <summary>The file in a project's runtime dir that records the hint was shown.</summary>
    public const string MarkerName = "init-hint";

    /// <summary>The hint.</summary>
    public const string Message =
        "no .pen/config.toml here: pen init writes one with this project's tools and hidden files";

    private const UnixFileMode File600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Checks whether to show the hint and, when it's shown, records that in the runtime dir.</summary>
    /// <param name="penDir">
    /// The project dir's .pen (<c>Project.PenDir</c>): the worktree's own in a linked worktree.
    /// </param>
    /// <param name="runtimeDir">The project's runtime dir, shared by its linked worktrees.</param>
    /// <returns>
    /// True when <paramref name="penDir"/> has neither config.toml nor the legacy config (each counted as there
    /// whatever it is, a dangling symlink included) and the marker wasn't there. False otherwise, and when a check
    /// fails.
    /// </returns>
    /// <remarks>
    /// The marker is created exclusively, so two sessions starting together show it once. A failure to create it
    /// for any other reason is ignored and the hint is shown, so it shows on each start while the runtime dir can't
    /// be written. Nothing here throws: the hint must never stop a session.
    /// </remarks>
    public static bool TryClaim(string penDir, string runtimeDir)
    {
        try
        {
            // pen init refuses beside a legacy config, so it isn't suggested there.
            if (IsPresent(Path.Combine(penDir, Config.FileName)) || IsPresent(Path.Combine(penDir, Config.LegacyName)))
                return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var marker = Path.Combine(runtimeDir, MarkerName);
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = File600,
            };
            using (new FileStream(marker, options))
            {
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return !IsPresentOrUnknown(marker);
        }
    }

    /// <summary>Checks whether anything is at <paramref name="path"/>, without following a final symlink.</summary>
    private static bool IsPresent(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget != null;

    /// <summary>Checks whether anything is at <paramref name="path"/>, counting a failed check as yes.</summary>
    private static bool IsPresentOrUnknown(string path)
    {
        try
        {
            return IsPresent(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
