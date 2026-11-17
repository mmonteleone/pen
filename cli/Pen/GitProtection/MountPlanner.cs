using System.Diagnostics.CodeAnalysis;
using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// Plans the mounts over the project mount that keep the agent from changing what host git reads or runs.
/// </summary>
/// <remarks>
/// <para>
/// The project dir is checked by <see cref="RepoGuard.CheckAtStart"/> first, and messages are relative to it. The
/// host user's home is for includes of ~/ paths.
/// </para>
/// <para>
/// It creates what must exist to be mounted (.pen/, .git/hooks, and a few files inside .git) so that the agent can't
/// create them instead. Like the rest of <see cref="RepoGuard"/>, it reads files directly and never runs git.
/// </para>
/// <para>
/// For a linked worktree, the main repo's git dir (the common dir: objects, refs, the worktree's own entry) is
/// mounted read-write, protected like a main checkout's .git; the worktree's .git file read-only; and the main
/// checkout's own HEAD and index read-only, since the main checkout isn't visible in the sandbox. The submodule repos
/// kept in that git dir are protected the same way (<see cref="ProtectModuleRepos"/>).
/// </para>
/// </remarks>
internal sealed class MountPlanner(string dir, string hostHome)
{
    /// <summary>The mounts so far, in the order they're planned; a path can be in it more than once.</summary>
    private readonly List<Mount> _mounts = [];

    /// <summary>
    /// The config files this run reads, each once however many walks reach it. It lives only as long as the planner,
    /// which plans once, since the files can change before the next session.
    /// </summary>
    private readonly GitConfigIncludes _includes = new(hostHome);

    /// <summary>
    /// Creates what must exist to be mounted, if it's missing, and plans the project's mounts. Call it once per
    /// planner.
    /// </summary>
    /// <returns>
    /// The mounts in the order first planned, one per path. A path planned more than once keeps its first mount,
    /// verified if any of its mounts was.
    /// </returns>
    public List<Mount> EnsureTargetsAndPlan()
    {
        var git = Path.Combine(dir, ".git");
        if (Directory.Exists(git))
            PlanMainCheckout(git);
        else if (File.Exists(git))
            PlanLinkedWorktree(git);

        // .pen/ sets the next session's policy, so the agent must not change or create it.
        var penDir = Path.Combine(dir, ".pen");
        EnsureDirectory(dir, penDir);
        _mounts.Add(new(penDir, ReadOnly: true));

        // A file that's also read as config (an include of HEAD) is verified however else it's mounted.
        return [.. _mounts.GroupBy(m => m.Path, StringComparer.Ordinal)
            .Select(g => g.First() with { Verify = g.Any(m => m.Verify) })];
    }

    /// <summary>Plans the mounts for a main checkout, whose .git is <paramref name="git"/>.</summary>
    private void PlanMainCheckout(string git)
    {
        ProtectGitDir(git, git);
        ProtectModuleRepos(git);
    }

    /// <summary>Plans the mounts for a linked worktree, whose .git file is <paramref name="git"/>.</summary>
    private void PlanLinkedWorktree(string git)
    {
        var common = RepoScan.ResolveWorktree(git)
            ?? throw new PenException(".git is a file that isn't a valid linked worktree");
        ProtectGitDir(common, common);
        foreach (var name in new[] { "HEAD", "index" })
        {
            var file = Path.Combine(common, name);
            if (!File.Exists(file))
                continue;
            RequireNotLink(dir, file);
            // Not verified: the main checkout's git changes these legitimately.
            _mounts.Add(new(file, ReadOnly: true));
        }
        // The .git file names the git dir, so the agent must not repoint it.
        _mounts.Add(new(git, ReadOnly: true, Verify: true));
        ProtectModuleRepos(common);
    }

