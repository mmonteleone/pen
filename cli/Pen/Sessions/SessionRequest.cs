using Pen.Tools;

namespace Pen.Sessions;

/// <summary>What a session runs, and the options it runs with.</summary>
/// <param name="Command">
/// The command to run in the sandbox (empty: a shell); its first word may name an agent to bring.
/// </param>
internal sealed record SessionRequest(IReadOnlyList<string> Command)
{
    /// <summary>
    /// Gets whether the session has Docker-in-Docker (pen --docker, or docker = true in .pen/config.toml).
    /// </summary>
    public bool Docker { get; init; }

    /// <summary>Gets the ports to forward from the host (pen --port and ports in .pen/config.toml, merged).</summary>
    public IReadOnlyList<Port> Ports { get; init; } = [];

    /// <summary>Gets the tools pen --tool names, which need no approval.</summary>
    public IReadOnlyList<ToolSpec> FlagTools { get; init; } = [];
}
