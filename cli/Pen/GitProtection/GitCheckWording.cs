using System.Text;
using Pen.Platform;
using Pen.Sessions;

namespace Pen.GitProtection;

/// <summary>
/// Words the .git check's warnings: what it moved aside and how to put it back, what to do about a session that kept
/// its copies, and that a directory wasn't checked.
/// </summary>
internal static class GitCheckWording
{
    /// <summary>
    /// Starts a session that does nothing, which first finishes any session left unfinished, or refuses.
    /// </summary>
    internal const string RetryCommand = "pen true";

    /// <summary>Words the loud warning: what moved or was removed, why, and how to put it back.</summary>
    /// <param name="dir">The project dir; paths are shown relative to it.</param>
    /// <param name="moved">What the check renamed or removed.</param>
    public static string Warning(string dir, IReadOnlyList<Quarantined> moved)
    {
        var text = new StringBuilder();
        text.AppendLine("pen: WARNING: this session left git files that " +
            "would make git on this machine run code from the sandbox.");
        text.AppendLine(moved.Any(quarantined => quarantined.Removed)
            ? "pen: They've been moved out of git's way, or removed:"
            : "pen: They've been moved out of git's way:");
        foreach (var quarantined in moved)
        {
            text.Append("  ").Append(PathChecks.FormatPath(dir, quarantined.From));
            if (quarantined.LinkTarget is { } target)
                text.Append(" (a symlink to ").Append(Out.Field(target)).Append(") removed");
            else if (!quarantined.Failed)
                text.Append(" → ").Append(PathChecks.FormatPath(dir, quarantined.To));
            text.AppendLine().Append("      ").AppendLine(quarantined.Why);
        }
        var restorable = moved.Where(quarantined => !quarantined.Failed).ToList();
        if (restorable.Count > 0)
        {
            text.AppendLine("pen: Inspect each one before putting it back (a hook or a " +
                "core.* setting in its config runs on this machine). To restore one:");
            foreach (var quarantined in restorable)
                AppendRestoreCommand(text, quarantined);
        }
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Appends the line that puts <paramref name="quarantined"/> back, or says why there's none.</summary>
    private static void AppendRestoreCommand(StringBuilder text, Quarantined quarantined)
    {
        // Command substitution drops trailing newlines, so no quoting
        // that works in every shell can name this one.
        if (quarantined.From.EndsWith('\n') || quarantined.LinkTarget?.EndsWith('\n') == true)
        {
            text.Append("  pen can't write a command for ").Append(Out.Field(quarantined.From)).AppendLine(
                quarantined.Removed
                    ? ", since its name or what it held ends in a newline: make it again by hand"
                    : ", since the name ends in a newline: rename it back by hand");
            return;
        }
        if (quarantined.LinkTargetEscaped)
        {
            text.Append("  pen can't write a command for ").Append(Out.Field(quarantined.From)).AppendLine(
                ", since what it held isn't valid UTF-8 (shown above with octal escapes): make it again by hand");
            return;
        }
        if (quarantined.LinkTarget is { } target)
        {
            text.Append("  ln -s ").Append(Shell.Quote(target)).Append(' ').Append(Shell.Quote(quarantined.From))
                .AppendLine();
            return;
        }
        text.Append("  mv ").Append(Shell.Quote(quarantined.To)).Append(' ').Append(Shell.Quote(quarantined.From));
        if (quarantined.Inner is { } inner)
        {
            text.Append(" && mv ").Append(Shell.Quote(Path.Combine(quarantined.From, Path.GetFileName(inner.To))))
                .Append(' ').Append(Shell.Quote(Path.Combine(quarantined.From, Path.GetFileName(inner.From))));
        }

        text.AppendLine();
    }

    /// <summary>
    /// Words what to do about a session whose protected files couldn't all be put back and which keeps its copies.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="blocked">The directories whose modes stopped the restore, or null if none are known.</param>
    /// <remarks>
    /// First the way that uses the copies, in one command so that nothing (a shell prompt) runs git between it and
    /// Pen's retry. Only then the way that gives them up.
    /// </remarks>
    public static string KeptCopiesAdvice(SessionDir session, IReadOnlyList<string>? blocked)
    {
        // A linked worktree's key is its main checkout's, and only there is Pen sure to find this session.
        var repoDir = session.ReadRepoDir();
        var retry = (repoDir is null ? "" : $"cd {Shell.Quote(repoDir)} && ") + RetryCommand;
        var fix = blocked is { Count: > 0 }
            ? $"chmod u+rwx {string.Join(' ', blocked.Select(Shell.Quote))} && {retry}"
            : $"deal with what's named above, then: {retry}";
        return $"""
            pen: To restore the good copies pen kept, run this, as one line: {fix}
            pen: Only if that can't work: rm -r {Shell.Quote(session.Path)} discards them and accepts what's on disk. Compare first with the copies in {Shell.Quote(session.ProtectedDir)} (manifest.json lists them).
            """;
    }

    /// <summary>Words the loud warning for a check that couldn't vouch for <paramref name="dir"/>.</summary>
    /// <param name="dir">The directory the check was for.</param>
    /// <param name="why">What stopped it.</param>
    public static string NotCheckedWarning(string dir, string why) =>
        $"""
        pen: WARNING: git wasn't checked: don't run git here ({Out.Field(dir)}).
        pen: The check for files that make git on this machine run code from the sandbox couldn't finish: {why}
        pen: Your shell prompt and IDE may run git too, so keep them out of this directory. Inspect its .git (config, hooks, modules, rebase-merge, rebase-apply, sequencer) and any repos nested in it by hand first.
        """;
}
