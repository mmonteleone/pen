using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Pen.TestSupport;

/// <summary>Turns off git's automatic maintenance for every git the test process starts.</summary>
/// <remarks>
/// Detached maintenance outlives the command and runs git worktree prune on the test's repo, which deletes
/// a worktree entry the test is building and hangs forever on a FIFO in one. The environment covers git run
/// from scripts and through HostGit too. It's set when this assembly is first used.
/// </remarks>
internal static class NoGitMaintenance
{
    /// <summary>Gets the environment variables the module initializer sets.</summary>
    public static IReadOnlyList<string> VariableNames { get; } =
        ["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0"];

    /// <summary>Sets maintenance.auto=false through GIT_CONFIG_COUNT in the process environment.</summary>
    [ModuleInitializer]
    [SuppressMessage("Usage", "CA2255", Justification = "It has to run before any test starts git.")]
    internal static void Disable()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "maintenance.auto");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "false");
    }
}
