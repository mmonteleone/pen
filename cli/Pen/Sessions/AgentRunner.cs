using System.Diagnostics;
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
        var startInfo = CreateStartInfo(command, token, userEnv);
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
            return Proc.ToExitCode(child.WaitForExitStatus());
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

    /// <summary>Creates the docker compose run of the agent, with the secrets in its env.</summary>
    /// <remarks>Secrets are passed by name only on the command line: their values are never on one.</remarks>
    private ProcessStartInfo CreateStartInfo(
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

        return new ComposeClient(session, env).CreateStartInfo(runArgs, secrets);
    }
}
