using System.Security.Cryptography;
using System.Text;
using Pen.Platform;
using Pen.Projects;

namespace Pen.GitProtection;

/// <summary>Refuses repo layouts the sandbox can't protect, and plans the mounts that protect the rest.</summary>
/// <remarks>
/// The mounts keep the agent from changing what host git runs. See AGENTS.md's Git protection architecture.
/// Everything here reads files directly and never runs git, since the agent may have planted git config and hooks.
/// </remarks>
internal static class RepoGuard
{
    /// <summary>How many paths a message lists before "… and N more".</summary>
    internal const int MaxListed = 10;

    /// <summary>How many config files one walk of includes follows before it's refused.</summary>
    internal const int MaxIncludeFiles = 100;

    /// <summary>The warning that the repo's git dir may fold case.</summary>
    internal const string CaseWarning = "warning: this repo's git dir isn't known to be case-sensitive, " +
        "so read-only protection of single files in it may not hold: the agent could change .git/config " +
        "by writing .git/CONFIG. Don't run git in this repo on this machine until the session ends";

    /// <summary>Refuses a project whose git config and hooks the sandbox can't protect.</summary>
    /// <param name="dir">The project dir, a real path.</param>
    /// <exception cref="PenException">The first problem found, in the order the checks run.</exception>
    /// <remarks>
    /// <para>
    /// Refused: a symlinked .git; a .git file that isn't a validated linked worktree, or a worktree of a bare repo;
    /// a git dir Pen can't list; a commondir in it; module repos it can't read; names that aren't valid UTF-8; a
    /// directory it can enter but not list; and any repo inside the project.
    /// </para>
    /// <para>
    /// A linked worktree of a non-bare main checkout is accepted when its .git file validates
    /// (<see cref="RepoScan.ResolveWorktree"/>); the main repo's git dir then gets the same checks as a main
    /// checkout's own. Submodules are accepted when their repos are kept in that git dir (modules/), so their
    /// checkouts have .git files that <see cref="RepoScan.ScanNested"/> validates; one with its own .git dir
    /// is refused like any nested repo, with advice to absorb it.
    /// </para>
    /// </remarks>
    public static void CheckAtStart(string dir)
    {
        var git = Path.Combine(dir, ".git");
        var common = ResolveCheckableGitDir(dir, git);
        RequireNoCommondir(dir, common);
        var modules = RepoScan.ScanModules(common);
        RequireReadableModules(dir, modules);
        // Defence in depth: ResolveModuleCheckout already rejects a checkout whose gitdir goes through a symlink
        // (it requires RealPath(gitdir) == gitdir), and planning the mounts refuses the symlink.
        var moduleRepos = modules.Repos.Where(repo => !IsLink(repo)).ToList();
        var scan = RepoScan.ScanNested(dir, common, moduleRepos, tryOpen: null);
        RequireDecodableNames(dir, common, modules, scan);
        RequireListable(dir, scan);
        RequireNoNestedRepos(dir, git, scan.Repos);
    }

    /// <summary>Finds the project's common git dir, refusing one Pen can't check.</summary>
    /// <returns>
    /// The common git dir, which needn't exist: <paramref name="git"/> unless that's a worktree's file.
    /// </returns>
    private static string ResolveCheckableGitDir(string dir, string git)
    {
        if (IsLink(git))
        {
            throw new PenException($"{FormatPath(dir, git)} is a symlink, which " +
                "pen doesn't support: run pen in the repo it points to");
        }

        var common = git;
        if (File.Exists(git))
            common = CheckWorktree(git);
        if (Directory.Exists(common) && !Native.CanList(common))
        {
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't read " +
                $"{FormatPath(dir, common)}, so it can't protect it: fix its permissions, then rerun");
        }

