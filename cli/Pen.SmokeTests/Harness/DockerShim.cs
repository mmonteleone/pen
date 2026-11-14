namespace Pen.SmokeTests.Harness;

/// <summary>A docker on PATH that records each image build and the image each session runs, then runs docker.</summary>
/// <remarks>So "no rebuild" is seen rather than guessed from timing.</remarks>
internal sealed class DockerShim
{
    /// <summary>Writes the shim into <paramref name="dir"/>, with an empty build record.</summary>
    /// <param name="dir">A directory of its own, created if missing.</param>
    public DockerShim(string dir)
    {
        Dir = dir;
        Directory.CreateDirectory(dir);
        ResetBuilds();
        var builds = ShellQuote.Quote(BuildsPath);
        var image = ShellQuote.Quote(ImagePath);
        var script = $$"""
            #!/bin/sh
            if [ "$1" = build ]; then echo build >>{{builds}}; fi
            case " $* " in *" run --rm "*) printf '%s\n' "$PEN_IMAGE" >{{image}} ;; esac
            exec {{ShellQuote.Quote(SmokeEnvironment.DockerPath)}} "$@"

            """;
        var shim = Path.Combine(dir, "docker");
        File.WriteAllText(shim, script);
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Gets the directory that goes first on PATH.</summary>
    public string Dir { get; }

    /// <summary>Gets how many image builds docker was asked for since the last reset.</summary>
    public int Builds => File.ReadAllLines(BuildsPath).Length;

    /// <summary>Gets the image the last session ran, or null if none ran through the shim.</summary>
    public string? LastImage => File.Exists(ImagePath) ? File.ReadAllText(ImagePath).TrimEnd('\n') : null;

    private string BuildsPath => Path.Combine(Dir, "builds");

    private string ImagePath => Path.Combine(Dir, "image");

    /// <summary>Starts counting builds from zero.</summary>
    public void ResetBuilds() => File.WriteAllText(BuildsPath, "");

    /// <summary>Gets the same pen with the shim first on PATH.</summary>
    /// <param name="pen">The pen.</param>
    /// <returns>The pen that runs docker through the shim.</returns>
    public PenCli Apply(PenCli pen) => pen.WithPathPrefix(Dir);

    /// <summary>Starts a session with these options (pen [options] run true) and gets the image it ran.</summary>
    /// <param name="pen">The pen, without the shim.</param>
    /// <param name="penOptions">Options before run, e.g. --tool pen-test.</param>
    /// <returns>The image.</returns>
    /// <exception cref="InvalidOperationException">The session failed.</exception>
    public string RunSessionImage(PenCli pen, params string[] penOptions)
    {
        var result = Apply(pen).Run([.. penOptions, "run", "true"]);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"pen {string.Join(' ', penOptions)} run true: {result.Describe()}");
        return LastImage ?? throw new InvalidOperationException("the session ran no image through the shim");
    }
}
