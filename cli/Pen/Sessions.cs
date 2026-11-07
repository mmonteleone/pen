namespace Pen;

/// <summary>Finding a project's sessions, and the locks sessions, pen sign and pen reset take.</summary>
internal static class Sessions
{
    /// <summary>
    /// The project's running sessions: open (compose.yaml still there) with
    /// their session.lock held by a live pen.
    /// </summary>
    public static List<SessionDir> Running(string projectKey, string runtimeDir)
    {
        var running = new List<SessionDir>();
        var sessions = Path.Combine(runtimeDir, "sessions");
        if (!Directory.Exists(sessions)) return running;
        foreach (var path in Directory.EnumerateDirectories(sessions).Order(StringComparer.Ordinal))
        {
            var s = new SessionDir(projectKey, path);
            if (!s.IsOpen) continue;
            using var probe = FileLock.TryTake(s.LockFile, exclusive: true);
            if (probe == null) running.Add(s);
        }
        return running;
    }

    /// <summary>Every project's running sessions, for pen allow --global.</summary>
    public static List<SessionDir> RunningEverywhere(HostEnv host)
    {
        var all = new List<SessionDir>();
        if (!Directory.Exists(host.StateRoot)) return all;
        foreach (var dir in Directory.EnumerateDirectories(host.StateRoot).Order(StringComparer.Ordinal))
            all.AddRange(Running(Path.GetFileName(dir), dir));
        return all;
    }

    /// <summary>
    /// The project's running sessions started in <paramref name="dir"/> (by
    /// their PROJECT_DIR): for a linked worktree, only that worktree's.
    /// </summary>
    public static List<SessionDir> RunningIn(string projectKey, string runtimeDir, string dir) =>
        Running(projectKey, runtimeDir)
            .Where(s => s.ReadEnv().TryGetValue("PROJECT_DIR", out var d) && d == dir)
            .ToList();

    public static string ProjectLockFile(string runtimeDir) => Path.Combine(runtimeDir, "project.lock");

    /// <summary>The lock for one dir (the main checkout or a linked worktree) of the project.</summary>
    public static string WorktreeLockFile(string runtimeDir, string dir) =>
        Path.Combine(runtimeDir, $"worktree-{Project.Hash(dir)}.lock");

    /// <summary>
    /// What a session holds for its whole run: project.lock shared (pen reset
    /// takes it exclusive) and its dir's worktree lock shared (pen sign there
    /// takes it exclusive).
    /// </summary>
    public static Locks TakeForSession(string runtimeDir, Project project)
    {
        var locks = new Locks();
        try
        {
            locks.Add(FileLock.TryTake(ProjectLockFile(runtimeDir), exclusive: false)
                ?? throw new PenException("a pen reset is running in this project: try again once it's done"));
            locks.Add(FileLock.TryTake(WorktreeLockFile(runtimeDir, project.Dir), exclusive: false)
                ?? throw new PenException("a pen sign is running in this directory: try again once it's done"));
            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What pen sign holds for its whole run: project.lock shared (so no reset
    /// runs) and its dir's worktree lock exclusive. It refuses while a session
    /// runs in this dir; sessions in the project's other worktrees carry on.
    /// </summary>
    public static Locks TakeForSign(HostEnv host, Project project)
    {
        var runtimeDir = host.RuntimeDir(project.Key);
        var locks = new Locks();
        try
        {
            locks.Add(FileLock.TryTake(ProjectLockFile(runtimeDir), exclusive: false)
                ?? throw new PenException("refusing to sign: a pen reset is running in this project: try again once it's done"));
            var l = FileLock.TryTake(WorktreeLockFile(runtimeDir, project.Dir), exclusive: true);
            if (l == null)
            {
                var running = RunningIn(project.Key, runtimeDir, project.Dir);
                throw new PenException(running.Count == 0
                    ? "refusing to sign: another pen sign is running in this directory: try again once it's done"
                    : Refusal("sign", running, "this directory"));
            }
            locks.Add(l);
            return locks;
        }
        catch
        {
            locks.Dispose();
            throw;
        }
    }

    /// <summary>
    /// project.lock, exclusive, for the whole of pen reset: it refuses while
    /// any session (or pen sign) runs in any of the project's dirs, and none
    /// starts until it's released.
    /// </summary>
    public static FileLock TakeForReset(HostEnv host, Project project)
    {
        var runtimeDir = host.RuntimeDir(project.Key);
        var l = FileLock.TryTake(ProjectLockFile(runtimeDir), exclusive: true);
        if (l != null) return l;

        var running = Running(project.Key, runtimeDir);
        throw new PenException(running.Count == 0
            ? "refusing to reset: a pen sign or another pen reset is running in this project: try again once it's done"
            : Refusal("reset", running, "this project"));
    }

    private static string Refusal(string what, List<SessionDir> running, string where) =>
        $"refusing to {what} while {(running.Count == 1 ? "a session is" : $"{running.Count} sessions are")} running in {where}:\n"
        + string.Join('\n', running.Select(s => "  " + s.Container))
        + "\nexit them first (or, for one you can't reach, docker rm -f <name>: its pen then ends the session)";

    /// <summary>Several locks held together, released in reverse order.</summary>
    internal sealed class Locks : IDisposable
    {
        private readonly List<FileLock> _held = [];
        public IReadOnlyList<FileLock> Held => _held;
        public void Add(FileLock l) => _held.Add(l);

        public void Dispose()
        {
            for (int i = _held.Count - 1; i >= 0; i--) _held[i].Dispose();
            _held.Clear();
        }
    }
}
