using Pen.GitProtection;
using Pen.Platform;
using Pen.Proxy;

namespace Pen.Sessions;

/// <summary>
/// Ends a live or abandoned session: stops what could write, checks .git, stops the stack, reports what the proxy
/// blocked and deletes the session's files unless they must stay for a retry.
/// </summary>
/// <remarks>
/// It never runs git itself, not even for the signing reminder: whether that's safe can depend on other sessions'
/// checks in the same repo, so the caller decides from the result.
/// </remarks>
internal static class SessionTeardown
{
    /// <summary>
    /// How the session-end .git check went, and whether the session's files stay for a retry because putting files
    /// back didn't finish.
    /// </summary>
    private sealed record EndCheck(SessionEndResult Result, bool KeepFiles);

    /// <summary>
    /// Ends a live or abandoned session: stops what could write, checks .git, stops the stack and cleans up.
    /// </summary>
    /// <returns>Whether the check finished, and the dir for the signing reminder.</returns>
    /// <remarks>
    /// <para>The agent container goes first (after a hangup compose may have left it running), and in a docker
    /// session dind with it, since containers inside dind can write to the project until it's gone. If that fails,
    /// the session's files are kept for a retry and this throws, before Git is checked. Then the .git check, then
    /// the stack, then the session's files except the proxy logs.</para>
    /// <para>The caller holds the session's lock and releases it after. A check that can't finish doesn't stop
    /// any of this: it warns, and the session ends.</para>
    /// </remarks>
    internal static SessionEndResult End(SessionDir session)
    {
        var env = session.ReadEnv();
        RemoveWriters(session);
        // The .git check, now that the agent is gone and before anything runs git.
        var check = CheckGit(session, env);
        // What the session ended up allowing and denying (pen allow may have changed it), so the blocked line doesn't
        // suggest a domain that's allowed by now, and says which hosts deny blocked.
        var egress = session.ReadEgress();
        StopStack(session, env, check.KeepFiles);
        ReportBlocked(session, egress.Allow, egress.Deny);
        return check.Result;
    }

    /// <summary>Removes the agent's container and, in a docker session, dind's.</summary>
    /// <remarks>
    /// Any failure throws once both were tried: the session is kept for a retry, and Git isn't checked.
    /// </remarks>
    private static void RemoveWriters(SessionDir session)
    {
        var containers = new List<string> { session.Container };
        if (File.Exists(session.DockerMarker))
            containers.Add(session.DindContainer);
        var failures = new List<string>();
        foreach (var container in containers)
        {
            try
            {
                Docker.RemoveWriter(container);
            }
            catch (PenException error)
            {
                failures.Add(error.Message);
            }
        }
        if (failures.Count > 0)
        {
            throw new PenException($"{string.Join("\n", failures)}\nsession {session.Id} is " +
                "retained for retry by the next pen command here; Git has not been checked");
        }
    }

    /// <summary>
    /// Runs the session-end .git check in the project dir that PROJECT_DIR in <paramref name="env"/>, the env the
    /// session started with, names, and reports it.
    /// </summary>
    /// <returns>
    /// The end result (no reminder unless the check finished, moved nothing and put nothing back), and whether the
    /// session's files must stay.
    /// </returns>
    /// <remarks>
    /// Without a project dir there's nothing to check, unless the session saved protected files: then its record
    /// is damaged, so they weren't compared, and its files stay.
    /// </remarks>
    private static EndCheck CheckGit(SessionDir session, Dictionary<string, string> env)
    {
        if (env.TryGetValue("PROJECT_DIR", out var projectDir))
        {
            var check = GitCheck.RunAtSessionEnd(projectDir, session);
            GitCheckReport.Print(projectDir, check);
            var isClean = check.Failure is null && check.Moved.Count == 0 && check.Restored.Count == 0;
            var result = new SessionEndResult(check.Failure is null, isClean ? projectDir : null);
            if (check.RestoreIncomplete)
                Out.WriteRaw(GitCheckWording.KeptCopiesAdvice(session, check.RestoreBlocked));
            return new(result, check.RestoreIncomplete);
        }

        if (File.Exists(session.ProtectedManifest))
        {
            Out.WriteRaw(GitCheckWording.NotCheckedWarning(session.Path, "the session's record " +
                "of its project is damaged, so its protected files weren't compared"));
            Out.WriteRaw(GitCheckWording.KeptCopiesAdvice(session, null));
            return new(new(false, null), KeepFiles: true);
        }
        return new(new(true, null), KeepFiles: false);
    }

    /// <summary>Stops the session's stack, then deletes its files unless <paramref name="keepFiles"/>.</summary>
    /// <remarks>
    /// A stack that won't stop, or whose compose down can't start, keeps the files, for a retry by the next pen
    /// command.
    /// </remarks>
    private static void StopStack(SessionDir session, Dictionary<string, string> env, bool keepFiles)
    {
        if (env.Count == 0)
        {
            if (!keepFiles)
                SessionFiles.Delete(session);
            return;
        }

        string? failure;
        try
        {
            var down = new ComposeClient(session, env).Capture("down", "-t", "1", "--remove-orphans");
            failure = down.ExitStatus.ExitCode != 0 ? down.StandardError.Trim() : null;
        }
        catch (PenException error)
        {
            failure = error.Message;
        }
        if (failure is not null)
        {
            Out.WriteLine($"stopping session {session.Id}'s stack failed: " +
                $"{failure} (it will be retried by the next pen command here)");
        }
        else if (!keepFiles)
        {
            SessionFiles.Delete(session);
        }
    }

    /// <summary>
    /// Writes the lines about what the proxy blocked and isn't in <paramref name="allowed"/>, if anything, with the
    /// hosts <paramref name="denied"/> covers apart (<see cref="Allowlist.FormatBlockedLines"/>).
    /// </summary>
    private static void ReportBlocked(SessionDir session, IReadOnlySet<string> allowed, IReadOnlySet<string> denied)
    {
        if (!File.Exists(session.ProxyLog) && !File.Exists(session.PreviousProxyLog))
            return;
        var blocked = Allowlist.FindBlockedHosts(session.ReadProxyLogLines());
        foreach (var line in Allowlist.FormatBlockedLines(blocked, allowed, denied))
            Out.WriteLine(line);
    }
}
