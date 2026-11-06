using System.Diagnostics;
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

    public string ComposeFile => File("compose.yaml");
    public string MountsFile => File("mounts.yaml");
    public string ProxyLog => File("proxy.log");
    public string LockFile => File("session.lock");
    public string EnvFile => File("session.env");
    public string StartedWithGit => File("started-with-git");

    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <summary>A session whose files are still there has a stack that may be up.</summary>
    public bool IsOpen => System.IO.File.Exists(ComposeFile);

    /// <summary>docker compose for this session's stack, with the env it was started with.</summary>
    public string[] ComposeArgs(params string[] args) =>
        System.IO.File.Exists(MountsFile)
            ? ["compose", "--progress", "quiet", "-f", ComposeFile, "-f", MountsFile, "-p", ComposeProject, .. args]
            : ["compose", "--progress", "quiet", "-f", ComposeFile, "-p", ComposeProject, .. args];

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

    public int Run(IReadOnlyList<string> command)
    {
        // 1. Every session holds project.lock shared; pen sign/reset hold it exclusive.
        EnsureRuntimeDir(host, project.Key);
        using var projectLock = FileLock.TryTake(System.IO.Path.Combine(RuntimeDir, "project.lock"), exclusive: false)
            ?? throw new PenException("a pen sign or pen reset is running in this project: try again once it's done");

        // 2. Sessions whose pen died get their end path now.
        FinishDeadSessions(project.Key, RuntimeDir);

        // 3. The repo refusals, the mounts (creating .pen/ and .git/hooks if
        // missing), images current, then everything else that can fail before
        // a session exists.
        RepoGuard.CheckAtStart(project.Dir);
        var mounts = RepoGuard.PrepareMounts(project.Dir, host.Home);
        Image.EnsureCurrent();
        var owner = GitHub.ProjectOwner(project, projectConfig);
        var token = GitHub.TokenFor(owner, user, RuntimeDir, host);
        var allowlist = Allowlist.Merge(Allowlist.ParseList(Embedded.Text("proxy/allowlist.txt")), user.Allow, projectConfig.Allow);
        foreach (var volume in new[] { "home", "nuget" })
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
            ["PEN_UID"] = host.Uid.ToString(),
            ["PEN_GID"] = host.Gid.ToString(),
            ["NUGET_PACKAGES"] = host.NuGetPackages,
            ["PEN_IMAGE"] = Image.Name,
        };
        File.WriteAllLines(session.EnvFile, env.Select(kv => $"{kv.Key}={kv.Value}"));
        if (project.HasGitDir) File.WriteAllText(session.StartedWithGit, "");
        File.WriteAllText(session.File("squid.conf"), Embedded.Text("proxy/squid.conf"));
        File.WriteAllText(session.File("allowlist.txt"), Allowlist.Render(allowlist));
        File.WriteAllText(session.MountsFile, RepoGuard.ComposeOverride(mounts));
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
    /// goes first (after a hangup compose may have left it running), then the
    /// stack, then the session's files except proxy.log. The caller holds the
    /// session's lock and releases it after.
    /// </summary>
    internal static void End(SessionDir session)
    {
        var env = session.ReadEnv();
        Proc.Capture("docker", ["rm", "-f", session.Container]);

        // The .git check, now that the agent is gone and before anything runs git.
        if (env.TryGetValue("PROJECT_DIR", out var projectDir))
        {
            var moved = GitCheck.Run(projectDir, File.Exists(session.StartedWithGit));
            if (moved.Count > 0) Out.Raw(GitCheck.Warning(projectDir, moved));
        }

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
            var line = Allowlist.BlockedLine(Allowlist.BlockedHosts(File.ReadLines(session.ProxyLog)));
            if (line != null) Out.Line(line);
        }
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