    /// <summary>
    /// Plans the mounts over a git dir (the project's .git, the common dir of the worktree it is, or a module repo in
    /// either) that keep the agent from changing what host git reads as config or runs as hooks, for this checkout
    /// and every other worktree sharing the dir. All of <paramref name="common"/>, the project's git dir, is writable
    /// in the sandbox, whichever repo in it <paramref name="gitDir"/> is.
    /// </summary>
    private void ProtectGitDir(string gitDir, string common)
    {
        // The git dir itself becomes a mount point, so it can't be renamed away and replaced.
        _mounts.Add(new(gitDir, ReadOnly: false));

        var config = Path.Combine(gitDir, "config");
        EnsureFile(config);
        _mounts.Add(new(config, ReadOnly: true, Verify: true));

        var hooks = Path.Combine(gitDir, "hooks");
        EnsureDirectory(dir, hooks);
        _mounts.Add(new(hooks, ReadOnly: true));

        var worktreeConfig = AddIncludes(common, config);
        // With extensions.worktreeConfig, git also reads config.worktree files, which the agent could otherwise
        // create. Any true value is enough, since includeIf conditions may differ in another worktree or branch
        // sharing this git dir.
        AddWorktreeConfig(common, Path.Combine(gitDir, "config.worktree"), worktreeConfig);
        ProtectWorktreeEntries(gitDir, common, worktreeConfig);
    }

    /// <summary>
    /// Plans the mounts over each linked worktree's entry in <paramref name="gitDir"/>.
    /// <paramref name="worktreeConfig"/> is whether any config in the git dir may enable extensions.worktreeConfig.
    /// </summary>
    /// <remarks>
    /// Host git in each linked worktree reads config and hooks from wherever its commondir points. The worktrees the
    /// project dir doesn't contain aren't visible in the sandbox, so their entries are read-only as a whole.
    /// </remarks>
    private void ProtectWorktreeEntries(string gitDir, string common, bool worktreeConfig)
    {
        var worktrees = Path.Combine(gitDir, "worktrees");
        if (PathChecks.IsLink(worktrees))
            throw new PenException($"{FormatPath(worktrees)} is a symlink, which pen doesn't support");
        if (!Directory.Exists(worktrees))
            return;
        // This is a mount point too, so the entries mounted below can't be moved aside together and replaced with
        // forged ones.
        _mounts.Add(new(worktrees, ReadOnly: false));
        foreach (var entry in Directory.EnumerateDirectories(worktrees).Order(StringComparer.Ordinal))
            ProtectWorktreeEntry(entry, common, worktreeConfig);
    }

    /// <summary>
    /// Plans the mounts over one linked worktree's entry, &lt;git dir&gt;/worktrees/&lt;name&gt;.
    /// <paramref name="worktreeConfig"/> is whether any config in the git dir may enable extensions.worktreeConfig.
    /// </summary>
    private void ProtectWorktreeEntry(string entry, string common, bool worktreeConfig)
    {
        if (PathChecks.IsLink(entry))
            throw new PenException($"{FormatPath(entry)} is a symlink, which pen doesn't support");
        var commondir = Path.Combine(entry, "commondir");
        if (PathChecks.ExistsOrIsLink(commondir))
            RequireNotLink(dir, commondir);
        if (!RepoScan.IsWorktreeInside(dir, entry))
        {
            // The worktree isn't visible in the sandbox, so its entry looks dangling there and `git worktree prune`
            // would delete it. Read-only keeps it, and its HEAD, index and commondir.
            _mounts.Add(new(entry, ReadOnly: true));
            // Its config can include a file in the writable project.
            var worktreeFile = Path.Combine(entry, "config.worktree");
            RequireNotLink(dir, worktreeFile);
            AddIncludes(common, worktreeFile);
            return;
        }
        if (PathChecks.ExistsOrIsLink(commondir))
            _mounts.Add(new(commondir, ReadOnly: true, Verify: true));
        AddWorktreeConfig(common, Path.Combine(entry, "config.worktree"), worktreeConfig);
    }

    /// <summary>
    /// Plans the mounts over each submodule repo kept in <paramref name="common"/> (see
    /// <see cref="RepoScan.FindModuleRepos"/>): the same protection as the git dir itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Host git running in a submodule checkout reads its config and runs its hooks. A module repo that's a symlink
    /// or has a commondir is refused, since either makes git read config and hooks from another directory.
    /// </para>
    /// <para>
    /// Each directory between <paramref name="common"/> and a module repo (modules/, the levels of a name with "/" in
    /// it, a worktree's entry) is a mount point too, like the repo itself: otherwise one could be moved aside, taking
    /// the repo's mounts with it, and a forged repo made at the path the session-end check knows as a submodule's.
    /// </para>
    /// </remarks>
    private void ProtectModuleRepos(string common)
    {
        foreach (var repo in RepoScan.FindModuleRepos(common))
            ProtectModuleRepo(repo, common);
    }

