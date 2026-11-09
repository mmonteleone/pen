namespace Pen;

/// <summary>
/// Warns when core.hooksPath leads somewhere the sandbox can write: hooks there
/// run on this machine, and a read-only mount wouldn't help much, since a hook
/// can run any project file. Reads files directly and never runs git.
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
/// </summary>
internal static class HooksPath
{
    /// <summary>git's own limit: past it, git refuses the config.</summary>
    private const int MaxIncludeDepth = 10;

    /// <summary>How many symlinks one path may pass through before it's taken as unresolvable.</summary>
    private const int MaxLinks = 40;

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

    /// <summary>A value the key may have, from <paramref name="File"/>; or, with a <paramref name="Problem"/>, why one can't be known.</summary>
    private readonly record struct Candidate(string? Value, string File, string? Problem = null);

    private enum Place { Elsewhere, Writable, Unknown }

    public static void WarnIfWritable(Project project, HostEnv host, Action<string> warn, Func<string, string?>? env = null)
    {
        if (Warning(project, host, env ?? Environment.GetEnvironmentVariable) is { } warning) warn(warning);
    }

    /// <summary>The warning for this session, if there's one to give. Never more than one, however many values qualify.</summary>
    internal static string? Warning(Project project, HostEnv host, Func<string, string?> env)
    {
        var walk = new Walk(host.Home);
        ReadUserConfig(project, host.Home, env, walk);

        // A plain directory has no hooks of its own to run, so a relative value
        // says nothing here, but one that leads into it serves other repos.
        if (project.CommonGitDir is not { } common)
        {
            ReadEnvironmentConfig(env, walk);
            return Decide(walk.Found, project, host, null, relativeKnown: true);
        }

        var config = Path.Combine(common, "config");
        walk.Read(config);
        var commonEntries = TryParse(config);
        var gitDir = project.IsWorktree ? RepoScan.WorktreeEntry(project.GitDir) : common;
        if (gitDir == null)
        {
            walk.Add(new(null, project.GitDir, "it can't read this worktree's .git file"));
            return Decide(walk.Found, project, host, null, relativeKnown: false);
        }

        var workTree = project.IsWorktree ? null : LastWorkTree(commonEntries);
        bool known = commonEntries != null;
        var extension = commonEntries?.LastOrDefault(e => e.Section == "extensions" && e.Subsection == null && e.Key == "worktreeconfig").Value;
        if (extension != null && GitConfigFile.IsTrue(extension))
        {
            var worktreeFile = Path.Combine(gitDir, "config.worktree");
            walk.Read(worktreeFile);
            var own = TryParse(worktreeFile);
            known &= own != null;
            workTree = LastWorkTree(own) ?? workTree;
        }
        ReadEnvironmentConfig(env, walk);

        // core.worktree is relative to the git dir holding it.
        var top = !known ? null
            : workTree == null ? project.Dir
            : workTree.Contains('\ufffd') ? null
            : RealPathWalk(Path.Combine(gitDir, workTree), _ => false, out _);
        return Decide(walk.Found, project, host, top, relativeKnown: top != null);
    }

    /// <summary>GIT_CONFIG_COUNT and its keys and values, which outrank every file, in the environment pen was started from.</summary>
    private static void ReadEnvironmentConfig(Func<string, string?> env, Walk walk)
    {
        if (!int.TryParse(env("GIT_CONFIG_COUNT"), System.Globalization.CultureInfo.InvariantCulture, out int count)) return;
        for (int i = 0; i < count && i < RepoGuard.MaxIncludeFiles; i++)
        {
            var key = env($"GIT_CONFIG_KEY_{i}") ?? "";
            var source = $"GIT_CONFIG_KEY_{i}";
            if (key.Equals("core.hooksPath", StringComparison.OrdinalIgnoreCase)) walk.Set(env($"GIT_CONFIG_VALUE_{i}") ?? "", source);
            else if (key.StartsWith("include", StringComparison.OrdinalIgnoreCase)) walk.Add(new(null, source, $"it won't follow an include in {source}"));
        }
    }

