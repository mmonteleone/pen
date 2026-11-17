using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// Plans the mounts over the project mount that keep the agent from changing what host git reads or runs.
/// </summary>
/// <param name="dir">
/// The project dir, checked by <see cref="RepoGuard.CheckAtStart"/>; messages are relative to it.
/// </param>
/// <param name="hostHome">The host user's home, for includes of ~/ paths.</param>
/// <remarks>
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

    /// <summary>The config files the current walk of includes has met, keyed by path as written.</summary>
    private readonly HashSet<string> _visited = new(StringComparer.Ordinal);

    /// <summary>Plans the project's mounts. Call it once per planner.</summary>
    /// <returns>
    /// The mounts in the order first planned, one per path. A path planned more than once keeps its first mount,
    /// verified if any of its mounts was.
    /// </returns>
    public List<Mount> Plan()
    {
        var git = Path.Combine(dir, ".git");
        if (Directory.Exists(git))
            PlanMainCheckout(git);
        else if (File.Exists(git))
            PlanLinkedWorktree(git);

        // .pen/ sets the next session's policy, so the agent must not change or create it.
        var penDir = Path.Combine(dir, ".pen");
        RepoGuard.EnsureDirectory(dir, penDir);
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
            RepoGuard.RequireNotLink(dir, file);
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
    /// and every other worktree sharing the dir.
    /// </summary>
    /// <param name="gitDir">The git dir to protect.</param>
    /// <param name="common">
    /// The project's git dir, all of it writable in the sandbox, whichever repo in it <paramref name="gitDir"/> is.
    /// </param>
    private void ProtectGitDir(string gitDir, string common)
    {
        // The git dir itself becomes a mount point, so it can't be renamed away and replaced.
        _mounts.Add(new(gitDir, ReadOnly: false));

        var config = Path.Combine(gitDir, "config");
        EnsureFile(config);
        _mounts.Add(new(config, ReadOnly: true, Verify: true));

        var hooks = Path.Combine(gitDir, "hooks");
        RepoGuard.EnsureDirectory(dir, hooks);
        _mounts.Add(new(hooks, ReadOnly: true));

        var worktreeConfig = AddIncludes(common, config);
        // With extensions.worktreeConfig, git also reads config.worktree
        // files, which the agent could otherwise create.
        // Any true value is enough: includeIf conditions may differ in a
        // different worktree or branch sharing this git dir.
        AddWorktreeConfig(common, Path.Combine(gitDir, "config.worktree"), worktreeConfig);
        ProtectWorktreeEntries(gitDir, common, worktreeConfig);
    }

    /// <summary>Plans the mounts over each linked worktree's entry in <paramref name="gitDir"/>.</summary>
    /// <param name="gitDir">The git dir whose worktrees/ to protect.</param>
    /// <param name="common">The project's git dir.</param>
    /// <param name="worktreeConfig">Whether any config in the git dir may enable extensions.worktreeConfig.</param>
    /// <remarks>
    /// Host git in each linked worktree reads config and hooks from wherever its commondir points. The worktrees the
    /// project dir doesn't contain aren't visible in the sandbox, so their entries are read-only as a whole.
    /// </remarks>
    private void ProtectWorktreeEntries(string gitDir, string common, bool worktreeConfig)
    {
        var worktrees = Path.Combine(gitDir, "worktrees");
        if (RepoGuard.IsLink(worktrees))
            throw new PenException($"{FormatPath(worktrees)} is a symlink, which pen doesn't support");
        if (!Directory.Exists(worktrees))
            return;
        // A mount point too, so the entries mounted below can't be moved
        // aside together and replaced with forged ones.
        _mounts.Add(new(worktrees, ReadOnly: false));
        foreach (var entry in Directory.EnumerateDirectories(worktrees).Order(StringComparer.Ordinal))
            ProtectWorktreeEntry(entry, common, worktreeConfig);
    }

    /// <summary>Plans the mounts over one linked worktree's entry, &lt;git dir&gt;/worktrees/&lt;name&gt;.</summary>
    /// <param name="entry">The worktree's entry.</param>
    /// <param name="common">The project's git dir.</param>
    /// <param name="worktreeConfig">Whether any config in the git dir may enable extensions.worktreeConfig.</param>
    private void ProtectWorktreeEntry(string entry, string common, bool worktreeConfig)
    {
        if (RepoGuard.IsLink(entry))
            throw new PenException($"{FormatPath(entry)} is a symlink, which pen doesn't support");
        var commondir = Path.Combine(entry, "commondir");
        if (RepoGuard.Exists(commondir))
            RepoGuard.RequireNotLink(dir, commondir);
        if (!RepoScan.IsWorktreeInside(dir, entry))
        {
            // The worktree isn't visible in the sandbox, so its entry
            // looks dangling there: `git worktree prune` would delete
            // it. Read-only keeps it, and its HEAD, index and commondir.
            _mounts.Add(new(entry, ReadOnly: true));
            // Its config can include a file in the writable project.
            var worktreeFile = Path.Combine(entry, "config.worktree");
            RepoGuard.RequireNotLink(dir, worktreeFile);
            AddIncludes(common, worktreeFile);
            return;
        }
        if (RepoGuard.Exists(commondir))
            _mounts.Add(new(commondir, ReadOnly: true, Verify: true));
        AddWorktreeConfig(common, Path.Combine(entry, "config.worktree"), worktreeConfig);
    }

    /// <summary>
    /// Plans the mounts over each submodule repo kept in <paramref name="common"/> (see
    /// <see cref="RepoScan.FindModuleRepos"/>): the same protection as the git dir itself.
    /// </summary>
    /// <param name="common">The project's git dir.</param>
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
    /// <param name="repo">The module repo.</param>
    /// <param name="common">The project's git dir.</param>
    private void ProtectModuleRepo(string repo, string common)
    {
        if (RepoGuard.IsLink(repo))
        {
            throw new PenException($"{FormatPath(repo)} is a symlink, which pen doesn't support: " +
                $"a submodule's git dir has to be a real directory inside {FormatPath(common)}");
        }

        var commondir = Path.Combine(repo, "commondir");
        if (RepoGuard.Exists(commondir))
        {
            throw new PenException($"{FormatPath(commondir)} exists, which a submodule's repo never has: it " +
                "makes git read config and hooks from another directory. Inspect it and remove it, then rerun");
        }

        // One under a path already mounted read-only (an external worktree's entry) is left alone: it's read-only
        // with it, and a writable mount of its own would undo that, though what its config includes can still be
        // writable.
        if (_mounts.Any(m => m.ReadOnly && RepoGuard.IsInside(m.Path, repo)))
        {
            foreach (var name in new[] { "config", "config.worktree" })
            {
                var file = Path.Combine(repo, name);
                RepoGuard.RequireNotLink(dir, file);
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
    private static Stack<string> ListDirsBetween(string common, string repo)
    {
        var between = new Stack<string>();
        for (var parent = Path.GetDirectoryName(repo);
            parent is not null && parent != common && RepoGuard.IsInside(common, parent);
            parent = Path.GetDirectoryName(parent))
        {
            between.Push(parent);
        }
        return between;
    }

    /// <summary>Plans a read-only mount over a config.worktree, with its includes.</summary>
    /// <param name="common">The project's git dir.</param>
    /// <param name="path">The config.worktree.</param>
    /// <param name="enabled">
    /// Whether extensions.worktreeConfig may be on; if not, only an existing file is mounted.
    /// </param>
    private void AddWorktreeConfig(string common, string path, bool enabled)
    {
        if (!enabled && !RepoGuard.Exists(path))
            return;
        EnsureFile(path);
        _mounts.Add(new(path, ReadOnly: true, Verify: true));
        AddIncludes(common, path);
    }

    /// <summary>
    /// Plans read-only mounts over the files <paramref name="configFile"/>'s include.path and includeIf.*.path
    /// settings name, recursively, wherever they're inside the project or <paramref name="common"/>.
    /// </summary>
    /// <param name="common">The project's git dir.</param>
    /// <param name="configFile">The config file whose includes to follow.</param>
    /// <returns>Whether any file in the walk sets extensions.worktreeConfig.</returns>
    /// <remarks>
    /// <para>
    /// The project and the project's git dir are what the sandbox can write, whichever repo in the git dir the
    /// including file belongs to. The agent could write a file there and host git would read it as config, so each is
    /// mounted read-only too. A missing one inside the git dir is created empty; a missing one in the working tree is
    /// refused, since creating it would add a file to the project. A path that wasn't valid UTF-8 is refused: decoded,
    /// it has U+FFFD where git has other bytes, so Pen would protect a different file from the one git reads.
    /// </para>
    /// <para>
    /// The agent can write some of these files, so each is walked only once, and a walk over
    /// <see cref="RepoGuard.MaxIncludeFiles"/> files is refused: cycles and fan-outs would otherwise take time
    /// exponential in git's depth limit of 10. Depth itself isn't limited, since a file first met deep down mustn't
    /// lose what it includes when it's met again higher up. Files are keyed by path as written, not real path: a
    /// relative include resolves from there. A ~user or %(prefix) path is refused: Pen can't tell where it leads.
    /// </para>
    /// </remarks>
    private bool AddIncludes(string common, string configFile)
    {
        // Each walk starts afresh: a file seen in another walk is followed again in this one.
        _visited.Clear();
        _visited.Add(configFile);
        return FollowIncludes(common, configFile);
    }

    /// <summary>Follows <paramref name="configFile"/>'s includes, within the current walk.</summary>
    /// <returns>Whether it or any file it leads to sets extensions.worktreeConfig.</returns>
    private bool FollowIncludes(string common, string configFile)
    {
        var worktreeConfig = false;
        foreach (var entry in GitConfigFile.ParseFile(configFile))
        {
            if (entry.Section == "extensions" && entry.Key == "worktreeconfig" && GitConfigFile.IsTrue(entry.Value))
                worktreeConfig = true;
            if (entry.Section is not ("include" or "includeif") || entry.Key != "path" || entry.Value.Length == 0)
                continue;
            var include = ResolveInclude(configFile, entry.Value);
            if (!_visited.Add(include.Path))
                continue;
            if (_visited.Count > RepoGuard.MaxIncludeFiles)
            {
                throw new PenException($"{FormatPath(configFile)} takes git config includes past " +
                    $"{RepoGuard.MaxIncludeFiles} files, more than pen will follow: remove it or the includes in it, " +
                    "then rerun");
            }

            if (include.Written != include.Real
                && (RepoGuard.IsInside(dir, include.Written) || RepoGuard.IsInside(common, include.Written)))
            {
                throw new PenException($"{FormatPath(configFile)} includes {FormatPath(include.Written)} through " +
                    "a symlink, which pen doesn't support: the agent could redirect the include");
            }

            if (Directory.Exists(include.Real))
                continue; // git ignores a directory
            ProtectIncludedFile(common, configFile, include.Real);
            // Even host-only includes can enable worktreeConfig or include
            // another file that the sandbox can write. Conditional includes
            // are followed conservatively for every worktree and branch.
            worktreeConfig |= FollowIncludes(common, include.Path);
        }
        return worktreeConfig;
    }

    /// <summary>A file an include names.</summary>
    /// <param name="Path">
    /// The path git reads it by, absolute: as written, or normalized when no symlink makes the two differ. It keys the
    /// walk, and a relative include in it resolves from there.
    /// </param>
    /// <param name="Written">The full path as written, with "." and ".." taken lexically.</param>
    /// <param name="Real">The real path, as the OS walks it.</param>
    private sealed record Include(string Path, string Written, string Real);

    /// <summary>Resolves the file an include in <paramref name="configFile"/> names.</summary>
    /// <param name="configFile">The config file with the include.</param>
    /// <param name="value">The include's path value.</param>
    /// <returns>The included file's paths.</returns>
    /// <exception cref="PenException">
    /// The path isn't valid UTF-8, is a form Pen can't resolve, or can't be followed.
    /// </exception>
    private Include ResolveInclude(string configFile, string value)
    {
        var expanded = GitConfigFile.ExpandPath(value, hostHome);
        var path = expanded.Refusal switch
        {
            GitConfigFile.PathRefusal.None => expanded.Path!,
            GitConfigFile.PathRefusal.NotUtf8 => throw new PenException($"{FormatPath(configFile)} includes a " +
                "path that isn't valid UTF-8, so pen can't tell which file git would read: remove the include or " +
                "rename the file, then rerun"),
            _ => throw new PenException($"{FormatPath(configFile)} includes {Out.Field(value)}, and " +
                "pen can't resolve that form of path, so it can't tell which file git " +
                "would read: write the path out in full or remove the include, then rerun"),
        };

        if (!Path.IsPathRooted(path))
            path = Path.Combine(Paths.GetParent(configFile), path);

        // As the OS walks it: ".." after a symlink is the parent of where that leads.
        var written = Path.GetFullPath(path);
        var real = HooksPath.WalkRealPath(path)
            ?? throw new PenException($"{FormatPath(configFile)} includes " +
                $"{FormatPath(written)}, and pen can't follow that path to the file git " +
                "would read: fix its permissions or remove the include, then rerun");
        if (real == written)
            path = written; // no symlink to tell them apart, and a cycle then repeats one name
        return new(path, written, real);
    }

    /// <summary>
    /// Plans a verified read-only mount over an included file the sandbox could write, creating it first if it's
    /// missing from the git dir.
    /// </summary>
    /// <param name="common">The project's git dir.</param>
    /// <param name="configFile">The config file with the include, for messages.</param>
    /// <param name="real">The included file's real path.</param>
    private void ProtectIncludedFile(string common, string configFile, string real)
    {
        var writable = RepoGuard.IsInside(dir, real) || RepoGuard.IsInside(common, real);
        if (!writable)
            return;
        if (!File.Exists(real))
            CreateIncludedFile(common, configFile, real);
        RepoGuard.RequireNotLink(dir, real);
        _mounts.Add(new(real, ReadOnly: true, Verify: true));
    }

    /// <summary>Creates a missing included file in the git dir empty, so that it can be mounted.</summary>
    /// <exception cref="PenException">
    /// The file would be in the working tree, where creating it would add a file to the project, or it can't be
    /// created.
    /// </exception>
    private void CreateIncludedFile(string common, string configFile, string real)
    {
        if (!RepoGuard.IsInside(common, real))
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
        RepoGuard.RequireNotLink(dir, path);
        if (Directory.Exists(path))
            throw new PenException($"{FormatPath(path)} is a directory, not a file");
        if (!File.Exists(path))
            File.WriteAllText(path, "");
    }

    /// <summary>
    /// Formats <paramref name="path"/> for a message, relative to the project dir (<see cref="RepoGuard.FormatPath"/>).
    /// </summary>
    private string FormatPath(string path) => RepoGuard.FormatPath(dir, path);
}
