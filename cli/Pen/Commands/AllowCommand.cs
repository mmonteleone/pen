using System.Diagnostics;
using System.Globalization;
using System.Text;
using Pen.Configuration;
using Pen.GitProtection;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Sessions;
using Pen.Tools;
using Pen.Trust;

namespace Pen.Commands;

/// <summary>
/// Runs pen allow: adds domains to allow in a config.toml and applies them to running sessions, or lists the
/// recently blocked domains.
/// </summary>
/// <param name="host">The host user's environment.</param>
/// <param name="catalog">The recipes that say which domains a tool brings.</param>
internal sealed class AllowCommand(HostEnv host, RecipeCatalog catalog)
{
    /// <summary>The text of a new .pen/config.toml, which pen allow and pen deny create when there's none.</summary>
    public const string ProjectConfigStub = """
        # pen project config (TOML), read-only inside the sandbox. Not for secrets:
        # it's usually committed. Edits apply to the next session, once approved;
        # `pen allow` and `pen deny` also apply to running ones.
        #
        # github_owner = "<owner>"   whose GitHub token to use (default: origin's owner)
        # docker = true              Docker-in-Docker for every session
        # ports = [5173]             forward localhost:5173 on the host to the sandbox
        # tools = ["go", "rust"]     toolchains, as name or name:version (pen --tool list)

        """;

    /// <summary>Adds <paramref name="domains"/> to allow and applies them to running sessions.</summary>
    /// <param name="domains">The domains to allow (lowercased), or <see cref="Allowlist.AnyDomain"/>.</param>
    /// <param name="global">
    /// Add to the user's config.toml and apply to every project's sessions (--global), rather than to .pen/ and
    /// the sessions started in this directory.
    /// </param>
    /// <returns>0, or 1 when applying to a running session failed; a failure to add throws.</returns>
    public int Run(IReadOnlyList<string> domains, bool global)
    {
        var project = PrepareProject(host, global);
        var addition = AddAllowed(host, project, global, domains);
        WriteAdded(addition, domains);
        WriteGlobHint("allow", addition);
        WarnIfDenied(project, domains);

        return ApplyToRunning(host, project, global) ? 0 : 1;
    }

    /// <summary>Resolves the project in the start directory, and finishes its sessions whose Pen died.</summary>
    /// <returns>The project, or null with <paramref name="global"/> when there isn't one to resolve there.</returns>
    /// <remarks>--global works from anywhere; without it, a failure to resolve throws.</remarks>
    internal static Project? PrepareProject(HostEnv host, bool global)
    {
        Project? project;
        try
        {
            project = Project.Resolve(StartDir.Get(), host);
        }
        catch (PenException) when (global)
        {
            return null;
        }

        Session.EnsureRuntimeDir(host, project);
        Session.FinishDeadSessions(project.Key, host.GetRuntimeDir(project.Key));
        return project;
    }

    /// <summary>
    /// Adds <paramref name="domains"/> to allow, as <see cref="AddToConfig"/> does, approving them for the project
    /// unless <paramref name="global"/>.
    /// </summary>
    /// <returns>The file, as messages name it, and the domains that weren't in its allow yet.</returns>
    /// <remarks>
    /// The user typed them, so they're approved, also if they were already in the file;
    /// <see cref="Allowlist.AnyDomain"/> too.
    /// </remarks>
    internal static ConfigAddition AddAllowed(
        HostEnv host,
        Project? project,
        bool global,
        IReadOnlyList<string> domains)
    {
        var addition = AddToConfig(host, project, global, ConfigEdit.AllowKey, domains);
        if (!global)
            TrustStore.For(host, project!).ApproveDomains(domains);
        return addition;
    }

    /// <summary>
    /// Adds <paramref name="domains"/> to <paramref name="key"/> in the user's config.toml (with
    /// <paramref name="global"/>) or the project's .pen/config.toml, creating that from
    /// <see cref="ProjectConfigStub"/> if it's missing.
    /// </summary>
    /// <returns>The file, as messages name it, and the domains that weren't in its list yet.</returns>
    internal static ConfigAddition AddToConfig(
        HostEnv host,
        Project? project,
        bool global,
        string key,
        IReadOnlyList<string> domains)
    {
        if (global)
        {
            var userPath = host.Display(host.UserConfigPath);
            return new(userPath, Append(host.UserConfigPath, userPath, ConfigKind.User, key, domains, stub: ""));
        }

        var where = ".pen/" + Config.FileName;
        RepoGuard.EnsureDirectory(project!.Dir, project.PenDir);
        RepoGuard.RequireNotLink(project.Dir, project.ProjectConfigPath);
        var added = Append(project.ProjectConfigPath, where, ConfigKind.Project, key, domains, ProjectConfigStub);
        return new(where, added);
    }

