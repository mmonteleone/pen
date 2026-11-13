namespace Pen.Sessions;

/// <summary>
/// The ports a session's ports service forwards from the host's 127.0.0.1 to the same ports on the agent.
/// </summary>
/// <param name="Ports">The ports.</param>
/// <param name="Container">The name of the ports service's container.</param>
/// <param name="AgentContainer">
/// The name of the agent's container: compose run gives the agent no service-name alias, so it's reached by
/// container name.
/// </param>
internal sealed record PortForward(IReadOnlyList<int> Ports, string Container, string AgentContainer);
