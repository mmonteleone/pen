namespace Pen.Platform;

/// <summary>Repeats an attempt with a fixed delay between tries, until it succeeds or a bound is reached.</summary>
/// <remarks>Nothing else in Pen sleeps.</remarks>
internal static class Retry
{
    /// <summary>
    /// Calls <paramref name="attempt"/> until it returns true, at most <paramref name="attempts"/> times.
    /// </summary>
    /// <returns>Whether an attempt returned true.</returns>
    public static bool Repeat(int attempts, TimeSpan delay, Func<bool> attempt)
    {
        for (var tried = 1; ; tried++)
        {
            if (attempt())
                return true;
            if (tried >= attempts)
                return false;
            Thread.Sleep(delay);
        }
    }

    /// <summary>Waits <paramref name="delay"/> between two tries of a loop that decides itself when to stop.</summary>
    public static void Wait(TimeSpan delay) => Thread.Sleep(delay);

    /// <summary>
    /// Calls <paramref name="attempt"/> until it returns a value, giving up once <paramref name="limit"/> has passed
    /// on <paramref name="clock"/> since the first call.
    /// </summary>
    /// <returns>The value, or null when the limit passed first.</returns>
    public static T? Poll<T>(Func<T?> attempt, TimeSpan delay, TimeSpan limit, TimeProvider clock)
        where T : class
    {
        var started = clock.GetTimestamp();
        while (true)
        {
            if (attempt() is { } result)
                return result;
            if (clock.GetElapsedTime(started) > limit)
                return null;
            Thread.Sleep(delay);
        }
    }
}
