using System.Text.RegularExpressions;

namespace Pen.SmokeTests.Harness;

/// <summary>Polls for a condition, such as a session's containers coming up.</summary>
internal static class Polling
{
    private static readonly TimeSpan ContainerTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Checks <paramref name="condition"/> every 100 ms until it holds or the time is up.</summary>
    /// <param name="condition">The condition.</param>
    /// <param name="timeout">How long to keep trying.</param>
    /// <returns>True if it held in time.</returns>
    public static bool WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (condition())
                return true;
            if (DateTime.UtcNow >= deadline)
                return false;
            Thread.Sleep(100);
        }
    }

    /// <summary>Waits up to 30 s until a running container's name matches (wait_for).</summary>
    /// <param name="namePattern">A regex for the name, e.g. ^key-.*-agent$.</param>
    /// <returns>True if one did.</returns>
    public static bool WaitForContainer(string namePattern) => WaitForContainers(namePattern, 1);

    /// <summary>Waits up to 30 s until at least <paramref name="count"/> running containers' names match.</summary>
    /// <param name="namePattern">A regex for the names.</param>
    /// <param name="count">How many.</param>
    /// <returns>True if they did.</returns>
    public static bool WaitForContainers(string namePattern, int count)
    {
        var pattern = new Regex(namePattern);
        return WaitUntil(() => Docker.ListRunningNames().Count(pattern.IsMatch) >= count, ContainerTimeout);
    }
}
