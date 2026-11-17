using Pen.GitProtection;
using Pen.Platform;
using Pen.Projects;
using Pen.Signing;
using Pen.Sessions;

namespace Pen.Commands;

/// <summary>Runs pen sign: re-creates the branch's unpushed commits on the host with the same trees, signed.</summary>
/// <param name="host">The host user's environment.</param>
/// <param name="project">The project to sign in. Its .pen/config.toml is never read.</param>
/// <param name="clock">The clock that says when an ended session's files are old enough to delete.</param>
/// <remarks>
/// The order matters: the locks (project.lock shared, so pen clean can't run, and this dir's worktree lock
/// exclusive, so no session runs here), then finishing dead sessions, then the session-end check of
/// <c>.git</c>, and only then any host git, all of it through <see cref="HostGit"/>'s overrides.
/// </remarks>
internal sealed class SignCommand(HostEnv host, Project project, TimeProvider clock)
{
    /// <summary>Checks the repo, then signs the checked-out branch's commits after the base.</summary>
    /// <param name="baseArg">The base named on the command line, or null for the upstream or origin/HEAD.</param>
    /// <returns>0 once signed or when there's nothing to sign; failures throw.</returns>
    /// <exception cref="PenException">
    /// It isn't a repo, it's locked, the repo fails its check, or signing fails; nothing was changed.
    /// </exception>
    public int Run(string? baseArg)
    {
        if (!project.IsRepo)
            throw new PenException($"{Out.Field(project.Dir)} isn't a git repo");
        Session.EnsureRuntimeDir(host, project);
        using var locks = SessionRegistry.TakeForSign(host, project);
        // This command's own check below takes the module repos there now, so
        // it can't stand in for an ended session's that didn't finish.
        if (!Session.FinishDeadSessions(project.Key, host.GetRuntimeDir(project.Key), clock))
            throw new PenException("not signing: deal with the warning above first");
        CheckGitDir();
        return new BranchSigner(new HostGit(project.Dir)).Sign(baseArg);
    }

    /// <summary>Runs the session-end check of <c>.git</c> before any host git, and reports what it found.</summary>
    /// <exception cref="PenException">It moved anything aside or couldn't finish.</exception>
    private void CheckGitDir()
    {
        var check = GitCheck.RunWithoutOpening(project.Dir, startedWithGit: true, project.CommonGitDir,
            modulesAtStart: null);
        if (check.Moved.Count > 0 || check.Failure is not null)
        {
            GitCheckReport.Report(project.Dir, check);
            throw new PenException("not signing: deal with the files above first");
        }
        // Paused operations it reports aren't a reason to refuse: signing doesn't continue them.
        GitCheckReport.Report(project.Dir, check);
    }
}
