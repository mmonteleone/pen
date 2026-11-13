using System.Security.Cryptography;
using System.Text;
using Pen.Platform;
using Pen.Projects;

namespace Pen.GitProtection;

/// <summary>
/// The start-of-session repo refusals and the mounts that keep the agent from
/// changing what host git runs. See AGENTS.md's Git protection architecture.
/// Everything here reads files directly and never runs git.
/// </summary>
internal static class RepoGuard
{
    internal const int MaxListed = 10;
    internal const int MaxIncludeFiles = 100;

    /// <summary>
    /// Refuses layouts whose config and hooks the sandbox can't protect. A
    /// linked worktree of a non-bare main checkout is accepted when its .git
    /// file validates (<see cref="RepoScan.ResolveWorktree"/>); the main
    /// repo's git dir then gets the same checks as a main checkout's own.
    /// Submodules are accepted when their repos are kept in that git dir
    /// (modules/), so their checkouts have .git files that
    /// <see cref="RepoScan.FindNested"/> validates; one with its own .git dir
    /// is refused like any nested repo, with advice to absorb it.
    /// </summary>
    public static void CheckAtStart(string dir)
    {
        var git = Path.Combine(dir, ".git");
        if (IsLink(git))
            throw new PenException($"{Rel(dir, git)} is a symlink, which pen doesn't support: run pen in the repo it points to");
        var common = git;
        if (File.Exists(git))
            common = CheckWorktree(dir, git);
        if (Directory.Exists(common) && !Native.CanList(common))
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't read {Rel(dir, common)}, so it can't protect it: fix its permissions, then rerun");
        if (Exists(Path.Combine(common, "commondir")))
            throw new PenException($"{Rel(dir, Path.Combine(common, "commondir"))} exists, which a normal repo never has: it makes git read config and hooks from another directory. Inspect it and remove it, then rerun");

        var modules = RepoScan.ScanModules(common);
        var unreadable = modules.Unreadable;
        if (unreadable.Count > 0)
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't read these directories where git keeps submodule repos, so it can't protect what's in them:\n"
                + string.Join('\n', unreadable.Select(u => "  " + Rel(dir, u)))
                + "\nfix their permissions or remove them, then rerun");

