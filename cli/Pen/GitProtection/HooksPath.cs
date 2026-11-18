using Pen.Platform;
using Pen.Projects;

namespace Pen.GitProtection;

/// <summary>Warns when core.hooksPath leads somewhere the sandbox can write.</summary>
/// <remarks>
/// Hooks there run on this machine, and a read-only mount wouldn't help much, since a hook can run any project file.
/// This reads files directly and never runs git. It follows git's config order as of git 2.56 and collects every
/// value the key may have, warning about the most decisive one.
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

    /// <summary>
    /// A value core.hooksPath may have, or (with a null Value) the Problem that keeps it from being known, and the
    /// File that sets it: a config file, or the environment variable.
    /// </summary>
    private readonly record struct Candidate(string? Value, string File, string? Problem = null);

    /// <summary>Where a path leads, as far as the sandbox is concerned.</summary>
    private enum SandboxReach { Elsewhere, Writable, Unknown }

    /// <summary>
    /// The core.worktree the config files themselves set: the last value, or null if there's none, and whether every
    /// file it could be in could be read.
    /// </summary>
    private readonly record struct WorkTreeSetting(string? Value, bool IsKnown);

    /// <summary>Warns if core.hooksPath may lead somewhere the sandbox can write, or can't be placed.</summary>
    /// <remarks>
    /// <paramref name="host"/> gives the user's home and how paths are shown, and <paramref name="env"/> reads an
    /// environment variable (<see cref="HostEnv.GetVariable"/> in production). This warns on every session and blocks
    /// nothing.
    /// </remarks>
    public static void WarnIfWritable(Project project, HostEnv host, Action<string> warn, Func<string, string?> env)
    {
        if (FindWarning(project, host, env) is { } warning)
            warn(warning);
    }

    /// <summary>Finds the warning for this session, if there's one to give.</summary>
    /// <returns>The warning, or null if there's none. Never more than one, however many values qualify.</returns>
    /// <remarks>
    /// Git reads the system config, the global config, the common git dir's config, then config.worktree, and the
    /// environment's GIT_CONFIG_COUNT pairs last. The last value wins, wherever an include puts it in that order.
    /// </remarks>
    internal static string? FindWarning(Project project, HostEnv host, Func<string, string?> env)
    {
        var walk = new Walk(new GitConfigIncludes(host.Home));
        ReadUserConfig(project, host.Home, env, walk);

        // A plain directory has no hooks of its own to run, so a relative value says nothing here, but one that
        // leads into it serves other repos.
        if (project.CommonGitDir is not { } common)
        {
            ReadEnvironmentConfig(env, walk);
            return DecideWarning(walk.Found, project, host, null, relativeKnown: true);
        }

        var config = Path.Combine(common, "config");
        walk.Read(config, conditional: false);
        var commonEntries = walk.ReadEntries(config);
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
    /// <remarks>
    /// <para>
    /// <paramref name="gitDir"/> is the checkout's own git dir: the common dir, or a linked worktree's entry.
    /// <paramref name="commonEntries"/> is null if the common config can't be read.
    /// </para>
    /// <para>
    /// Git reads config.worktree only if extensions.worktreeConfig's last value in the common config itself is true;
    /// a value from an include doesn't count. Like the extension, core.worktree comes only from the config files
    /// themselves, and for a linked worktree only from its config.worktree.
    /// </para>
    /// </remarks>
    private static WorkTreeSetting ReadWorkTree(
        Project project,
        string gitDir,
        IReadOnlyList<GitConfigFile.Entry>? commonEntries,
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
            var own = walk.ReadEntries(worktreeFile);
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
    /// <remarks>
    /// The global config is $XDG_CONFIG_HOME/git/config then ~/.gitconfig, or only $GIT_CONFIG_GLOBAL. Which system
    /// config the host's git reads depends on how it was built, so each known place is read as a maybe.
    /// </remarks>
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
    private static string? FindLastWorkTree(IReadOnlyList<GitConfigFile.Entry>? entries) =>
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
            var reach = candidate.Value is null
                ? SandboxReach.Unknown
                : Locate(candidate.Value, project, host.Home, top, relativeKnown);
            if (reach == SandboxReach.Writable)
            {
                return $"warning: core.hooksPath ({PausedGit.FormatLine(candidate.Value!)}, " +
                    $"in {Out.Field(host.Display(candidate.File))}) " +
                    $"is inside this {(project.IsWorktree ? "worktree or its repo's git dir" : "project")}: those " +
                    "hooks can be changed from the sandbox, and git on this machine will run them. Check them before " +
                    "running git here";
            }

            if (reach == SandboxReach.Unknown)
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
    /// <para>
    /// A relative one starts at the work tree's top, but also at the git dir: hooks for a push into this repo run
    /// there. So "../x" reaches project/x that way, though it's outside as a work tree path.
    /// </para>
    /// <para>
    /// An empty value means the root directory. ":(optional)" only makes a missing path count as unset, and the
    /// sandbox could create the path, so the prefix is ignored.
    /// </para>
    /// </remarks>
    private static SandboxReach Locate(string raw, Project project, string home, string? top, bool relativeKnown)
    {
        var written = raw.StartsWith(Optional, StringComparison.Ordinal) ? raw[Optional.Length..] : raw;
        // HostEnv.Home is always absolute (HostEnv.FromProcess refuses anything else), so the rooted check
        // takes the expanded path as written.
        if (GitConfigFile.ExpandPath(written, home) is not GitConfigFile.ExpandedPath.Expanded { Path: var value })
            return SandboxReach.Unknown;
        if (value.Length == 0)
            return LocatePath("/", project); // git looks for /pre-commit
        if (Path.IsPathRooted(value))
            return LocatePath(value, project);

        SandboxReach fromTop;
        if (!relativeKnown)
            fromTop = SandboxReach.Unknown;
        else if (top is null)
            fromTop = SandboxReach.Elsewhere;
        else
            fromTop = LocatePath(top + "/" + value, project);
        if (fromTop == SandboxReach.Writable || project.CommonGitDir is not { } common)
            return fromTop;
        // .git/hooks, as a work tree path: the read-only one is the one meant.
        if (top is not null && WalkRealPath(top + "/" + value) is { } real
            && PathChecks.IsInside(Path.Combine(common, "hooks"), real))
        {
            return fromTop;
        }

        var fromGitDir = LocatePath(common + "/" + value, project);
        return fromGitDir == SandboxReach.Elsewhere ? fromTop : fromGitDir;
    }

    /// <summary>Finds where an absolute path leads: anywhere on its walk in reach makes it writable.</summary>
    /// <remarks>
    /// Where the path leads now isn't enough. The sandbox can replace anything below the project dir or git dir,
    /// since those dirs are mount points, so a symlink there that leads out could become a real directory. The path
    /// as written, any name on the walk and the real path all count.
    /// </remarks>
    private static SandboxReach LocatePath(string path, Project project)
    {
        var real = WalkRealPath(path, p => CanBeReplaced(p, project), out var replaceable);
        if (replaceable)
            return SandboxReach.Writable;
        if (real is null)
            return SandboxReach.Unknown;
        // As written. Differing only by case is a folding file system's same path, which the walk has seen.
        var written = Path.GetFullPath(path);
        if (!string.Equals(written, real, StringComparison.OrdinalIgnoreCase) && CanBeReplaced(written, project))
            return SandboxReach.Writable;
        return CanBeReplaced(real, project) ? SandboxReach.Writable : SandboxReach.Elsewhere;
    }

    /// <summary>
    /// Whether the sandbox can change what's at this path: it's below the project dir or git dir, and not in
    /// .git/hooks, which is mounted read-only.
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
    /// <returns>
    /// The real path, or null if a directory on the way can't be entered or the path passes through too many links.
    /// </returns>
    internal static string? WalkRealPath(string path) => WalkRealPath(path, _ => false, out _);

    /// <summary>
    /// Resolves the real path of <paramref name="path"/> as the OS would walk it, one name at a time, setting
    /// <paramref name="replaceable"/> if <paramref name="canBeReplaced"/> (whether the sandbox can change what's at a
    /// path) held for any name on the walk so far, links included, even if the walk then fails.
    /// </summary>
    /// <returns>
    /// The real path, or null if a directory on the way can't be entered, so what's in it can't be seen, or the path
    /// passes through too many links.
    /// </returns>
    internal static string? WalkRealPath(string path, Func<string, bool> canBeReplaced, out bool replaceable) =>
        WalkRealPath(path, canBeReplaced, out replaceable, out _, out _);

    /// <summary>
    /// Resolves the real path of <paramref name="path"/> as <see cref="WalkRealPath(string, Func{string, bool}, out
    /// bool)"/> does, also setting <paramref name="undoesReplaceable"/> if a ".." on the walk came after a name
    /// <paramref name="canBeReplaced"/> held for, and <paramref name="followsReplaceableLink"/> if the walk followed a
    /// symlink it held for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk matches the OS's: ".." after a symlink is the parent of where it leads. A missing name is taken as it
    /// reads, since the sandbox may be able to create it; a dangling symlink is followed for the same reason.
    /// </para>
    /// <para>
    /// A ".." after a name the sandbox can replace goes wherever the sandbox likes: it can make that name a symlink,
    /// or point it elsewhere if it is one, and the ".." then leaves where that leads.
    /// </para>
    /// </remarks>
    internal static string? WalkRealPath(
        string path,
        Func<string, bool> canBeReplaced,
        out bool replaceable,
        out bool undoesReplaceable,
        out bool followsReplaceableLink)
    {
        replaceable = false;
        undoesReplaceable = false;
        followsReplaceableLink = false;
        try
        {
            return WalkNames(path, canBeReplaced, ref replaceable, ref undoesReplaceable, ref followsReplaceableLink);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Walks <paramref name="path"/> one name at a time, following symlinks, for
    /// <see cref="WalkRealPath(string, Func{string, bool}, out bool, out bool, out bool)"/>.
    /// </summary>
    /// <returns>The real path, or null if a directory can't be entered or there are too many links.</returns>
    private static string? WalkNames(
        string path,
        Func<string, bool> canBeReplaced,
        ref bool replaceable,
        ref bool undoesReplaceable,
        ref bool followsReplaceableLink)
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
                undoesReplaceable |= replaceable;
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
            var linkReplaceable = canBeReplaced(Path.Combine(PathChecks.ResolveExisting(current), name));
            replaceable |= linkReplaceable;
            followsReplaceableLink |= linkReplaceable;
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
    /// <remarks>
    /// What can't be decided here counts as a maybe, such as a conditional include that may apply to another branch
    /// or worktree. A definite value replaces the values before it, and a maybe joins them.
    /// </remarks>
    private sealed class Walk(GitConfigIncludes includes)
    {
        private int _files;

        public List<Candidate> Found { get; } = [];

        /// <summary>Adds a candidate after those found so far.</summary>
        public void Add(Candidate candidate) => Found.Add(candidate);

        /// <summary>Adds a definite value, replacing what came before.</summary>
        public void Set(string value, string source)
        {
            Found.Clear();
            Found.Add(new(value, source));
        }

        /// <summary>
        /// Reads a config file (null for none) and the files it includes. <paramref name="conditional"/> is whether
        /// git may not read this file at all.
        /// </summary>
        public void Read(string? file, bool conditional) => Read(file, conditional, 0);

        /// <summary>Gives a config file's own entries, not its includes', or null if it can't be read.</summary>
        public IReadOnlyList<GitConfigFile.Entry>? ReadEntries(string file) =>
            includes.TryRead(file, out var lines, out _) ? [.. lines.Select(line => line.Entry)] : null;

        /// <summary>Reads a config file at <paramref name="depth"/> includes down, and the files it includes.</summary>
        private void Read(string? file, bool conditional, int depth)
        {
            if (file is null)
                return;
            // Each read counts, since a file included twice applies twice: without a limit, includes could fan out
            // exponentially in the depth.
            if (depth > MaxIncludeDepth || ++_files > GitConfigIncludes.MaxFiles)
            {
                Found.Add(new(null, file, "its config includes go too deep or too wide to follow"));
                return;
            }

            if (!includes.TryRead(file, out var lines, out _))
            {
                Found.Add(new(null, file, $"it can't read the config file {Out.Field(file)}"));
                return;
            }

            ReadLines(file, lines, conditional, depth);
        }

        /// <summary>
        /// Takes in a config file's entries: its hooksPath values and the files it includes, in order. The include
        /// forms git ignores (<see cref="IncludeReading.Never"/>) are ignored here too.
        /// </summary>
        private void ReadLines(string file, IReadOnlyList<ConfigLine> lines, bool conditional, int depth)
        {
            foreach (var (entry, include) in lines)
            {
                if (entry.Section == "core" && entry.Subsection is null && entry.Key == "hookspath")
                {
                    if (!conditional)
                        Found.Clear();
                    Found.Add(new(entry.Value, file));
                }
                else if (include is { Reading: not IncludeReading.Never })
                {
                    if (include.Target is GitConfigFile.ExpandedPath.Expanded { Path: var included })
                    {
                        Read(included, conditional || include.Reading == IncludeReading.Maybe, depth + 1);
                    }
                    else
                    {
                        Found.Add(new(null, file, "it can't resolve the include " +
                            $"{PausedGit.FormatLine(include.Value)} in {Out.Field(file)}"));
                    }
                }
            }
        }
    }
}
