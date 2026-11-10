using System.Diagnostics;
using System.Security.Cryptography;

namespace Pen;

/// <summary>
/// How ending a session went: whether its .git check finished, and the dir to
/// give a signing reminder for (null if the check failed or moved something).
/// </summary>
internal readonly record struct SessionEndResult(bool CheckFinished, string? ReminderDir);

/// <summary>Session lifecycle: prepare protections, run the agent, stop writers and check Git.</summary>
internal sealed class Session(HostEnv host, Project project, Config user, Config projectConfig)
{
    private const UnixFileMode Dir700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static readonly TimeSpan KeepEndedSessions = TimeSpan.FromDays(7);

    private string RuntimeDir => host.RuntimeDir(project.Key);

    /// <param name="docker">with Docker-in-Docker (pen --docker, or docker = true in .pen/config.toml)</param>
    /// <param name="ports">to forward from the host (pen --port and ports in .pen/config.toml, merged)</param>
    /// <param name="answers">where approvals of .pen/ come from (default: this process's stdin)</param>
    /// <param name="flagTools">from pen --tool, which need no approval</param>
    /// <param name="recipes">where tools come from (default: the embedded recipes)</param>
    public int Run(IReadOnlyList<string> command, bool docker = false, IReadOnlyList<Port>? ports = null, Answers? answers = null, IReadOnlyList<ToolSpec>? flagTools = null, RecipeCatalog? recipes = null)
    {
        var catalog = recipes ?? RecipeCatalog.Embedded;
        // Every session holds project.lock and its dir's worktree lock
        // shared; pen reset holds the first exclusive, pen sign the second.
        EnsureRuntimeDir(host, project);
        using var locks = Sessions.TakeForSession(RuntimeDir, project);

        // A check that didn't finish vouches for nothing: starting now would take
        // the module repos there as this session's baseline, and its end would
        // run git in the directory just warned about.
        if (!FinishDeadSessions(project.Key, RuntimeDir))
            throw new PenException("not starting: deal with the warning above first");

        RepoGuard.CheckAtStart(project.Dir);
        // Tools that can't go together stop the session, whatever's approved.
        var tools = ToolSpec.Merge(catalog, user.Tools, projectConfig.Tools, flagTools ?? []);
        // Before anything below uses a setting from .pen/.
        Trust.RequireForSession(host, project, user, projectConfig, answers ?? Answers.FromProcess(), flagTools, catalog);
        var mounts = RepoGuard.PrepareMounts(project.Dir, host.Home);
        // What the session-end check takes as the submodules' own (PrepareMounts protects them).
        var modules = project.IsRepo ? RepoScan.FindModuleRepos(project.CommonGitDir!) : [];
        Image.EnsureCurrent();
        var toolsImage = ToolsImage.Ensure(tools, catalog);
        var image = Image.EnsureProjectLayer(host, project, baseImage: toolsImage) ?? toolsImage;
        // Under rootless Docker, the host user is container root: the agent
        // runs as UID 0 (with no capabilities) on a read-only root filesystem.
        var rootless = Docker.IsRootless();
        var owner = GitHub.ProjectOwner(project, projectConfig);
        var token = GitHub.TokenFor(owner, user, RuntimeDir, host);
        var toolDomains = ToolSpec.DomainsOf(tools, catalog);
        var allowlist = Allowlist.Merge(Allowlist.ParseList(Embedded.Text("proxy/allowlist.txt")), user.Allow, projectConfig.Allow, toolDomains);

        // One docker session per project: two daemons can't share the dind
        // volume. A dind still using it (an earlier session's teardown failed)
        // would be corrupted by a second, so that's refused too.
        using var dindLock = docker ? TakeDindLock() : null;

        // Ports already in use on the host: a --port is refused, a config
        // port skipped. (One taken between here and compose run fails it.)
        var forwarded = Port.Choose(ports ?? [], Port.IsTakenOnHost, Out.Line);

        foreach (var volume in docker ? ["home", "nuget", "dind"] : new[] { "home", "nuget" })
        {
            var r = Proc.Capture("docker", ["volume", "create", $"{project.Key}_{volume}"]);
            if (r.ExitStatus.ExitCode != 0)
                throw new PenException($"can't create volume {project.Key}_{volume}: {r.StandardError.Trim()}");
        }

        // From here on Ctrl-C belongs to the agent, and a hangup still ends the session cleanly.
        using var signals = new SessionSignals();

        // Past the refusals, so a session that won't start doesn't warn, and with
        // signals held, so Ctrl-C can't end Pen between the probe's create and delete.
        RepoGuard.WarnIfCaseMayFold(project, Out.Line);
        HooksPath.WarnIfWritable(project, host, Out.Line);

        // compose.yaml goes last:
        // its presence is what marks a session as open.
        var session = CreateSessionDir();
        using var sessionLock = FileLock.TryTake(session.LockFile, exclusive: true)
            ?? throw new InvalidOperationException("new session's lock is taken");
        ProtectedFiles.Save(session, mounts, project.Dir, Sessions.Running(project.Key, RuntimeDir));
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PROJECT_DIR"] = project.Dir,
            ["PEN_PROJECT"] = project.Key,
            ["PEN_UID"] = rootless ? "0" : host.Uid.ToString(),
            ["PEN_GID"] = rootless ? "0" : host.Gid.ToString(),
            ["NUGET_PACKAGES"] = host.NuGetPackages,
            ["PEN_IMAGE"] = image,
            ["PEN_SEED"] = host.SeedDir,
        };
        File.WriteAllLines(session.EnvFile, env.Select(kv => $"{kv.Key}={kv.Value}"));
        if (project.IsRepo)
        {
            File.WriteAllText(session.StartedWithGit, project.CommonGitDir + "\n");
            session.WriteModules(modules);
        }
        if (docker) File.WriteAllText(session.DockerMarker, "");
        session.WritePorts(forwarded);
        session.WriteTools(tools, toolDomains);
        session.WriteCommand(command);
        session.WriteStarted(DateTime.UtcNow);
        File.WriteAllText(session.File("squid.conf"), Embedded.Text("proxy/squid.conf"));
        File.WriteAllText(session.File("allowlist.txt"), Allowlist.Render(allowlist));
        File.WriteAllText(session.OverrideFile, SessionCompose.Override(mounts, readOnlyRoot: rootless,
            dindContainer: docker ? session.DindContainer : null,
            ports: new PortForward(forwarded, session.PortsContainer, session.Container)));
        File.WriteAllText(session.ProxyLog, "");
        // Squid's own user writes the log on Linux; the session dir itself is 700.
        File.SetUnixFileMode(session.ProxyLog, (UnixFileMode)0b110_110_110);
        File.WriteAllText(session.ComposeFile, Embedded.Text("compose.yaml"));

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        if (token != null) secrets["GH_TOKEN"] = token;
        foreach (var (k, v) in user.Env) secrets[k] = v;