        var scan = RepoScan.ScanNested(dir, common);
        var badNames = modules.BadNames.Concat(scan.BadNames).ToList();
        if (badNames.Count > 0)
        {
            var command = badNames.Any(n => !n.EndsInNewline)
                ? ": cd to its directory and run mv -- \"$(printf 'NAME')\" new-name, with NAME as shown"
                : "";
            // A repo renamed there would be adopted as a submodule's at the next start.
            var gitDirNote = modules.BadNames.Count > 0
                ? $"\ninspect any under {Rel(dir, common)} first and prefer removing them: a repo renamed there is trusted as a submodule's repo at the next start"
                : "";
            throw new PenException($"refusing to run in {Out.Field(dir)}: these names aren't valid UTF-8, so pen can't look inside them, though git on this machine can still enter them and run what's there (shown with octal escapes):\n"
                + string.Join('\n', ListedBadNames(dir, badNames).Select(n => "  " + n))
                + "\nrename or remove each, then rerun" + command + gitDirNote);
        }
        if (scan.Unlistable.Count > 0)
        {
            var listed = scan.Unlistable.Take(MaxListed).Select(u => "  " + Rel(dir, u));
            if (scan.Unlistable.Count > MaxListed) listed = listed.Append($"  … and {scan.Unlistable.Count - MaxListed} more");
            throw new PenException($"refusing to run in {Out.Field(dir)}: pen can't list these directories, though git on this machine can still enter them, so it can't see a repo inside one:\n"
                + string.Join('\n', listed)
                + "\nfix their permissions (chmod u+rx) or remove them, then rerun");
        }
        var nested = scan.Found;
        if (nested.Count == 0) return;
        var sb = new StringBuilder();
        sb.Append(Directory.Exists(git)
            ? $"refusing to run in {dir}: it has other git repos inside it, whose config and hooks pen can't protect:"
            : $"refusing to run in {dir}: it contains git repos, whose config and hooks pen can't protect:");
        bool anySubmodule = false, anyOther = false;
        foreach (var n in nested.Take(MaxListed))
        {
            bool submodule = n.Kind == NestedKind.GitDir && IsSubmodulePath(dir, n.RepoDir);
            sb.Append($"\n  {Rel(dir, n.RepoDir)}{(submodule ? " (a submodule with its own .git dir)" : Describe(n.Kind))}");
            if (submodule) anySubmodule = true; else anyOther = true;
        }
        if (nested.Count > MaxListed) { sb.Append($"\n  … and {nested.Count - MaxListed} more"); anyOther = true; }
        if (anySubmodule)
            sb.Append("\nfor a submodule, run git submodule absorbgitdirs here on this machine: it moves the submodule's .git dir into this repo's, where pen can protect it");
        if (anyOther) sb.Append("\nrun pen in each repo instead");
        throw new PenException(sb.ToString());
    }

    /// <summary>The first few of <paramref name="names"/> for a message, and how many more there are.</summary>
    internal static IEnumerable<string> ListedBadNames(string dir, List<BadName> names)
    {
        var listed = names.Take(MaxListed).Select(n => Rel(dir, n.Shown) + (n.EndsInNewline ? " (it ends in a newline, so no command can name it here: rename it by hand)" : ""));
        return names.Count > MaxListed ? listed.Append($"… and {names.Count - MaxListed} more") : listed;
    }

    /// <summary>
    /// Whether <paramref name="repoDir"/> is a submodule's checkout by the
    /// .gitmodules of the project or of a directory between it and the
    /// checkout (a nested submodule's superproject): one lists it as a
    /// submodule.*.path. Only for the refusal's advice, so a forged entry
    /// changes nothing but the wording.
    /// </summary>
    internal static bool IsSubmodulePath(string dir, string repoDir)
    {
        for (var super = Path.GetDirectoryName(repoDir); super != null && IsInside(dir, super); super = Path.GetDirectoryName(super))
        {
            var gitmodules = Path.Combine(super, ".gitmodules");
            if (!File.Exists(gitmodules) || IsLink(gitmodules)) continue;
            var rel = Path.GetRelativePath(super, repoDir);
            try
            {
                if (GitConfigFile.ParseFile(gitmodules).Any(e =>
                        e.Section == "submodule" && e.Key == "path" && e.Value.TrimEnd('/') == rel))
                    return true;
            }
            catch (PenException) { } // no advice from a file that can't be read
        }
        return false;
    }

    /// <summary>
    /// The common git dir of the linked worktree whose .git file is
    /// <paramref name="git"/>, refusing anything else: a .git file that doesn't
    /// validate (a submodule's checkout, or a forged one), or a worktree of a
    /// bare repo, which has no main checkout to share Pen's state with.
    /// </summary>
    private static string CheckWorktree(string dir, string git)
    {
        var common = RepoScan.ResolveWorktree(git)
            ?? throw new PenException(".git is a file that isn't a valid linked worktree (a submodule's checkout, or a worktree whose entry in the main repo is missing or moved), which pen doesn't support. For a submodule, run pen in the repo that has it; for a worktree, git worktree repair in the main checkout may fix it");
        // ResolveWorktree requires the path to be real already; this is belt and braces.
        if (IsLink(common) || !Directory.Exists(common) || Native.RealPath(common) != common)
            throw new PenException($"this worktree's git dir {Out.Field(common)} isn't a real directory, which pen doesn't support");
        if (!IsMainCheckoutGitDir(common))
            throw new PenException($"this is a worktree of the bare repo {Out.Field(common)}, which pen doesn't support: run pen in a worktree of a repo with a main checkout");
        return common;
    }

    /// <summary>
    /// Whether <paramref name="common"/> is a non-bare main checkout's .git:
    /// named .git, a directory, and not core.bare.
    /// </summary>
    internal static bool IsMainCheckoutGitDir(string common)
    {
        if (Path.GetFileName(common) != ".git" || !Directory.Exists(common)) return false;
        var bare = GitConfigFile.ParseFile(Path.Combine(common, "config"))
            .Where(e => e.Section == "core" && e.Key == "bare").Select(e => e.Value).LastOrDefault();
        return bare == null || !GitConfigFile.IsTrue(bare);
    }

    private static string Describe(NestedKind kind) => kind switch
    {
        NestedKind.Bare => " (a bare repo)",
        NestedKind.GitFile => " (a .git file that isn't one of this repo's worktrees or submodules)",
        NestedKind.GitLink => " (a .git symlink)",
        _ => "",
    };

    internal const string CaseWarning = "warning: this repo's git dir isn't known to be case-sensitive, so read-only protection of single files in it may not hold: the agent could change .git/config by writing .git/CONFIG. Don't run git in this repo on this machine until the session ends";

    /// <summary>
    /// Warns once if the repo's common git dir may fold case: that's where
    /// config and hooks are, for a linked worktree too. A plain directory has
    /// no git dir to probe, so no warning.
    /// </summary>
    internal static void WarnIfCaseMayFold(Project project, Action<string> warn, Func<string, bool>? probe = null)
    {
        if (project.CommonGitDir is { } common && (probe ?? MayFoldCase)(common)) warn(CaseWarning);
    }

    internal static bool MayFoldCase(string gitDir) => MayFoldCase(gitDir, NewCaseProbeName());

    /// <summary>
    /// A random name git has no use for: the files it reads in a git dir have
    /// fixed names, and the only other names it takes there as refs are all
    /// caps with no dot. Lower case, so the upper-case form is a different name.
    /// </summary>
    internal static string NewCaseProbeName() =>
        ".pen-case-probe-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    /// <summary>
    /// Whether <paramref name="gitDir"/> may fold case: an empty file created
    /// there is also found under its upper-case name. Anything short of a clean
    /// "no" counts as yes: either name there already (it isn't this probe's to
    /// touch), the file not being created, or it having gone by the time it's
    /// looked for. Exclusive creation doesn't follow a symlink, and the file is
    /// removed on every path once creating it has been tried.
    /// </summary>
    internal static bool MayFoldCase(string gitDir, string name)
    {
        var probe = Path.Combine(gitDir, name);
        var upper = Path.Combine(gitDir, name.ToUpperInvariant());
        try
        {
            if (Exists(probe) || Exists(upper)) return true;
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
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The mounts over the project mount, creating what must exist to be
    /// mounted (.pen/, .git/hooks, and a few files inside .git) so that the
    /// agent can't create them instead. Call after <see cref="CheckAtStart"/>.
    /// <para>
    /// For a linked worktree, the main repo's git dir (the common dir: objects,
    /// refs, the worktree's own entry) is mounted read-write, protected like a
    /// main checkout's .git; the worktree's .git file read-only; and the main
    /// checkout's own HEAD and index read-only, since the main checkout isn't
    /// visible in the sandbox.
    /// </para>
    /// <para>
    /// The submodule repos kept in that git dir are protected the same way
    /// (<see cref="ProtectModuleRepos"/>).
    /// </para>
    /// </summary>
    public static List<Mount> PrepareMounts(string dir, string hostHome)
    {
        var mounts = new List<Mount>();
        var git = Path.Combine(dir, ".git");
        if (Directory.Exists(git))
        {
            ProtectGitDir(git, git, dir, hostHome, mounts);
            ProtectModuleRepos(git, dir, hostHome, mounts);
        }
        else if (File.Exists(git))
        {
            var common = RepoScan.ResolveWorktree(git)
                ?? throw new PenException(".git is a file that isn't a valid linked worktree");
            ProtectGitDir(common, common, dir, hostHome, mounts);
            foreach (var name in new[] { "HEAD", "index" })
            {
                var f = Path.Combine(common, name);
                if (!File.Exists(f)) continue;
                RequireNotLink(dir, f);
                // Not verified: the main checkout's git changes these legitimately.
                mounts.Add(new(f, ReadOnly: true));
            }
            // The .git file names the git dir, so the agent must not repoint it.
            mounts.Add(new(git, ReadOnly: true, Verify: true));
            ProtectModuleRepos(common, dir, hostHome, mounts);
        }

        // .pen/ sets the next session's policy, so the agent must not change or create it.
        EnsureDirectory(dir, Path.Combine(dir, ".pen"));
        mounts.Add(new(Path.Combine(dir, ".pen"), ReadOnly: true));

        // A file that's also read as config (an include of HEAD) is verified however else it's mounted.
        return mounts.GroupBy(m => m.Path, StringComparer.Ordinal)
            .Select(g => g.First() with { Verify = g.Any(m => m.Verify) })
            .ToList();
    }

    /// <summary>
    /// The mounts over a git dir (the project's .git, or the common dir of the
    /// worktree it is) that keep the agent from changing what host git reads
    /// as config or runs as hooks, for this checkout and every other worktree
    /// sharing the dir. <paramref name="dir"/> is the project dir: messages
    /// are relative to it, and the worktrees it doesn't contain aren't visible
    /// in the sandbox, so their entries are read-only as a whole.
    /// <paramref name="common"/> is the project's git dir, all of it writable in
    /// the sandbox, whichever repo in it <paramref name="gitDir"/> is.
    /// </summary>
    private static void ProtectGitDir(string gitDir, string common, string dir, string hostHome, List<Mount> mounts)
    {
        // The git dir itself becomes a mount point, so it can't be renamed away and replaced.
        mounts.Add(new(gitDir, ReadOnly: false));

        var config = Path.Combine(gitDir, "config");
        EnsureFile(dir, config);
        mounts.Add(new(config, ReadOnly: true, Verify: true));

        var hooks = Path.Combine(gitDir, "hooks");
        EnsureDirectory(dir, hooks);
        mounts.Add(new(hooks, ReadOnly: true));

        bool worktreeConfig = AddIncludes(dir, common, config, hostHome, mounts);
        // With extensions.worktreeConfig, git also reads config.worktree
        // files, which the agent could otherwise create.
        // Any true value is enough: includeIf conditions may differ in a
        // different worktree or branch sharing this git dir.
        AddWorktreeConfig(dir, common, Path.Combine(gitDir, "config.worktree"), worktreeConfig, hostHome, mounts);

        // Each linked worktree: host git in it reads config and hooks from
        // wherever its commondir points.
        var worktrees = Path.Combine(gitDir, "worktrees");
        if (IsLink(worktrees))
            throw new PenException($"{Rel(dir, worktrees)} is a symlink, which pen doesn't support");
        if (Directory.Exists(worktrees))
        {
            // A mount point too, so the entries mounted below can't be moved
            // aside together and replaced with forged ones.
            mounts.Add(new(worktrees, ReadOnly: false));
            foreach (var wt in Directory.EnumerateDirectories(worktrees).Order(StringComparer.Ordinal))
            {
                if (IsLink(wt))
                    throw new PenException($"{Rel(dir, wt)} is a symlink, which pen doesn't support");
                var commondir = Path.Combine(wt, "commondir");
                if (Exists(commondir)) RequireNotLink(dir, commondir);
                if (!IsWorktreeInside(dir, wt))
                {
                    // The worktree isn't visible in the sandbox, so its entry
                    // looks dangling there: `git worktree prune` would delete
                    // it. Read-only keeps it, and its HEAD, index and commondir.
                    mounts.Add(new(wt, ReadOnly: true));
                    // Its config can include a file in the writable project.
                    var worktreeFile = Path.Combine(wt, "config.worktree");
                    RequireNotLink(dir, worktreeFile);
                    AddIncludes(dir, common, worktreeFile, hostHome, mounts);
                    continue;
                }
                if (Exists(commondir)) mounts.Add(new(commondir, ReadOnly: true, Verify: true));
                AddWorktreeConfig(dir, common, Path.Combine(wt, "config.worktree"), worktreeConfig, hostHome, mounts);
            }
        }
    }

    /// <summary>
    /// Each submodule repo kept in <paramref name="gitDir"/> (see
    /// <see cref="RepoScan.FindModuleRepos"/>) gets the same protection as the
    /// git dir itself: host git running in a submodule checkout reads its
    /// config and runs its hooks. One under a path already mounted read-only
    /// (an external worktree's entry) is left alone: it's read-only with it,
    /// and a writable mount of its own would undo that, though what its config
    /// includes can still be writable. A module repo that's a
    /// symlink or has a commondir is refused, since either makes git read
    /// config and hooks from another directory.
    /// <para>
    /// Each directory between <paramref name="gitDir"/> and a module repo
    /// (modules/, the levels of a name with "/" in it, a worktree's entry) is a
    /// mount point too, like the repo itself: otherwise one could be moved
    /// aside, taking the repo's mounts with it, and a forged repo made at the
    /// path the session-end check knows as a submodule's.
    /// </para>
    /// </summary>
    private static void ProtectModuleRepos(string gitDir, string dir, string hostHome, List<Mount> mounts)
    {
        foreach (var repo in RepoScan.FindModuleRepos(gitDir))
        {
            if (IsLink(repo))
                throw new PenException($"{Rel(dir, repo)} is a symlink, which pen doesn't support: a submodule's git dir has to be a real directory inside {Rel(dir, gitDir)}");
            var commondir = Path.Combine(repo, "commondir");
            if (Exists(commondir))
                throw new PenException($"{Rel(dir, commondir)} exists, which a submodule's repo never has: it makes git read config and hooks from another directory. Inspect it and remove it, then rerun");
            if (mounts.Any(m => m.ReadOnly && IsInside(m.Path, repo)))
            {
                foreach (var name in new[] { "config", "config.worktree" })
                {
                    var file = Path.Combine(repo, name);
                    RequireNotLink(dir, file);
                    AddIncludes(dir, gitDir, file, hostHome, mounts);
                }
                continue;
            }
            var between = new Stack<string>();
            for (var p = Path.GetDirectoryName(repo); p != null && p != gitDir && IsInside(gitDir, p); p = Path.GetDirectoryName(p))
                between.Push(p);
            foreach (var p in between) mounts.Add(new(p, ReadOnly: false));
            ProtectGitDir(repo, gitDir, dir, hostHome, mounts);
        }
    }

    /// <summary>
    /// Whether the worktree whose entry is <paramref name="entry"/> (a
    /// &lt;git dir&gt;/worktrees/&lt;name&gt;) is inside <paramref name="dir"/>, by the
    /// path its gitdir file names. An entry without a usable one counts as
    /// outside.
    /// </summary>
    internal static bool IsWorktreeInside(string dir, string entry)
    {
        var gitdirFile = Path.Combine(entry, "gitdir");
        try
        {
            if (!File.Exists(gitdirFile) || IsLink(gitdirFile)) return false;
            var text = Native.ReadRegularFile(gitdirFile)?.TrimEnd('\n', '\r');
            if (string.IsNullOrEmpty(text) || text.Contains('\n') || text.Contains('\0')) return false;
            if (!Path.IsPathRooted(text)) text = Path.Combine(entry, text);
            return IsInside(dir, ResolveExisting(Path.GetFullPath(text)));
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static void AddWorktreeConfig(string dir, string common, string path, bool enabled, string hostHome, List<Mount> mounts)
    {
        if (!enabled && !Exists(path)) return;
        EnsureFile(dir, path);
        mounts.Add(new(path, ReadOnly: true, Verify: true));
        AddIncludes(dir, common, path, hostHome, mounts);
    }

    /// <summary>
    /// include.path and includeIf.*.path files inside the project or
    /// <paramref name="common"/>, the project's git dir: those are what the sandbox
    /// can write, whichever repo in the git dir the including file belongs to. The agent could write one of those and host git would read it as
    /// config, so each is mounted read-only too. A missing one inside the git
    /// dir is created empty; a missing one in the working tree is refused,
    /// since creating it would add a file to the project. A path that wasn't valid
    /// UTF-8 is refused: decoded, it has U+FFFD where git has other bytes, so
    /// Pen would protect a different file from the one git reads.
    /// Returns whether any file in the walk sets extensions.worktreeConfig.
    /// <para>
    /// The agent can write some of these files, so each is walked only once, and
    /// a walk over <see cref="MaxIncludeFiles"/> files is refused: cycles and
    /// fan-outs would otherwise take time exponential in git's depth limit of
    /// 10. Depth itself isn't limited, since a file first met deep down mustn't
    /// lose what it includes when it's met again higher up. Files are keyed by
    /// path as written, not real path: a relative include resolves from there.
    /// A ~user or %(prefix) path is refused: Pen can't tell where it leads.
    /// </para>
    /// </summary>
    private static bool AddIncludes(string dir, string common, string configFile, string hostHome, List<Mount> mounts) =>
        AddIncludes(dir, common, configFile, hostHome, mounts, new HashSet<string>(StringComparer.Ordinal) { configFile });

    private static bool AddIncludes(string dir, string common, string configFile, string hostHome, List<Mount> mounts, HashSet<string> visited)
    {
        bool worktreeConfig = false;
        foreach (var e in GitConfigFile.ParseFile(configFile))
        {
            if (e.Section == "extensions" && e.Key == "worktreeconfig" && GitConfigFile.IsTrue(e.Value)) worktreeConfig = true;
            if (e.Section is not ("include" or "includeif") || e.Key != "path" || e.Value.Length == 0) continue;
            if (e.Value.Contains('\ufffd'))
                throw new PenException($"{Rel(dir, configFile)} includes a path that isn't valid UTF-8, so pen can't tell which file git would read: remove the include or rename the file, then rerun");
            var p = e.Value;
            if (p == "~" || p.StartsWith("~/", StringComparison.Ordinal)) p = hostHome + p[1..];
            else if (p.StartsWith('~') || p.StartsWith("%(prefix)/", StringComparison.Ordinal))
                throw new PenException($"{Rel(dir, configFile)} includes {Out.Field(p)}, and pen can't resolve that form of path, so it can't tell which file git would read: write the path out in full or remove the include, then rerun");
            if (!Path.IsPathRooted(p)) p = Path.Combine(Path.GetDirectoryName(configFile)!, p);

            // As the OS walks it: ".." after a symlink is the parent of where that leads.
            var written = Path.GetFullPath(p);
            var real = HooksPath.RealPathWalk(p, _ => false, out _)
                ?? throw new PenException($"{Rel(dir, configFile)} includes {Rel(dir, written)}, and pen can't follow that path to the file git would read: fix its permissions or remove the include, then rerun");
            if (real == written) p = written; // no symlink to tell them apart, and a cycle then repeats one name
            if (!visited.Add(p)) continue;
            if (visited.Count > MaxIncludeFiles)
                throw new PenException($"{Rel(dir, configFile)} takes git config includes past {MaxIncludeFiles} files, more than pen will follow: remove it or the includes in it, then rerun");

            if (written != real && (IsInside(dir, written) || IsInside(common, written)))
                throw new PenException($"{Rel(dir, configFile)} includes {Rel(dir, written)} through a symlink, which pen doesn't support: the agent could redirect the include");
            if (Directory.Exists(real)) continue; // git ignores a directory
            bool writable = IsInside(dir, real) || IsInside(common, real);
            if (writable && !File.Exists(real))
            {
                if (!IsInside(common, real))
                    throw new PenException($"{Rel(dir, configFile)} includes {Rel(dir, real)}, which doesn't exist: the agent could create it, and git on this machine would read it as config. Create it (it can be empty) or remove the include, then rerun");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(real)!);
                    File.WriteAllText(real, "");
                }
                catch (Exception x) when (x is IOException or UnauthorizedAccessException)
                {
                    throw new PenException($"{Rel(dir, configFile)} includes {Rel(dir, real)}, which pen can't create to protect it (a parent is a file, the name is too long, or it's not writable): remove it or the include in it, then rerun");
                }
            }
            if (writable)
            {
                RequireNotLink(dir, real);
                mounts.Add(new(real, ReadOnly: true, Verify: true));
            }
            // Even host-only includes can enable worktreeConfig or include
            // another file that the sandbox can write. Conditional includes
            // are followed conservatively for every worktree and branch.
            worktreeConfig |= AddIncludes(dir, common, p, hostHome, mounts, visited);
        }
        return worktreeConfig;
    }

    /// <summary>The real path of <paramref name="path"/>, or of its deepest existing ancestor plus the rest.</summary>
    internal static string ResolveExisting(string path)
    {
        var rest = new Stack<string>();
        var p = path;
        while (true)
        {
            var real = Native.RealPath(p);
            if (real != null) return rest.Aggregate(real, Path.Combine);
            var parent = Path.GetDirectoryName(p);
            if (parent == null) return path;
            rest.Push(Path.GetFileName(p));
            p = parent;
        }
    }

    internal static void EnsureDirectory(string dir, string path)
    {
        RequireNotLink(dir, path);
        if (File.Exists(path))
            throw new PenException($"{Rel(dir, path)} is a file, not a directory: pen needs to mount it read-only as a directory");
        Directory.CreateDirectory(path);
    }

    private static void EnsureFile(string dir, string path)
    {
        RequireNotLink(dir, path);
        if (Directory.Exists(path))
            throw new PenException($"{Rel(dir, path)} is a directory, not a file");
        if (!File.Exists(path)) File.WriteAllText(path, "");
    }

    internal static void RequireNotLink(string dir, string path)
    {
        if (IsLink(path))
            throw new PenException($"{Rel(dir, path)} is a symlink, which pen doesn't support here: it has to mount it read-only");
    }

    internal static bool IsLink(string path) => new FileInfo(path).LinkTarget != null;

    /// <summary>Exists, including as a dangling symlink.</summary>
    internal static bool Exists(string path) => Path.Exists(path) || IsLink(path);

    internal static bool IsInside(string dir, string path) =>
        path == dir || path.StartsWith(dir.TrimEnd('/') + "/", StringComparison.Ordinal);

    /// <summary>A path for messages, relative to <paramref name="dir"/> when it's inside, safe to print (<see cref="Out.Field"/>).</summary>
    internal static string Rel(string dir, string path) =>
        Out.Field(path == dir ? "." : IsInside(dir, path) ? path[(dir.TrimEnd('/').Length + 1)..] : path);
}
