using Pen.Platform;
using Pen.Projects;

namespace Pen.Sessions;

/// <summary>Finds a project's sessions, and takes the locks sessions, pen sign and pen clean hold.</summary>
internal static class SessionRegistry
{
    /// <summary>Finds the project's running sessions.</summary>
    /// <returns>
    /// The sessions that are open (compose.yaml still there) with their session.lock held by a live Pen, by ID.
    /// </returns>
    public static List<SessionDir> FindRunning(string projectKey, string runtimeDir)
    {
        var running = new List<SessionDir>();
        foreach (var session in FindOpen(projectKey, runtimeDir))
        {
            using var probe = FileLock.TryTake(session.LockFile, exclusive: true);
            if (probe is null)
                running.Add(session);
        }
        return running;
    }

    /// <summary>Finds the project's open sessions, whether or not their Pen is still running.</summary>
    /// <returns>The sessions whose compose.yaml is there, by ID.</returns>
    /// <remarks>
    /// One whose Pen died is open until the next run ends it, and its agent's container may still be running, so
    /// whatever has to allow for what other sessions' agents could write goes by this.
    /// </remarks>
    public static List<SessionDir> FindOpen(string projectKey, string runtimeDir)
    {
        var open = new List<SessionDir>();
        var sessions = Path.Combine(runtimeDir, "sessions");
        if (!Directory.Exists(sessions))
            return open;
        foreach (var path in Directory.EnumerateDirectories(sessions).Order(StringComparer.Ordinal))
        {
            var session = new SessionDir(projectKey, path);
            if (session.IsOpen)
                open.Add(session);
        }
        return open;
    }

    /// <summary>Finds every project's running sessions, for pen ps and pen allow --global.</summary>
    public static List<SessionDir> FindRunningEverywhere(HostEnv host)
    {
        var all = new List<SessionDir>();
        if (!Directory.Exists(host.StateRoot))
            return all;
        foreach (var dir in Directory.EnumerateDirectories(host.StateRoot).Order(StringComparer.Ordinal))
            all.AddRange(FindRunning(Path.GetFileName(dir), dir));
        return all;
    }

    /// <summary>
    /// Finds the project's running sessions started in <paramref name="dir"/> (by their PROJECT_DIR).
    /// </summary>
    /// <returns>The sessions; for a linked worktree, only that worktree's.</returns>
    public static List<SessionDir> FindRunningIn(string projectKey, string runtimeDir, string dir) =>
        FindRunning(projectKey, runtimeDir)
            .Where(session => session.ReadEnv().TryGetValue("PROJECT_DIR", out var started) && started == dir)
            .ToList();

    /// <summary>Gets the path of the project's project.lock.</summary>
    public static string GetProjectLockFile(string runtimeDir) => Path.Combine(runtimeDir, "project.lock");

    /// <summary>
    /// Gets the path of the lock for one dir (the main checkout or a linked worktree) of the project.
    /// </summary>
    public static string GetWorktreeLockFile(string runtimeDir, string dir) =>
        Path.Combine(runtimeDir, $"worktree-{Project.HashPath(dir)}.lock");

    /// <summary>Takes what a session holds for its whole run.</summary>
    /// <returns>project.lock shared, then its dir's worktree lock shared.</returns>
    /// <remarks>pen clean takes project.lock exclusive, and pen sign in the same dir the worktree lock.</remarks>
    public static Locks TakeForSession(string runtimeDir, Project project)
    {
        var locks = new Locks();
        try
        {
            locks.Add(FileLock.TryTake(GetProjectLockFile(runtimeDir), exclusive: false)
                ?? throw new PenException("a pen clean is running in this project: try again once it's done"));
            locks.Add(FileLock.TryTake(GetWorktreeLockFile(runtimeDir, project.Dir), exclusive: false)
                ?? throw new PenException("a pen sign is running in this directory: try again once it's done"));
            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    /// <summary>Takes what pen sign holds for its whole run.</summary>
    /// <returns>project.lock shared (so no pen clean runs), then its dir's worktree lock exclusive.</returns>
    /// <remarks>
    /// It refuses while a session runs in this dir; sessions in the project's other worktrees carry on.
    /// </remarks>
    public static Locks TakeForSign(HostEnv host, Project project)
    {
        var runtimeDir = host.GetRuntimeDir(project.Key);
        var locks = new Locks();
        try
        {
            locks.Add(FileLock.TryTake(GetProjectLockFile(runtimeDir), exclusive: false)
                ?? throw new PenException("refusing to sign: a pen clean " +
                    "is running in this project: try again once it's done"));
            var worktreeLock = FileLock.TryTake(GetWorktreeLockFile(runtimeDir, project.Dir), exclusive: true);
            if (worktreeLock is null)
            {
                var running = FindRunningIn(project.Key, runtimeDir, project.Dir);
                throw new PenException(running.Count == 0
                    ? "refusing to sign: another pen sign is running in this directory: try again once it's done"
                    : DescribeRefusal("sign", running, "this directory"));
            }
            locks.Add(worktreeLock);
            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    /// <summary>Takes project.lock exclusive, for the whole of pen clean.</summary>
    /// <remarks>
    /// It refuses while any session (or pen sign) runs in any of the project's dirs, and none starts until it's
    /// released.
    /// </remarks>
    public static FileLock TakeForClean(HostEnv host, Project project)
    {
        var runtimeDir = host.GetRuntimeDir(project.Key);
        var projectLock = FileLock.TryTake(GetProjectLockFile(runtimeDir), exclusive: true);
        if (projectLock is not null)
            return projectLock;

        var running = FindRunning(project.Key, runtimeDir);
        throw new PenException(running.Count == 0
            ? "refusing to clean: a pen sign or another pen clean is running in this project: try again once it's done"
            : DescribeRefusal("clean", running, "this project"));
    }

    /// <summary>Describes why <paramref name="what"/> is refused while <paramref name="running"/> run.</summary>
    private static string DescribeRefusal(string what, List<SessionDir> running, string where) =>
        $"refusing to {what} while {(running.Count == 1 ? "a session is" : $"{running.Count} sessions are")} " +
            $"running in {where}:\n"
        + string.Join('\n', running.Select(session => "  " + session.Container))
        + "\nexit them first (or, for one you can't reach, docker rm -f <name>: its pen then ends the session)";

    /// <summary>Several locks held together, released in reverse order.</summary>
    internal sealed class Locks : IDisposable
    {
        private readonly List<FileLock> _held = [];

        /// <summary>Gets the locks held, in the order they were taken.</summary>
        public IReadOnlyList<FileLock> Held => _held;

        /// <summary>Adds <paramref name="fileLock"/> to the locks held.</summary>
        public void Add(FileLock fileLock) => _held.Add(fileLock);

        /// <summary>Releases the locks, the last taken first.</summary>
        public void Dispose()
        {
            for (var i = _held.Count - 1; i >= 0; i--)
                _held[i].Dispose();
            _held.Clear();
        }
    }
}