        return common;
    }

    /// <summary>Refuses a commondir in the common git dir.</summary>
    /// <remarks>It makes git read config and hooks from another directory, and a normal repo never has one.</remarks>
    private static void RequireNoCommondir(string dir, string common)
    {
        var commondir = Path.Combine(common, "commondir");
        if (Exists(commondir))
        {
            throw new PenException($"{FormatPath(dir, commondir)} exists, which a normal repo never " +
                "has: it makes git read config and hooks from another directory. Inspect it and remove it, then rerun");
        }
    }

    /// <summary>
    /// Refuses directories holding submodule repos that Pen can't read, since it can't protect what's in them.
    /// </summary>
    private static void RequireReadableModules(string dir, ModuleScan modules)
    {
        var unreadable = modules.UnreadableDirs;
        if (unreadable.Count > 0)
        {
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't read these " +
                "directories where git keeps submodule repos, so it can't protect what's in them:\n"
                + string.Join('\n', unreadable.Select(u => "  " + FormatPath(dir, u)))
                + "\nfix their permissions or remove them, then rerun");
        }
    }

    /// <summary>Refuses names in the git dir's module repos or the tree that aren't valid UTF-8.</summary>
    /// <remarks>
    /// Pen can't look inside them, though git on this machine can still enter them and run what's there.
    /// </remarks>
    private static void RequireDecodableNames(string dir, string common, ModuleScan modules, NestedScan scan)
    {
        var badNames = modules.BadNames.Concat(scan.BadNames).ToList();
        if (badNames.Count == 0)
            return;
        var command = "";
        if (badNames.Any(n => !n.EndsInNewline))
            command = ": cd to its directory and run mv -- \"$(printf 'NAME')\" new-name, with NAME as shown";
        var gitDirNote = "";
        // A repo renamed there would be adopted as a submodule's at the next start.
        if (modules.BadNames.Count > 0)
        {
            gitDirNote = $"\ninspect any under {FormatPath(dir, common)} first and prefer removing them: " +
                "a repo renamed there is trusted as a submodule's repo at the next start";
        }

        throw new PenException($"refusing to run in {Out.Field(dir)}: these names aren't " +
            "valid UTF-8, so pen can't look inside them, though git on this machine can " +
            "still enter them and run what's there (shown with octal escapes):\n"
            + string.Join('\n', FormatBadNames(dir, badNames).Select(n => "  " + n))
            + "\nrename or remove each, then rerun" + command + gitDirNote);
    }

    /// <summary>Refuses directories in the tree that can be entered but not listed.</summary>
    /// <remarks>Git on this machine can still enter them, so Pen can't see a repo inside one.</remarks>
    private static void RequireListable(string dir, NestedScan scan)
    {
        var unlistable = scan.UnlistableDirs;
        if (unlistable.Count == 0)
            return;
        var listed = ListFirst(unlistable, MaxListed, u => FormatPath(dir, u));
        throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't list these directories, " +
            "though git on this machine can still enter them, so it can't see a repo inside one:\n"
            + string.Join('\n', listed.Select(line => "  " + line))
            + "\nfix their permissions (chmod u+rx) or remove them, then rerun");
    }

    /// <summary>Refuses repos inside the project, whose config and hooks Pen can't protect.</summary>
    /// <param name="dir">The project dir.</param>
    /// <param name="git">The project's .git, which decides the message's wording.</param>
    /// <param name="nested">The repos found inside the project.</param>
    private static void RequireNoNestedRepos(string dir, string git, List<NestedRepo> nested)
    {
        if (nested.Count == 0)
            return;
        var message = new StringBuilder(FormatNestedHeading(dir, git));
        bool anySubmodule = false;
        bool anyOther = false;
        // Only the repos listed are looked at for the advice; any not listed may be other repos.
        var listed = ListFirst(nested, MaxListed, repo =>
        {
            bool submodule = repo.Kind == NestedKind.GitDir && IsSubmodulePath(dir, repo.RepoDir);
            anySubmodule |= submodule;
            anyOther |= !submodule;
            var kind = submodule ? " (a submodule with its own .git dir)" : Describe(repo.Kind);
            return FormatPath(dir, repo.RepoDir) + kind;
        });
        foreach (var line in listed)
            message.Append("\n  ").Append(line);
        anyOther |= nested.Count > MaxListed;
        if (anySubmodule)
        {
            message.Append("\nfor a submodule, run git submodule absorbgitdirs here on this machine: " +
                "it moves the submodule's .git dir into this repo's, where pen can protect it");
        }

        if (anyOther)
            message.Append("\nrun pen in each repo instead");
        throw new PenException(message.ToString());
    }

    /// <summary>Formats the first line of the nested-repo refusal, worded for a repo or a plain directory.</summary>
    private static string FormatNestedHeading(string dir, string git)
    {
        if (Directory.Exists(git))
        {
            return $"refusing to run in {dir}: it has other git repos inside it, " +
                "whose config and hooks pen can't protect:";
        }

        return $"refusing to run in {dir}: it contains git repos, whose config and hooks pen can't protect:";
    }

    /// <summary>Formats the first few of <paramref name="names"/> for a message, and how many more there are.</summary>
    /// <returns>One line per name listed, without indent, then "… and N more" if any weren't.</returns>
    internal static List<string> FormatBadNames(string dir, IReadOnlyList<BadName> names) =>
        ListFirst(names, MaxListed, n => FormatPath(dir, n.DisplayPath) + (n.EndsInNewline ? " " +
            "(it ends in a newline, so no command can name it here: rename it by hand)" : ""));

    /// <summary>
    /// Formats the first <paramref name="max"/> of <paramref name="items"/>, then how many more there are.
    /// </summary>
    /// <returns>One line per item listed, without indent, then "… and N more" if any weren't.</returns>
    /// <remarks>Formats only the items listed, each once and in order, before returning.</remarks>
    internal static List<string> ListFirst<T>(IReadOnlyCollection<T> items, int max, Func<T, string> format)
    {
        var lines = items.Take(max).Select(format).ToList();
        if (items.Count > max)
            lines.Add($"… and {items.Count - max} more");
        return lines;
    }

    /// <summary>
    /// Whether <paramref name="repoDir"/> is a submodule's checkout by the .gitmodules of the project or of a
    /// directory between it and the checkout (a nested submodule's superproject): one lists it as a
    /// submodule.*.path.
    /// </summary>
    /// <remarks>Only for the refusal's advice, so a forged entry changes nothing but the wording.</remarks>
    internal static bool IsSubmodulePath(string dir, string repoDir)
    {
        for (var super = Path.GetDirectoryName(repoDir);
            super != null && IsInside(dir, super);
            super = Path.GetDirectoryName(super))
        {
            if (ListsSubmodulePath(super, repoDir))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether <paramref name="super"/>'s own .gitmodules lists <paramref name="repoDir"/> as a path.
    /// </summary>
    private static bool ListsSubmodulePath(string super, string repoDir)
    {
        var gitmodules = Path.Combine(super, ".gitmodules");
        if (!File.Exists(gitmodules) || IsLink(gitmodules))
            return false;
        var relative = Path.GetRelativePath(super, repoDir);
        // No advice from a file that can't be read.
        return GitConfigFile.TryParseFile(gitmodules)?.Any(e =>
            e.Section == "submodule" && e.Key == "path" && e.Value.TrimEnd('/') == relative) ?? false;
    }

    /// <summary>Finds the common git dir of the linked worktree whose .git file is <paramref name="git"/>.</summary>
    /// <returns>The main checkout's .git.</returns>
    /// <exception cref="PenException">
    /// The .git file doesn't validate (a submodule's checkout, or a forged one), or it's a worktree of a bare repo,
    /// which has no main checkout to share Pen's state with.
    /// </exception>
    private static string CheckWorktree(string git)
    {
        var common = RepoScan.ResolveWorktree(git)
            ?? throw new PenException(".git is a file that isn't a valid linked worktree (a " +
                "submodule's checkout, or a worktree whose entry in the main repo is missing or " +
                "moved), which pen doesn't support. For a submodule, run pen in the repo that " +
                "has it; for a worktree, git worktree repair in the main checkout may fix it");
        // ResolveWorktree requires the path to be real already; this is belt and braces.
        if (IsLink(common) || !Directory.Exists(common) || Native.RealPath(common) != common)
        {
            throw new PenException($"this worktree's git dir {Out.Field(common)} " +
                "isn't a real directory, which pen doesn't support");
        }

        if (!IsMainCheckoutGitDir(common))
        {
            throw new PenException($"this is a worktree of the bare repo {Out.Field(common)}, " +
                "which pen doesn't support: run pen in a worktree of a repo with a main checkout");
        }

        return common;
    }

    /// <summary>
    /// Whether <paramref name="common"/> is a non-bare main checkout's .git: named .git, a directory, and not
    /// core.bare.
    /// </summary>
    internal static bool IsMainCheckoutGitDir(string common)
    {
        if (Path.GetFileName(common) != ".git" || !Directory.Exists(common))
            return false;
        // Reads only the common config, not config.worktree (git also reads core.bare there when
        // extensions.worktreeConfig is on); one the agent planted is quarantined by the session-end check.
        // Only core.bare itself: git ignores a bare key in a subsection such as [core "x"].
        var bare = GitConfigFile.FindLast(GitConfigFile.ParseFile(Path.Combine(common, "config")), "core", "bare");
        return bare == null || !GitConfigFile.IsTrue(bare);
    }

    /// <summary>Describes what makes a nested repo one, for the refusal; nothing for a plain .git dir.</summary>
    private static string Describe(NestedKind kind) => kind switch
    {
        NestedKind.Bare => " (a bare repo)",
        NestedKind.GitFile => " (a .git file that isn't one of this repo's worktrees or submodules)",
        NestedKind.GitLink => " (a .git symlink)",
        _ => "",
    };

    /// <summary>Warns once if the repo's common git dir may fold case.</summary>
    /// <param name="project">The project; a plain directory has no git dir to probe, so no warning.</param>
    /// <param name="warn">Where the warning goes.</param>
    /// <param name="probe">Whether a git dir may fold case: <see cref="MayFoldCase(string)"/> in production.</param>
    /// <remarks>
    /// The common git dir is where config and hooks are, for a linked worktree too. On a folding file system the
    /// agent could change .git/config by writing .git/CONFIG, past its single-file mount. This warns on every
    /// session and blocks nothing.
    /// </remarks>
    internal static void WarnIfCaseMayFold(Project project, Action<string> warn, Func<string, bool> probe)
    {
        if (project.CommonGitDir is { } common && probe(common))
            warn(CaseWarning);
    }

    /// <summary>Whether <paramref name="gitDir"/> may fold case, by probing it with a new random name.</summary>
    internal static bool MayFoldCase(string gitDir) => MayFoldCase(gitDir, GenerateCaseProbeName());

    /// <summary>Generates a random name for the case probe.</summary>
    /// <remarks>
    /// It's a name git has no use for: the files it reads in a git dir have fixed names, and the only other names it
    /// takes there as refs are all caps with no dot. Lower case, so the upper-case form is a different name.
    /// </remarks>
    internal static string GenerateCaseProbeName() =>
        ".pen-case-probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>
    /// Whether <paramref name="gitDir"/> may fold case: an empty file created there is also found under its
    /// upper-case name.
    /// </summary>
    /// <returns>False only for a clean "no".</returns>
    /// <remarks>
    /// Anything short of a clean "no" counts as yes: either name there already (it isn't this probe's to touch), the
    /// file not being created, or it having gone by the time it's looked for. Exclusive creation doesn't follow a
    /// symlink, and the file is removed on every path once creating it has been tried.
    /// </remarks>
    internal static bool MayFoldCase(string gitDir, string name)
    {
        var probe = Path.Combine(gitDir, name);
        var upper = Path.Combine(gitDir, name.ToUpperInvariant());
        try
        {
            if (Exists(probe) || Exists(upper))
                return true;
            try
            {
                new FileStream(probe, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                }).Dispose();
                return Exists(upper) || !Exists(probe);
            }
            finally
            {
                File.Delete(probe);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Plans the mounts over the project mount, creating what must exist to be mounted (.pen/, .git/hooks, and a few
    /// files inside .git) so that the agent can't create them instead.
    /// </summary>
    /// <param name="dir">The project dir, checked by <see cref="CheckAtStart"/> first.</param>
    /// <param name="hostHome">The host user's home, for includes of ~/ paths.</param>
    /// <returns>The mounts, in order, one per path (see <see cref="MountPlanner.Plan"/>).</returns>
    public static List<Mount> PrepareMounts(string dir, string hostHome) => new MountPlanner(dir, hostHome).Plan();

    /// <summary>
    /// Resolves the real path of <paramref name="path"/>, or of its deepest existing ancestor plus the rest.
    /// </summary>
    /// <returns>The resolved path, or <paramref name="path"/> itself if no ancestor resolves.</returns>
    internal static string ResolveExisting(string path)
    {
        var rest = new Stack<string>();
        var current = path;
        while (true)
        {
            var real = Native.RealPath(current);
            if (real != null)
                return rest.Aggregate(real, Path.Combine);
            var parent = Path.GetDirectoryName(current);
            if (parent == null)
                return path;
            rest.Push(Path.GetFileName(current));
            current = parent;
        }
    }

    /// <summary>
    /// Creates <paramref name="path"/> as a directory to be mounted, refusing a symlink or a file there.
    /// </summary>
    /// <param name="dir">The project dir, for messages.</param>
    /// <param name="path">The directory.</param>
    internal static void EnsureDirectory(string dir, string path)
    {
        RequireNotLink(dir, path);
        if (File.Exists(path))
        {
            throw new PenException($"{FormatPath(dir, path)} is a file, not a " +
                "directory: pen needs to mount it read-only as a directory");
        }

        Directory.CreateDirectory(path);
    }

    /// <summary>Refuses a symlink at <paramref name="path"/>, which Pen has to mount read-only.</summary>
    /// <param name="dir">The project dir, for messages.</param>
    /// <param name="path">The path to be mounted.</param>
    internal static void RequireNotLink(string dir, string path)
    {
        if (IsLink(path))
        {
            throw new PenException($"{FormatPath(dir, path)} is a symlink, which " +
                "pen doesn't support here: it has to mount it read-only");
        }
    }

    /// <summary>Whether <paramref name="path"/> is a symlink, dangling or not.</summary>
    internal static bool IsLink(string path) => new FileInfo(path).LinkTarget != null;

    /// <summary>Whether <paramref name="path"/> exists, including as a dangling symlink.</summary>
    internal static bool Exists(string path) => Path.Exists(path) || IsLink(path);

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="dir"/> or below it, comparing names exactly.
    /// </summary>
    internal static bool IsInside(string dir, string path) =>
        path == dir || path.StartsWith(dir.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>
    /// Formats <paramref name="path"/> for a message: relative to <paramref name="dir"/> when it's inside, and safe to
    /// print (<see cref="Out.Field"/>).
    /// </summary>
    internal static string FormatPath(string dir, string path)
    {
        if (path == dir)
            return Out.Field(".");
        if (IsInside(dir, path))
            return Out.Field(path[(dir.TrimEnd('/').Length + 1)..]);
        return Out.Field(path);
    }
}
