namespace Pen.SmokeTests.Harness;

/// <summary>A throwaway project under the real HOME, with its key, runtime dirs and full cleanup.</summary>
/// <remarks>
/// It's under the real HOME so that checks of what the sandbox sees under the host's home root mean something.
/// Cleanup removes the state of the resolved key and every key pen made for its path, under the real HOME and
/// each throwaway HOME it was told about: containers, networks, volumes, images and runtime dirs.
/// </remarks>
internal sealed class SmokeProject : IDisposable
{
    private readonly List<string> _homes = [SmokeEnvironment.RealHome];
    private readonly List<string> _extraKeys = [];
    private string? _key;

    private SmokeProject(string dir)
    {
        Dir = dir;
        Hash = Files.HashPath(dir);
    }

    /// <summary>Gets the project's real path.</summary>
    public string Dir { get; }

    /// <summary>Gets the hash pen puts at the end of the project's key.</summary>
    public string Hash { get; }

    /// <summary>Gets the project key, once <see cref="ResolveKey"/> has found it.</summary>
    /// <exception cref="InvalidOperationException">No session has fixed the key yet.</exception>
    public string Key => _key ?? throw new InvalidOperationException($"no project key resolved for {Dir} yet");

    /// <summary>Creates a git repo with one empty commit, as smoke.sh's fixture project is (.pen-smoke.*).</summary>
    /// <remarks>If git fails, the directory is deleted before the error is thrown: nothing tracks it yet.</remarks>
    /// <returns>The project.</returns>
    public static SmokeProject CreateRepo()
    {
        var project = CreateEmpty(".pen-smoke.");
        try
        {
            project.RunGit("init", "-q");
            project.RunGit(
                "-c", "user.name=smoke", "-c", "user.email=smoke@example.invalid", "-c", "commit.gpgsign=false",
                "commit", "-q", "--allow-empty", "-m", "init");
        }
        catch
        {
            // Git's error is the one to report; one from the deletion would only hide it.
            new Cleanup().Run(() => Files.DeleteTree(project.Dir));
            throw;
        }
        return project;
    }

    /// <summary>Creates an empty directory that isn't a repo.</summary>
    /// <param name="prefix">The start of its directory's name under the real HOME.</param>
    /// <returns>The project.</returns>
    public static SmokeProject CreateEmpty(string prefix) =>
        new(Files.CreateUniqueDir(SmokeEnvironment.RealHome, prefix));

    /// <summary>Has cleanup also remove the project's state under a throwaway HOME.</summary>
    /// <param name="home">The HOME pen ran with.</param>
    public void CleanUpUnder(string home) => _homes.Add(home);

    /// <summary>Has cleanup also remove another key's state, e.g. one whose directory a test deleted.</summary>
    /// <param name="key">The project key.</param>
    public void ForgetKeyOnDispose(string key) => _extraKeys.Add(key);

    /// <summary>Finds the runtime dirs pen made for this path under a HOME (pen-*-hash).</summary>
    /// <param name="home">The HOME.</param>
    /// <returns>Their paths.</returns>
    public IReadOnlyList<string> FindRuntimeDirs(string home)
    {
        var stateRoot = Path.Combine(home, ".local", "state", "pen");
        if (!Directory.Exists(stateRoot))
            return [];
        return [.. Directory.EnumerateDirectories(stateRoot, $"pen-*-{Hash}").Order(StringComparer.Ordinal)];
    }

    /// <summary>Fixes <see cref="Key"/> from the one runtime dir a session made under a HOME.</summary>
    /// <param name="home">The HOME pen ran with.</param>
    /// <returns>The key.</returns>
    /// <exception cref="InvalidOperationException">There isn't exactly one such runtime dir.</exception>
    public string ResolveKey(string home)
    {
        var dirs = FindRuntimeDirs(home);
        if (dirs.Count != 1)
            throw new InvalidOperationException($"expected one runtime dir for {Dir} under {home}, found {dirs.Count}");
        _key = Path.GetFileName(dirs[0]);
        return _key;
    }

    /// <summary>Gets the newest session dir under a runtime dir, as ls -td sessions/*/ | head -1 does.</summary>
    /// <param name="runtimeDir">The project's runtime dir.</param>
    /// <returns>The session dir.</returns>
    public static string FindLastSession(string runtimeDir) =>
        new DirectoryInfo(Path.Combine(runtimeDir, "sessions")).EnumerateDirectories()
            .OrderByDescending(dir => dir.LastWriteTimeUtc).First().FullName;

    /// <summary>Runs host git in the project with the user's own config, as smoke.sh's bare git does.</summary>
    /// <param name="args">Git's arguments.</param>
    /// <returns>Git's stdout.</returns>
    /// <exception cref="InvalidOperationException">Git exited non-zero.</exception>
    public string RunGit(params string[] args)
    {
        var result = new HostCommand("git", ["-C", Dir, .. args]).Run();
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.Output}");
        return result.Stdout;
    }

    /// <summary>Removes the project's state under every HOME it knows, the extra keys' state and the project.</summary>
    /// <remarks>Every step runs even if an earlier one fails; the first error is thrown at the end.</remarks>
    public void Dispose()
    {
        var cleanup = new Cleanup();
        List<string> keys = [];
        if (_key != null)
            keys.Add(_key);
        foreach (var home in _homes.Distinct())
            cleanup.Run(() => keys.AddRange(FindRuntimeDirs(home).Select(dir => Path.GetFileName(dir))));
        keys.AddRange(_extraKeys);
        foreach (var key in keys.Distinct())
            ForgetKey(key, cleanup);
        cleanup.Run(() => Files.DeleteTree(Dir));
        cleanup.ThrowIfFailed();
    }

    private void ForgetKey(string key, Cleanup cleanup)
    {
        cleanup.Run(() => Docker.RemoveKey(key));
        foreach (var home in _homes.Distinct())
            cleanup.Run(() => Files.DeleteTree(Path.Combine(home, ".local", "state", "pen", key)));
    }
}
