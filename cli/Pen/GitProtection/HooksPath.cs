using Pen.Platform;
using Pen.Projects;

namespace Pen.GitProtection;

/// <summary>Warns when core.hooksPath leads somewhere the sandbox can write.</summary>
/// <remarks>
/// <para>
/// Hooks there run on this machine, and a read-only mount wouldn't help much, since a hook can run any project file.
/// Reads files directly and never runs git.
/// </para>
/// <para>
/// What this follows, checked against git 2.56. Config is read as system, global
/// ($XDG_CONFIG_HOME/git/config, then ~/.gitconfig, or just $GIT_CONFIG_GLOBAL),
/// the common git dir's config, then config.worktree, and the last value wins,
/// wherever an include puts it in that order; a file included twice applies
/// twice. config.worktree (the checkout's own: .git/config.worktree, or the
/// linked worktree's entry) is read only if extensions.worktreeConfig's last
/// value in the common config itself is true, not one from an include. A relative
/// hooksPath is taken from the work tree's top: the project dir, unless
/// core.worktree moves it (which, like the extension, comes only from the config
/// files themselves, and for a linked worktree only from its config.worktree); and
/// for a push into this repo, from the git dir, where those hooks run. An
/// empty value means the root directory, and ":(optional)" only makes a missing
/// path count as unset, which the sandbox could change by creating it.
/// </para>
/// <para>
/// Some things can't be decided here, so they count as maybe: a conditional
/// include may apply to some other branch or worktree, and which system config
/// the host's git reads depends on how it was built. So the result is every value
/// the key may have: a definite value replaces those before it, a maybe joins them.
/// GIT_CONFIG_COUNT and its keys and values outrank every file.
/// </para>
/// <para>
/// Paths are walked as the OS would walk them: ".." after a symlink is the
/// parent of where it leads. Where a path leads now isn't enough, since the
/// sandbox can replace anything below the project dir or git dir, those dirs
/// themselves being mount points: a symlink there that leads out could become
/// a real directory. So it warns if the path as written, any name on the walk,
/// or the real path is in reach. Only .git/hooks, mounted read-only, is exempt.
/// </para>
/// </remarks>
internal static class HooksPath
{
    /// <summary>git's own limit: past it, git refuses the config.</summary>
    private const int MaxIncludeDepth = 10;

    /// <summary>How many symlinks one path may pass through before it's taken as unresolvable.</summary>
    private const int MaxLinks = 40;

    /// <summary>How many GIT_CONFIG_KEY_n/GIT_CONFIG_VALUE_n pairs are read from the environment.</summary>
    private const int MaxEnvironmentConfigs = 100;

    private const string Optional = ":(optional)";

    /// <summary>The places a system config is kept, by how git was built and installed.</summary>
    private static readonly string[] SystemConfigs =
    [
        "/etc/gitconfig",
        "/opt/homebrew/etc/gitconfig",
        "/usr/local/etc/gitconfig",
        "/home/linuxbrew/.linuxbrew/etc/gitconfig",
        "/Library/Developer/CommandLineTools/usr/share/git-core/gitconfig",
        "/Applications/Xcode.app/Contents/Developer/usr/share/git-core/gitconfig",
    ];

    /// <summary>A value core.hooksPath may have, or why one can't be known.</summary>
    /// <param name="Value">The value, or null with a <paramref name="Problem"/>.</param>
    /// <param name="File">Where it's set: a config file, or the environment variable.</param>
    /// <param name="Problem">Why a value can't be known, if it can't.</param>
    private readonly record struct Candidate(string? Value, string File, string? Problem = null);

    /// <summary>Where a path leads, as far as the sandbox is concerned.</summary>
    private enum Place { Elsewhere, Writable, Unknown }

    /// <summary>The core.worktree the config files themselves set.</summary>
    /// <param name="Value">The last value, or null if there's none.</param>
    /// <param name="IsKnown">Whether every file it could be in could be read.</param>
    private readonly record struct WorkTreeSetting(string? Value, bool IsKnown);

