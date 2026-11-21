namespace Pen.IntegrationTests.Harness;

/// <summary>Runs the real docker on the host, for setup, cleanup and checks on what pen left running.</summary>
internal static class Docker
{
    /// <summary>Runs docker to its end.</summary>
    /// <param name="args">Docker's arguments.</param>
    /// <returns>Its output and exit code.</returns>
    public static CommandResult Run(params string[] args) =>
        new HostCommand(IntegrationEnvironment.DockerPath, args).Run();

    /// <summary>Runs docker and gets its stdout lines, failing if docker fails.</summary>
    /// <param name="args">Docker's arguments.</param>
    /// <returns>The non-empty lines of its stdout.</returns>
    /// <exception cref="InvalidOperationException">Docker exited non-zero.</exception>
    public static IReadOnlyList<string> ReadLines(params string[] args)
    {
        var result = Run(args);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"docker {string.Join(' ', args)} failed: {result.Output}");
        return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Gets the names of the running containers.</summary>
    /// <returns>The names.</returns>
    public static IReadOnlyList<string> ListRunningNames() => ReadLines("ps", "--format", "{{.Names}}");

    /// <summary>Counts a project key's containers, running or not (docker ps -a).</summary>
    /// <param name="key">The project key.</param>
    /// <returns>The count.</returns>
    public static int CountContainers(string key) => ReadLines("ps", "-aq", "--filter", $"name=^{key}-").Count;

    /// <summary>Gets the repository:tag of each image matching a reference filter.</summary>
    /// <param name="reference">The filter, e.g. pen-tools.</param>
    /// <returns>The images, sorted.</returns>
    public static IReadOnlyList<string> ListImages(string reference) =>
        [.. ReadLines("image", "ls", "--format", "{{.Repository}}:{{.Tag}}", "--filter", $"reference={reference}")
            .Order(StringComparer.Ordinal)];

    /// <summary>Removes a project key's containers, networks, volumes and images, ignoring failures.</summary>
    /// <param name="key">The project key.</param>
    public static void RemoveKey(string key)
    {
        RemoveEach(["rm", "-f"], Run("ps", "-aq", "--filter", $"name=^{key}-"));
        RemoveEach(["network", "rm"], Run("network", "ls", "-q", "--filter", $"name=^{key}-"));
        Run("volume", "rm", $"{key}_home", $"{key}_nuget", $"{key}_dind");
        RemoveEach(["image", "rm", "-f"], Run("image", "ls", "-q", "--filter", $"reference={key}*"));
    }

    /// <summary>Removes images by name, ignoring failures.</summary>
    /// <param name="images">The images.</param>
    public static void RemoveImages(IEnumerable<string> images)
    {
        string[] names = [.. images];
        if (names.Length > 0)
            Run(["image", "rm", "-f", .. names]);
    }

    /// <summary>Removes containers by name or ID (docker rm -f), ignoring failures.</summary>
    /// <param name="containers">The containers.</param>
    public static void RemoveContainers(IEnumerable<string> containers)
    {
        string[] names = [.. containers];
        if (names.Length > 0)
            Run(["rm", "-f", .. names]);
    }

    private static void RemoveEach(string[] command, CommandResult listed)
    {
        string[] ids = [.. listed.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Distinct()];
        if (listed.ExitCode == 0 && ids.Length > 0)
            Run([.. command, .. ids]);
    }
}
