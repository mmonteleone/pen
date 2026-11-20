using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pen.GitProtection;
using Pen.Images;

namespace Pen.Sessions;

/// <summary>Writes the compose override for a session's mounts, Docker-in-Docker and forwarded ports.</summary>
internal static class SessionCompose
{
    /// <summary>
    /// Generates the session's compose override (session.yaml): the mounts over the project mount, and the dind and
    /// ports services when the session has them.
    /// </summary>
    /// <param name="mounts">The mounts over the project mount.</param>
    /// <param name="readOnlyRoot">
    /// Make the agent's root filesystem read-only: under rootless Docker the agent is container root and would
    /// otherwise own the image's files.
    /// </param>
    /// <param name="dindContainer">In a docker session, the name of the dind container.</param>
    /// <param name="ports">The ports to forward; none adds no ports service.</param>
    /// <returns>The override, as indented JSON and a final newline.</returns>
    /// <remarks>
    /// <para>It's JSON, which YAML reads as is, so any path is quoted correctly; $ is doubled, since compose
    /// interpolates it.</para>
    /// <para>Dind gets the agent's mounts, so containers the agent starts with -v $PWD:… can't write what the agent
    /// can't.</para>
    /// <para>Forwarded ports need a service of their own because the agent's network is internal and Docker
    /// publishes no ports there. The agent can reach that service, but only to be relayed back to itself.</para>
    /// </remarks>
    public static string GenerateOverride(
        IReadOnlyList<Mount> mounts,
        bool readOnlyRoot,
        string? dindContainer,
        PortForward? ports)
    {
        if (ports is { Ports.Count: 0 })
            ports = null;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream,
            new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("x-pen",
                "this session's mounts over the project mount and its services, written by pen");
            writer.WriteStartObject("services");
            WriteAgentService(writer, mounts, readOnlyRoot, dindContainer, ports);
            if (dindContainer is not null)
                WriteDindService(writer, mounts, dindContainer);
            if (ports is not null)
                WritePortsService(writer, ports);
            writer.WriteEndObject();
            if (ports is not null)
                WritePortsNetwork(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    /// <summary>
    /// Writes the agent service: its root filesystem, dind's settings, its dependencies and the mounts.
    /// </summary>
    private static void WriteAgentService(
        Utf8JsonWriter writer,
        IReadOnlyList<Mount> mounts,
        bool readOnlyRoot,
        string? dindContainer,
        PortForward? ports)
    {
        writer.WriteStartObject("agent");
        if (readOnlyRoot)
            writer.WriteBoolean("read_only", true);
        if (dindContainer is not null)
        {
            writer.WriteStartObject("environment");
            writer.WriteString("DOCKER_HOST", "tcp://dind:2375");
            writer.WriteString("PEN_DOCKER", "1");
            writer.WriteEndObject();
        }
        if (dindContainer is not null || ports is not null)
            WriteAgentDependencies(writer, hasDind: dindContainer is not null, hasPorts: ports is not null);
        WriteMounts(writer, mounts);
        writer.WriteEndObject();
    }

    /// <summary>Writes the agent's depends_on: dind once healthy, the ports service once started.</summary>
    private static void WriteAgentDependencies(Utf8JsonWriter writer, bool hasDind, bool hasPorts)
    {
        // compose run starts only the agent's dependencies.
        writer.WriteStartObject("depends_on");
        if (hasDind)
        {
            writer.WriteStartObject("dind");
            writer.WriteString("condition", "service_healthy");
            writer.WriteEndObject();
        }
        if (hasPorts)
        {
            writer.WriteStartObject("ports");
            writer.WriteString("condition", "service_started");
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    /// <summary>Writes the dind service's container name and the agent's mounts.</summary>
    private static void WriteDindService(Utf8JsonWriter writer, IReadOnlyList<Mount> mounts, string dindContainer)
    {
        writer.WriteStartObject("dind");
        writer.WriteString("container_name", dindContainer);
        WriteMounts(writer, mounts);
        writer.WriteEndObject();
    }

    /// <summary>Writes the ports network, the non-internal one the ports service publishes on.</summary>
    private static void WritePortsNetwork(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("networks");
        writer.WriteStartObject("ports");
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    /// <summary>Writes the ports service: a socat per port, relaying the host's 127.0.0.1 to the agent.</summary>
    private static void WritePortsService(Utf8JsonWriter writer, PortForward ports)
    {
        writer.WriteStartObject("ports");
        writer.WriteString("image", Image.Name);
        writer.WriteString("container_name", ports.Container);
        writer.WriteStartArray("entrypoint");
        writer.WriteEndArray();
        // A socat per port; tini (init) as PID 1 stops them all at once.
        var script = string.Concat(ports.Ports.Select(p => "socat " +
            $"TCP-LISTEN:{p},fork,reuseaddr TCP:{ports.AgentContainer}:{p} & ")) + "wait";
        writer.WriteStartArray("command");
        writer.WriteStringValue("sh");
        writer.WriteStringValue("-c");
        writer.WriteStringValue(script.Replace("$", "$$", StringComparison.Ordinal));
        writer.WriteEndArray();
        writer.WriteBoolean("init", true);
        writer.WriteString("user", "nobody");
        writer.WriteStartArray("cap_drop");
        writer.WriteStringValue("ALL");
        writer.WriteEndArray();
        writer.WriteStartArray("security_opt");
        writer.WriteStringValue("no-new-privileges:true");
        writer.WriteEndArray();
        writer.WriteBoolean("read_only", true);
        writer.WriteNumber("pids_limit", 1024);
        writer.WriteStartArray("ports");
        foreach (var port in ports.Ports)
            writer.WriteStringValue($"127.0.0.1:{port}:{port}");
        writer.WriteEndArray();
        writer.WriteStartArray("networks");
        writer.WriteStringValue("sandbox");
        writer.WriteStringValue("ports");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>Writes <paramref name="mounts"/> as the service's bind mounts.</summary>
    private static void WriteMounts(Utf8JsonWriter writer, IReadOnlyList<Mount> mounts)
    {
        writer.WriteStartArray("volumes");
        foreach (var mount in mounts)
        {
            var source = (mount.Source ?? mount.Path).Replace("$", "$$", StringComparison.Ordinal);
            var target = mount.Path.Replace("$", "$$", StringComparison.Ordinal);
            writer.WriteStartObject();
            writer.WriteString("type", "bind");
            writer.WriteString("source", source);
            writer.WriteString("target", target);
            writer.WriteBoolean("read_only", mount.ReadOnly);
            // A missing source is an error, never created (as root, on Linux).
            writer.WriteStartObject("bind");
            writer.WriteBoolean("create_host_path", false);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }
}