    /// <summary>Warns if core.hooksPath may lead somewhere the sandbox can write, or can't be placed.</summary>
    /// <param name="project">The project.</param>
    /// <param name="host">The host environment, for the user's home and how paths are shown.</param>
    /// <param name="warn">Where the warning goes.</param>
    /// <param name="env">
    /// Reads an environment variable: <see cref="HostEnv.GetVariable"/> in production.
    /// </param>
    /// <remarks>This warns on every session and blocks nothing.</remarks>
    public static void WarnIfWritable(Project project, HostEnv host, Action<string> warn, Func<string, string?> env)
    {
        if (FindWarning(project, host, env) is { } warning)
            warn(warning);
    }

    /// <summary>Finds the warning for this session, if there's one to give.</summary>
    /// <returns>The warning, or null if there's none. Never more than one, however many values qualify.</returns>
    internal static string? FindWarning(Project project, HostEnv host, Func<string, string?> env)
    {
        var walk = new Walk(host.Home);
        ReadUserConfig(project, host.Home, env, walk);

        // A plain directory has no hooks of its own to run, so a relative value
        // says nothing here, but one that leads into it serves other repos.
        if (project.CommonGitDir is not { } common)
        {
            ReadEnvironmentConfig(env, walk);
            return DecideWarning(walk.Found, project, host, null, relativeKnown: true);
        }

        var config = Path.Combine(common, "config");
        walk.Read(config, conditional: false);
        var commonEntries = GitConfigFile.TryParseFile(config);
        var gitDir = project.IsWorktree ? RepoScan.ReadWorktreeEntry(project.GitDir) : common;
        if (gitDir is null)
        {
            walk.Add(new(null, project.GitDir, "it can't read this worktree's .git file"));
            return DecideWarning(walk.Found, project, host, null, relativeKnown: false);
        }

        var workTree = ReadWorkTree(project, gitDir, commonEntries, walk);
        ReadEnvironmentConfig(env, walk);
        var top = FindWorkTreeTop(project, gitDir, workTree);
        return DecideWarning(walk.Found, project, host, top, relativeKnown: top is not null);
    }

    /// <summary>
    /// Reads core.worktree from the common config's own entries and, if extensions.worktreeConfig is on there, from
    /// the checkout's config.worktree, which the walk then follows too.
    /// </summary>
    /// <param name="project">The project.</param>
    /// <param name="gitDir">The checkout's own git dir: the common dir, or a linked worktree's entry.</param>
    /// <param name="commonEntries">The common config's entries, or null if it can't be read.</param>
    /// <param name="walk">The walk so far.</param>
    /// <remarks>For a linked worktree, core.worktree comes only from its config.worktree.</remarks>
    private static WorkTreeSetting ReadWorkTree(
        Project project,
        string gitDir,
        List<GitConfigFile.Entry>? commonEntries,
        Walk walk)
    {
        var workTree = project.IsWorktree ? null : FindLastWorkTree(commonEntries);
        var known = commonEntries is not null;
        var extension = commonEntries is null
            ? null
            : GitConfigFile.FindLast(commonEntries, "extensions", "worktreeconfig");
        if (extension is not null && GitConfigFile.IsTrue(extension))
        {
            var worktreeFile = Path.Combine(gitDir, "config.worktree");
            walk.Read(worktreeFile, conditional: false);
            var own = GitConfigFile.TryParseFile(worktreeFile);
            known &= own is not null;
            workTree = FindLastWorkTree(own) ?? workTree;
        }
        return new(workTree, known);
    }

    /// <summary>Finds the work tree's top, which a relative hooksPath is taken from.</summary>
    /// <returns>
    /// The top's real path: the project dir unless core.worktree moves it; null if it can't be known.
    /// </returns>
    private static string? FindWorkTreeTop(Project project, string gitDir, WorkTreeSetting workTree)
    {
        if (!workTree.IsKnown)
            return null;
        if (workTree.Value is null)
            return project.Dir;
        if (workTree.Value.Contains('\ufffd'))
            return null;
        // core.worktree is relative to the git dir holding it.
        return WalkRealPath(Path.Combine(gitDir, workTree.Value));
    }

