using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Pen.GitProtection;
using Pen.Images;
using Pen.Platform;

namespace Pen.Sessions;

/// <summary>Compose serialization for the session mounts, Docker and forwarded ports.</summary>
internal static class SessionCompose
{
    /// <summary>
    /// The session's compose override (session.yaml): it adds <paramref name="mounts"/> to the agent
    /// service. It's JSON, which YAML reads as is, so any path is quoted
    /// correctly; $ is doubled, since compose interpolates it.
    /// <paramref name="readOnlyRoot"/> also makes the agent's root filesystem
    /// read-only (rootless Docker, where the agent is container root and would
    /// otherwise own the image's files).
    /// <paramref name="dindContainer"/>, in docker sessions, names the dind
    /// container, gives dind the same mounts (so containers the agent starts
    /// with -v $PWD:… can't write what the agent can't), and points the agent
    /// at dind once it's healthy.
    /// <paramref name="ports"/>, when it has any, adds the ports service: the
    /// agent's network is internal, and Docker publishes no ports there, so a
    /// socat per port on both the sandbox network and a non-internal ports
    /// network relays 127.0.0.1:&lt;p&gt; on the host to &lt;p&gt; on the agent. It
    /// runs pen:latest as nobody with no capabilities on a read-only root; the
    /// agent can reach it, but only to be relayed back to itself.
    /// </summary>
    public static string Override(IReadOnlyList<Mount> mounts, bool readOnlyRoot = false, string? dindContainer = null, PortForward? ports = null)
    {
        if (ports is { Ports.Count: 0 }) ports = null;
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("x-pen", "this session's mounts over the project mount and its services, written by pen");
            w.WriteStartObject("services");
            w.WriteStartObject("agent");
            if (readOnlyRoot) w.WriteBoolean("read_only", true);
            if (dindContainer != null)
            {
                w.WriteStartObject("environment");
                w.WriteString("DOCKER_HOST", "tcp://dind:2375");
                w.WriteString("PEN_DOCKER", "1");
                w.WriteEndObject();
            }
            if (dindContainer != null || ports != null)
            {
                // compose run starts only the agent's dependencies.
                w.WriteStartObject("depends_on");
                if (dindContainer != null)
                {
                    w.WriteStartObject("dind");
                    w.WriteString("condition", "service_healthy");
                    w.WriteEndObject();
                }
                if (ports != null)
                {
                    w.WriteStartObject("ports");
                    w.WriteString("condition", "service_started");
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            WriteMounts(w, mounts);
            w.WriteEndObject();
            if (dindContainer != null)
            {
                w.WriteStartObject("dind");
                w.WriteString("container_name", dindContainer);
                WriteMounts(w, mounts);
                w.WriteEndObject();
            }
            if (ports != null) WritePortsService(w, ports);
            w.WriteEndObject();
            if (ports != null)
            {
                w.WriteStartObject("networks");
                w.WriteStartObject("ports");
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    private static void WritePortsService(Utf8JsonWriter w, PortForward ports)
    {
        w.WriteStartObject("ports");
        w.WriteString("image", Image.Name);
        w.WriteString("container_name", ports.Container);
        w.WriteStartArray("entrypoint");
        w.WriteEndArray();
        // A socat per port; tini (init) as PID 1 stops them all at once.
        var script = string.Concat(ports.Ports.Select(p => $"socat TCP-LISTEN:{p},fork,reuseaddr TCP:{ports.AgentContainer}:{p} & ")) + "wait";
        w.WriteStartArray("command");
        w.WriteStringValue("sh");
        w.WriteStringValue("-c");
        w.WriteStringValue(script.Replace("$", "$$", StringComparison.Ordinal));
        w.WriteEndArray();
        w.WriteBoolean("init", true);
        w.WriteString("user", "nobody");
        w.WriteStartArray("cap_drop");
        w.WriteStringValue("ALL");
        w.WriteEndArray();
        w.WriteStartArray("security_opt");
        w.WriteStringValue("no-new-privileges:true");
        w.WriteEndArray();
        w.WriteBoolean("read_only", true);
        w.WriteNumber("pids_limit", 1024);
        w.WriteStartArray("ports");
        foreach (var p in ports.Ports) w.WriteStringValue($"127.0.0.1:{p}:{p}");
        w.WriteEndArray();
        w.WriteStartArray("networks");
        w.WriteStringValue("sandbox");
        w.WriteStringValue("ports");
        w.WriteEndArray();
        w.WriteEndObject();
    }

    private static void WriteMounts(Utf8JsonWriter w, IReadOnlyList<Mount> mounts)
    {
        w.WriteStartArray("volumes");
        foreach (var m in mounts)
        {
            var path = m.Path.Replace("$", "$$", StringComparison.Ordinal);
            w.WriteStartObject();
            w.WriteString("type", "bind");
            w.WriteString("source", path);
            w.WriteString("target", path);
            w.WriteBoolean("read_only", m.ReadOnly);
            // A missing source is an error, never created (as root, on Linux).
            w.WriteStartObject("bind");
            w.WriteBoolean("create_host_path", false);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }
}