    /// <summary>Says which of <paramref name="domains"/> were added, and which were there already.</summary>
    internal static void WriteAdded(ConfigAddition addition, IReadOnlyList<string> domains)
    {
        var skipped = domains.Except(addition.Added).ToList();
        if (addition.Added.Count > 0)
            Out.WriteLine($"added to {addition.Where}: {string.Join(' ', addition.Added)}");
        if (skipped.Count > 0)
            Out.WriteLine($"already in {addition.Where}: {string.Join(' ', skipped)}");
    }

    /// <summary>Warns about each of <paramref name="domains"/> a deny in either config file still covers.</summary>
    /// <remarks>A config file that can't be read now is left out: applying to a running session reports it.</remarks>
    private void WarnIfDenied(Project? project, IReadOnlyList<string> domains)
    {
        var denies = new List<(string Where, List<string> Deny)>();
        try
        {
            denies.Add((host.Display(host.UserConfigPath),
                Config.Load(host.ConfigDir, host.Display(host.ConfigDir), ConfigKind.User).Deny));
            if (project != null)
                denies.Add((".pen/" + Config.FileName, Config.Load(project.PenDir, ".pen", ConfigKind.Project).Deny));
        }
        catch (PenException)
        {
            // Warn from what could be read.
        }

        foreach (var line in FormatDenyWins(denies, domains))
            Out.WriteLine(line);
    }

    /// <summary>Formats the warning for each of <paramref name="domains"/> a deny list covers.</summary>
    /// <param name="denies">Each config file's deny list, with the file as messages name it.</param>
    /// <param name="domains">The domains just allowed.</param>
    /// <returns>One line per covered domain, naming the files whose deny covers it.</returns>
    /// <remarks><see cref="Allowlist.AnyDomain"/> is left out: deny is meant to narrow it.</remarks>
    internal static List<string> FormatDenyWins(
        IReadOnlyList<(string Where, List<string> Deny)> denies,
        IReadOnlyList<string> domains)
    {
        var lines = new List<string>();
        foreach (var domain in domains.Where(domain => domain != Allowlist.AnyDomain))
        {
            var by = denies.Where(file => Allowlist.IsDenied(file.Deny, domain)).Select(file => file.Where).ToList();
            if (by.Count > 0)
                lines.Add($"{domain} is also denied by {string.Join(" and ", by)}; deny wins");
        }
        return lines;
    }

    /// <summary>
    /// Says, when an added argument of pen <paramref name="command"/> is also the name of a file or directory in
    /// the start directory, that the shell may have expanded an unquoted *, and where the names went.
    /// </summary>
    /// <param name="command">The subcommand.</param>
    /// <param name="addition">What was added, and to which file.</param>
    /// <remarks>The domains are still added: a file and a domain can share a name.</remarks>
    internal static void WriteGlobHint(string command, ConfigAddition addition)
    {
        if (TryStartDir() is { } dir && FormatGlobHint(command, dir, addition.Added, addition.Where) is { } hint)
            Out.WriteLine(hint);
    }

    /// <summary>Gets the start directory.</summary>
    /// <returns>The directory, or null when it can't be read (a command that needs it reports that).</returns>
    internal static string? TryStartDir()
    {
        try
        {
            return StartDir.Get();
        }
        catch (PenException)
        {
            return null;
        }
    }

    /// <summary>Formats <see cref="WriteGlobHint"/>'s hint for <paramref name="dir"/>.</summary>
    /// <param name="command">The subcommand.</param>
    /// <param name="dir">The start directory.</param>
    /// <param name="domains">The domains that were added.</param>
    /// <param name="where">The config file they were added to, as messages name it.</param>
    /// <returns>The hint, or null when no argument is named like one, or the directory can't be listed.</returns>
    /// <remarks>Names are compared ignoring case, since the parser lowercased the arguments.</remarks>
    internal static string? FormatGlobHint(
        string command,
        string dir,
        IReadOnlyList<string> domains,
        string where)
    {
        var matches = domains.Where(domain => NamesEntry(dir, domain)).ToList();
        if (matches.Count == 0)
            return null;
        var what = matches.Count == 1 ? "names a file here" : "name files here";
        var were = matches.Count == 1 ? "it was" : "they were";
        return $"{string.Join(' ', matches.Select(Out.Field))} {what}: {GlobAdvice(command)}; " +
            $"{were} added to {where}: remove {(matches.Count == 1 ? "it" : "them")} there if " +
            $"{(matches.Count == 1 ? "it isn't a domain" : "they aren't domains")}";
    }