    private static void ReadUserConfig(Project project, string home, Func<string, string?> env, Walk walk)
    {
        if (!GitConfigFile.IsTrue(env("GIT_CONFIG_NOSYSTEM") ?? ""))
        {
            if (env("GIT_CONFIG_SYSTEM") is { } system) walk.Read(FromEnv(system, project));
            else foreach (var file in SystemConfigs) walk.Read(file, conditional: true);
        }
        if (env("GIT_CONFIG_GLOBAL") is { } global)
        {
            walk.Read(FromEnv(global, project));
            return;
        }
        var xdg = env("XDG_CONFIG_HOME");
        walk.Read(Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(home, ".config") : xdg, "git", "config"));
        walk.Read(Path.Combine(home, ".gitconfig"));
    }

    /// <summary>A config file named by the environment, relative to where git would run. Null for /dev/null and empty, which are no config at all.</summary>
    private static string? FromEnv(string value, Project project) =>
        value.Length == 0 || value == "/dev/null" ? null : Path.Combine(project.Dir, value);

    /// <summary>The last core.worktree in a file's own entries, not its includes.</summary>
    private static string? LastWorkTree(List<GitConfigFile.Entry>? entries) =>
        entries?.LastOrDefault(e => e.Section == "core" && e.Subsection == null && e.Key == "worktree").Value;

    private static List<GitConfigFile.Entry>? TryParse(string file)
    {
        try { return GitConfigFile.ParseFile(file); }
        catch (PenException) { return null; }
    }

    /// <summary>The warning for the most decisive candidate: the last one in reach, otherwise the last that can't be placed.</summary>
    private static string? Decide(List<Candidate> found, Project project, HostEnv host, string? top, bool relativeKnown)
    {
        Candidate? unknown = null;
        foreach (var c in Enumerable.Reverse(found))
        {
            var place = c.Value == null ? Place.Unknown : Locate(c.Value, project, host.Home, top, relativeKnown);
            if (place == Place.Writable)
                return $"warning: core.hooksPath ({PausedGit.Shown(c.Value!)}, in {Out.Field(host.Display(c.File))}) is inside this {(project.IsWorktree ? "worktree or its repo's git dir" : "project")}: those hooks can be changed from the sandbox, and git on this machine will run them. Check them before running git here";
            if (place == Place.Unknown) unknown ??= c;
        }
        if (unknown is not { } u) return null;
        var why = u.Problem ?? $"{PausedGit.Shown(u.Value!)}, in {Out.Field(host.Display(u.File))}";
        return $"warning: pen can't tell where core.hooksPath leads ({why}): those hooks may be inside this project, where they can be changed from the sandbox, and git on this machine would run them. Check them before running git here";
    }

    /// <summary>
    /// Where a value of core.hooksPath leads. A relative one starts at the work
    /// tree's top, but also at the git dir: hooks for a push into this repo run
    /// there. So "../x" reaches project/x that way, though it's outside as a work
    /// tree path.
    /// </summary>
    private static Place Locate(string raw, Project project, string home, string? top, bool relativeKnown)
    {
        var value = raw.StartsWith(Optional, StringComparison.Ordinal) ? raw[Optional.Length..] : raw;
        if (value.Contains('\ufffd')) return Place.Unknown;
        if (value.StartsWith("%(prefix)/", StringComparison.Ordinal)) return Place.Unknown;
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal)) return LocatePath(home + value[1..], project);
        if (value.StartsWith('~')) return Place.Unknown; // ~user/: pen doesn't look up other users' homes
        if (value.Length == 0) return LocatePath("/", project); // git looks for /pre-commit
        if (Path.IsPathRooted(value)) return LocatePath(value, project);

        var fromTop = !relativeKnown ? Place.Unknown : top == null ? Place.Elsewhere : LocatePath(top + "/" + value, project);
        if (fromTop == Place.Writable || project.CommonGitDir is not { } common) return fromTop;
        // .git/hooks, as a work tree path: the read-only one is the one meant.
        if (top != null && RealPathWalk(top + "/" + value, _ => false, out _) is { } real
            && RepoGuard.IsInside(Path.Combine(common, "hooks"), real)) return fromTop;
        var fromGitDir = LocatePath(common + "/" + value, project);
        return fromGitDir == Place.Elsewhere ? fromTop : fromGitDir;
    }

    private static Place LocatePath(string path, Project project)
    {
        var real = RealPathWalk(path, p => CanBeReplaced(p, project), out bool replaceable);
        if (replaceable) return Place.Writable;
        if (real == null) return Place.Unknown;
        // As written. Differing only by case is a folding file system's same path, which the walk has seen.
        var written = Path.GetFullPath(path);
        if (!string.Equals(written, real, StringComparison.OrdinalIgnoreCase) && CanBeReplaced(written, project)) return Place.Writable;
        return CanBeReplaced(real, project) ? Place.Writable : Place.Elsewhere;
    }

    /// <summary>Whether the sandbox can change what's at this path: it's below the project dir or git dir, and not in .git/hooks.</summary>
    private static bool CanBeReplaced(string path, Project project)
    {
        if (project.CommonGitDir is { } common)
        {
            if (path == common || RepoGuard.IsInside(Path.Combine(common, "hooks"), path)) return false;
            if (RepoGuard.IsInside(common, path)) return true;
        }
        return path != project.Dir && RepoGuard.IsInside(project.Dir, path);
    }

    /// <summary>
    /// The real path of <paramref name="path"/> as the OS would walk it, one name
    /// at a time. A missing name is taken as it reads, since the sandbox may be
    /// able to create it; a dangling symlink is followed for the same reason.
    /// Null if a directory on the way can't be entered, so what's in it can't be
    /// seen, or the path passes through too many links. <paramref name="replaceable"/>
    /// says whether <paramref name="canBeReplaced"/> held for any name on the walk
    /// so far, links included, even if the walk then fails.
    /// </summary>
    internal static string? RealPathWalk(string path, Func<string, bool> canBeReplaced, out bool replaceable)
    {
        replaceable = false;
        var pending = new Stack<string>(path.Split('/').Reverse());
        var current = "/";
        int links = 0;
        try
        {
            while (pending.TryPop(out var name))
            {
                if (name is "" or ".") continue;
                if (name == "..")
                {
                    current = Path.GetDirectoryName(current) ?? "/";
                    continue;
                }
                if (Directory.Exists(current) && !Native.CanEnter(current)) return null;
                var next = Path.Combine(current, name);
                var target = new FileInfo(next).LinkTarget;
                if (target == null)
                {
                    current = next;
                    replaceable |= canBeReplaced(RepoGuard.ResolveExisting(next));
                    continue;
                }
                replaceable |= canBeReplaced(Path.Combine(RepoGuard.ResolveExisting(current), name));
                if (++links > MaxLinks || target.Contains('\ufffd')) return null;
                if (Path.IsPathRooted(target)) current = "/";
                foreach (var part in target.Split('/').Reverse()) pending.Push(part);
            }
            return RepoGuard.ResolveExisting(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Follows the config files in git's order, collecting what core.hooksPath may be.</summary>
    private sealed class Walk(string home)
    {
        private int _files;

        public List<Candidate> Found { get; } = [];

        public void Add(Candidate c) => Found.Add(c);

        /// <summary>A definite value, replacing what came before.</summary>
        public void Set(string value, string source)
        {
            Found.Clear();
            Found.Add(new(value, source));
        }

        /// <param name="file">a config file; null for none</param>
        /// <param name="conditional">whether git may not read this file at all</param>
        public void Read(string? file, bool conditional = false) => Read(file, conditional, 0);

        private void Read(string? file, bool conditional, int depth)
        {
            if (file == null) return;
            // Each read counts, as a file included twice applies twice: without a
            // limit, includes could fan out exponentially in the depth.
            if (depth > MaxIncludeDepth || ++_files > RepoGuard.MaxIncludeFiles)
            {
                Found.Add(new(null, file, "its config includes go too deep or too wide to follow"));
                return;
            }

            List<GitConfigFile.Entry> entries;
            try { entries = GitConfigFile.ParseFile(file); }
            catch (PenException)
            {
                Found.Add(new(null, file, $"it can't read the config file {Out.Field(file)}"));
                return;
            }

            foreach (var e in entries)
            {
                if (e.Section == "core" && e.Subsection == null && e.Key == "hookspath")
                {
                    if (!conditional) Found.Clear();
                    Found.Add(new(e.Value, file));
                }
                else if (e.Key == "path" && e.Value.Length > 0
                    && ((e.Section == "include" && e.Subsection == null) || (e.Section == "includeif" && e.Subsection != null)))
                {
                    var included = IncludePath(e.Value, file);
                    if (included == null)
                        Found.Add(new(null, file, $"it can't resolve the include {PausedGit.Shown(e.Value)} in {Out.Field(file)}"));
                    else
                        Read(included, conditional || e.Section == "includeif", depth + 1);
                }
            }
        }

        /// <summary>The file an include names, or null if pen can't tell.</summary>
        private string? IncludePath(string value, string from)
        {
            if (value.Contains('\ufffd') || value.StartsWith("%(prefix)/", StringComparison.Ordinal)) return null;
            if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal)) return home + value[1..];
            if (value.StartsWith('~')) return null;
            return Path.IsPathRooted(value) ? value : Path.Combine(Path.GetDirectoryName(from)!, value);
        }
    }
}
