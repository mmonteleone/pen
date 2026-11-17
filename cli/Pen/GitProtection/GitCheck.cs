using Pen.Sessions;

namespace Pen.GitProtection;

/// <summary>
/// Runs the .git check that ends a session or precedes pen sign.
/// </summary>
/// <remarks>
/// The check reads files directly and never runs git, and it renames whatever would make host git read config or
/// hooks the agent could write, before the shell prompt or an IDE runs git there. It runs before Pen's own git calls.
/// Each run is a <see cref="GitCheckRun"/>; <see cref="GitCheckReport"/> prints what it found.
/// </remarks>
internal static class GitCheck
{
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
    /// The result, with <see cref="CheckResult.ModeChanges"/>, <see cref="CheckResult.Restored"/> and
    /// <see cref="CheckResult.RestoreBlocked"/> empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// It changes no modes: a session may still be running in another worktree of the repo, so a directory that
    /// can't be listed is a Failure rather than something to make listable.
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
        new GitCheckRun(dir, mayChangeModes: false).RunWithoutRestoring(startedWithGit, commonGitDir, modulesAtStart);

    /// <summary>
    /// Checks <paramref name="dir"/> for a session that's ending, against what <paramref name="session"/> recorded
    /// at its start, first putting its protected files back.
    /// </summary>
    /// <returns>The result; the caller must not run git unless its Failure is null.</returns>
    /// <remarks>See <see cref="GitCheckRun.RunAtSessionEnd"/>. Never throws.</remarks>
    public static CheckResult RunAtSessionEnd(string dir, SessionDir session) =>
        new GitCheckRun(dir, mayChangeModes: true).RunAtSessionEnd(session);
}