    /// <summary>Tells whether <paramref name="name"/> names an entry of <paramref name="dir"/>, ignoring case.
    /// </summary>
    /// <returns>False when the directory can't be listed.</returns>
    internal static bool NamesEntry(string dir, string name)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(dir)
                .Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Formats the advice for an argument of pen <paramref name="command"/> named like a file.</summary>
    /// <remarks>
    /// pen deny never takes <see cref="Allowlist.AnyDomain"/>, so its advice doesn't suggest quoting it.
    /// </remarks>
    internal static string GlobAdvice(string command) =>
        "did your shell expand *? " + (command == "deny"
            ? "pen deny takes domains, not '*'"
            : $"quote it: pen {command} '*'");

    /// <summary>Applies the config files to the running sessions a change in them is for.</summary>
    /// <returns>Whether every one of them took it; each failure is reported, naming the session.</returns>
    internal static bool ApplyToRunning(HostEnv host, Project? project, bool global)
    {
        int applied = 0;
        int failed = 0;
        foreach (var session in FindTargets(host, project, global))
        {
            if (Apply(host, session))
                applied++;
            else
                failed++;
        }
        if (applied > 0)
        {
            Out.WriteLine($"applied to {applied} running session{(applied == 1 ? "" : "s")}"
                + $"{(global ? "" : " in this directory")}");
        }

        return failed == 0;
    }

    /// <summary>
    /// Adds the domains not already in <paramref name="key"/> to the config.toml at <paramref name="path"/>, creating
    /// it from <paramref name="stub"/> if it's missing.
    /// </summary>
    /// <param name="path">The config.toml to change.</param>
    /// <param name="displayPath">Names the file in messages.</param>
    /// <param name="kind">Which keys the file may hold.</param>
    /// <param name="key">The list to add to: allow or deny.</param>
    /// <param name="domains">The domains to add.</param>
    /// <param name="stub">The text a missing file starts from.</param>
    /// <returns>The domains added, lowercased, in order; none when the file is left alone.</returns>
    /// <remarks>
    /// The edit is <see cref="ConfigEdit.AddToList"/>'s, and the file is replaced by rename, keeping its mode.
    /// </remarks>
    public static IReadOnlyList<string> Append(
        string path,
        string displayPath,
        ConfigKind kind,
        string key,
        IReadOnlyList<string> domains,
        string stub)
    {
        var existing = Config.ReadText(path, displayPath);
        bool exists = existing != null;
        var edit = ConfigEdit.AddToList(existing ?? stub, displayPath, kind, key, domains);
        if (edit.Added.Count == 0)
            return edit.Added;

        ReplaceFile(path, edit.Text, ChooseMode(path, exists, kind));
        return edit.Added;
    }

    /// <summary>Chooses the mode for the config.toml at <paramref name="path"/>.</summary>
    /// <returns>
    /// The file's own mode when it <paramref name="exists"/>; otherwise 600 for the user's (it may hold secrets)
    /// and 644 for a project's.
    /// </returns>
    private static UnixFileMode ChooseMode(string path, bool exists, ConfigKind kind)
    {
        if (exists)
            return File.GetUnixFileMode(path);
        if (kind == ConfigKind.User)
            return UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    }

    /// <summary>
    /// Replaces the file at <paramref name="path"/> with <paramref name="text"/>, in mode <paramref name="mode"/>,
    /// by writing a temp file and renaming it over.
    /// </summary>
    private static void ReplaceFile(string path, string text, UnixFileMode mode)
    {
        var tmp = $"{path}.tmp-{Environment.ProcessId}";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = mode,
            };
            using (var file = new FileStream(tmp, options))
            {
                file.Write(Encoding.UTF8.GetBytes(text));
            }

            File.SetUnixFileMode(tmp, mode); // in case a stale tmp existed with another mode
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Finds the running sessions an added domain (allowed or denied) applies to.</summary>
    /// <returns>
    /// With <paramref name="global"/>, every project's; otherwise only those started in
    /// <paramref name="project"/>'s dir.
    /// </returns>
    /// <remarks>.pen/ is per worktree: a session in another worktree of the repo reads its own.</remarks>
    internal static List<SessionDir> FindTargets(HostEnv host, Project? project, bool global) =>
        global
            ? SessionRegistry.FindRunningEverywhere(host)
            : SessionRegistry.FindRunningIn(project!.Key, host.GetRuntimeDir(project.Key), project.Dir);

