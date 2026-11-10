using System.Diagnostics;
using System.Text;

namespace Pen;

/// <summary>The result of a git call: exit code, raw stdout, and stderr text.</summary>
internal sealed record GitResult(int ExitCode, byte[] Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    public string Text => Encoding.UTF8.GetString(Stdout);
    public IEnumerable<string> Lines => Text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>
/// Every git call Pen makes on the host goes through here.
/// Each one gets command-line overrides, which beat repo
/// config, so nothing in the repo's config runs a program on the host: no
/// hooks (update-ref runs reference-transaction), no fsmonitor, no pager, and
/// no lazy fetch from a promisor remote for an object the agent deleted. Replace
/// refs are off too, so an object read by its hash is that object's own bytes.
/// That doesn't cover history walks: .git/info/grafts and .git/shallow can still
/// change which parents one follows.
/// </summary>
internal sealed class HostGit
{
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

    /// <param name="dir">The repo to run in, or null for a call that reads only global config.</param>
    /// <param name="config">Extra "-c key=value" pairs, already split (e.g. the signing settings).</param>
    /// <param name="env">Extra environment for every call, such as another HOME.</param>
    public HostGit(string? dir, IReadOnlyList<string>? config = null, IReadOnlyDictionary<string, string>? env = null)
    {
        _dir = dir;
        _config = config ?? [];
        _env = env;
    }

    /// <summary>For global-config reads: run from /, so no repo is discovered at all.</summary>
    public static HostGit Global { get; } = new(null);

    public HostGit With(IReadOnlyList<string> config) => new(_dir, [.. _config, .. config], _env);

    /// <summary>The full argument list for <c>git</c>: the overrides always come first.</summary>
    public List<string> Args(IEnumerable<string> args)
    {
        var all = new List<string>();
        if (_dir != null) all.AddRange(["-C", _dir]);
        all.AddRange(Overrides);
        all.AddRange(_config);
        all.AddRange(args);
        return all;
    }

    public GitResult Run(IEnumerable<string> args, byte[]? stdin = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = Proc.Info("git", Args(args), _env);
        if (env != null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        foreach (var name in ScrubbedEnv) psi.Environment.Remove(name);
        foreach (var name in ForcedEnv) psi.Environment[name] = "1";
        if (_dir == null) psi.WorkingDirectory = "/";
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;

        using var p = Proc.Start(psi);
        var stderr = p.StandardError.ReadToEndAsync();
        var stdout = new MemoryStream();
        var copy = p.StandardOutput.BaseStream.CopyToAsync(stdout);
        try
        {
            using var input = p.StandardInput.BaseStream;
            if (stdin != null) input.Write(stdin);
        }
        catch (IOException) { } // git exited without reading it; its exit code says why
        copy.Wait();
        p.WaitForExit();
        return new GitResult(p.ExitCode, stdout.ToArray(), stderr.Result);
    }

    public GitResult Run(params string[] args) => Run((IEnumerable<string>)args);

    /// <summary>Trimmed stdout, or a PenException with git's error.</summary>
    public string Out(params string[] args)
    {
        var r = Run(args);
        if (!r.Ok) throw new PenException($"git {string.Join(' ', args)} failed: {r.Stderr.Trim()}");
        return r.Text.Trim();
    }

    /// <summary>Trimmed stdout, or null when git fails (e.g. a config key that isn't set).</summary>
    public string? TryOut(params string[] args)
    {
        var r = Run(args);
        return r.Ok ? r.Text.Trim() : null;
    }

    /// <summary>A global config value (includes followed), or null if unset. Never reads this repo's config.</summary>
    public string? GlobalValue(string key) =>
        new HostGit(null, null, _env).TryOut("config", "--global", "--includes", "--get", key);

    public bool GlobalFlag(string key) =>
        new HostGit(null, null, _env).TryOut("config", "--global", "--includes", "--type=bool", "--get", key) == "true";

    public static string? GlobalConfig(string key) => Global.GlobalValue(key);
}
