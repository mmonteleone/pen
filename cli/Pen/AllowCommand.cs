using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pen;

/// <summary>
/// pen allow &lt;domain…&gt; [--global]: adds to allow in config.toml and applies them to
/// running sessions; pen allow: lists recently blocked domains.
/// </summary>
internal static class AllowCommand
{
    public const string ProjectConfigStub = """
        # pen project config (TOML), read-only inside the sandbox. Not for secrets:
        # it's usually committed. Edits apply to the next session, once approved;
        # `pen allow` also applies to running ones.
        #
        # github_owner = "<owner>"   whose GitHub token to use (default: origin's owner)
        # docker = true              Docker-in-Docker for every session

        """;

    /// <summary>
    /// Adds the domains not already allowed to the config.toml at
    /// <paramref name="path"/> (see <see cref="ConfigEdit"/>), creating it from
    /// <paramref name="stub"/> if it's missing. Replaces the file by rename,
    /// keeping its mode. Returns the added domains.
    /// </summary>
    public static List<string> Append(string path, string displayPath, ConfigKind kind, IReadOnlyList<string> domains, string stub)
    {
        var existing = Config.ReadText(path, displayPath);
        bool exists = existing != null;
        var updated = ConfigEdit.AddAllowed(existing ?? stub, displayPath, kind, domains, out var added);
        if (added.Count == 0) return added;

        var mode = exists ? File.GetUnixFileMode(path)
            : kind == ConfigKind.User ? UnixFileMode.UserRead | UnixFileMode.UserWrite
            : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        var tmp = $"{path}.tmp-{Environment.ProcessId}";
        try
        {
            using (var f = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = mode }))
                f.Write(Encoding.UTF8.GetBytes(updated));
            File.SetUnixFileMode(tmp, mode); // in case a stale tmp existed with another mode
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
        return added;
    }

    public static int Add(HostEnv host, IReadOnlyList<string> domains, bool global)
    {
        Project? project = null;
        try { project = Project.Resolve(StartDir.Get(), host); }
        catch (PenException) when (global) { } // --global works from anywhere
        if (project != null)
        {
            Session.EnsureRuntimeDir(host, project);
            Session.FinishDeadSessions(project.Key, host.RuntimeDir(project.Key));
        }

        List<string> added;
        string where;
        if (global)
        {
            where = host.Display(host.UserConfigPath);
            added = Append(host.UserConfigPath, where, ConfigKind.User, domains, stub: "");
        }
        else
        {
            where = ".pen/" + Config.FileName;
            RepoGuard.EnsureDirectory(project!.Dir, project.PenDir);
            RepoGuard.RequireNotLink(project.Dir, project.ProjectConfigPath);
            added = Append(project.ProjectConfigPath, where, ConfigKind.Project, domains, ProjectConfigStub);
            // The user typed them, so they're approved, also if they were already in the file.
            TrustStore.For(host, project).ApproveDomains(domains);
        }

        var skipped = domains.Except(added).ToList();
        if (added.Count > 0) Out.Line($"added to {where}: {string.Join(' ', added)}");
        if (skipped.Count > 0) Out.Line($"already in {where}: {string.Join(' ', skipped)}");

        int applied = 0, failed = 0;
        foreach (var s in Targets(host, project, global))
        {
            if (Apply(host, s)) applied++; else failed++;
        }
        if (applied > 0)
            Out.Line($"applied to {applied} running session{(applied == 1 ? "" : "s")}{(global ? "" : " in this directory")}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The running sessions an added domain applies to: with --global, every
    /// project's; otherwise only those started in this dir, since .pen/ is per
    /// worktree (a session in another worktree of the repo reads its own).
    /// </summary>
    internal static List<SessionDir> Targets(HostEnv host, Project? project, bool global) =>
        global ? Sessions.RunningEverywhere(host)
        : Sessions.RunningIn(project!.Key, host.RuntimeDir(project.Key), project.Dir);

    /// <summary>
    /// Regenerates one running session's allowlist from the current config
    /// files, of its project's only the approved domains, plus the domains of the
    /// tools it started with, and has its Squid reload it. A tools entry added to
    /// .pen/ since isn't in that record, so it adds nothing. Reports a failure, naming
    /// the session.
    /// </summary>
    internal static bool Apply(HostEnv host, SessionDir s)
    {
        try
        {
            var env = s.ReadEnv();
            if (!env.TryGetValue("PROJECT_DIR", out var projectDir))
                throw new PenException("its session.env is missing");
            var user = Config.Load(host.ConfigDir, host.Display(host.ConfigDir), ConfigKind.User);
            var projectConfig = Config.Load(Path.Combine(projectDir, ".pen"), Out.Field(Path.Combine(projectDir, ".pen")), ConfigKind.Project);
            var approved = TrustStore.Load(host.RuntimeDir(s.ProjectKey), projectDir).ApprovedOf(projectConfig.Allow);
            var merged = Allowlist.Merge(Allowlist.ParseList(Embedded.Text("proxy/allowlist.txt")), user.Allow, approved, s.ReadToolDomains());
            // Truncate and write, never rename: the proxy's single-file bind
            // mount pins the inode.
            using (var f = new FileStream(s.File("allowlist.txt"), FileMode.Truncate, FileAccess.Write))
                f.Write(Encoding.UTF8.GetBytes(Allowlist.Render(merged)));

            var psi = Proc.Info("docker", s.ComposeArgs("exec", "-T", "proxy", "squid", "-k", "reconfigure"), env);
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            Session.ScrubComposeEnv(psi);
            var r = Process.RunAndCaptureText(psi);
            if (r.ExitStatus.ExitCode != 0)
                throw new PenException($"squid -k reconfigure failed: {(r.StandardError + r.StandardOutput).Trim()}");
            return true;
        }
        catch (Exception e) when (e is PenException or IOException or UnauthorizedAccessException)
        {
            Out.Line($"couldn't apply it to the running session {s.ComposeProject}: {Out.Describe(e)}");
            return false;
        }
    }

    /// <summary>
    /// pen allow --defaults: the built-in allowlist from its first section on,
    /// keeping the section comments (which say why each domain is there) and
    /// dropping the header about Squid's file format.
    /// </summary>
    internal static string Defaults()
    {
        var lines = Embedded.Text("proxy/allowlist.txt").Split('\n');
        int first = Array.FindIndex(lines, l => l.StartsWith("# ---", StringComparison.Ordinal));
        return string.Join('\n', lines[Math.Max(first, 0)..]).TrimEnd('\n') + "\n";
    }

    /// <summary>A blocked domain and when it was last blocked.</summary>
    internal sealed record Blocked(string Host, DateTimeOffset Last);

    /// <summary>
    /// Denied hosts across log files, newest last-denial first. Lines are in
    /// squid.conf's "pen" format, whose first field is the UTC time.
    /// </summary>
    internal static List<Blocked> BlockedAcross(IEnumerable<IEnumerable<string>> logs)
    {
        var last = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var log in logs)
        {
            foreach (var line in log)
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5 || !f[2].StartsWith("TCP_DENIED", StringComparison.Ordinal)) continue;
                var h = Allowlist.HostOf(f[4]);
                if (h.Length == 0) continue;
                if (!DateTimeOffset.TryParse(f[0], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
                    t = DateTimeOffset.MinValue;
                if (!last.TryGetValue(h, out var seen) || t > seen) last[h] = t;
            }
        }
        return last.Select(kv => new Blocked(kv.Key, kv.Value))
            .OrderByDescending(b => b.Last).ThenBy(b => b.Host, StringComparer.Ordinal).ToList();
    }

    public static int List(HostEnv host, Project project, Config user, Config projectConfig)
    {
        var runtimeDir = host.RuntimeDir(project.Key);
        Session.EnsureRuntimeDir(host, project);
        Session.FinishDeadSessions(project.Key, runtimeDir);

        var logs = Directory.EnumerateDirectories(Path.Combine(runtimeDir, "sessions"))
            .Select(d => new SessionDir(project.Key, d).ProxyLogLines());
        // What a session would get: an unapproved domain in .pen/ stays listed.
        var store = TrustStore.For(host, project);
        var approved = store.ApprovedOf(projectConfig.Allow);
        var toolDomains = ToolSpec.DomainsOf(user.Tools.Concat(store.ApprovedToolsOf(projectConfig.Tools, RecipeCatalog.Embedded)), RecipeCatalog.Embedded);
        var allowed = Allowlist.Merge(Allowlist.ParseList(Embedded.Text("proxy/allowlist.txt")), user.Allow, approved, toolDomains).ToHashSet(StringComparer.Ordinal);
        var blocked = BlockedAcross(logs).Where(b => !Allowlist.IsAllowed(allowed, b.Host)).ToList();
        if (blocked.Count == 0)
        {
            Out.Stdout("nothing blocked in this project's sessions (last 7 days) that isn't allowed now\n");
            return 0;
        }

        // The newest few: the agent decides how many there are.
        var shown = blocked.Take(Allowlist.MaxListed).ToList();
        Out.Stdout("blocked in this project's sessions, newest first:\n");
        foreach (var b in shown)
        {
            var when = b.Last == DateTimeOffset.MinValue ? "" : b.Last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Out.Stdout($"  {Allowlist.ShownHost(b.Host),-40} {when}\n");
        }
        if (blocked.Count > shown.Count) Out.Stdout($"  and {blocked.Count - shown.Count} more, blocked earlier\n");
        var allowable = shown.Select(b => b.Host).Where(Allowlist.IsValidDomain).ToList();
        if (allowable.Count > 0)
            Out.Stdout($"pen allow {string.Join(' ', allowable)}   (add --global for all projects)\n");
        return 0;
    }
}
