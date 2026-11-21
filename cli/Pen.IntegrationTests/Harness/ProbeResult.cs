namespace Pen.IntegrationTests.Harness;

/// <summary>The output of a <see cref="SandboxProbe"/> session, read by probe name or by whole line.</summary>
/// <param name="Session">The session's result.</param>
internal sealed record ProbeResult(CommandResult Session)
{
    /// <summary>Checks whether a probe reported "ok".</summary>
    /// <param name="name">The probe's name.</param>
    /// <returns>True if the output has the line "ok: name".</returns>
    public bool HasPassed(string name) => HasLine($"ok: {name}");

    /// <summary>Checks whether the session's output has exactly this line (grep -qxF).</summary>
    /// <param name="line">The whole line.</param>
    /// <returns>True if it's there.</returns>
    public bool HasLine(string line) => Session.OutputLines.Contains(line, StringComparer.Ordinal);

    /// <summary>Describes the session for an assertion message.</summary>
    /// <returns>Its exit code and output.</returns>
    public string Describe() => Session.Describe();
}
