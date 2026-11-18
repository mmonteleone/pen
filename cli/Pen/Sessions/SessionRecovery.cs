using Pen.Platform;
using Pen.Signing;

namespace Pen.Sessions;

/// <summary>What finishing a project's abandoned sessions found.</summary>
/// <param name="EveryCheckFinished">
/// Whether every ended session's .git check finished; when one didn't, the caller must not run git, and recovery
/// gave no signing reminders.
/// </param>
internal readonly record struct RecoveryResult(bool EveryCheckFinished);

/// <summary>
/// Ends a project's sessions whose Pen crashed or was killed, through <see cref="SessionTeardown"/>, and deletes ended
/// sessions' dirs once they're old.
/// </summary>
/// <remarks>
/// The caller holds whatever project and worktree locks its command takes; each abandoned session is ended holding
/// its own lock.
/// </remarks>
internal static class SessionRecovery
{
    /// <summary>How long an ended session's directory (and so its proxy.log) is kept after its last activity.</summary>
    internal static readonly TimeSpan KeepEndedSessions = TimeSpan.FromDays(7);

    /// <summary>
    /// Ends every session whose Pen crashed or was killed, and deletes ended sessions' dirs after
    /// <see cref="KeepEndedSessions"/>.
    /// </summary>
    /// <returns>Whether every ended session's check finished: if not, neither the caller nor this runs git.</returns>
    /// <remarks>
    /// An abandoned session still has a compose.yaml, but nobody holds its lock. These sessions may share a repo, so
    /// their reminders wait until every check has finished. One whose writers can't be removed throws, keeping it
    /// for a retry, and gives no reminders.
    /// </remarks>
    internal static RecoveryResult FinishAbandoned(string projectKey, string runtimeDir, TimeProvider clock)
    {
        var sessions = Path.Combine(runtimeDir, "sessions");
        if (!Directory.Exists(sessions))
            return new(EveryCheckFinished: true);
        var ended = new List<SessionEndResult>();
        foreach (var path in Directory.EnumerateDirectories(sessions))
        {
            var session = new SessionDir(projectKey, path);
            if (session.IsOpen())
            {
                if (TryEndAbandoned(session) is { } result)
                    ended.Add(result);
            }
            else if (clock.GetUtcNow().UtcDateTime - ReadLastActivity(path) > KeepEndedSessions)
            {
                DeleteEnded(path);
            }
        }
        if (ended.Any(result => !result.CheckFinished))
            return new(EveryCheckFinished: false);
        foreach (var result in ended)
            WriteSigningReminder(result);
        return new(EveryCheckFinished: true);
    }

    /// <summary>Writes the signing reminder for <paramref name="result"/>'s dir, if it has one.</summary>
    /// <remarks>
    /// It runs Pen's own git call (the unsigned-commit count), so only once no check it depends on has failed: this
    /// session's and, in a batch, every other's.
    /// </remarks>
    internal static void WriteSigningReminder(SessionEndResult result)
    {
        if (result.ReminderDir is null)
            return;
        var reminder = BranchSigner.FormatReminderIn(result.ReminderDir);
        if (reminder is not null)
            Out.WriteLine(reminder);
    }

    /// <summary>Ends <paramref name="session"/> if its Pen is gone, holding its lock meanwhile.</summary>
    /// <returns>How ending it went, or null if it's running or ended while this looked.</returns>
    private static SessionEndResult? TryEndAbandoned(SessionDir session)
    {
        using var sessionLock = FileLock.TryTake(session.LockFile, exclusive: true);
        if (sessionLock is null || !session.IsOpen())
            return null; // running, or ended while we looked
        Out.WriteLine($"ending session {session.Id}, whose pen exited without ending it");
        return SessionTeardown.End(session);
    }

    /// <summary>Deletes an ended session's directory, leaving it to the next run if that fails.</summary>
    private static void DeleteEnded(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Reads when a session's directory, or its proxy.log, last changed.</summary>
    private static DateTime ReadLastActivity(string dir)
    {
        var latest = Directory.GetLastWriteTimeUtc(dir);
        var log = Path.Combine(dir, "proxy.log");
        if (File.Exists(log))
        {
            var logTime = File.GetLastWriteTimeUtc(log);
            if (logTime > latest)
                latest = logTime;
        }
        return latest;
    }
}
