using System.Diagnostics;
using System.Text;
using Pen.Platform;
using Pen.Proxy;

namespace Pen.Sessions;

/// <summary>Runs a session's agent: the docker compose run of its command, with the secrets in its env.</summary>
internal sealed class AgentRunner(SessionDir session, IReadOnlyDictionary<string, string> env, SessionSignals signals)
{
    /// <summary>
    /// Runs <paramref name="command"/> in the sandbox and waits for it, trimming the proxy log. The GitHub token, if
    /// there's one, and the user's [env] reach the agent as secrets.
    /// </summary>
    /// <returns>The agent's exit code, or 1 when it couldn't be started (reported).</returns>
    /// <remarks>A stop asked for before the start refuses it; one asked for after stops the child.</remarks>
    public int Run(
        IReadOnlyList<string> command,
        string? token,
        IReadOnlyList<KeyValuePair<string, string>> userEnv)
    {
        var compose = new ComposeClient(session, env);
        var startInfo = CreateStartInfo(compose, command, token, userEnv);
        try
        {
            if (signals.Stopping)
            {
                Out.WriteLine("stopped before the session started");
                return 1;
            }
            using var child = Proc.Start(startInfo);
            signals.Track(session.Container, child);
            using var trimmer = ProxyLogTrimmer.Start(session, env);
            var exitCode = Proc.ToExitCode(child.WaitForExitStatus());
            if (exitCode != 0 && !signals.Stopping)
                ReportExitedProxy(compose);
            return exitCode;
        }
        catch (PenException error)
        {
            Out.WriteLine(error.Message);
            return 1;
        }
        finally
        {
            signals.Untrack();
        }
    }

    /// <summary>Reports Squid's stderr before teardown removes an exited proxy.</summary>
    private static void ReportExitedProxy(ComposeClient compose)
    {
        try
        {
            var timeout = TimeSpan.FromSeconds(2);
            var container = Proc.CaptureBytes(
                compose.CreateStartInfo(["ps", "--all", "--status", "exited", "--quiet", "proxy"]),
                limit: 256, timeout: timeout);
            if (container is not { End: CaptureEnd.Exited, ExitCode: 0 })
                return;
            var id = Encoding.UTF8.GetString(container.Stdout).Trim();
            if (id.Length == 0 || !id.All(char.IsAsciiHexDigit))
                return;

            // The image streams its access log to stdout. Report only Squid's own diagnostics from stderr.
            var logs = Proc.CaptureBytes(Proc.CreateStartInfo("docker", ["logs", "--tail", "30", id]),
                limit: 16_384, timeout: timeout);
            if (logs is { End: CaptureEnd.Exited, ExitCode: 0 }
                && Encoding.UTF8.GetString(logs.Stderr).Trim() is { Length: > 0 } error)
            {
                Out.WriteLine("Squid proxy diagnostics:\n" + error);
            }
        }
        catch (Exception error) when (error is PenException or IOException)
        {
            // The Compose error was already printed. A failed diagnostic must not change the exit code.
        }
    }

    /// <summary>Creates the docker compose run of the agent, with the secrets in its env.</summary>
    /// <remarks>Secrets are passed by name only on the command line: their values are never on one.</remarks>
    private ProcessStartInfo CreateStartInfo(
        ComposeClient compose,
        IReadOnlyList<string> command,
        string? token,
        IReadOnlyList<KeyValuePair<string, string>> userEnv)
    {
        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (token is not null)
            secrets["GH_TOKEN"] = token;
        foreach (var (name, value) in userEnv)
            secrets[name] = value;

        var runArgs = new List<string> { "run", "--rm", "--name", session.Container };
        if (!Native.IsATty(0) || !Native.IsATty(1))
            runArgs.Add("-T");
        foreach (var name in secrets.Keys)
            runArgs.AddRange(["-e", name]); // by name only: values never on a command line
        runArgs.Add("agent");
        runArgs.AddRange(command);

        return compose.CreateStartInfo(runArgs, secrets);
    }
}
