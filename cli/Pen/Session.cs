using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Pen;

/// <summary>
/// One session's directory under the project's runtime dir: its lock, the
/// generated compose.yaml/squid.conf/allowlist, the env compose needs, and the
/// proxy log, which outlives the session.
/// </summary>
internal sealed class SessionDir(string projectKey, string path)
{
    public string Path { get; } = path;
    public string Id { get; } = System.IO.Path.GetFileName(path);
    public string ComposeProject => $"{projectKey}-{Id}";
    public string Container => $"{ComposeProject}-agent";
    public string DindContainer => $"{ComposeProject}-dind";

    public string ComposeFile => File("compose.yaml");
    public string MountsFile => File("mounts.yaml");
    public string ProxyLog => File("proxy.log");
    public string LockFile => File("session.lock");
    public string EnvFile => File("session.env");
    /// <summary>Present when the session started in a repo; holds its common git dir (empty from older pens: PROJECT_DIR/.git).</summary>
    public string StartedWithGit => File("started-with-git");
    /// <summary>The command run in the sandbox, each argument NUL-terminated (empty: a shell), for pen ps.</summary>
    public string CommandFile => File("command");
    /// <summary>When the session started, in UTC, for pen ps.</summary>
    public string StartedFile => File("started");
    /// <summary>Marks a docker session: its compose calls all pass --profile docker.</summary>
    public string DockerMarker => File("docker");

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>A session whose files are still there has a stack that may be up.</summary>
    public bool IsOpen => System.IO.File.Exists(ComposeFile);

    /// <summary>docker compose for this session's stack, with the env it was started with.</summary>
    public string[] ComposeArgs(params string[] args)
    {
        var a = new List<string> { "compose", "--progress", "quiet" };
        if (System.IO.File.Exists(DockerMarker)) a.AddRange(["--profile", "docker"]);
        a.AddRange(["-f", ComposeFile]);
        if (System.IO.File.Exists(MountsFile)) a.AddRange(["-f", MountsFile]);
        a.AddRange(["-p", ComposeProject]);
        a.AddRange(args);
        return [.. a];
    }

    public void WriteCommand(IReadOnlyList<string> command) =>
        System.IO.File.WriteAllText(CommandFile, string.Concat(command.Select(a => a + "\0")));

    /// <summary>The command, or null for a session started by a pen that didn't record it.</summary>
    public string[]? ReadCommand() =>
        System.IO.File.Exists(CommandFile) ? System.IO.File.ReadAllText(CommandFile).Split('\0')[..^1] : null;

    public void WriteStarted(DateTime utc) =>
        System.IO.File.WriteAllText(StartedFile, utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));

    /// <summary>The common git dir the session started with, or null when it didn't start in a repo.</summary>
    public (bool StartedWithGit, string? CommonGitDir) ReadGit()
    {
        if (!System.IO.File.Exists(StartedWithGit)) return (false, null);
        var common = System.IO.File.ReadAllText(StartedWithGit).TrimEnd('\n');
        return (true, common.Length == 0 ? null : common);
    }

    public DateTime? ReadStarted() =>
        System.IO.File.Exists(StartedFile)
        && DateTime.TryParse(System.IO.File.ReadAllText(StartedFile).Trim(), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) ? t : null;

    public Dictionary<string, string> ReadEnv()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!System.IO.File.Exists(EnvFile)) return env;
        foreach (var line in System.IO.File.ReadAllLines(EnvFile))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) env[line[..eq]] = line[(eq + 1)..];
        }
        return env;
    }
}

/// <summary>Session start, run and end (see "CLI" in PLAN.md).</summary>
internal sealed class Session(HostEnv host, Project project, Config user, Config projectConfig)
{
    private const UnixFileMode Dir700 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    internal static readonly TimeSpan KeepEndedSessions = TimeSpan.FromDays(7);

    private string RuntimeDir => host.RuntimeDir(project.Key);

