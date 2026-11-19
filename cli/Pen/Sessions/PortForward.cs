namespace Pen.Sessions;

/// <summary>
/// The ports a session's ports service forwards from the host's 127.0.0.1 to the same ports on the agent.
/// </summary>
/// <remarks>
/// The agent is reached by its container's name, since compose run gives it no service-name alias.
/// </remarks>
internal sealed record PortForward(IReadOnlyList<int> Ports, string Container, string AgentContainer);
