using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>Prints what a run of the .git check found and changed.</summary>
internal static class GitCheckReport
{
    /// <summary>
    /// Prints what a check found: first what it renamed, put back and opened, then any paused operations, then that
    /// it didn't finish.
    /// </summary>
    /// <param name="dir">The directory checked; paths are shown relative to it.</param>
    /// <param name="check">The check's result.</param>
    public static void Report(string dir, CheckResult check)
    {
        if (check.Moved.Count > 0)
            Out.WriteRaw(GitCheckWording.Warning(dir, check.Moved));
        foreach (var restored in check.Restored)
            Out.WriteLine($"{PathChecks.FormatPath(dir, restored.Path)}: {restored.What}");
        foreach (var change in check.Opened)
        {
            Out.WriteLine(
                $"changed {PathChecks.FormatPath(dir, change.Path)} from mode {FileModes.Octal(change.Was)} " +
                $"to {FileModes.Octal(change.Now)} so it could be checked, and left it that way");
        }

        if (check.Paused.Count > 0)
            Out.WriteRaw(PausedGit.Warning(dir, check.Paused));
        if (check.Failure is not null)
            Out.WriteRaw(GitCheckWording.NotCheckedWarning(dir, check.Failure));
    }
}
