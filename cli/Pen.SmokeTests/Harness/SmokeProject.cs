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

    /// <summary>Creates a git repo with one empty commit, like a fixture project (.pen-smoke.*).</summary>
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

    /// <summary>Creates a directory, not a repo, at a given path, e.g. in a fixture's scratch directory.</summary>
    /// <param name="path">The directory, as a real path; its parent must exist.</param>
    /// <returns>The project.</returns>
    public static SmokeProject CreateAt(string path)
    {
        Directory.CreateDirectory(path);
        return new(path);
    }

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

    /// <summary>Runs host git in the project with the user's own config.</summary>
    /// <param name="args">Git's arguments.</param>
    /// <returns>Git's stdout.</returns>
    /// <exception cref="InvalidOperationException">Git exited non-zero.</exception>
    public string RunGit(params string[] args) => SmokeGit.Run(Dir, args);

    /// <summary>Removes the project's state under every HOME it knows, the extra keys' state and the project.</summary>
    /// <remarks>Every step runs even if an earlier one fails; the first error is thrown at the end.</remarks>
    public void Dispose()
    {
        var cleanup = new Cleanup();
        foreach (var key in FindKeys(cleanup))
            ForgetKey(key, cleanup);
        cleanup.Run(() => Files.DeleteTree(Dir));
        cleanup.ThrowIfFailed();
    }

    /// <summary>Finds every key this project's state is under: the resolved one, those of its runtime dirs under each
    /// HOME and the extra ones.</summary>
    /// <param name="cleanup">Keeps the error if a HOME can't be read.</param>
    /// <returns>The keys, without duplicates.</returns>
    public IReadOnlyList<string> FindKeys(Cleanup cleanup)
    {
        List<string> keys = [];
        if (_key != null)
            keys.Add(_key);
        foreach (var home in _homes.Distinct())
            cleanup.Run(() => keys.AddRange(FindRuntimeDirs(home).Select(dir => Path.GetFileName(dir))));
        keys.AddRange(_extraKeys);
        return [.. keys.Distinct()];
    }

    private void ForgetKey(string key, Cleanup cleanup)
    {
        cleanup.Run(() => Docker.RemoveKey(key));
        foreach (var home in _homes.Distinct())
            cleanup.Run(() => Files.DeleteTree(Path.Combine(home, ".local", "state", "pen", key)));
    }
}
