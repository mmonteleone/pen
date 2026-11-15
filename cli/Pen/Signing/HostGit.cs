using System.Diagnostics;
using System.Text;
using Pen.Platform;

namespace Pen.Signing;

/// <summary>Runs git on the host, with overrides that keep the repo's own config from running anything.</summary>
/// <remarks>
/// Every git call Pen makes on the host goes through here. Each one gets command-line overrides, which beat repo
/// config, so nothing in the repo's config runs a program on the host: no hooks (update-ref runs
/// reference-transaction), no fsmonitor, no pager, and no lazy fetch from a promisor remote for an object the
/// agent deleted. Replace refs are off too, so an object read by its hash is that object's own bytes. That doesn't
/// cover history walks: .git/info/grafts and .git/shallow can still change which parents one follows.
/// </remarks>
internal sealed class HostGit
{
    /// <summary>The overrides in every call, after any -C &lt;dir&gt; and before every other argument.</summary>
    public static readonly IReadOnlyList<string> Overrides =
        ["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "--no-pager"];

    // Host env that would point git at another repo than the one named here.
    private static readonly string[] ScrubbedEnv =
    [
        "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
        "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_NAMESPACE", "GIT_PAGER", "PAGER",
    ];

    private static readonly string[] ForcedEnv = ["GIT_NO_LAZY_FETCH", "GIT_NO_REPLACE_OBJECTS"];

    private readonly string? _dir;
    private readonly IReadOnlyList<string> _config;
    private readonly IReadOnlyDictionary<string, string>? _env;

    /// <summary>Creates a HostGit for <paramref name="dir"/> with no extra settings or environment.</summary>
    /// <param name="dir">The repo to run in, or null for a call that reads only global config.</param>
    public HostGit(string? dir)
        : this(dir, [], null)
    {
    }

    /// <summary>Creates a HostGit for <paramref name="dir"/> with extra settings and environment.</summary>
    /// <param name="dir">The repo to run in, or null for a call that reads only global config.</param>
    /// <param name="config">Extra "-c key=value" pairs, already split (e.g. the signing settings).</param>
    /// <param name="env">Extra environment for every call, such as another HOME, or null for none.</param>
    public HostGit(string? dir, IReadOnlyList<string> config, IReadOnlyDictionary<string, string>? env)
    {
        _dir = dir;
        _config = config;
        _env = env;
    }

    /// <summary>A HostGit for global-config reads: it runs from /, so no repo is discovered at all.</summary>
    public static HostGit Global { get; } = new(null);

    /// <summary>Creates a HostGit like this one with <paramref name="config"/> added after its own settings.</summary>
    public HostGit WithConfig(IReadOnlyList<string> config) => new(_dir, [.. _config, .. config], _env);

    /// <summary>Builds the full argument list for <c>git</c>.</summary>
    /// <returns>
    /// <c>-C</c> and the dir if there is one, then the overrides, the extra settings and the arguments.
    /// </returns>
    public List<string> BuildArgs(IEnumerable<string> args)
    {
        var all = new List<string>();
        if (_dir != null)
            all.AddRange(["-C", _dir]);
        all.AddRange(Overrides);
        all.AddRange(_config);
        all.AddRange(args);
        return all;
    }

    /// <summary>Runs git with <paramref name="args"/>, capturing its output.</summary>
    /// <param name="args">The arguments after the overrides and extra settings.</param>
    /// <param name="stdin">What to write to git's stdin, or null to close it straight away.</param>
    /// <param name="env">Environment for this call only, or null for none.</param>
    /// <returns>git's exit code and output, whether it succeeded or not.</returns>
    public GitResult Run(
        IEnumerable<string> args,
        byte[]? stdin = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        using var process = Proc.Start(CreateStartInfo(args, env));
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(stdout);
        try
        {
            using var input = process.StandardInput.BaseStream;
            if (stdin != null)
                input.Write(stdin);
        }
        catch (IOException)
        {
            // git exited without reading it; its exit code says why
        }

        copy.Wait();
        process.WaitForExit();
        return new GitResult(process.ExitCode, stdout.ToArray(), stderr.Result);
    }

    /// <summary>Runs git with <paramref name="args"/> and nothing on stdin, capturing its output.</summary>
    /// <returns>git's exit code and output, whether it succeeded or not.</returns>
    public GitResult Run(params string[] args) => Run((IEnumerable<string>)args);

    /// <summary>Runs git and reads its stdout.</summary>
    /// <returns>The trimmed stdout.</returns>
    /// <exception cref="PenException">git failed; the message has its error.</exception>
    public string ReadOutput(params string[] args)
    {
        var result = Run(args);
        if (!result.Ok)
            throw new PenException($"git {string.Join(' ', args)} failed: {result.Stderr.Trim()}");
        return result.Text.Trim();
    }

    /// <summary>Runs git and reads its stdout, if it succeeds.</summary>
    /// <returns>The trimmed stdout, or null when git fails (e.g. a config key that isn't set).</returns>
    public string? TryReadOutput(params string[] args)
    {
        var result = Run(args);
        return result.Ok ? result.Text.Trim() : null;
    }

    /// <summary>Reads a value from the global git config, following includes, never this repo's config.</summary>
    /// <returns>The value, or null if it isn't set.</returns>
    public string? ReadGlobalValue(string key) =>
        new HostGit(null, [], _env).TryReadOutput("config", "--global", "--includes", "--get", key);

    /// <summary>Checks whether a boolean in the global git config is true, following includes.</summary>
    /// <returns>True only if it's set and git reads it as true.</returns>
    public bool IsGlobalFlagOn(string key) =>
        new HostGit(null, [], _env).TryReadOutput("config", "--global", "--includes", "--type=bool", "--get", key)
            == "true";

    /// <summary>Creates the start info for a call, with the environment every call needs.</summary>
    private ProcessStartInfo CreateStartInfo(
        IEnumerable<string> args,
        IReadOnlyDictionary<string, string>? env)
    {
        var startInfo = Proc.CreateStartInfo("git", BuildArgs(args), _env);
        if (env != null)
        {
            foreach (var (name, value) in env)
                startInfo.Environment[name] = value;
        }

        foreach (var name in ScrubbedEnv)
            startInfo.Environment.Remove(name);
        foreach (var name in ForcedEnv)
            startInfo.Environment[name] = "1";
        if (_dir == null)
            startInfo.WorkingDirectory = "/";
        startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;
        startInfo.RedirectStandardInput = true;
        return startInfo;
    }
}

/// <summary>The result of a git call: exit code, raw stdout, and stderr text.</summary>
/// <param name="ExitCode">git's exit code.</param>
/// <param name="Stdout">What git wrote to stdout, as bytes.</param>
/// <param name="Stderr">What git wrote to stderr.</param>
internal sealed record GitResult(int ExitCode, byte[] Stdout, string Stderr)
{
    /// <summary>Whether git exited with 0.</summary>
    public bool Ok => ExitCode == 0;

    /// <summary>Stdout decoded as UTF-8.</summary>
    public string Text => Encoding.UTF8.GetString(Stdout);

    /// <summary>The non-empty lines of stdout.</summary>
    public IEnumerable<string> Lines => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
