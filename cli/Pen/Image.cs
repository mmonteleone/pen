using System.Diagnostics;

namespace Pen;

/// <summary>
/// pen:latest, built from the embedded image/ files and labeled with their
/// hash, so a launch can tell when it's missing or stale.
/// </summary>
internal static class Image
{
    public const string Name = "pen:latest";
    public const string HashLabel = "pen.image-hash";

    /// <summary>The label of the current image, or null if there's no image.</summary>
    public static string? CurrentHash()
    {
        var r = Proc.Capture("docker", ["image", "inspect", "--format", $"{{{{ index .Config.Labels \"{HashLabel}\" }}}}", Name]);
        return r.ExitStatus.ExitCode == 0 ? r.StandardOutput.Trim() : null;
    }

    /// <summary>Builds pen:latest first when it's missing or doesn't match this CLI's image files.</summary>
    public static void EnsureCurrent()
    {
        var want = Embedded.ImageHash();
        var have = CurrentHash();
        if (have == want) return;
        Out.Line(have == null
            ? $"building {Name} (first run, this takes a few minutes)"
            : $"rebuilding {Name}: its image files changed");
        Build(want, refresh: null, pull: false, noCache: false);
    }

    public static void Build(string hash, string? refresh, bool pull, bool noCache)
    {
        var args = new List<string> { "build", "-t", Name, "--label", $"{HashLabel}={hash}" };
        if (pull) args.Add("--pull");
        if (noCache) args.Add("--no-cache");
        if (refresh != null) args.AddRange(["--build-arg", $"PEN_REFRESH={refresh}"]);
        args.Add("-");

        var psi = Proc.Info("docker", args);
        psi.RedirectStandardInput = true;
        using var p = Proc.Start(psi);
        try
        {
            using (var stdin = p.StandardInput.BaseStream)
                Embedded.WriteImageContext(stdin);
        }
        catch (IOException)
        {
            // docker exited before reading the whole context; its exit code says why.
        }
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new PenException($"building {Name} failed (docker build exited {p.ExitCode})");
    }

    /// <summary>
    /// The project layer: .pen/Dockerfile (FROM pen:latest), built with .pen/
    /// as its context as &lt;project key&gt;:latest. Built on every launch; the
    /// cache makes that near-instant, and a rebuilt pen:latest invalidates it.
    /// Returns the image to run, or null when the project has no layer.
    /// Output stays hidden unless the build is slow or fails.
    /// </summary>
    public static string? EnsureProjectLayer(Project project, bool noCache = false)
    {
        var dockerfile = Path.Combine(project.PenDir, "Dockerfile");
        if (!File.Exists(dockerfile)) return null;
        var name = $"{project.Key}:latest";

        var args = new List<string> { "build", "--progress", "plain", "-t", name, "--label", $"pen.project={project.Key}" };
        if (noCache) args.Add("--no-cache");
        args.AddRange(["-f", dockerfile, project.PenDir]);
        var psi = Proc.Info("docker", args);
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;

        var gate = new object();
        var buffered = new List<string>();
        bool live = false;
        void OnLine(string? line)
        {
            if (line == null) return;
            lock (gate)
            {
                if (live) Out.Raw(line); else buffered.Add(line);
            }
        }

        using var p = Proc.Start(psi);
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(TimeSpan.FromSeconds(1.5)))
        {
            lock (gate)
            {
                Out.Line($"building the project layer {name} from .pen/Dockerfile");
                foreach (var l in buffered) Out.Raw(l);
                buffered.Clear();
                live = true;
            }
        }
        p.WaitForExit(); // also drains the output
        if (p.ExitCode != 0)
        {
            lock (gate)
            {
                foreach (var l in buffered.TakeLast(40)) Out.Raw(l);
            }
            throw new PenException($"building the project layer from .pen/Dockerfile failed (docker build exited {p.ExitCode})");
        }
        return name;
    }

    /// <summary>
    /// pen upgrade: a newer base image, the agents reinstalled (PEN_REFRESH
    /// busts their cached layers), and newer sidecar images.
    /// </summary>
    public static void Upgrade(bool clean)
    {
        Out.Line($"upgrading {Name}{(clean ? " from scratch (--clean)" : "")}");
        Build(Embedded.ImageHash(), refresh: DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), pull: true, noCache: clean);

        // The project layer too, when run in a project that has one.
        Project? project = null;
        try { project = Project.Resolve(Environment.CurrentDirectory, HostEnv.FromProcess()); }
        catch (PenException) { }
        if (project != null && File.Exists(Path.Combine(project.PenDir, "Dockerfile")))
        {
            Out.Line($"rebuilding the project layer {project.Key}:latest");
            EnsureProjectLayer(project, noCache: clean);
        }

        Out.Line("pulling sidecar images");
        // compose reads the embedded compose.yaml from stdin. Its required
        // variables only need values for interpolation; nothing is started.
        var env = new Dictionary<string, string>
        {
            ["PROJECT_DIR"] = "/nonexistent",
            ["PEN_PROJECT"] = "pen-upgrade",
            ["PEN_UID"] = "1",
            ["PEN_GID"] = "1",
            ["NUGET_PACKAGES"] = "/nonexistent",
            ["PEN_SEED"] = "/nonexistent",
        };
        var psi = Proc.Info("docker", ["compose", "-f", "-", "-p", "pen-upgrade", "pull", "proxy"], env);
        psi.RedirectStandardInput = true;
        using var p = Proc.Start(psi);
        using (var stdin = p.StandardInput)
            stdin.Write(Embedded.Text("compose.yaml"));
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new PenException($"docker compose pull failed (exit {p.ExitCode})");
        Out.Line("upgrade done");
    }
}
