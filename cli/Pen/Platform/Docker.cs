namespace Pen.Platform;

/// <summary>Queries and changes to the Docker daemon that a session's setup and teardown depend on.</summary>
internal static class Docker
{
    /// <summary>Removes a container that can write to the project, before the Git check.</summary>
    /// <exception cref="PenException">The container's absence can't be established.</exception>
    /// <remarks>
    /// A failed rm is safe only when a successful daemon query establishes that the container is absent.
    /// Never infer absence from an inspect error (the daemon may be down).
    /// </remarks>
    public static void RemoveWriter(string container)
    {
        var removed = Proc.Capture("docker", ["rm", "-f", container]);
        if (removed.ExitStatus.ExitCode == 0)
            return;
        var remaining = Proc.Capture("docker",
            ["ps", "-a", "--filter", $"name=^/{container}$", "--format", "{{.Names}}"]);
        if (remaining.ExitStatus.ExitCode == 0 && string.IsNullOrWhiteSpace(remaining.StandardOutput))
            return;
        throw new PenException(($"can't establish that {container} has stopped: " +
            $"{removed.StandardError.Trim()} {remaining.StandardError.Trim()}").Trim());
    }

    /// <summary>Checks whether the daemon is rootless Docker, from <c>docker info</c>'s security options.</summary>
    /// <exception cref="PenException">docker info failed.</exception>
    /// <remarks>
    /// Under rootless Docker the host user is root (UID 0) inside containers, and container UIDs
    /// above 0 are unprivileged sub-UIDs that can't touch the user's files.
    /// </remarks>
    public static bool IsRootless()
    {
        var info = Proc.Capture("docker", ["info", "--format", "{{json .SecurityOptions}}"]);
        if (info.ExitStatus.ExitCode != 0)
            throw new PenException($"docker info failed: is Docker running? {info.StandardError.Trim()}");
        return IsRootless(info.StandardOutput);
    }

    /// <summary>Lists the containers, running or not, that use <paramref name="volume"/>.</summary>
    /// <returns>Their names.</returns>
    /// <exception cref="PenException">docker ps failed.</exception>
    /// <remarks>For the dind volume, any at all means an earlier session's dind outlived it.</remarks>
    public static IReadOnlyList<string> ListContainersUsing(string volume)
    {
        var containers = Proc.Capture("docker",
            ["ps", "-a", "--filter", $"volume={volume}", "--format", "{{.Names}}"]);
        if (containers.ExitStatus.ExitCode != 0)
            throw new PenException($"docker ps failed: {containers.StandardError.Trim()}");
        return [.. containers.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// Checks whether <c>docker info --format '{{json .SecurityOptions}}'</c> output names rootless
    /// (e.g. ["name=seccomp,profile=builtin","name=rootless"]).
    /// </summary>
    internal static bool IsRootless(string securityOptionsJson) =>
        securityOptionsJson.Split('"')
            .Any(option => option.Split(',').Contains("name=rootless", StringComparer.Ordinal));
}