    /// <summary>
    /// Reads GIT_CONFIG_COUNT and its keys and values, which outrank every file, from the environment Pen was started
    /// from.
    /// </summary>
    private static void ReadEnvironmentConfig(Func<string, string?> env, Walk walk)
    {
        if (!int.TryParse(env("GIT_CONFIG_COUNT"), System.Globalization.CultureInfo.InvariantCulture, out var count))
            return;
        for (var i = 0; i < count && i < MaxEnvironmentConfigs; i++)
        {
            var key = env($"GIT_CONFIG_KEY_{i}") ?? "";
            var source = $"GIT_CONFIG_KEY_{i}";
            if (key.Equals("core.hooksPath", StringComparison.OrdinalIgnoreCase))
                walk.Set(env($"GIT_CONFIG_VALUE_{i}") ?? "", source);
            else if (key.StartsWith("include", StringComparison.OrdinalIgnoreCase))
                walk.Add(new(null, source, $"it won't follow an include in {source}"));
        }
    }

    /// <summary>Reads the system and global config files, as the environment selects them.</summary>
    private static void ReadUserConfig(Project project, string home, Func<string, string?> env, Walk walk)
    {
        if (!GitConfigFile.IsTrue(env("GIT_CONFIG_NOSYSTEM") ?? ""))
        {
            if (env("GIT_CONFIG_SYSTEM") is { } system)
            {
                walk.Read(ResolveEnvConfigFile(system, project), conditional: false);
            }
            else
            {
                foreach (var file in SystemConfigs)
                    walk.Read(file, conditional: true);
            }
        }
        if (env("GIT_CONFIG_GLOBAL") is { } global)
        {
            walk.Read(ResolveEnvConfigFile(global, project), conditional: false);
            return;
        }
        var xdg = env("XDG_CONFIG_HOME");
        var configHome = string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg;
        walk.Read(Path.Combine(configHome, "git", "config"), conditional: false);
        walk.Read(Path.Combine(home, ".gitconfig"), conditional: false);
    }

    /// <summary>Resolves a config file named by the environment, relative to where git would run.</summary>
    /// <returns>The file, or null for /dev/null and empty, which are no config at all.</returns>
    private static string? ResolveEnvConfigFile(string value, Project project) =>
        value.Length == 0 || value == "/dev/null" ? null : Path.Combine(project.Dir, value);

    /// <summary>Finds the last core.worktree in a file's own entries, not its includes.</summary>
    private static string? FindLastWorkTree(List<GitConfigFile.Entry>? entries) =>
        entries is null ? null : GitConfigFile.FindLast(entries, "core", "worktree");

    /// <summary>
    /// Decides the warning for the most decisive candidate: the last one in reach, otherwise the last that can't be
    /// placed.
    /// </summary>
    /// <returns>The warning, or null if no candidate is in reach or unplaceable.</returns>
    private static string? DecideWarning(
        List<Candidate> found,
        Project project,
        HostEnv host,
        string? top,
        bool relativeKnown)
    {
        Candidate? unknown = null;
        foreach (var candidate in Enumerable.Reverse(found))
        {
            var place = candidate.Value is null
                ? Place.Unknown
                : Locate(candidate.Value, project, host.Home, top, relativeKnown);
            if (place == Place.Writable)
            {
                return $"warning: core.hooksPath ({PausedGit.FormatLine(candidate.Value!)}, " +
                    $"in {Out.Field(host.Display(candidate.File))}) " +
                    $"is inside this {(project.IsWorktree ? "worktree or its repo's git dir" : "project")}: those " +
                    "hooks can be changed from the sandbox, and git on this machine will run them. Check them before " +
                    "running git here";
            }

            if (place == Place.Unknown)
                unknown ??= candidate;
        }
        if (unknown is not { } first)
            return null;
        var why = first.Problem ?? $"{PausedGit.FormatLine(first.Value!)}, in {Out.Field(host.Display(first.File))}";
        return $"warning: pen can't tell where core.hooksPath leads ({why}): those hooks " +
            "may be inside this project, where they can be changed from the sandbox, and " +
            "git on this machine would run them. Check them before running git here";
    }

