using System.Diagnostics.CodeAnalysis;
using System.Text;
using Pen.Platform;
using Pen.Sessions;

namespace Pen.GitProtection;

/// <summary>
/// Runs the .git check that ends a session or precedes pen sign, and words what it found.
/// </summary>
/// <remarks>
/// The check reads files directly and never runs git, and it renames whatever would make host git read config or
/// hooks the agent could write, before the shell prompt or an IDE runs git there. It runs before Pen's own git calls.
/// Each run is a <see cref="GitCheckRun"/>.
/// </remarks>
internal static class GitCheck
{
    /// <summary>The suffix of the names the check renames things to.</summary>
    public const string Suffix = ".pen-quarantine";

    /// <summary>
    /// Starts a session that does nothing, which first finishes any session left unfinished, or refuses.
    /// </summary>
    internal const string RetryCommand = "pen true";

    /// <summary>
    /// Checks <paramref name="dir"/> and quarantines what it flags, changing no modes, reporting whether it finished.
    /// </summary>
    /// <param name="dir">The project dir.</param>
    /// <param name="startedWithGit">Whether the session started with a .git there.</param>
    /// <param name="commonGitDir">
    /// The git dir it started with, or null for dir/.git. For a linked worktree it's the main checkout's: the
    /// .git-level checks run on it, and worktrees of it in the tree are the project's own.
    /// </param>
    /// <param name="modulesAtStart">
    /// The submodule repos in that git dir when the session started: any other is quarantined, and the checkouts of
    /// those left are the project's own. Null (pen sign, which doesn't end a session) takes the ones there now.
    /// </param>
    /// <returns>
    /// The result, with <see cref="CheckResult.Opened"/>, <see cref="CheckResult.Restored"/> and
    /// <see cref="CheckResult.RestoreBlocked"/> empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// It changes no modes: a session may still be running in another worktree of the repo, so a directory that
    /// can't be listed is a Failure rather than something to open.
    /// </para>
    /// <para>
    /// No config.worktree is touched: with no record of the start, one can't be told from the user's.
    /// </para>
    /// </remarks>
    public static CheckResult RunWithoutOpening(
        string dir,
        bool startedWithGit,
        string? commonGitDir,
        IReadOnlyCollection<string>? modulesAtStart) =>
        new GitCheckRun(dir, mayOpen: false).RunWithoutRestoring(startedWithGit, commonGitDir, modulesAtStart);

    /// <summary>
    /// Checks <paramref name="dir"/> for a session that's ending, against what <paramref name="session"/> recorded
    /// at its start, first putting its protected files back.
    /// </summary>
    /// <returns>The result; the caller must not run git unless its Failure is null.</returns>
    /// <remarks>See <see cref="GitCheckRun.RunAtSessionEnd"/>. Never throws.</remarks>
    public static CheckResult RunAtSessionEnd(string dir, SessionDir session) =>
        new GitCheckRun(dir, mayOpen: true).RunAtSessionEnd(session);

