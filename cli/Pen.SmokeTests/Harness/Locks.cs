using Pen.Platform;

namespace Pen.SmokeTests.Harness;

/// <summary>Checks and holds pen's flock(2) locks from the test process, with pen's <see cref="FileLock"/>.</summary>
internal static class Locks
{
    /// <summary>Checks whether another process could take a lock on <paramref name="path"/> right now.</summary>
    /// <param name="path">The lock file, created if missing.</param>
    /// <param name="exclusive">True for an exclusive lock, false for a shared one.</param>
    /// <returns>True if the lock was free (it's released again at once).</returns>
    public static bool IsFree(string path, bool exclusive)
    {
        using var taken = FileLock.TryTake(path, exclusive);
        return taken != null;
    }

    /// <summary>Takes a lock and holds it until it's disposed, waiting up to 30 s for it.</summary>
    /// <param name="path">The lock file, created if missing.</param>
    /// <param name="exclusive">True for an exclusive lock, false for a shared one.</param>
    /// <returns>The held lock, which commands started meanwhile don't inherit (it's close-on-exec).</returns>
    /// <exception cref="TimeoutException">Someone else held a conflicting lock for 30 s.</exception>
    public static FileLock Hold(string path, bool exclusive)
    {
        FileLock? taken = null;
        if (!Polling.WaitUntil(() => (taken = FileLock.TryTake(path, exclusive)) != null, TimeSpan.FromSeconds(30)))
            throw new TimeoutException($"couldn't take {path} for 30 s");
        return taken!;
    }
}
