using System.Diagnostics;
using Pen.Platform;

namespace Pen.Sessions;

/// <summary>Runs docker compose on one session's stack, and commands in its proxy container.</summary>
/// <param name="session">The session.</param>
/// <param name="env">The env its compose calls run with, as the session started with it.</param>
/// <remarks>
/// Every call names the session's compose.yaml, its session.yaml override when there is one, and its compose
/// project, and runs without the host env that would change what compose loads or starts.
/// </remarks>
internal sealed class ComposeClient(SessionDir session, IReadOnlyDictionary<string, string> env)
{
    /// <summary>Builds the arguments of a docker compose call on the session's stack.</summary>
    /// <param name="args">The compose subcommand and its arguments.</param>
    /// <returns>The arguments for docker.</returns>
    public string[] BuildArgs(params string[] args)
    {
        var composeArgs = new List<string> { "compose", "--progress", "quiet" };
        if (File.Exists(session.DockerMarker))
            composeArgs.AddRange(["--profile", "docker"]);
        composeArgs.AddRange(["-f", session.ComposeFile]);
        if (File.Exists(session.OverrideFile))
            composeArgs.AddRange(["-f", session.OverrideFile]);
        composeArgs.AddRange(["-p", session.ComposeProject]);
        composeArgs.AddRange(args);
        return [.. composeArgs];
    }

    /// <summary>Creates the start info of a docker compose call on the session's stack.</summary>
    /// <param name="args">The compose subcommand and its arguments.</param>
    /// <param name="secrets">Variables set on top of the env, if any, before the compose env is scrubbed.</param>
    public ProcessStartInfo CreateStartInfo(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string>? secrets = null)
    {
        var startInfo = Proc.CreateStartInfo("docker", BuildArgs([.. args]), env);
        if (secrets is not null)
        {
            foreach (var (name, value) in secrets)
                startInfo.Environment[name] = value;
        }
        ScrubEnv(startInfo);
        return startInfo;
    }

    /// <summary>Runs a docker compose call on the session's stack and captures its output.</summary>
    /// <param name="args">The compose subcommand and its arguments.</param>
    /// <returns>Its exit status and output.</returns>
    public ProcessTextOutput Capture(params string[] args)
    {
        var startInfo = CreateStartInfo(args);
        startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;
        return Proc.CaptureText(startInfo);
    }

    /// <summary>Runs <paramref name="command"/> in the session's proxy container.</summary>
    /// <returns>Its exit status and output.</returns>
    public ProcessTextOutput ExecInProxy(params string[] command) => Capture(["exec", "-T", "proxy", .. command]);

    /// <summary>
    /// Runs <paramref name="command"/> in the session's proxy container, capturing its output as bytes.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="limit">The most bytes of stdout or stderr it may write before it's killed.</param>
    /// <param name="timeout">How long it may run before it's killed.</param>
    /// <returns>How it ended, and its output (<see cref="Proc.CaptureBytes"/>).</returns>
    public CapturedBytes ExecInProxyCapturingBytes(IReadOnlyList<string> command, int limit, TimeSpan timeout) =>
        Proc.CaptureBytes(CreateStartInfo(["exec", "-T", "proxy", .. command]), limit: limit, timeout: timeout);

    /// <summary>
    /// Removes the host env that would change what compose loads or starts from <paramref name="startInfo"/>.
    /// </summary>
    private static void ScrubEnv(ProcessStartInfo startInfo)
    {
        foreach (var name in new[]
            {
                "COMPOSE_FILE", "COMPOSE_PROJECT_NAME", "COMPOSE_PROFILES", "COMPOSE_ENV_FILES",
                "COMPOSE_PATH_SEPARATOR"
            })
        {
            startInfo.Environment.Remove(name);
        }
    }
}
