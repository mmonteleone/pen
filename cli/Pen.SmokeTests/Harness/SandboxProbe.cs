using System.Text;
using System.Text.RegularExpressions;

namespace Pen.SmokeTests.Harness;

/// <summary>Named shell probes run in one sandbox session, each reporting "ok: name" or "fail: name".</summary>
/// <remarks>
/// Each probe's command is passed to <see cref="Runner"/> as $1, quoted by the harness, so a test writes it
/// as the agent would type it. <see cref="Setup"/> runs first in the same shell, and its output lines can be
/// checked with <see cref="ProbeResult.HasLine"/>.
/// </remarks>
internal sealed partial class SandboxProbe
{
    /// <summary>The default runner: the probe's command, run by the session's own shell.</summary>
    public const string Eval = "eval \"$1\"";

    /// <summary>A runner for probes that exit or change directory, as their own bash -c would: the probe's command
    /// in a subshell.</summary>
    public const string EvalInSubshell = "(eval \"$1\")";

    private readonly List<(string Name, string Command)> _probes = [];

    /// <summary>Gets the shell that runs before the probes, in the same session.</summary>
    public string Setup { get; init; } = "";

    /// <summary>Gets the shell that runs one probe's command, given as $1, e.g. inside an inner container.</summary>
    public string Runner { get; init; } = Eval;

    /// <summary>Adds a probe.</summary>
    /// <param name="name">Its name, a word that's unique in this probe set.</param>
    /// <param name="command">The shell command it runs; a zero exit status is "ok".</param>
    /// <returns>This probe set.</returns>
    /// <exception cref="ArgumentException">The name isn't a word or is already used.</exception>
    public SandboxProbe Add(string name, string command)
    {
        if (!ProbeName().IsMatch(name) || _probes.Any(probe => probe.Name == name))
            throw new ArgumentException($"probe name {name} isn't a new word", nameof(name));
        _probes.Add((name, command));
        return this;
    }

    /// <summary>Builds the session's bash script: t(), <see cref="Setup"/>, then one line per probe.</summary>
    /// <returns>The script.</returns>
    public string BuildScript()
    {
        var script = new StringBuilder();
        script.Append("t() { if ").Append(Runner)
            .Append(" >/dev/null 2>&1; then echo \"ok: $2\"; else echo \"fail: $2\"; fi; }\n");
        script.Append(Setup).Append('\n');
        foreach (var (name, command) in _probes)
            script.Append("t ").Append(ShellQuote.Quote(command)).Append(' ').Append(name).Append('\n');
        return script.ToString();
    }

    /// <summary>Runs the probes in one session: pen [options] run bash -c script.</summary>
    /// <param name="pen">The pen to run, in the project directory.</param>
    /// <param name="penOptions">Options before run, e.g. --docker.</param>
    /// <returns>The session's result, by probe name.</returns>
    public ProbeResult Run(PenCli pen, params string[] penOptions) =>
        new(pen.Run([.. penOptions, "run", "bash", "-c", BuildScript()]));

    [GeneratedRegex("^[A-Za-z0-9_.-]+$")]
    private static partial Regex ProbeName();
}