    /// <summary>Plans the mounts over one submodule repo kept in <paramref name="common"/>.</summary>
    private void ProtectModuleRepo(string repo, string common)
    {
        if (PathChecks.IsLink(repo))
        {
            throw new PenException($"{FormatPath(repo)} is a symlink, which pen doesn't support: " +
                $"a submodule's git dir has to be a real directory inside {FormatPath(common)}");
        }

        var commondir = Path.Combine(repo, "commondir");
        if (PathChecks.ExistsOrIsLink(commondir))
        {
            throw new PenException($"{FormatPath(commondir)} exists, which a submodule's repo never has: it " +
                "makes git read config and hooks from another directory. Inspect it and remove it, then rerun");
        }

        // One under a path already mounted read-only (an external worktree's entry) is left alone: it's read-only
        // with it, and a writable mount of its own would undo that, though what its config includes can still be
        // writable.
        if (_mounts.Any(m => m.ReadOnly && PathChecks.IsInside(m.Path, repo)))
        {
            foreach (var name in new[] { "config", "config.worktree" })
            {
                var file = Path.Combine(repo, name);
                RequireNotLink(dir, file);
                AddIncludes(common, file);
            }
            return;
        }
        foreach (var between in ListDirsBetween(common, repo))
            _mounts.Add(new(between, ReadOnly: false));
        ProtectGitDir(repo, common);
    }

    /// <summary>Lists the directories strictly between <paramref name="common"/> and <paramref name="repo"/>.</summary>
    /// <returns>The directories, outermost first.</returns>
    private static Stack<string> ListDirsBetween(string common, string repo) =>
        new(Paths.EnumerateAncestors(repo)
            .TakeWhile(parent => parent != common && PathChecks.IsInside(common, parent)));

    /// <summary>
    /// Plans a read-only mount over a config.worktree, with its includes. Unless extensions.worktreeConfig may be on
    /// (<paramref name="enabled"/>), only an existing file is mounted.
    /// </summary>
    private void AddWorktreeConfig(string common, string path, bool enabled)
    {
        if (!enabled && !PathChecks.ExistsOrIsLink(path))
            return;
        EnsureFile(path);
        _mounts.Add(new(path, ReadOnly: true, Verify: true));
        AddIncludes(common, path);
    }

    /// <summary>
    /// Plans read-only mounts over the files <paramref name="configFile"/>'s include.path and includeIf.*.path
    /// settings name, recursively, wherever they're inside the project or <paramref name="common"/>.
    /// </summary>
    /// <returns>Whether any file in the walk sets extensions.worktreeConfig.</returns>
    /// <remarks>
    /// <para>
    /// The project and the project's git dir are what the sandbox can write, whichever repo in the git dir the
    /// including file belongs to. The agent could write a file there and host git would read it as config, so each is
    /// mounted read-only too. A missing one inside the git dir is created empty; a missing one in the working tree is
    /// refused, since creating it would add a file to the project. A directory there is refused: it isn't mounted, so
    /// the agent could replace it with a file. A path that wasn't valid UTF-8 is refused: decoded, it has U+FFFD where
    /// git has other bytes, so Pen would protect a different file from the one git reads.
    /// </para>
    /// <para>
    /// The agent can write some of these files, so each is walked only once, and a walk over
    /// <see cref="GitConfigIncludes.MaxFiles"/> files is refused. Depth itself isn't limited, since a file first met
    /// deep down mustn't lose what it includes when it's met again higher up. Files are keyed by path as written, not
    /// real path (<see cref="IncludeLocation.Path"/>): a relative include resolves from there. A ~user or %(prefix)
    /// path is refused: Pen can't tell where it leads.
    /// </para>
    /// </remarks>
    private bool AddIncludes(string common, string configFile)
    {
        // Each walk starts afresh: a file seen in another walk is followed again in this one.
        var visited = new HashSet<string>(StringComparer.Ordinal) { configFile };
        return FollowIncludes(common, configFile, visited);
    }