    /// <summary>
    /// Regenerates one running session's ACL files from the current config files and has its Squid reload them.
    /// </summary>
    /// <returns>Whether it worked; a failure is reported, naming the session.</returns>
    /// <remarks>
    /// <para>Of its project's config, only the domains the trust gate lets through count; the tools' domains are the
    /// ones it started with (tool-domains), so a tools entry added to .pen/ since adds nothing. The deny lists count
    /// in full.</para>
    /// <para>A session started by a Pen without deny gets only its allowlist, never with
    /// <see cref="Allowlist.AnyDomain"/> (see <see cref="WriteAclFiles"/>).</para>
    /// </remarks>
    internal static bool Apply(HostEnv host, SessionDir session)
    {
        try
        {
            var env = session.ReadEnv();
            if (!env.TryGetValue("PROJECT_DIR", out var projectDir))
                throw new PenException("its session.env is missing");
            var pending = WriteAclFiles(session, MergeEgress(host, session, projectDir));

            var startInfo = Proc.CreateStartInfo("docker",
                session.BuildComposeArgs("exec", "-T", "proxy", "squid", "-k", "reconfigure"), env);
            startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;
            Session.ScrubComposeEnv(startInfo);
            var result = Process.RunAndCaptureText(startInfo);
            if (result.ExitStatus.ExitCode != 0)
            {
                throw new PenException(
                    $"squid -k reconfigure failed: {(result.StandardError + result.StandardOutput).Trim()}");
            }
            if (pending != null)
            {
                Out.WriteLine($"the running session {session.ComposeProject} was started by an older Pen: " +
                    $"{pending} only to sessions started from now on");
            }
            return true;
        }
        catch (Exception error) when (error is PenException or IOException or UnauthorizedAccessException)
        {
            Out.WriteLine($"couldn't apply it to the running session {session.ComposeProject}: {Out.Describe(error)}");
            return false;
        }
    }

    /// <summary>What a running session gets now, merged from the config files.</summary>
    /// <param name="Allow">The merged allow list; just <see cref="Allowlist.AnyDomain"/> when a source has it.</param>
    /// <param name="NamedAllow">The merged allow list without <see cref="Allowlist.AnyDomain"/>.</param>
    /// <param name="Deny">The merged deny list.</param>
    internal sealed record Egress(List<string> Allow, List<string> NamedAllow, List<string> Deny);

    /// <summary>
    /// Merges what <paramref name="session"/>, started in <paramref name="projectDir"/>, gets now.
    /// </summary>
    /// <returns>
    /// The allow lists from the defaults, the user's allow, the project's allow as
    /// <see cref="TrustGate.SelectAllowedDomains"/> takes it (as session start does) and the session's tools'
    /// domains, and the deny list from the user's and the project's deny.
    /// </returns>
    private static Egress MergeEgress(HostEnv host, SessionDir session, string projectDir)
    {
        var user = Config.Load(host.ConfigDir, host.Display(host.ConfigDir), ConfigKind.User);
        var projectConfig = Config.Load(Path.Combine(projectDir, ".pen"),
            Out.Field(Path.Combine(projectDir, ".pen")), ConfigKind.Project);
        var approved = TrustGate.SelectAllowedDomains(user, projectConfig,
            TrustStore.Load(host.GetRuntimeDir(session.ProjectKey), projectDir));
        IEnumerable<string>[] sources = [Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt")), user.Allow,
            approved, session.ReadToolDomains()];
        return new(Allowlist.Merge(sources), Allowlist.MergeNamed(sources), Allowlist.MergeDeny(user, projectConfig));
    }