    /// <summary>Finds where a value of core.hooksPath leads.</summary>
    /// <remarks>
    /// A relative one starts at the work tree's top, but also at the git dir: hooks for a push into this repo run
    /// there. So "../x" reaches project/x that way, though it's outside as a work tree path.
    /// </remarks>
    private static Place Locate(string raw, Project project, string home, string? top, bool relativeKnown)
    {
        var written = raw.StartsWith(Optional, StringComparison.Ordinal) ? raw[Optional.Length..] : raw;
        // HostEnv.Home is always absolute (HostEnv.FromProcess refuses anything else), so the rooted check
        // takes the expanded path as written.
        if (GitConfigFile.ExpandPath(written, home) is not GitConfigFile.ExpandedPath.Expanded { Path: var value })
            return Place.Unknown;
        if (value.Length == 0)
            return LocatePath("/", project); // git looks for /pre-commit
        if (Path.IsPathRooted(value))
            return LocatePath(value, project);

        Place fromTop;
        if (!relativeKnown)
            fromTop = Place.Unknown;
        else if (top is null)
            fromTop = Place.Elsewhere;
        else
            fromTop = LocatePath(top + "/" + value, project);
        if (fromTop == Place.Writable || project.CommonGitDir is not { } common)
            return fromTop;
        // .git/hooks, as a work tree path: the read-only one is the one meant.
        if (top is not null && WalkRealPath(top + "/" + value) is { } real
            && PathChecks.IsInside(Path.Combine(common, "hooks"), real))
        {
            return fromTop;
        }

        var fromGitDir = LocatePath(common + "/" + value, project);
        return fromGitDir == Place.Elsewhere ? fromTop : fromGitDir;
    }

    /// <summary>Finds where an absolute path leads: anywhere on its walk in reach makes it writable.</summary>
    private static Place LocatePath(string path, Project project)
    {
        var real = WalkRealPath(path, p => CanBeReplaced(p, project), out var replaceable);
        if (replaceable)
            return Place.Writable;
        if (real is null)
            return Place.Unknown;
        // As written. Differing only by case is a folding file system's same path, which the walk has seen.
        var written = Path.GetFullPath(path);
        if (!string.Equals(written, real, StringComparison.OrdinalIgnoreCase) && CanBeReplaced(written, project))
            return Place.Writable;
        return CanBeReplaced(real, project) ? Place.Writable : Place.Elsewhere;
    }

    /// <summary>
    /// Whether the sandbox can change what's at this path: it's
    /// below the project dir or git dir, and not in .git/hooks.
    /// </summary>
    private static bool CanBeReplaced(string path, Project project)
    {
        if (project.CommonGitDir is { } common)
        {
            if (path == common || PathChecks.IsInside(Path.Combine(common, "hooks"), path))
                return false;
            if (PathChecks.IsInside(common, path))
                return true;
        }
        return path != project.Dir && PathChecks.IsInside(project.Dir, path);
    }

    /// <summary>
    /// Resolves the real path of <paramref name="path"/> as the OS would walk it, one name at a time, when nothing on
    /// the walk needs checking for whether the sandbox can replace it.
    /// </summary>
    /// <param name="path">The path to walk.</param>
    /// <returns>
    /// The real path, or null if a directory on the way can't be entered or the path passes through too many links.
    /// </returns>
    internal static string? WalkRealPath(string path) => WalkRealPath(path, _ => false, out _);

