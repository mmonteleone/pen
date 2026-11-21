using System.Collections.Immutable;
using Pen.TestSupport;

namespace Pen.IntegrationTests.Harness;

/// <summary>Runs the pen under test in a directory, with a HOME of the test's choosing and stdin closed or fed a
/// given text.</summary>
/// <remarks>
/// DOCKER_CONFIG and NUGET_PACKAGES always point at the real HOME's, so a throwaway HOME still reaches Docker
/// and mounts the same NuGet folder. Otherwise pen gets the environment the tests were started with, less what
/// the .NET hosts that run them added (<see cref="IsBuildHostVariable"/>), as it would from the user's shell.
/// </remarks>
internal sealed record PenCli
{
    /// <summary>Gets the HOME pen runs with.</summary>
    public required string Home { get; init; }

    /// <summary>Gets the directory pen runs in.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Gets further environment changes: a null value removes the variable.</summary>
    public ImmutableDictionary<string, string?> Environment { get; init; } =
        ImmutableDictionary<string, string?>.Empty;

    /// <summary>Gets text written to pen's stdin, which is then closed, as echo y | pen does; null for none.</summary>
    public string? Stdin { get; init; }

    /// <summary>Gets how long one pen command may run.</summary>
    public TimeSpan Timeout { get; init; } = HostCommand.DefaultTimeout;

    /// <summary>Creates a pen that runs with a throwaway HOME.</summary>
    /// <param name="home">The throwaway HOME.</param>
    /// <param name="dir">The directory pen runs in.</param>
    /// <returns>The pen.</returns>
    public static PenCli CreateFor(FakeHome home, string dir) => new() { Home = home.Path, WorkingDirectory = dir };

    /// <summary>Creates a pen that runs with the real HOME, and so the real ~/.config/pen.</summary>
    /// <param name="dir">The directory pen runs in.</param>
    /// <returns>The pen.</returns>
    public static PenCli CreateForRealHome(string dir) =>
        new() { Home = IntegrationEnvironment.RealHome, WorkingDirectory = dir };

    /// <summary>Gets the same pen in another directory.</summary>
    /// <param name="dir">The directory.</param>
    /// <returns>The pen.</returns>
    public PenCli WithWorkingDirectory(string dir) => this with { WorkingDirectory = dir };

    /// <summary>Gets the same pen with an environment variable set, or removed when the value is null.</summary>
    /// <param name="name">The variable.</param>
    /// <param name="value">Its value, or null.</param>
    /// <returns>The pen.</returns>
    public PenCli WithEnvironment(string name, string? value) =>
        this with { Environment = Environment.SetItem(name, value) };

    /// <summary>Gets the same pen with <paramref name="text"/> written to its stdin, e.g. y and a newline for a
    /// prompt.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The pen.</returns>
    public PenCli WithStdin(string text) => this with { Stdin = text };

    /// <summary>Gets the same pen with <paramref name="dir"/> first on PATH, e.g. for a docker shim.</summary>
    /// <param name="dir">The directory.</param>
    /// <returns>The pen.</returns>
    public PenCli WithPathPrefix(string dir)
    {
        var path = Environment.GetValueOrDefault("PATH") ?? System.Environment.GetEnvironmentVariable("PATH") ?? "";
        return WithEnvironment("PATH", $"{dir}:{path}");
    }

    /// <summary>Runs pen to its end.</summary>
    /// <param name="args">Pen's arguments.</param>
    /// <returns>Its output and exit code.</returns>
    public CommandResult Run(params string[] args) => CreateCommand(args).Run();

    /// <summary>Starts pen in the background, with stdin closed or fed <see cref="Stdin"/>.</summary>
    /// <param name="args">Pen's arguments.</param>
    /// <returns>The running pen.</returns>
    public RunningCommand Start(params string[] args) => CreateCommand(args).Start();

    /// <summary>Starts pen in the background with stdin open until <see cref="RunningCommand.CloseStdin"/>, after
    /// <see cref="Stdin"/> if it's set.</summary>
    /// <param name="args">Pen's arguments.</param>
    /// <returns>The running pen.</returns>
    public RunningCommand StartWithOpenStdin(params string[] args)
    {
        var command = CreateCommand(args) with { KeepStdinOpen = true };
        return command.Start();
    }

    /// <summary>Gets the host command that runs pen with these arguments.</summary>
    /// <param name="args">Pen's arguments.</param>
    /// <returns>The command.</returns>
    public HostCommand CreateCommand(params string[] args) => Wrap(IntegrationEnvironment.PenPath, args);

    /// <summary>Gets a host command that runs another program with pen's environment and directory.</summary>
    /// <param name="fileName">The program, e.g. fish or python3.</param>
    /// <param name="args">Its arguments.</param>
    /// <returns>The command.</returns>
    public HostCommand Wrap(string fileName, params string[] args) => new(fileName, args)
    {
        WorkingDirectory = WorkingDirectory,
        Environment = BuildEnvironment(),
        Timeout = Timeout,
        Stdin = Stdin,
    };

    /// <summary>Builds the environment changes pen runs with.</summary>
    /// <returns>HOME, DOCKER_CONFIG, NUGET_PACKAGES and PWD, the test process's git settings and the build
    /// hosts' variables removed, then <see cref="Environment"/>.</returns>
    public ImmutableDictionary<string, string?> BuildEnvironment()
    {
        var builder = ImmutableDictionary.CreateBuilder<string, string?>();
        foreach (var name in System.Environment.GetEnvironmentVariables().Keys.OfType<string>())
        {
            if (IsBuildHostVariable(name))
                builder[name] = null;
        }
        // The tests' own maintenance.auto setting isn't pen's environment.
        foreach (var name in NoGitMaintenance.VariableNames)
            builder[name] = null;
        builder["HOME"] = Home;
        builder["DOCKER_CONFIG"] = IntegrationEnvironment.DockerConfig;
        builder["NUGET_PACKAGES"] = IntegrationEnvironment.NuGetPackages;
        // As a shell's cd leaves it, so a symlinked directory keeps its spelling.
        builder["PWD"] = WorkingDirectory;
        foreach (var (name, value) in Environment)
            builder[name] = value;
        return builder.ToImmutable();
    }

    /// <summary>Checks whether a variable is one that dotnet run or the test platform sets for the processes they
    /// start, rather than one a user's shell would have.</summary>
    /// <remarks>
    /// The rule goes by name: MSBuild* (the SDK and extension paths, MSBUILDFAILONDRIVEENUMERATINGWILDCARD),
    /// DOTNET_HOST_PATH, DOTNET_ROOT_&lt;arch&gt; (DOTNET_ROOT_ARM64 and the like) and TESTINGPLATFORM_*.
    /// Settings a user sets themselves, such as DOTNET_ROOT or DOTNET_CLI_TELEMETRY_OPTOUT, pass through.
    /// </remarks>
    /// <param name="name">The variable's name.</param>
    /// <returns>True if pen shouldn't get it.</returns>
    private static bool IsBuildHostVariable(string name) =>
        name.StartsWith("MSBUILD", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("DOTNET_ROOT_", StringComparison.Ordinal)
        || name.StartsWith("TESTINGPLATFORM_", StringComparison.Ordinal)
        || name == "DOTNET_HOST_PATH";
}