    /// <summary>Writes <paramref name="egress"/> into <paramref name="session"/>'s ACL files.</summary>
    /// <returns>
    /// What its proxy can't take (sessions started from now on get it), worded for the notice; null when it took
    /// everything.
    /// </returns>
    /// <remarks>
    /// <para>Each file is truncated and written, never renamed: the proxy's single-file bind mounts pin the inodes.
    /// A file that's gone fails the write rather than being made, since the proxy wouldn't see a new one.</para>
    /// <para>A session without a denylist.txt was started by a Pen whose squid.conf has no deny or open ACL, so only
    /// its allowlist.txt is written, from the allow list without <see cref="Allowlist.AnyDomain"/>: a literal "*"
    /// there would be a domain to squid, and leaving the file as it was would keep domains taken out of the config
    /// since. The named domains are what the config grants by name now, so it never gets more than that.</para>
    /// </remarks>
    internal static string? WriteAclFiles(SessionDir session, Egress egress)
    {
        if (!session.HasDenyAndOpen)
        {
            Overwrite(session.AllowlistFile, Allowlist.Render(egress.NamedAllow));
            bool open = egress.Allow.Contains(Allowlist.AnyDomain);
            return (egress.Deny.Count > 0, open) switch
            {
                (true, true) => "deny and allow = [\"*\"] apply",
                (true, false) => "deny applies",
                (false, true) => "allow = [\"*\"] applies",
                _ => null,
            };
        }

        var acls = Allowlist.RenderFiles(egress.NamedAllow, egress.Allow.Contains(Allowlist.AnyDomain), egress.Deny);
        Overwrite(session.DenylistFile, acls.DenylistText);
        Overwrite(session.AllowlistFile, acls.AllowlistText);
        Overwrite(session.OpenEgressFile, acls.OpenEgressText);
        return null;
    }

    /// <summary>Truncates the existing file at <paramref name="path"/> and writes <paramref name="text"/>.</summary>
    private static void Overwrite(string path, string text)
    {
        using var file = new FileStream(path, FileMode.Truncate, FileAccess.Write);
        file.Write(Encoding.UTF8.GetBytes(text));
    }

    /// <summary>Formats pen allow --defaults: the built-in allowlist from its first section on.</summary>
    /// <remarks>
    /// The section comments stay, since they say why each domain is there; the header about Squid's file format
    /// goes.
    /// </remarks>
    internal static string FormatDefaults()
    {
        var lines = Embedded.ReadText("proxy/allowlist.txt").Split('\n');
        int first = Array.FindIndex(lines, line => line.StartsWith("# ---", StringComparison.Ordinal));
        return string.Join('\n', lines[Math.Max(first, 0)..]).TrimEnd('\n') + "\n";
    }

    /// <summary>A blocked domain and when it was last blocked.</summary>
    /// <param name="Host">The host the proxy denied.</param>
    /// <param name="Last">
    /// When it last denied it; <see cref="DateTimeOffset.MinValue"/> when that can't be read.
    /// </param>
    internal sealed record Blocked(string Host, DateTimeOffset Last);

