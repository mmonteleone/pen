using System.Diagnostics;
using Pen.Platform;
using Pen.Projects;
using Pen.Trust;

namespace Pen.Images;

/// <summary>Builds pen:latest from the embedded image/ files, and project layers on top of it.</summary>
/// <remarks>
/// pen:latest is labeled with the hash of its image files, so a launch can tell when it's missing or stale.
/// </remarks>
internal static class Image
{
    public const string Name = "pen:latest";
    public const string HashLabel = "pen.image-hash";

    /// <summary>Reads the image-files hash label of the current pen:latest.</summary>
    /// <returns>The label, or null if there's no image.</returns>
    public static string? ReadCurrentHash()
    {
        var result = Proc.Capture("docker",
            ["image", "inspect", "--format", $"{{{{ index .Config.Labels \"{HashLabel}\" }}}}", Name]);
        return result.ExitStatus.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    /// <summary>Builds pen:latest first when it's missing or doesn't match this CLI's image files.</summary>
    public static void EnsureCurrent()
    {
        var want = Embedded.ComputeImageHash();
        var have = ReadCurrentHash();
        if (have == want)
            return;
        Out.WriteLine(have is null
            ? $"building {Name} (first run, this takes a few minutes)"
            : $"rebuilding {Name}: its image files changed");
        Build(want, refresh: null, pull: false, noCache: false);
    }

    /// <summary>Builds pen:latest from the embedded image context, labeled with <paramref name="hash"/>.</summary>
    /// <param name="hash">The hash of the image files, for the label.</param>
    /// <param name="refresh">The PEN_REFRESH build argument, which gives the base a new ID, or null.</param>
    /// <param name="pull">Whether to pull a newer base image (--pull).</param>
    /// <param name="noCache">Whether to build without the cache (--no-cache).</param>
    public static void Build(string hash, string? refresh, bool pull, bool noCache)
    {
        var args = new List<string> { "build", "-t", Name, "--label", $"{HashLabel}={hash}" };
        if (pull)
            args.Add("--pull");
        if (noCache)
            args.Add("--no-cache");
        if (refresh is not null)
            args.AddRange(["--build-arg", $"PEN_REFRESH={refresh}"]);
        args.Add("-");

        var startInfo = Proc.CreateStartInfo("docker", args);
        startInfo.RedirectStandardInput = true;
        using var process = Proc.Start(startInfo);
        try
        {
            using (var stdin = process.StandardInput.BaseStream)
                Embedded.WriteImageContext(stdin);
        }
        catch (IOException)
        {
            // docker exited before reading the whole context; its exit code says why.
        }
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new PenException($"building {Name} failed (docker build exited {process.ExitCode})");
    }

    /// <summary>
    /// Builds the project layer, .pen/Dockerfile (FROM pen:latest) with .pen/ as its context, as
    /// <see cref="GetProjectLayerName"/>, with its output shown as <see cref="RunBuild"/> does.
    /// </summary>
    /// <param name="host">The host user's environment.</param>
    /// <param name="project">The project whose .pen/ to build.</param>
    /// <param name="noCache">Whether to build without the cache (--no-cache).</param>
    /// <param name="baseImage">The image that stands in for pen:latest in the Dockerfile, which stays as it is.</param>
    /// <returns>The image to run, or null when the project has no layer.</returns>
    /// <remarks>
    /// <para>
    /// It's built on every launch; the cache makes that near-instant, and a rebuilt pen:latest invalidates it.
    /// </para>
    /// <para>
    /// Docker only ever reads a private copy, whose hash has to be the approved
    /// one: a session in the main checkout can write to an in-tree worktree's .pen/
    /// at any moment, so checking .pen/ itself would prove nothing about the build.
    /// </para>
    /// <para>
    /// A tag says nothing about which .pen/ made it, so an existing image must never
    /// skip the build: only building is what ties it to the approved copy.
    /// </para>
    /// </remarks>
    public static string? EnsureProjectLayer(
        HostEnv host,
        Project project,
        string baseImage,
        bool noCache = false)
    {
        var dockerfile = Path.Combine(project.PenDir, "Dockerfile");
        if (!File.Exists(dockerfile))
            return null;
        using var context = LayerContext.Take(host, project);
        RequireApproved(context, host, project);

        var name = GetProjectLayerName(project, baseImage);
        var args = new List<string>
        {
            "build", "--progress", "plain", "-t", name, "--label", $"pen.project={project.Key}"
        };
        if (noCache)
            args.Add("--no-cache");
        if (baseImage != Name)
            args.AddRange(["--build-context", $"{Name}=docker-image://{baseImage}"]);
        args.AddRange(["-f", Path.Combine(context.Dir, "Dockerfile"), context.Dir]);
        RunBuild(args, context: null, $"building the project layer {name} from .pen/Dockerfile",
            "building the project layer from .pen/Dockerfile failed");
        return name;
    }

    /// <summary>Throws unless the private copy of .pen/ in <paramref name="context"/> is the approved one.</summary>
    private static void RequireApproved(LayerContext context, HostEnv host, Project project)
    {
        if (context.Result.Hash is null)
            throw new PenException($"can't build from .pen/: {context.Result.Problem}");
        if (context.Result.Hash != TrustStore.For(host, project).LayerHash)
        {
            throw new PenException(".pen/ isn't what was approved (it may " +
                "have just changed): review it with pen trust, then rerun");
        }
    }

    /// <summary>
    /// Runs docker build with <paramref name="args"/>, with its output hidden unless the build is slow,
    /// when <paramref name="announce"/> and the output are shown, or fails, when the end of the output
    /// and <paramref name="failure"/> are.
    /// </summary>
    /// <param name="args">The docker arguments.</param>
    /// <param name="context">Writes the tar context to docker's stdin, if given.</param>
    /// <param name="announce">The line shown before the output of a slow build.</param>
    /// <param name="failure">The start of the error when the build fails.</param>
    internal static void RunBuild(IReadOnlyList<string> args, Action<Stream>? context, string announce, string failure)
    {
        var startInfo = Proc.CreateStartInfo("docker", args);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = context is not null;

        var output = new BuildOutput();
        using var process = Proc.Start(startInfo);
        process.OutputDataReceived += (_, e) => output.Add(e.Data);
        process.ErrorDataReceived += (_, e) => output.Add(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (context is not null)
            WriteContext(process, context);
        if (!process.WaitForExit(TimeSpan.FromSeconds(1.5)))
            output.GoLive(announce);
        process.WaitForExit(); // also drains the output
        if (process.ExitCode != 0)
        {
            output.WriteTail();
            throw new PenException($"{failure} (docker build exited {process.ExitCode})");
        }
    }

    /// <summary>Writes the build context to the stdin of <paramref name="process"/>.</summary>
    private static void WriteContext(Process process, Action<Stream> context)
    {
        try
        {
            using var stdin = process.StandardInput.BaseStream;
            context(stdin);
        }
        catch (IOException)
        {
            // docker exited before reading the whole context; its exit code says why.
        }
    }

    /// <summary>Gets the name of the project layer built on <paramref name="baseImage"/>.</summary>
    /// <returns>
    /// &lt;key&gt; for the main checkout (or a plain dir), and &lt;key&gt;-&lt;hash(dir)&gt; for a linked
    /// worktree, whose .pen/ is its own; tagged latest on pen:latest, and on a tools image with that
    /// image's hash.
    /// </returns>
    /// <remarks>The tag keeps tool sets and bases from sharing a project layer.</remarks>
    public static string GetProjectLayerName(Project project, string baseImage)
    {
        var tag = baseImage == Name ? "latest" : baseImage[(baseImage.LastIndexOf(':') + 1)..];
        return project.Dir == project.RepoDir
            ? $"{project.Key}:{tag}"
            : $"{project.Key}-{Project.HashPath(project.Dir)}:{tag}";
    }

    /// <summary>Holds a build's output lines: held back while the build is quick, shown once it's slow.</summary>
    private sealed class BuildOutput
    {
        private readonly object _gate = new();
        private readonly List<string> _buffered = [];
        private bool _live;

        /// <summary>Shows <paramref name="line"/>, or holds it back while the build isn't live.</summary>
        public void Add(string? line)
        {
            if (line is null)
                return;
            lock (_gate)
            {
                if (_live)
                    Out.WriteRaw(line);
                else
                    _buffered.Add(line);
            }
        }

        /// <summary>Shows <paramref name="announce"/> and the lines held back, and every line from now on.</summary>
        public void GoLive(string announce)
        {
            lock (_gate)
            {
                Out.WriteLine(announce);
                foreach (var line in _buffered)
                    Out.WriteRaw(line);
                _buffered.Clear();
                _live = true;
            }
        }

        /// <summary>Shows the last 40 lines still held back, for a failed build.</summary>
        public void WriteTail()
        {
            lock (_gate)
            {
                foreach (var line in _buffered.TakeLast(40))
                    Out.WriteRaw(line);
            }
        }
    }
}