    /// <summary>
    /// Follows <paramref name="configFile"/>'s includes, within one walk, whose config files met so far are in
    /// <paramref name="visited"/>.
    /// </summary>
    /// <returns>Whether it or any file it leads to sets extensions.worktreeConfig.</returns>
    /// <remarks>
    /// Every include is followed, whatever its <see cref="IncludeReading"/>: conditional includes for every worktree
    /// and branch, and the forms git ignores too, conservatively.
    /// </remarks>
    private bool FollowIncludes(string common, string configFile, HashSet<string> visited)
    {
        if (!_includes.TryRead(configFile, out var lines, out var error))
            throw new PenException(error);
        var worktreeConfig = false;
        foreach (var (entry, include) in lines)
        {
            if (entry.Section == "extensions" && entry.Key == "worktreeconfig" && GitConfigFile.IsTrue(entry.Value))
                worktreeConfig = true;
            if (include is null)
                continue;
            var location = Locate(configFile, include);
            if (!visited.Add(location.Path))
                continue;
            if (visited.Count > GitConfigIncludes.MaxFiles)
            {
                throw new PenException($"{FormatPath(configFile)} takes git config includes past " +
                    $"{GitConfigIncludes.MaxFiles} files, more than pen will follow: remove it or the includes in " +
                    "it, then rerun");
            }

            if (location.Written != location.Real && IsWritable(common, location.Written))
            {
                throw new PenException($"{FormatPath(configFile)} includes {FormatPath(location.Written)} through " +
                    "a symlink, which pen doesn't support: the agent could redirect the include");
            }

            if (Directory.Exists(location.Real))
            {
                // Git fails on a directory when it reaches the include, so one outside the sandbox is left alone. One
                // the sandbox can write isn't mounted, so the agent could replace it with a file git would read: for a
                // conditional include, once the agent makes the condition hold (by switching branch, say).
                if (IsWritable(common, location.Real))
                {
                    throw new PenException($"{FormatPath(configFile)} includes {FormatPath(location.Real)}, " +
                        "which is a directory: git can't read it as config, and the agent could replace it with a " +
                        "file that git on this machine would read. Point the include at a file or remove it, then " +
                        "rerun");
                }
                continue;
            }
            ProtectIncludedFile(common, configFile, location.Real);
            // Even host-only includes can enable worktreeConfig or include another file that the sandbox can write.
            worktreeConfig |= FollowIncludes(common, location.Path, visited);
        }
        return worktreeConfig;
    }

    /// <summary>Finds the file an include in <paramref name="configFile"/> names.</summary>
    /// <exception cref="PenException">
    /// The path isn't valid UTF-8, is a form Pen can't resolve, or can't be followed.
    /// </exception>
    private IncludeLocation Locate(string configFile, ConfigInclude include)
    {
        var opened = include.Target switch
        {
            GitConfigFile.ExpandedPath.Expanded expanded => expanded.Path,
            GitConfigFile.ExpandedPath.Refused { Refusal: GitConfigFile.PathRefusal.NotUtf8 } =>
                throw new PenException($"{FormatPath(configFile)} includes a path that isn't valid UTF-8, so " +
                    "pen can't tell which file git would read: remove the include or rename the file, then rerun"),
            _ => throw new PenException($"{FormatPath(configFile)} includes {Out.Field(include.Value)}, and " +
                "pen can't resolve that form of path, so it can't tell which file git " +
                "would read: write the path out in full or remove the include, then rerun"),
        };

        if (!TryLocate(opened, out var location))
        {
            throw new PenException($"{FormatPath(configFile)} includes " +
                $"{FormatPath(Path.GetFullPath(opened))}, and pen can't follow that path to the file git " +
                "would read: fix its permissions or remove the include, then rerun");
        }
        return location;
    }

    /// <summary>
    /// Finds where the file <paramref name="opened"/> names is on disk, walking it as the OS does.
    /// </summary>
    /// <returns>Whether the path could be walked (<see cref="HooksPath.WalkRealPath(string)"/>).</returns>
    /// <remarks>
    /// The written and real paths are taken on paper where a name is missing, so "missing/../x" is the x beside it,
    /// though the OS can't open that path until missing exists.
    /// </remarks>
    private static bool TryLocate(string opened, [NotNullWhen(true)] out IncludeLocation? location)
    {
        // As the OS walks it: ".." after a symlink is the parent of where that leads.
        var written = Path.GetFullPath(opened);
        if (HooksPath.WalkRealPath(opened) is not { } real)
        {
            location = null;
            return false;
        }
        // No symlink to tell them apart, and a cycle then repeats one name.
        location = new(real == written ? written : opened, written, real);
        return true;
    }

