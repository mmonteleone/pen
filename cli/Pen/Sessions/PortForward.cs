namespace Pen.Sessions;

/// <summary>
/// Ports forwarded from the host's 127.0.0.1 by the session's ports service
/// (named <paramref name="Container"/>) to the same ports on the agent
/// container (<paramref name="AgentContainer"/>: compose run gives the agent
/// no service-name alias, so it's reached by container name).
/// </summary>
internal sealed record PortForward(IReadOnlyList<int> Ports, string Container, string AgentContainer);