    /// <summary>Finds the denied hosts across <paramref name="logs"/>.</summary>
    /// <returns>Each host once, newest last denial first.</returns>
    /// <remarks>Lines are in squid.conf's "pen" format, whose first field is the UTC time.</remarks>
    internal static List<Blocked> FindBlockedAcross(IEnumerable<IEnumerable<string>> logs)
    {
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var line in logs.SelectMany(log => log))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5 || !fields[2].StartsWith("TCP_DENIED", StringComparison.Ordinal))
                continue;
            var blockedHost = Allowlist.ParseHost(fields[4]);
            if (blockedHost.Length == 0)
                continue;
            if (!DateTimeOffset.TryParse(fields[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
                    out var time))
            {
                time = DateTimeOffset.MinValue;
            }

            if (!last.TryGetValue(blockedHost, out var seen) || time > seen)
                last[blockedHost] = time;
        }
        return last.Select(entry => new Blocked(entry.Key, entry.Value))
            .OrderByDescending(blocked => blocked.Last)
            .ThenBy(blocked => blocked.Host, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Runs pen allow without domains: lists what the project's sessions had blocked and isn't allowed now, apart
    /// from it what nothing pen allow adds would let through, and what deny covers now.
    /// </summary>
    /// <param name="context">The project and both configs, which say what a session would be allowed.</param>
    /// <returns>0.</returns>
    public int ListBlocked(ProjectContext context)
    {
        var project = context.Project;
        var runtimeDir = host.GetRuntimeDir(project.Key);
        Session.EnsureRuntimeDir(host, project);
        Session.FinishDeadSessions(project.Key, runtimeDir);

        var logs = Directory.EnumerateDirectories(Path.Combine(runtimeDir, "sessions"))
            .Select(dir => new SessionDir(project.Key, dir).ReadProxyLogLines());
        var (named, open, denied) = MergeAllowedNow(context);
        // Grouped as the session-end lines are: deny beats every allow, "*" included, and in open egress a name it
        // blocked anyway (one resolving privately, say) is listed apart, with no pen allow line.
        var groups = FindBlockedAcross(logs)
            .ToLookup(entry => Allowlist.ClassifyBlocked(entry.Host, named, open, denied));
        var blocked = groups[BlockedAs.NotAllowed].ToList();
        var unreachable = groups[BlockedAs.Unreachable].ToList();
        var deniedNow = groups[BlockedAs.Denied].ToList();
        if (blocked.Count == 0 && unreachable.Count == 0 && deniedNow.Count == 0)
        {
            Out.WriteStdout("nothing blocked in this project's sessions (last 7 days) that isn't allowed now\n");
            return 0;
        }

        if (blocked.Count > 0)
            WriteBlocked(blocked);
        if (unreachable.Count > 0)
        {
            WriteHosts($"blocked in this project's sessions ({Allowlist.DescribeUnreachable(open)}), newest first:",
                unreachable);
        }
        if (deniedNow.Count > 0)
            WriteDenied(deniedNow);
        return 0;
    }

    /// <summary>Merges what a session started now would be allowed and denied.</summary>
    /// <returns>
    /// What the merged allow list allows by name, whether it has <see cref="Allowlist.AnyDomain"/>, and the merged
    /// deny list.
    /// </returns>
    /// <remarks>
    /// Only approved domains and tools from .pen/ count, so an unapproved domain there stays listed. Deny needs no
    /// approval, so all of it counts.
    /// </remarks>
    private (HashSet<string> Named, bool Open, HashSet<string> Denied) MergeAllowedNow(ProjectContext context)
    {
        var store = TrustStore.For(host, context.Project);
        var approved = TrustGate.SelectAllowedDomains(context.User, context.ProjectConfig, store);
        var toolDomains = ToolSpec.ListDomains(
            context.User.Tools.Concat(store.SelectApprovedTools(context.ProjectConfig.Tools, catalog)),
            catalog);
        IEnumerable<string>[] sources = [Allowlist.ParseList(Embedded.ReadText("proxy/allowlist.txt")),
            context.User.Allow, approved, toolDomains];
        var named = Allowlist.MergeNamed(sources).ToHashSet(StringComparer.Ordinal);
        bool open = Allowlist.Merge(sources).Contains(Allowlist.AnyDomain);
        var denied = Allowlist.MergeDeny(context.User, context.ProjectConfig).ToHashSet(StringComparer.Ordinal);
        return (named, open, denied);
    }

    /// <summary>Writes the newest of <paramref name="blocked"/> to stdout, and the pen allow line for them.</summary>
    /// <remarks>Only the newest few: the agent decides how many there are.</remarks>
    private static void WriteBlocked(List<Blocked> blocked)
    {
        var shown = WriteHosts("blocked in this project's sessions, newest first:", blocked);
        var allowable = shown.Select(entry => entry.Host).Where(Allowlist.IsValidDomain).ToList();
        if (allowable.Count > 0)
            Out.WriteStdout($"pen allow {string.Join(' ', allowable)}   (add --global for all projects)\n");
    }

    /// <summary>Writes the newest of <paramref name="denied"/>, which deny covers now, to stdout.</summary>
    /// <remarks>No pen allow line: deny beats every allow, so it wouldn't let them through.</remarks>
    private static void WriteDenied(List<Blocked> denied) =>
        WriteHosts("denied by config, blocked in this project's sessions, newest first:", denied);

    /// <summary>Writes <paramref name="heading"/> and the newest of <paramref name="hosts"/> to stdout.</summary>
    /// <returns>The ones shown.</returns>
    /// <remarks>Only the newest few: the agent decides how many there are.</remarks>
    private static List<Blocked> WriteHosts(string heading, List<Blocked> hosts)
    {
        var shown = hosts.Take(Allowlist.MaxListed).ToList();
        Out.WriteStdout(heading + "\n");
        foreach (var entry in shown)
        {
            var when = entry.Last == DateTimeOffset.MinValue
                ? ""
                : entry.Last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Out.WriteStdout($"  {Allowlist.FormatShownHost(entry.Host),-40} {when}\n");
        }
        if (hosts.Count > shown.Count)
            Out.WriteStdout($"  and {hosts.Count - shown.Count} more, blocked earlier\n");
        return shown;
    }
}

/// <summary>What <see cref="AllowCommand.AddToConfig"/> changed.</summary>
/// <param name="Where">The config.toml, as messages name it.</param>
/// <param name="Added">The domains that weren't in its list yet, lowercased, in order.</param>
internal sealed record ConfigAddition(string Where, IReadOnlyList<string> Added);
