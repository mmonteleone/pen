using Pen.Platform;

namespace Pen.IntegrationTests.Harness;

/// <summary>Facts about the machine the integration tests run on: the pen under test, the real HOME and
/// Docker.</summary>
internal static class IntegrationEnvironment
{
    private static readonly Lazy<string> LazyPenPath = new(FindPen);
    private static readonly Lazy<string> LazyDockerPath = new(() => FindOnPath("docker")
        ?? throw new InvalidOperationException("docker not found on PATH"));
    private static readonly Lazy<bool> LazyIsRootless = new(ReadIsRootless);

    /// <summary>Gets the absolute path of the pen under test: $PEN, or pen on PATH.</summary>
    public static string PenPath => LazyPenPath.Value;

    /// <summary>Gets the absolute path of the real docker, found on PATH at the start.</summary>
    public static string DockerPath => LazyDockerPath.Value;

    /// <summary>Gets the HOME the tests were started with.</summary>
    public static string RealHome { get; } = ReadRealHome();

    /// <summary>Gets the host's NuGet folder that sessions mount: $NUGET_PACKAGES or ~/.nuget/packages.</summary>
    public static string NuGetPackages { get; } = NonEmpty("NUGET_PACKAGES")
        ?? Path.Combine(RealHome, ".nuget", "packages");

    /// <summary>Gets the Docker client config pen uses under any HOME: $DOCKER_CONFIG or ~/.docker.</summary>
    public static string DockerConfig { get; } = NonEmpty("DOCKER_CONFIG") ?? Path.Combine(RealHome, ".docker");

    /// <summary>Gets the repo root, which holds tests/pty-run.py.</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    /// <summary>Gets the host user's UID.</summary>
    public static uint HostUid { get; } = Native.GetUid();

    /// <summary>Gets the host user's primary GID.</summary>
    public static uint HostGid { get; } = Native.GetGid();

    /// <summary>Gets whether Docker runs rootless, which makes the agent container root (UID 0).</summary>
    public static bool IsRootless => LazyIsRootless.Value;

    /// <summary>Gets the agent's UID inside the sandbox: the host user's, or 0 under rootless Docker.</summary>
    public static uint AgentUid => IsRootless ? 0 : HostUid;

    /// <summary>Gets the agent's GID inside the sandbox: the host user's, or 0 under rootless Docker.</summary>
    public static uint AgentGid => IsRootless ? 0 : HostGid;

    /// <summary>Gets the agent's user name inside the sandbox: agent, or root under rootless Docker.</summary>
    public static string AgentUser => IsRootless ? "root" : "agent";

    /// <summary>Gets whether INTEGRATION_SLOW=1 asks for the slow checks too
    /// (<see cref="SlowFactAttribute"/>).</summary>
    public static bool IsSlow => Environment.GetEnvironmentVariable("INTEGRATION_SLOW") == "1";

    /// <summary>Gets a credential from the environment, or skips the calling test when it isn't set.</summary>
    /// <remarks>
    /// Call it from a fact, never from a fixture: Assert.Skip skips only a test, so in a fixture's setup it fails
    /// the whole class instead. A fixture uses <see cref="FindCredential"/>, leaves out its credential session
    /// when that's null, and the facts that read that session's result call this.
    /// </remarks>
    /// <param name="name">The environment variable, e.g. CLAUDE_CODE_OAUTH_TOKEN.</param>
    /// <returns>The credential.</returns>
    public static string RequireCredential(string name)
    {
        var value = FindCredential(name);
        if (value == null)
            Assert.Skip($"set {name} to run it");
        return value;
    }

    /// <summary>Gets a credential from the environment, for a fixture that runs a session only when it's set.</summary>
    /// <param name="name">The environment variable, e.g. CLAUDE_CODE_OAUTH_TOKEN.</param>
    /// <returns>The credential, or null if it isn't set or is empty.</returns>
    public static string? FindCredential(string name) => NonEmpty(name);

    /// <summary>Finds a program on PATH, as command -v does.</summary>
    /// <param name="name">The program's name.</param>
    /// <returns>Its absolute path, or null if it isn't on PATH.</returns>
    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate) && Native.CanEnter(candidate))
                return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static string FindPen()
    {
        var pen = NonEmpty("PEN") ?? "pen";
        if (pen.Contains('/'))
            return Path.GetFullPath(pen);
        return FindOnPath(pen) ?? throw new InvalidOperationException($"{pen} not found on PATH: run cli/install.sh");
    }

    private static bool ReadIsRootless()
    {
        var info = Docker.Run("info", "--format", "{{json .SecurityOptions}}");
        if (info.ExitCode != 0)
            throw new InvalidOperationException($"docker info failed: {info.Output}");
        return info.Stdout.Contains("name=rootless", StringComparison.Ordinal);
    }

    private static string ReadRealHome()
    {
        var home = NonEmpty("HOME") ?? throw new InvalidOperationException("HOME isn't set");
        return home.TrimEnd('/');
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "tests", "pty-run.py")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"no tests/pty-run.py above {AppContext.BaseDirectory}");
    }

    private static string? NonEmpty(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