        var runArgs = new List<string> { "run", "--rm", "--name", session.Container };
        if (!Native.IsATty(0) || !Native.IsATty(1)) runArgs.Add("-T");
        foreach (var name in secrets.Keys) runArgs.AddRange(["-e", name]); // by name only: values never on a command line
        runArgs.Add("agent");
        runArgs.AddRange(command);

        int exitCode;
        var psi = Proc.Info("docker", session.ComposeArgs([.. runArgs]), env);
        foreach (var (k, v) in secrets) psi.Environment[k] = v;
        ScrubComposeEnv(psi);
        try
        {
            if (signals.Stopping) throw new PenException("stopped before the session started");
            using var child = Proc.Start(psi);
            signals.Container = session.Container;
            signals.Child = child;
            using var trimmer = ProxyLogTrimmer.Start(session, env);
            exitCode = Proc.Code(child.WaitForExitStatus());
        }
        catch (PenException e)
        {
            Out.Line(e.Message);
            exitCode = 1;
        }
        finally
        {
            signals.Child = null;
        }

        EndAndRemind(session);
        return exitCode;
    }

    private FileLock TakeDindLock()
    {
        var l = FileLock.TryTake(System.IO.Path.Combine(RuntimeDir, "dind.lock"), exclusive: true)
            ?? throw new PenException("another --docker session is running in this project, and only one can run at a time (they'd share its Docker volume): exit it, or start this one without --docker");
        var volume = $"{project.Key}_dind";
        var stale = Docker.ContainersUsing(volume);
        if (stale.Count > 0)
        {
            l.Dispose();
            throw new PenException($"{string.Join(", ", stale)} still uses {volume}, left over from an earlier session: remove it with docker rm -f {string.Join(' ', stale)}, then rerun");
        }
        return l;
    }

    /// <summary>Host env that would change what compose loads or starts.</summary>
    internal static void ScrubComposeEnv(ProcessStartInfo psi)
    {
        foreach (var name in new[] { "COMPOSE_FILE", "COMPOSE_PROJECT_NAME", "COMPOSE_PROFILES", "COMPOSE_ENV_FILES", "COMPOSE_PATH_SEPARATOR" })
            psi.Environment.Remove(name);
    }

    /// <summary>
    /// Creates the project's runtime dir and records the main checkout's real
    /// path in its project-dir file, so pen prune can tell which keys belong to
    /// projects that were moved or deleted. Every command that resolves a
    /// project calls this, so active projects fill it in over time.
    /// </summary>
    internal static void EnsureRuntimeDir(HostEnv host, Project project)
    {
        var runtimeDir = host.RuntimeDir(project.Key);
        Directory.CreateDirectory(host.StateRoot, Dir700);
        Directory.CreateDirectory(runtimeDir, Dir700);
        Directory.CreateDirectory(System.IO.Path.Combine(runtimeDir, "sessions"), Dir700);
        WriteProjectDir(runtimeDir, project.RepoDir);
    }

    internal const string ProjectDirFile = "project-dir";

    /// <summary>Writes <paramref name="repoDir"/> to project-dir unless it's already there, via temp file + rename.</summary>
    internal static void WriteProjectDir(string runtimeDir, string repoDir)
    {
        var path = System.IO.Path.Combine(runtimeDir, ProjectDirFile);
        var content = repoDir + "\n";
        try { if (File.ReadAllText(path) == content) return; }
        catch (FileNotFoundException) { }
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private SessionDir CreateSessionDir()
    {
        var sessions = System.IO.Path.Combine(RuntimeDir, "sessions");
        while (true)
        {
            var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            var path = System.IO.Path.Combine(sessions, id);
            if (Directory.Exists(path)) continue;
            Directory.CreateDirectory(path, Dir700);
            return new SessionDir(project.Key, path);
        }
    }

    /// <summary>
    /// The end path, for a live session or a dead one: the agent container
    /// goes first (after a hangup compose may have left it running), and in a
    /// docker session dind with it, since containers inside dind can write to
    /// the project until it's gone. Then the stack, then the session's files
    /// except proxy.log. The caller holds the session's lock and releases it after.
    /// A check that can't finish doesn't stop any of this: it warns, and the
    /// session ends. End never runs git itself: whether that's safe can depend
    /// on other sessions' checks in the same repo, so the caller decides.
    /// </summary>
    internal static SessionEndResult End(SessionDir session)
    {
        var env = session.ReadEnv();
        var failures = new List<string>();
        foreach (var container in File.Exists(session.DockerMarker)
                     ? new[] { session.Container, session.DindContainer } : [session.Container])
        {
            try { Docker.RemoveWriter(container); }
            catch (PenException e) { failures.Add(e.Message); }
        }
        if (failures.Count > 0)
            throw new PenException($"{string.Join("\n", failures)}\nsession {session.Id} is retained for retry by the next pen command here; Git has not been checked");

        // The .git check, now that the agent is gone and before anything runs git.
        var result = new SessionEndResult(true, null);
        bool keepFiles = false;
        IReadOnlyList<string>? restoreBlocked = null;
        if (env.TryGetValue("PROJECT_DIR", out var projectDir))
        {
            var check = GitCheck.RunAtSessionEnd(projectDir, session);
            GitCheck.Report(projectDir, check);
            result = new(check.Failure == null, check.Failure == null && check.Moved.Count == 0 && check.Restored is not { Count: > 0 } ? projectDir : null);
            keepFiles = check.RestoreIncomplete;
            restoreBlocked = check.RestoreBlocked;
        }
        else if (File.Exists(session.ProtectedManifest))
        {
            Out.Raw(GitCheck.NotCheckedWarning(session.Path, "the session's record of its project is damaged, so its protected files weren't compared"));
            result = new(false, null);
            keepFiles = true;
        }
        if (keepFiles)
            Out.Raw(GitCheck.KeptCopiesAdvice(session, restoreBlocked));

        // What the session ended up allowing (pen allow may have added to it),
        // so the blocked line doesn't suggest a domain that's allowed by now.
        var allowedFile = session.File("allowlist.txt");
        var allowed = File.Exists(allowedFile) ? Allowlist.ParseList(File.ReadAllText(allowedFile)).ToHashSet(StringComparer.Ordinal) : [];

        if (env.Count > 0)
        {
            var psi = Proc.Info("docker", session.ComposeArgs("down", "-t", "1", "--remove-orphans"), env);
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            ScrubComposeEnv(psi);
            var down = Process.RunAndCaptureText(psi);
            if (down.ExitStatus.ExitCode != 0)
                Out.Line($"stopping session {session.Id}'s stack failed: {down.StandardError.Trim()} (it will be retried by the next pen command here)");
            else if (!keepFiles)
                DeleteSessionFiles(session);
        }
        else if (!keepFiles)
        {
            DeleteSessionFiles(session);
        }

        if (File.Exists(session.ProxyLog) || File.Exists(session.PreviousProxyLog))
        {
            var blocked = Allowlist.BlockedHosts(session.ProxyLogLines()).Where(h => !Allowlist.IsAllowed(allowed, h)).ToList();
            var line = Allowlist.BlockedLine(blocked);
            if (line != null) Out.Line(line);
        }
        return result;
    }

    /// <summary>The end path for a session that's the only one this command is ending.</summary>
    internal static SessionEndResult EndAndRemind(SessionDir session)
    {
        var result = End(session);
        Remind(result);
        return result;
    }

    /// <summary>Runs Pen's own git call (the unsigned-commit count), so only once no check has failed.</summary>
    private static void Remind(SessionEndResult result)
    {
        if (result.ReminderDir == null) return;
        var reminder = Sign.Reminder(result.ReminderDir);
        if (reminder != null) Out.Line(reminder);
    }

    private static void DeleteSessionFiles(SessionDir session)
    {
        foreach (var f in Directory.EnumerateFileSystemEntries(session.Path))
        {
            if (f == session.ProxyLog || f == session.PreviousProxyLog || f == session.LockFile || f == session.ComposeFile || f == session.RestoredManifest) continue;
            if (Directory.Exists(f)) Directory.Delete(f, recursive: true); else File.Delete(f);
        }
        // compose.yaml marks the session open, so it goes last but for the lock and
        // the record of what it vouched for: an open session always has that record.
        File.Delete(session.ComposeFile);
        File.Delete(session.RestoredManifest);
        File.Delete(session.LockFile);
    }

    /// <summary>
    /// Runs the end path for every session that still has a compose.yaml but
    /// whose lock nobody holds (its Pen crashed or was killed), and deletes
    /// ended sessions' dirs after <see cref="KeepEndedSessions"/>. Returns false
    /// if one's .git check didn't finish: the caller must not run git then, and
    /// neither does this. These sessions may share a repo, so their reminders
    /// wait until every check has finished.
    /// </summary>
    internal static bool FinishDeadSessions(string projectKey, string runtimeDir)
    {
        var sessions = System.IO.Path.Combine(runtimeDir, "sessions");
        if (!Directory.Exists(sessions)) return true;
        var ended = new List<SessionEndResult>();
        foreach (var path in Directory.EnumerateDirectories(sessions))
        {
            var s = new SessionDir(projectKey, path);
            if (s.IsOpen)
            {
                using var l = FileLock.TryTake(s.LockFile, exclusive: true);
                if (l == null || !s.IsOpen) continue; // running, or ended while we looked
                Out.Line($"ending session {s.Id}, whose pen exited without ending it");
                ended.Add(End(s));
            }
            else if (DateTime.UtcNow - LastActivity(path) > KeepEndedSessions)
            {
                try { Directory.Delete(path, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        if (ended.Any(e => !e.CheckFinished)) return false;
        foreach (var e in ended) Remind(e);
        return true;
    }

    private static DateTime LastActivity(string dir)
    {
        var t = Directory.GetLastWriteTimeUtc(dir);
        var log = System.IO.Path.Combine(dir, "proxy.log");
        if (File.Exists(log)) { var lt = File.GetLastWriteTimeUtc(log); if (lt > t) t = lt; }
        return t;
    }
}