    /// <summary>
    /// Where an included file is on disk, for a caller that visits each file once.
    /// </summary>
    /// <param name="Path">
    /// The path git reads it by, absolute: as opened, or normalized when no symlink makes the two differ. It keys the
    /// walk, and a relative include in it resolves from there.
    /// </param>
    /// <param name="Written">The full path as written, with "." and ".." taken lexically.</param>
    /// <param name="Real">The real path, as the OS walks it.</param>
    private sealed record IncludeLocation(string Path, string Written, string Real);

    /// <summary>
    /// Plans a verified read-only mount over an included file, by its real path, if the sandbox could write it,
    /// creating it first if it's missing from the git dir.
    /// </summary>
    private void ProtectIncludedFile(string common, string configFile, string real)
    {
        if (!IsWritable(common, real))
            return;
        if (!File.Exists(real))
            CreateIncludedFile(common, configFile, real);
        RequireNotLink(dir, real);
        _mounts.Add(new(real, ReadOnly: true, Verify: true));
    }

    /// <summary>
    /// Whether the sandbox can write <paramref name="path"/>: it's in the project or <paramref name="common"/>, the
    /// project's git dir.
    /// </summary>
    private bool IsWritable(string common, string path) =>
        PathChecks.IsInside(dir, path) || PathChecks.IsInside(common, path);

    /// <summary>Creates a missing included file in the git dir empty, so that it can be mounted.</summary>
    /// <exception cref="PenException">
    /// The file would be in the working tree, where creating it would add a file to the project, or it can't be
    /// created.
    /// </exception>
    private void CreateIncludedFile(string common, string configFile, string real)
    {
        if (!PathChecks.IsInside(common, real))
        {
            throw new PenException($"{FormatPath(configFile)} includes {FormatPath(real)}, which " +
                "doesn't exist: the agent could create it, and git on this machine would read " +
                "it as config. Create it (it can be empty) or remove the include, then rerun");
        }

        try
        {
            Directory.CreateDirectory(Paths.GetParent(real));
            File.WriteAllText(real, "");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new PenException($"{FormatPath(configFile)} includes {FormatPath(real)}, " +
                "which pen can't create to protect it (a parent is a file, the name is too " +
                "long, or it's not writable): remove it or the include in it, then rerun");
        }
    }

    /// <summary>
    /// Creates <paramref name="path"/> empty if it's missing, refusing a symlink or a directory there.
    /// </summary>
    private void EnsureFile(string path)
    {
        RequireNotLink(dir, path);
        if (Directory.Exists(path))
            throw new PenException($"{FormatPath(path)} is a directory, not a file");
        if (!File.Exists(path))
            File.WriteAllText(path, "");
    }

    /// <summary>
    /// Formats <paramref name="path"/> for a message, relative to the project dir
    /// (<see cref="PathChecks.FormatPath"/>).
    /// </summary>
    private string FormatPath(string path) => PathChecks.FormatPath(dir, path);

    /// <summary>
    /// Creates <paramref name="path"/> as a directory to be mounted, refusing a symlink or a file there.
    /// </summary>
    internal static void EnsureDirectory(string dir, string path)
    {
        RequireNotLink(dir, path);
        if (File.Exists(path))
        {
            throw new PenException($"{PathChecks.FormatPath(dir, path)} is a file, not a " +
                "directory: pen needs to mount it read-only as a directory");
        }

        Directory.CreateDirectory(path);
    }

    /// <summary>Refuses a symlink at <paramref name="path"/>, which Pen has to mount read-only.</summary>
    internal static void RequireNotLink(string dir, string path)
    {
        if (PathChecks.IsLink(path))
        {
            throw new PenException($"{PathChecks.FormatPath(dir, path)} is a symlink, which " +
                "pen doesn't support here: it has to mount it read-only");
        }
    }
}
