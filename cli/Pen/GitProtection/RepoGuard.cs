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
    /// directory it can enter but not list; any repo inside the project; a repo anywhere in the git dir where git
    /// keeps none (<see cref="RepoScan.ScanGitDir"/>); and a symlink in the project, of any name, that leads into the
    /// git dir, or would once a directory it names is made (<see cref="GitDirLinkCheck"/>).
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
        // Defence in depth: ResolveModuleCheckout already rejects a checkout whose gitdir resolves through a symlink
        // (it requires the gitdir's real path to be the path it reads as), and planning the mounts refuses the symlink.
        var moduleRepos = modules.Repos.Where(repo => !PathChecks.IsLink(repo)).ToList();
        var scan = RepoScan.ScanNested(dir, common, moduleRepos, tryMakeListable: null);
        var inGitDir = RepoScan.ScanGitDir(common, moduleRepos, tryMakeListable: null);
        RequireDecodableNames(dir, common, modules, scan, inGitDir);
        RequireListable(dir, scan, inGitDir);
        RequireNoNestedRepos(dir, git, scan.Repos);
        RequireNoGitDirRepos(dir, common, inGitDir.Repos);
        RequireNoGitDirLinks(dir, scan.GitDirLinks, scan.UntracedLinks);
    }

    /// <summary>Finds the project's common git dir, refusing one Pen can't check.</summary>
    /// <returns>
    /// The common git dir, which needn't exist: <paramref name="git"/> unless that's a worktree's file.
    /// </returns>
    private static string ResolveCheckableGitDir(string dir, string git)
    {
        if (PathChecks.IsLink(git))
        {
            throw new PenException($"{PathChecks.FormatPath(dir, git)} is a symlink, which " +
                "pen doesn't support: run pen in the repo it points to");
        }

        var common = git;
        if (File.Exists(git))
            common = CheckWorktree(git);
        if (Directory.Exists(common) && !Native.CanList(common))
        {
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't read " +
                $"{PathChecks.FormatPath(dir, common)}, so it can't protect it: fix its permissions, then rerun");
        }

        return common;
    }

    /// <summary>Refuses a commondir in the common git dir.</summary>
    /// <remarks>It makes git read config and hooks from another directory, and a normal repo never has one.</remarks>
    private static void RequireNoCommondir(string dir, string common)
    {
        var commondir = Path.Combine(common, "commondir");
        if (PathChecks.ExistsOrIsLink(commondir))
        {
            throw new PenException($"{PathChecks.FormatPath(dir, commondir)} exists, which a normal repo never " +
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
                + string.Join('\n', unreadable.Select(u => "  " + PathChecks.FormatPath(dir, u)))
                + "\nfix their permissions or remove them, then rerun");
        }
    }

    /// <summary>Refuses names in the git dir, its module repos or the tree that aren't valid UTF-8.</summary>
    /// <remarks>
    /// Pen can't look inside them, though git on this machine can still enter them and run what's there. The walk of
    /// the git dir meets the names the module walk reports too, so each is listed once.
    /// </remarks>
    private static void RequireDecodableNames(
        string dir,
        string common,
        ModuleScan modules,
        NestedScan scan,
        NestedScan inGitDir)
    {
        var badNames = modules.BadNames.Concat(inGitDir.BadNames).Concat(scan.BadNames)
            .DistinctBy(n => n.DisplayPath, StringComparer.Ordinal).ToList();
        if (badNames.Count == 0)
            return;
        var command = "";
        if (badNames.Any(n => !n.EndsInNewline))
            command = ": cd to its directory and run mv -- \"$(printf 'NAME')\" new-name, with NAME as shown";
        var gitDirNote = "";
        // A repo renamed there would be adopted as a submodule's at the next start.
        if (modules.BadNames.Count > 0)
        {
            gitDirNote = $"\ninspect any under {PathChecks.FormatPath(dir, common)} first and prefer removing them: " +
                "a repo renamed there is trusted as a submodule's repo at the next start";
        }

        throw new PenException($"refusing to run in {Out.Field(dir)}: these names aren't " +
            "valid UTF-8, so pen can't look inside them, though git on this machine can " +
            "still enter them and run what's there (shown with octal escapes):\n"
            + string.Join('\n', FormatBadNames(dir, badNames).Select(n => "  " + n))
            + "\nrename or remove each, then rerun" + command + gitDirNote);
    }

    /// <summary>Refuses directories in the git dir or the tree that can be entered but not listed.</summary>
    /// <remarks>Git on this machine can still enter them, so Pen can't see a repo inside one.</remarks>
    private static void RequireListable(string dir, NestedScan scan, NestedScan inGitDir)
    {
        var unlistable = inGitDir.UnlistableDirs.Concat(scan.UnlistableDirs).ToList();
        if (unlistable.Count == 0)
            return;
        var listed = CappedList.FormatLines(unlistable, MaxListed, u => PathChecks.FormatPath(dir, u));
        throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't list these directories, " +
            "though git on this machine can still enter them, so it can't see a repo inside one:\n"
            + string.Join('\n', listed.Select(line => "  " + line))
            + "\nfix their permissions (chmod u+rx) or remove them, then rerun");
    }

    /// <summary>Refuses repos inside the project, whose config and hooks Pen can't protect.</summary>
    /// <param name="dir">The project dir.</param>
    /// <param name="git">The project's .git, which decides the message's wording.</param>
    /// <param name="nested">The repos found inside the project.</param>
    private static void RequireNoNestedRepos(string dir, string git, IReadOnlyList<NestedRepo> nested)
    {
        if (nested.Count == 0)
            return;
        var message = new StringBuilder(FormatNestedHeading(dir, git));
        var anySubmodule = false;
        var anyOther = false;
        // Only the repos listed are looked at for the advice; any not listed may be other repos.
        var anyQuarantined = false;
        var listed = CappedList.FormatLines(nested, MaxListed, repo =>
        {
            var submodule = repo.Kind == NestedKind.GitDir && IsSubmodulePath(dir, repo.RepoDir);
            var quarantined = IsInQuarantine(dir, repo.RepoDir);
            anySubmodule |= submodule;
            anyQuarantined |= quarantined;
            anyOther |= !submodule && !quarantined;
            var kind = submodule
                ? " (a submodule with its own .git dir)"
                : NestedRepoWording.DescribeRefused(repo.Kind);
            return PathChecks.FormatPath(dir, repo.RepoDir) + kind;
        });
        foreach (var line in listed)
            message.Append("\n  ").Append(line);
        anyOther |= nested.Count > MaxListed;
        if (anySubmodule)
        {
            message.Append("\nfor a submodule, run git submodule absorbgitdirs here on this machine: " +
                "it moves the submodule's .git dir into this repo's, where pen can protect it");
        }

        if (anyQuarantined)
            message.Append(QuarantineHint);
        if (anyOther)
            message.Append("\nrun pen in each repo instead");
        throw new PenException(message.ToString());
    }

    /// <summary>Refuses repos in the git dir where git keeps none (<see cref="RepoScan.ScanGitDir"/>).</summary>
    /// <param name="dir">The project dir.</param>
    /// <param name="common">The common git dir, which the repos found are inside.</param>
    /// <param name="found">The repos found in the git dir.</param>
    /// <remarks>
    /// Only the repos listed are looked at for the advice: one in refs/ or logs/ is made by ref names, which
    /// renaming fixes, and a worktree entry may be a stale one, which git can tidy.
    /// </remarks>
    private static void RequireNoGitDirRepos(string dir, string common, IReadOnlyList<NestedRepo> found)
    {
        if (found.Count == 0)
            return;
        var anyRefs = false;
        var anyEntry = false;
        var anyQuarantined = false;
        var listed = CappedList.FormatLines(found, MaxListed, repo =>
        {
            var refs = repo is { Region: GitDirRegion.Refs, Kind: NestedKind.Bare };
            anyRefs |= refs;
            anyQuarantined |= IsInQuarantine(common, repo.RepoDir);
            anyEntry |= repo is { Region: GitDirRegion.WorktreeEntry, Kind: NestedKind.Bare };
            return PathChecks.FormatPath(dir, repo.RepoDir) + (refs ? " (ref names that lay it out as a git dir)" :
                NestedRepoWording.DescribeRefusedInGitDir(repo));
        });
        var message = new StringBuilder($"refusing to run in {Out.Field(dir)}: its git dir has repos where git " +
            "keeps none, whose config and hooks pen can't protect, and git run in their directories (through a " +
            "symlink, say) would use them:");
        foreach (var line in listed)
            message.Append("\n  ").Append(line);
        if (anyRefs)
        {
            message.Append("\nthose in refs/ or logs/ are ref names that git takes for a repo's layout (a branch " +
                "named HEAD beside ones under objects/ and refs/, say): rather than removing them, rename those " +
                "branches or refs");
        }
        if (anyEntry)
        {
            message.Append("\na worktree entry whose commondir leads elsewhere may be a stale one: git worktree " +
                "prune removes an entry whose worktree is gone, and git worktree repair mends one whose worktree " +
                "moved");
        }
        if (anyQuarantined)
            message.Append(QuarantineHint);
        message.Append(anyRefs || anyEntry
            ? "\ninspect any others and remove them, then rerun"
            : "\ninspect each and remove it, then rerun");
        throw new PenException(message.ToString());
    }

    /// <summary>
    /// Refuses symlinks in the tree that lead into the git dir, or would once a directory they name is made, and
    /// those Pen can't tell lead elsewhere (<see cref="GitDirLinkCheck"/>).
    /// </summary>
    /// <param name="dir">The project dir.</param>
    /// <param name="links">The symlinks that lead into the git dir.</param>
    /// <param name="untraced">The symlinks Pen can't trace.</param>
    /// <remarks>Git run through one starts in the git dir, where the sandbox could put a repo for it to use.</remarks>
    private static void RequireNoGitDirLinks(string dir, IReadOnlyList<string> links, IReadOnlyList<string> untraced)
    {
        if (links.Count == 0 && untraced.Count == 0)
            return;
        var message = new StringBuilder($"refusing to run in {Out.Field(dir)}: ");
        if (links.Count > 0)
        {
            message.Append("these symlinks lead into its git dir (or would, once a directory they name is made), so " +
                "git run through one would use any repo the sandbox puts there:");
            foreach (var line in CappedList.FormatLines(links, MaxListed, link => PathChecks.FormatPath(dir, link)))
                message.Append("\n  ").Append(line);
        }
        if (untraced.Count > 0)
        {
            message.Append(links.Count > 0 ? "\nand pen can't tell where these lead" : "pen can't tell where these " +
                "symlinks lead");
            message.Append(" (a directory on the way can't be searched, or a target can't be read or isn't valid " +
                "UTF-8), so they may lead into its git dir, where git run through one would use any repo the " +
                "sandbox puts there:");
            foreach (var line in CappedList.FormatLines(untraced, MaxListed, link => PathChecks.FormatPath(dir, link)))
                message.Append("\n  ").Append(line);
        }
        message.Append("\nremove them, then rerun");
        throw new PenException(message.ToString());
    }

    /// <summary>The advice for a repo found in something Pen quarantined (<see cref="IsInQuarantine"/>).</summary>
    private const string QuarantineHint = "\nthose under a name holding " + GitQuarantine.Suffix +
        " are inside what pen put aside at the end of an earlier session (a repo in it was left intact): " +
        "inspect them and remove them";

    /// <summary>
    /// Whether <paramref name="repoDir"/> is inside something Pen quarantined: a component of its path below
    /// <paramref name="dir"/> holds <see cref="GitQuarantine.Suffix"/>, which Pen adds when it moves a thing aside
    /// (with -2, -3, … after it when the name is taken).
    /// </summary>
    /// <remarks>Components above <paramref name="dir"/>, should the repo be outside it, don't count.</remarks>
    private static bool IsInQuarantine(string dir, string repoDir) =>
        Path.GetRelativePath(dir, repoDir).Split('/').SkipWhile(part => part == "..")
            .Any(part => part.Contains(GitQuarantine.Suffix, StringComparison.Ordinal));

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
        CappedList.FormatLines(names, MaxListed, n => PathChecks.FormatPath(dir, n.DisplayPath) + (n.EndsInNewline
            ? " (it ends in a newline, so no command can name it here: rename it by hand)" : ""));

    /// <summary>
    /// Whether <paramref name="repoDir"/> is a submodule's checkout by the .gitmodules of the project or of a
    /// directory between it and the checkout (a nested submodule's superproject): one lists it as a
    /// submodule.*.path.
    /// </summary>
    /// <remarks>Only for the refusal's advice, so a forged entry changes nothing but the wording.</remarks>
    internal static bool IsSubmodulePath(string dir, string repoDir) =>
        Paths.EnumerateAncestors(repoDir)
            .TakeWhile(super => PathChecks.IsInside(dir, super))
            .Any(super => ListsSubmodulePath(super, repoDir));

    /// <summary>
    /// Whether <paramref name="super"/>'s own .gitmodules lists <paramref name="repoDir"/> as a path.
    /// </summary>
    private static bool ListsSubmodulePath(string super, string repoDir)
    {
        var gitmodules = Path.Combine(super, ".gitmodules");
        if (!File.Exists(gitmodules) || PathChecks.IsLink(gitmodules))
            return false;
        var relative = Path.GetRelativePath(super, repoDir);
        // No advice from a file that can't be read.
        return GitConfigFile.TryParseFile(gitmodules, out var entries, out _) && entries.Any(e =>
            e.Section == "submodule" && e.Key == "path" && e.Value.TrimEnd('/') == relative);
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
        if (PathChecks.IsLink(common) || !Directory.Exists(common) || Native.RealPath(common) != common)
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
        return bare is null || !GitConfigFile.IsTrue(bare);
    }

    /// <summary>Warns once if the repo's common git dir may fold case.</summary>
    /// <param name="project">The project; a plain directory has no git dir to probe, so no warning.</param>
    /// <param name="warn">Where the warning goes.</param>
    /// <param name="probe">
    /// Whether a git dir may fold case: <see cref="PathChecks.MayFoldCase(string)"/> in production.
    /// </param>
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

    /// <summary>
    /// Plans the mounts over the project mount, creating what must exist to be mounted (.pen/, .git/hooks, and a few
    /// files inside .git) so that the agent can't create them instead.
    /// </summary>
    /// <param name="dir">The project dir, checked by <see cref="CheckAtStart"/> first.</param>
    /// <param name="hostHome">The host user's home, for includes of ~/ paths.</param>
    /// <returns>The mounts, in order, one per path (see <see cref="MountPlanner.EnsureTargetsAndPlan"/>).</returns>
    public static List<Mount> EnsureMountTargetsAndPlan(string dir, string hostHome) =>
        new MountPlanner(dir, hostHome).EnsureTargetsAndPlan();
}
