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
/// <param name="dir">The repo to run in, or null for a call that reads only global config.</param>
/// <param name="config">Extra "-c key=value" pairs, already split (e.g. the signing settings).</param>
/// <param name="env">Extra environment for every call, such as another HOME, or null for none.</param>
internal sealed class HostGit(string? dir, IReadOnlyList<string> config, IReadOnlyDictionary<string, string>? env)
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

    /// <summary>
    /// The start of a <c>git log</c> that prints exactly the commits given one per line on stdin, in that order,
    /// without signature output or notes; a --format argument follows.
    /// </summary>
    public static readonly IReadOnlyList<string> LogEachArgs =
        ["log", "--no-walk=unsorted", "--stdin", "--no-show-signature", "--no-notes"];

    private readonly string? _dir = dir;
    private readonly IReadOnlyList<string> _config = config;
    private readonly IReadOnlyDictionary<string, string>? _env = env;

    /// <summary>Creates a HostGit for <paramref name="dir"/> with no extra settings or environment.</summary>
    public HostGit(string? dir)
        : this(dir, [], null)
    {
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
        if (_dir is not null)
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
    /// <remarks>
    /// There's no timeout or limit: git runs to its end, however long it takes and however much it writes (a
    /// cat-file of a large blob is held whole).
    /// </remarks>
    public GitResult Run(
        IEnumerable<string> args,
        byte[]? stdin = null,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var output = Proc.CaptureBytes(CreateStartInfo(args, env), stdin);
        return new GitResult(output.ExitCode, output.Stdout, DecodeStderr(output.Stderr));
    }

    /// <summary>Runs git with <paramref name="args"/> and nothing on stdin, capturing its output.</summary>
    /// <returns>git's exit code and output, whether it succeeded or not.</returns>
    public GitResult Run(params string[] args) => Run((IEnumerable<string>)args);

    /// <summary>Runs git and reads its stdout, trimmed.</summary>
    /// <exception cref="PenException">git failed; the message has its error.</exception>
    public string ReadOutput(params string[] args)
    {
        var result = Run(args);
        if (!result.Ok)
            throw new PenException($"git {string.Join(' ', args)} failed: {result.Stderr.Trim()}");
        return result.Text.Trim();
    }

    /// <summary>Runs git and reads the non-empty lines of its stdout.</summary>
    /// <exception cref="PenException">git failed; the message names its subcommand and has its error.</exception>
    public IEnumerable<string> ReadLines(params string[] args)
    {
        var result = Run(args);
        if (!result.Ok)
            throw new PenException($"git {args[0]} failed: {result.Stderr.Trim()}");
        return result.Lines;
    }

    /// <summary>Encodes <paramref name="lines"/> for git's stdin, each ending in a newline.</summary>
    public static byte[] EncodeLines(IEnumerable<string> lines) =>
        Encoding.ASCII.GetBytes(string.Join('\n', lines) + "\n");

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
        if (env is not null)
        {
            foreach (var (name, value) in env)
                startInfo.Environment[name] = value;
        }

        foreach (var name in ScrubbedEnv)
            startInfo.Environment.Remove(name);
        foreach (var name in ForcedEnv)
            startInfo.Environment[name] = "1";
        if (_dir is null)
            startInfo.WorkingDirectory = "/";
        return startInfo;
    }

    /// <summary>
    /// Decodes git's stderr as Process decodes a redirected stream by default on Unix: UTF-8, unless a byte order
    /// mark at the start says otherwise.
    /// </summary>
    private static string DecodeStderr(byte[] stderr)
    {
        using var reader = new StreamReader(new MemoryStream(stderr), Encoding.Default,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

/// <summary>The result of a git call: exit code, raw stdout, and stderr text.</summary>
internal sealed record GitResult(int ExitCode, byte[] Stdout, string Stderr)
{
    /// <summary>Whether git exited with 0.</summary>
    public bool Ok => ExitCode == 0;

    /// <summary>Stdout decoded as UTF-8.</summary>
    public string Text => Encoding.UTF8.GetString(Stdout);

    /// <summary>The non-empty lines of stdout.</summary>
    public IEnumerable<string> Lines => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