    /// <param name="docker">with Docker-in-Docker (pen --docker, or docker = true in .pen/config.toml)</param>
    public int Run(IReadOnlyList<string> command, bool docker = false)
    {
        // 1. Every session holds project.lock and its dir's worktree lock
        // shared; pen reset holds the first exclusive, pen sign the second.
        EnsureRuntimeDir(host, project.Key);
        using var locks = Sessions.TakeForSession(RuntimeDir, project);

        // 2. Sessions whose pen died get their end path now.
        FinishDeadSessions(project.Key, RuntimeDir);

        // 3. The repo refusals, the mounts (creating .pen/ and .git/hooks if
        // missing), images current, then everything else that can fail before
        // a session exists.
        RepoGuard.CheckAtStart(project.Dir);
        var mounts = RepoGuard.PrepareMounts(project.Dir, host.Home);
        Image.EnsureCurrent();
        var image = Image.EnsureProjectLayer(project) ?? Image.Name;
        // Under rootless Docker, the host user is container root: the agent
        // runs as UID 0 (with no capabilities) on a read-only root filesystem.
        var rootless = Docker.IsRootless();
        var owner = GitHub.ProjectOwner(project, projectConfig);
        var token = GitHub.TokenFor(owner, user, RuntimeDir, host);
        var allowlist = Allowlist.Merge(Allowlist.ParseList(Embedded.Text("proxy/allowlist.txt")), user.Allow, projectConfig.Allow);

        // One docker session per project: two daemons can't share the dind
        // volume. A dind still using it (an earlier session's teardown failed)
        // would be corrupted by a second, so that's refused too.
        using var dindLock = docker ? TakeDindLock() : null;

        foreach (var volume in docker ? ["home", "nuget", "dind"] : new[] { "home", "nuget" })
        {
            var r = Proc.Capture("docker", ["volume", "create", $"{project.Key}_{volume}"]);
            if (r.ExitStatus.ExitCode != 0)
                throw new PenException($"can't create volume {project.Key}_{volume}: {r.StandardError.Trim()}");
        }

        // From here on Ctrl-C belongs to the agent, and a hangup still ends the session cleanly.
        using var signals = new SessionSignals();

        // 4. The session's dir and lock, then its files. compose.yaml goes last:
        // its presence is what marks a session as open.
        var session = CreateSessionDir();
        using var sessionLock = FileLock.TryTake(session.LockFile, exclusive: true)
            ?? throw new InvalidOperationException("new session's lock is taken");
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
        if (project.IsRepo) File.WriteAllText(session.StartedWithGit, project.CommonGitDir + "\n");
        if (docker) File.WriteAllText(session.DockerMarker, "");
        session.WriteCommand(command);
        session.WriteStarted(DateTime.UtcNow);
        File.WriteAllText(session.File("squid.conf"), Embedded.Text("proxy/squid.conf"));
        File.WriteAllText(session.File("allowlist.txt"), Allowlist.Render(allowlist));
        File.WriteAllText(session.MountsFile, RepoGuard.ComposeOverride(mounts, readOnlyRoot: rootless, dindContainer: docker ? session.DindContainer : null));
        File.WriteAllText(session.ProxyLog, "");
        // Squid's own user writes the log on Linux; the session dir itself is 700.
        File.SetUnixFileMode(session.ProxyLog, (UnixFileMode)0b110_110_110);
        File.WriteAllText(session.ComposeFile, Embedded.Text("compose.yaml"));

        // 5. compose run as a child, so pen gets control back when it ends.
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

        End(session);
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

    internal static void EnsureRuntimeDir(HostEnv host, string projectKey)
    {
        Directory.CreateDirectory(host.StateRoot, Dir700);
        Directory.CreateDirectory(host.RuntimeDir(projectKey), Dir700);
        Directory.CreateDirectory(System.IO.Path.Combine(host.RuntimeDir(projectKey), "sessions"), Dir700);
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
    /// </summary>
    internal static void End(SessionDir session)
    {
        var env = session.ReadEnv();
        Proc.Capture("docker", File.Exists(session.DockerMarker)
            ? ["rm", "-f", session.Container, session.DindContainer]
            : ["rm", "-f", session.Container]);

        // The .git check, now that the agent is gone and before anything runs
        // git. pen's own git call (the unsigned-commit count) only runs when
        // the check moved nothing.
        string? reminder = null;
        if (env.TryGetValue("PROJECT_DIR", out var projectDir))
        {
            var (startedWithGit, commonGitDir) = session.ReadGit();
            var moved = GitCheck.Run(projectDir, startedWithGit, commonGitDir);
            if (moved.Count > 0) Out.Raw(GitCheck.Warning(projectDir, moved));
            else reminder = Sign.Reminder(projectDir);
        }

        // What the session ended up allowing (pen allow may have added to it),
        // so the blocked line doesn't suggest a domain that's allowed by now.
        var allowedFile = session.File("allowlist.txt");
        var allowed = File.Exists(allowedFile) ? Allowlist.ParseList(File.ReadAllText(allowedFile)).ToList() : [];

        if (env.Count > 0)
        {
            var psi = Proc.Info("docker", session.ComposeArgs("down", "-t", "1", "--remove-orphans"), env);
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            ScrubComposeEnv(psi);
            var down = Process.RunAndCaptureText(psi);
            if (down.ExitStatus.ExitCode != 0)
                Out.Line($"stopping session {session.Id}'s stack failed: {down.StandardError.Trim()} (it will be retried by the next pen command here)");
            else
                DeleteSessionFiles(session);
        }
        else
        {
            DeleteSessionFiles(session);
        }

        if (File.Exists(session.ProxyLog))
        {
            var blocked = Allowlist.BlockedHosts(File.ReadLines(session.ProxyLog)).Where(h => !Allowlist.IsAllowed(allowed, h)).ToList();
            var line = Allowlist.BlockedLine(blocked);
            if (line != null) Out.Line(line);
        }
        if (reminder != null) Out.Line(reminder);
    }

    private static void DeleteSessionFiles(SessionDir session)
    {
        foreach (var f in Directory.EnumerateFileSystemEntries(session.Path))
        {
            var name = System.IO.Path.GetFileName(f);
            if (name is "proxy.log" or "session.lock" or "compose.yaml") continue;
            if (Directory.Exists(f)) Directory.Delete(f, recursive: true); else File.Delete(f);
        }
        // compose.yaml marks the session open, so it goes last but for the lock.
        File.Delete(session.ComposeFile);
        File.Delete(session.LockFile);
    }

    /// <summary>
    /// Runs the end path for every session that still has a compose.yaml but
    /// whose lock nobody holds (its pen crashed or was killed), and deletes
    /// ended sessions' dirs after <see cref="KeepEndedSessions"/>.
    /// </summary>
    internal static void FinishDeadSessions(string projectKey, string runtimeDir)
    {
        var sessions = System.IO.Path.Combine(runtimeDir, "sessions");
        if (!Directory.Exists(sessions)) return;
        foreach (var path in Directory.EnumerateDirectories(sessions))
        {
            var s = new SessionDir(projectKey, path);
            if (s.IsOpen)
            {
                using var l = FileLock.TryTake(s.LockFile, exclusive: true);
                if (l == null || !s.IsOpen) continue; // running, or ended while we looked
                Out.Line($"ending session {s.Id}, whose pen exited without ending it");
                End(s);
            }
            else if (DateTime.UtcNow - LastActivity(path) > KeepEndedSessions)
            {
                try { Directory.Delete(path, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static DateTime LastActivity(string dir)
    {
        var t = Directory.GetLastWriteTimeUtc(dir);
        var log = System.IO.Path.Combine(dir, "proxy.log");
        if (File.Exists(log)) { var lt = File.GetLastWriteTimeUtc(log); if (lt > t) t = lt; }
        return t;
    }
}

/// <summary>
/// While a session runs: SIGINT and SIGQUIT are ignored (the terminal delivers
/// them to the agent). SIGHUP and SIGTERM don't stop pen either: the child is
/// stopped, and pen then runs the normal end path.
/// </summary>
internal sealed class SessionSignals : IDisposable
{
    private readonly PosixSignalRegistration[] _registrations;
    private volatile Process? _child;
    private volatile string? _container;
    private int _stopping;

    public Process? Child { set => _child = value; }
    public string? Container { set => _container = value; }
    public bool Stopping => _stopping == 1;

    public SessionSignals()
    {
        _registrations =
        [
            PosixSignalRegistration.Create(PosixSignal.SIGINT, c => c.Cancel = true),
            PosixSignalRegistration.Create(PosixSignal.SIGQUIT, c => c.Cancel = true),
            PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop),
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop),
        ];
    }

    private void Stop(PosixSignalContext c)
    {
        c.Cancel = true;
        if (Interlocked.Exchange(ref _stopping, 1) == 1) return;
        var child = _child;
        if (child == null) return; // not started yet or already done: the end path runs anyway
        var container = _container;
        _ = Task.Run(() =>
        {
            try
            {
                // With a TTY, compose forwards neither signal and keeps waiting
                // on the container, so the container goes first (as in the end
                // path), and compose then exits on its own.
                if (container != null) Proc.Capture("docker", ["rm", "-f", container]);
                if (!child.WaitForExit(TimeSpan.FromSeconds(5)))
                {
                    child.Signal(PosixSignal.SIGTERM);
                    if (!child.WaitForExit(TimeSpan.FromSeconds(5))) child.Kill();
                }
            }
            catch (InvalidOperationException) { } // already exited
        });
    }

    public void Dispose()
    {
        foreach (var r in _registrations) r.Dispose();
    }
}
