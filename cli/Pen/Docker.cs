namespace Pen;

/// <summary>Facts about the Docker daemon that change how a session is set up.</summary>
internal static class Docker
{
    /// <summary>
    /// Whether the daemon is rootless Docker, from <c>docker info</c>'s
    /// security options. There, the host user is root (UID 0) inside
    /// containers, and container UIDs above 0 are unprivileged sub-UIDs that
    /// can't touch the user's files (see "Rootless Docker" in PLAN.md).
    /// </summary>
    public static bool IsRootless()
    {
        var r = Proc.Capture("docker", ["info", "--format", "{{json .SecurityOptions}}"]);
        if (r.ExitStatus.ExitCode != 0)
            throw new PenException($"docker info failed: is Docker running? {r.StandardError.Trim()}");
        return IsRootless(r.StandardOutput);
    }

    /// <summary>Parses <c>docker info --format '{{json .SecurityOptions}}'</c> (e.g. ["name=seccomp,profile=builtin","name=rootless"]).</summary>
    internal static bool IsRootless(string securityOptionsJson) =>
        securityOptionsJson.Split('"').Any(opt => opt.Split(',').Contains("name=rootless", StringComparer.Ordinal));
}
