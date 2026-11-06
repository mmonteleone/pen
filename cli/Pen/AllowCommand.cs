using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pen;

/// <summary>
/// pen allow &lt;domain…&gt; [--global]: adds to allow in config.toml and applies them to
/// running sessions; pen allow: lists recently blocked domains (see "Network
/// egress" in PLAN.md).
/// </summary>
internal static class AllowCommand
{
    public const string ProjectConfigStub = """
        # pen project config (TOML), read-only inside the sandbox. Not for secrets:
        # it's usually committed. Edits apply to the next session; `pen allow` also
        # applies to running ones.
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
        bool exists = File.Exists(path);
        var text = exists ? File.ReadAllText(path) : stub;
        var updated = ConfigEdit.AddAllowed(text, displayPath, kind, domains, out var added);
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
        try { project = Project.Resolve(Environment.CurrentDirectory, host); }
        catch (PenException) when (global) { } // --global works from anywhere
        if (project != null)
        {
            Session.EnsureRuntimeDir(host, project.Key);
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
        }

        var skipped = domains.Except(added).ToList();
        if (added.Count > 0) Out.Line($"added to {where}: {string.Join(' ', added)}");
        if (skipped.Count > 0) Out.Line($"already in {where}: {string.Join(' ', skipped)}");

        if (added.Count == 0) return 0;
        var sessions = global ? Sessions.RunningEverywhere(host) : Sessions.Running(project!.Key, host.RuntimeDir(project.Key));
        int applied = 0, failed = 0;
        foreach (var s in sessions)
        {
            if (Apply(host, s)) applied++; else failed++;
        }
        if (applied > 0)
            Out.Line($"applied to {applied} running session{(applied == 1 ? "" : "s")}{(global ? "" : " of this project")}");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Regenerates one running session's allowlist from the current config
    /// files and has its Squid reload it. Reports a failure, naming the session.
    /// </summary>
    internal static bool Apply(HostEnv host, SessionDir s)
    {
        try
        {
            var env = s.ReadEnv();
            if (!env.TryGetValue("PROJECT_DIR", out var projectDir))
                throw new PenException("its session.env is missing");
            var user = Config.Load(host.ConfigDir, host.Display(host.ConfigDir), ConfigKind.User);
            var projectConfig = Config.Load(Path.Combine(projectDir, ".pen"), Path.Combine(projectDir, ".pen"), ConfigKind.Project);
            var merged = Allowlist.Merge(Allowlist.ParseList(Embedded.Text("proxy/allowlist.txt")), user.Allow, projectConfig.Allow);
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
            Out.Line($"couldn't apply it to the running session {s.ComposeProject}: {e.Message}");
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
        Session.EnsureRuntimeDir(host, project.Key);
        Session.FinishDeadSessions(project.Key, runtimeDir);

        var logs = Directory.EnumerateDirectories(Path.Combine(runtimeDir, "sessions"))
            .Select(d => Path.Combine(d, "proxy.log"))
            .Where(File.Exists)
            .Select(p => File.ReadLines(p));
        var allowed = Allowlist.Merge(Allowlist.ParseList(Embedded.Text("proxy/allowlist.txt")), user.Allow, projectConfig.Allow);
        var blocked = BlockedAcross(logs).Where(b => !Allowlist.IsAllowed(allowed, b.Host)).ToList();
        if (blocked.Count == 0)
        {
            Console.Out.WriteLine("nothing blocked in this project's sessions (last 7 days) that isn't allowed now");
            return 0;
        }

        Console.Out.WriteLine("blocked in this project's sessions, newest first:");
        foreach (var b in blocked)
        {
            var when = b.Last == DateTimeOffset.MinValue ? "" : b.Last.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            Console.Out.WriteLine($"  {b.Host,-40} {when}");
        }
        var allowable = blocked.Select(b => b.Host).Where(Allowlist.IsValidDomain).ToList();
        if (allowable.Count > 0)
            Console.Out.WriteLine($"pen allow {string.Join(' ', allowable)}   (add --global for all projects)");
        return 0;
    }
}