    /// <summary>
    /// Resolves the real path of <paramref name="path"/> as the OS would walk it, one name at a time.
    /// </summary>
    /// <param name="path">The path to walk.</param>
    /// <param name="canBeReplaced">Whether the sandbox can change what's at a path met on the walk.</param>
    /// <param name="replaceable">
    /// Whether <paramref name="canBeReplaced"/> held for any name on the walk so far, links included, even if the
    /// walk then fails.
    /// </param>
    /// <returns>
    /// The real path, or null if a directory on the way can't be entered, so what's in it can't be seen, or the path
    /// passes through too many links.
    /// </returns>
    /// <remarks>
    /// A missing name is taken as it reads, since the sandbox may be able to create it; a dangling symlink is
    /// followed for the same reason.
    /// </remarks>
    internal static string? WalkRealPath(string path, Func<string, bool> canBeReplaced, out bool replaceable)
    {
        replaceable = false;
        try
        {
            return WalkNames(path, canBeReplaced, ref replaceable);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Walks <paramref name="path"/> one name at a time, following symlinks, for
    /// <see cref="WalkRealPath(string, Func{string, bool}, out bool)"/>.
    /// </summary>
    /// <returns>The real path, or null if a directory can't be entered or there are too many links.</returns>
    private static string? WalkNames(string path, Func<string, bool> canBeReplaced, ref bool replaceable)
    {
        var pending = new Stack<string>(path.Split('/').Reverse());
        var current = "/";
        var links = 0;
        while (pending.TryPop(out var name))
        {
            if (name is "" or ".")
                continue;
            if (name == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }
            if (Directory.Exists(current) && !Native.CanEnter(current))
                return null;
            var next = Path.Combine(current, name);
            var target = new FileInfo(next).LinkTarget;
            if (target is null)
            {
                current = next;
                replaceable |= canBeReplaced(PathChecks.ResolveExisting(next));
                continue;
            }
            replaceable |= canBeReplaced(Path.Combine(PathChecks.ResolveExisting(current), name));
            if (++links > MaxLinks || target.Contains('\ufffd'))
                return null;
            if (Path.IsPathRooted(target))
                current = "/";
            foreach (var part in target.Split('/').Reverse())
                pending.Push(part);
        }
        return PathChecks.ResolveExisting(current);
    }

    /// <summary>Follows the config files in git's order, collecting what core.hooksPath may be.</summary>
    private sealed class Walk(string home)
    {
        private int _files;

        public List<Candidate> Found { get; } = [];

        /// <summary>Adds a candidate after those found so far.</summary>
        public void Add(Candidate candidate) => Found.Add(candidate);

        /// <summary>A definite value, replacing what came before.</summary>
        public void Set(string value, string source)
        {
            Found.Clear();
            Found.Add(new(value, source));
        }

        /// <summary>Reads a config file and the files it includes.</summary>
        /// <param name="file">The config file; null for none.</param>
        /// <param name="conditional">Whether git may not read this file at all.</param>
        public void Read(string? file, bool conditional) => Read(file, conditional, 0);

        /// <summary>Reads a config file at <paramref name="depth"/> includes down, and the files it includes.</summary>
        private void Read(string? file, bool conditional, int depth)
        {
            if (file is null)
                return;
            // Each read counts, as a file included twice applies twice: without a
            // limit, includes could fan out exponentially in the depth.
            if (depth > MaxIncludeDepth || ++_files > RepoGuard.MaxIncludeFiles)
            {
                Found.Add(new(null, file, "its config includes go too deep or too wide to follow"));
                return;
            }

            if (GitConfigFile.TryParseFile(file) is not { } entries)
            {
                Found.Add(new(null, file, $"it can't read the config file {Out.Field(file)}"));
                return;
            }

            ReadEntries(file, entries, conditional, depth);
        }

        /// <summary>
        /// Takes in a config file's entries: its hooksPath values and the files it includes, in order.
        /// </summary>
        private void ReadEntries(string file, List<GitConfigFile.Entry> entries, bool conditional, int depth)
        {
            foreach (var entry in entries)
            {
                if (entry.Section == "core" && entry.Subsection is null && entry.Key == "hookspath")
                {
                    if (!conditional)
                        Found.Clear();
                    Found.Add(new(entry.Value, file));
                }
                else if (entry.Key == "path" && entry.Value.Length > 0
                    && ((entry.Section == "include" && entry.Subsection is null)
                        || (entry.Section == "includeif" && entry.Subsection is not null)))
                {
                    var included = ResolveInclude(entry.Value, file);
                    if (included is null)
                    {
                        Found.Add(new(null, file, "it can't resolve the include " +
                            $"{PausedGit.FormatLine(entry.Value)} in {Out.Field(file)}"));
                    }
                    else
                    {
                        Read(included, conditional || entry.Section == "includeif", depth + 1);
                    }
                }
            }
        }

        /// <summary>Resolves the file an include names.</summary>
        /// <returns>The file, or null if Pen can't tell.</returns>
        private string? ResolveInclude(string value, string from)
        {
            // HostEnv.Home is always absolute (HostEnv.FromProcess refuses anything else), so the rooted check
            // takes the expanded path as written.
            if (GitConfigFile.ExpandPath(value, home) is not GitConfigFile.ExpandedPath.Expanded { Path: var path })
                return null;
            return Path.IsPathRooted(path) ? path : Path.Combine(Paths.GetParent(from), path);
        }
    }
}
