namespace Pen;

/// <summary>Finding a project's sessions, and the exclusive hold pen sign and pen reset take.</summary>
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
    /// project.lock, exclusive, for the whole of pen sign or reset (<paramref name="what"/>: "sign", "reset"): it
    /// refuses while any session runs, and no session starts until it's released.
    /// </summary>
    public static FileLock TakeExclusive(HostEnv host, Project project, string what)
    {
        var runtimeDir = host.RuntimeDir(project.Key);
        var l = FileLock.TryTake(Path.Combine(runtimeDir, "project.lock"), exclusive: true);
        if (l != null) return l;

        var running = Running(project.Key, runtimeDir);
        var msg = running.Count == 0
            ? $"refusing to {what}: another pen sign or pen reset is running in this project: try again once it's done"
            : $"refusing to {what} while {(running.Count == 1 ? "a session is" : $"{running.Count} sessions are")} running in this project:\n"
              + string.Join('\n', running.Select(s => "  " + s.Container))
              + "\nexit them first (or, for one you can't reach, docker rm -f <name>: its pen then ends the session)";
        throw new PenException(msg);
    }
}