    /// <summary>Adds u+rx to a directory, and u+w if <paramref name="write"/>.</summary>
    /// <param name="path">The directory.</param>
    /// <param name="write">Whether to add u+w too.</param>
    /// <param name="change">The mode change made, when it returns true.</param>
    /// <returns>
    /// Whether it changed the mode: false for a symlink, a directory that already has the bits, or one it can't
    /// change.
    /// </returns>
    /// <remarks>
    /// Never through a symlink, which an overlapping session could have swapped in since the listing: chmod would
    /// change whatever it points to. Checking just before is the portable way, since glibc's fchmodat can't do
    /// AT_SYMLINK_NOFOLLOW.
    /// </remarks>
    internal static bool TryOpen(string path, bool write, [NotNullWhen(true)] out ModeChange? change)
    {
        change = null;
        try
        {
            if (RepoGuard.IsLink(path))
                return false;
            var was = File.GetUnixFileMode(path);
            var added = UnixFileMode.UserRead | UnixFileMode.UserExecute;
            if (write)
                added |= UnixFileMode.UserWrite;
            var now = was | added;
            if (now == was)
                return false; // the mode isn't why it can't be listed
            File.SetUnixFileMode(path, now);
            change = new(path, was, now);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Renames <paramref name="path"/> to a free "….pen-quarantine" name.</summary>
    /// <param name="path">What to move.</param>
    /// <param name="why">Why it's moved, for the warning.</param>
    /// <param name="breakLayout">
    /// Whether it's a .git directory, whose HEAD goes too: renamed, it would still look like a bare repo to git.
    /// </param>
    /// <returns>
    /// The rename made, or one with an empty <see cref="Quarantined.To"/> whose Why says it couldn't be moved.
    /// </returns>
    /// <remarks>
    /// Never fatal: the rest still gets checked, and the warning names what couldn't be moved.
    /// </remarks>
    internal static Quarantined Move(string path, string why, bool breakLayout)
    {
        try
        {
            var to = FreeName(path);
            Native.Rename(path, to);
            var inner = breakLayout ? MoveHeadAside(to) : null;
            return new(path, to, why, inner);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Native.Rename's message is strerror's.
            var reason = exception is UnauthorizedAccessException denied
                ? Out.DescribePermissionDenied(denied)
                : exception.Message;
            return new(path, "", $"{why}. COULD NOT MOVE IT ({reason}): " +
                "don't run git here until you've dealt with it");
        }
    }

    /// <summary>Removes the symlink <paramref name="path"/>, keeping what it held for the warning.</summary>
    /// <param name="root">The project dir, which <paramref name="path"/> is below.</param>
    /// <param name="path">The symlink.</param>
    /// <param name="why">Why it's removed, for the warning.</param>
    /// <returns>
    /// The removal made, or one with neither <see cref="Quarantined.To"/> nor <see cref="Quarantined.LinkTarget"/>
    /// whose Why says it couldn't be removed.
    /// </returns>
    /// <remarks>
    /// Renamed, a symlink would lead to the same place, so it's removed instead: it holds no data. Only ever a
    /// symlink is removed, in the directory reached from <paramref name="root"/> without following a symlink
    /// (<see cref="Native.RemoveSymlink"/>), so another session's agent can't swap a directory on the way for one
    /// that leads elsewhere; anything but a symlink there now is left, and the removal fails. Never fatal, like
    /// <see cref="Move"/>.
    /// </remarks>
    internal static Quarantined RemoveLink(string root, string path, string why)
    {
        try
        {
            var target = Native.RemoveSymlink(root, path);
            return StrictUtf8.DecodeOrNull(target) is { } text
                ? new(path, "", why, LinkTarget: text)
                : new(path, "", why, LinkTarget: EscapeTarget(target), LinkTargetEscaped: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var reason = exception is UnauthorizedAccessException denied
                ? Out.DescribePermissionDenied(denied)
                : exception.Message;
            return new(path, "", $"{why}. COULD NOT REMOVE IT ({reason}): " +
                "don't run git here until you've dealt with it");
        }
    }

    /// <summary>Shows a target that isn't valid UTF-8: octal escapes for all but printable ASCII.</summary>
    private static string EscapeTarget(byte[] target)
    {
        var text = new StringBuilder();
        foreach (var value in target)
        {
            if (value is >= 0x20 and < 0x7f && value != (byte)'\\')
                text.Append((char)value);
            else
                text.Append('\\').Append(Convert.ToString(value, 8).PadLeft(3, '0'));
        }
        return text.ToString();
    }

    /// <summary>Renames the HEAD inside a .git directory that was just moved to <paramref name="moved"/>.</summary>
    /// <returns>The rename made, or null if it isn't a real directory or has no HEAD.</returns>
    private static (string From, string To)? MoveHeadAside(string moved)
    {
        if (!Directory.Exists(moved) || RepoGuard.IsLink(moved))
            return null;
        var head = Path.Combine(moved, "HEAD");
        if (!RepoGuard.Exists(head))
            return null;
        var headTo = FreeName(head);
        Native.Rename(head, headTo);
        return (head, headTo);
    }

    /// <summary>Finds a name beside <paramref name="path"/> to quarantine it under.</summary>
    /// <returns>path.pen-quarantine, or the first of path.pen-quarantine-2, -3, … that nothing has.</returns>
    internal static string FreeName(string path)
    {
        var to = path + Suffix;
        for (var i = 2; RepoGuard.Exists(to); i++)
            to = $"{path}{Suffix}-{i}";
        return to;
    }

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
            text.Append("  ").Append(RepoGuard.FormatPath(dir, quarantined.From));
            if (quarantined.LinkTarget is { } target)
                text.Append(" (a symlink to ").Append(Out.Field(target)).Append(") removed");
            else if (!quarantined.Failed)
                text.Append(" → ").Append(RepoGuard.FormatPath(dir, quarantined.To));
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
            text.Append("  ln -s ").Append(Quote(target)).Append(' ').Append(Quote(quarantined.From)).AppendLine();
            return;
        }
        text.Append("  mv ").Append(Quote(quarantined.To)).Append(' ').Append(Quote(quarantined.From));
        if (quarantined.Inner is { } inner)
        {
            text.Append(" && mv ").Append(Quote(Path.Combine(quarantined.From, Path.GetFileName(inner.To))))
                .Append(' ').Append(Quote(Path.Combine(quarantined.From, Path.GetFileName(inner.From))));
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
        var retry = (repoDir is null ? "" : $"cd {Quote(repoDir)} && ") + RetryCommand;
        var fix = blocked is { Count: > 0 }
            ? $"chmod u+rwx {string.Join(' ', blocked.Select(Quote))} && {retry}"
            : $"deal with what's named above, then: {retry}";
        return $"""
            pen: To restore the good copies pen kept, run this, as one line: {fix}
            pen: Only if that can't work: rm -r {Quote(session.Path)} discards them and accepts what's on disk. Compare first with the copies in {Quote(session.ProtectedDir)} (manifest.json lists them).
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

    /// <summary>
    /// Prints what a check found: first what it renamed, put back and opened, then any paused operations, then that
    /// it didn't finish.
    /// </summary>
    /// <param name="dir">The directory checked; paths are shown relative to it.</param>
    /// <param name="check">The check's result.</param>
    public static void Report(string dir, CheckResult check)
    {
        if (check.Moved.Count > 0)
            Out.WriteRaw(Warning(dir, check.Moved));
        foreach (var restored in check.Restored)
            Out.WriteLine($"{RepoGuard.FormatPath(dir, restored.Path)}: {restored.What}");
        foreach (var change in check.Opened)
        {
            Out.WriteLine($"changed {RepoGuard.FormatPath(dir, change.Path)} from mode {Octal(change.Was)} " +
                $"to {Octal(change.Now)} so it could be checked, and left it that way");
        }

        if (check.Paused.Count > 0)
            Out.WriteRaw(PausedGit.Warning(dir, check.Paused));
        if (check.Failure is not null)
            Out.WriteRaw(NotCheckedWarning(dir, check.Failure));
    }

    /// <summary>Formats a file mode in octal, as chmod takes it.</summary>
    internal static string Octal(UnixFileMode mode) => Convert.ToString((int)mode, 8);

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
