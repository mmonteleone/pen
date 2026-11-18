using System.Diagnostics;

namespace Pen.Tests;

[Collection(nameof(LockAndProcessTests))]
public class SessionEndTests
{
    private static SessionDir Create(TempDir t, bool docker)
    {
        var repo = Git.Repo(t.Sub("repo"));
        var session = new SessionDir("pen-test-12345678", t.Sub("runtime", "sessions", "12345678"));
        File.WriteAllText(session.ComposeFile, "stack");
        File.WriteAllText(session.EnvFile, $"PROJECT_DIR={repo}\n");
        File.WriteAllText(session.StartedWithGit, Path.Combine(repo, ".git") + "\n");
        File.WriteAllText(session.ModulesFile, "");
        File.WriteAllText(session.GetPath("allowlist.txt"), "github.com\n");
        File.WriteAllText(session.ProxyLog, "");
        if (docker) File.WriteAllText(session.DockerMarker, "");
        File.WriteAllText(Path.Combine(repo, ".git", "commondir"), "planted\n");
        return session;
    }

    /// <summary>
    /// compose.yaml marks a session open, and another session starting reads an open one's records: so they go after
    /// it. Each record is made undeletable in turn, and compose.yaml is already gone when that fails.
    /// </summary>
    [Theory]
    [InlineData("started-with-git")]
    [InlineData("modules")]
    [InlineData("restored-manifest.json")]
    public void DeleteSessionFiles_RemovesComposeYamlBeforeTheRecordsAnOpenSessionHas(string record)
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        File.WriteAllText(session.LockFile, "");
        // File.Delete fails on a directory.
        File.Delete(session.GetPath(record));
        Directory.CreateDirectory(Path.Combine(session.GetPath(record), "keep"));

        Assert.ThrowsAny<Exception>(() => SessionTeardown.DeleteSessionFiles(session));

        Assert.False(session.IsOpen());
        Assert.True(Directory.Exists(session.GetPath(record)));
        Assert.True(File.Exists(session.LockFile));
        Assert.False(File.Exists(session.EnvFile));
        Assert.True(File.Exists(session.ProxyLog));
    }

    [Fact]
    public void DeleteSessionFiles_LeavesOnlyTheProxyLogs()
    {
        using var t = new TempDir();
        var session = Create(t, docker: true);
        File.WriteAllText(session.LockFile, "");
        File.WriteAllText(session.RestoredManifest, "{}");

        SessionTeardown.DeleteSessionFiles(session);

        Assert.Equal([session.ProxyLog], Directory.EnumerateFileSystemEntries(session.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedRemovalRetainsEverythingAndRetriesBeforeCheckingGit(bool docker)
    {
        using var t = new TempDir();
        var session = Create(t, docker);
        using var fake = new FakeDocker(t, """
            case "$1" in
              rm) echo 'removal failed' >&2; exit 1 ;;
              ps) if [ ! -f absent ]; then echo still-present; fi ;;
              compose) exit 0 ;;
              *) exit 99 ;;
            esac
            """);
        var error = Assert.Throws<PenException>(() => SessionTeardown.End(session));
        Assert.Contains("retained for retry", error.Message);
        Assert.True(session.IsOpen());
        Assert.True(File.Exists(session.EnvFile));
        Assert.True(File.Exists(session.ModulesFile));
        var commondir = Path.Combine(t.Path, "repo", ".git", "commondir");
        Assert.True(File.Exists(commondir));
        Assert.False(File.Exists(commondir + ".pen-quarantine"));
        var calls = File.ReadAllText(Path.Combine(t.Path, "calls"));
        Assert.DoesNotContain("compose", calls);
        if (docker) Assert.Contains("rm -f " + session.DindContainer, calls);

        // rm also fails for already-absent containers. A successful ps with
        // no match establishes absence, allowing recovery after a crash.
        File.WriteAllText(Path.Combine(t.Path, "absent"), "");
        SessionRecovery.FinishAbandoned("pen-test-12345678", Path.Combine(t.Path, "runtime"), TimeProvider.System);
        Assert.False(session.IsOpen());
        Assert.False(File.Exists(session.EnvFile));
        Assert.False(File.Exists(commondir));
        Assert.True(File.Exists(commondir + ".pen-quarantine"));
        Assert.True(File.Exists(session.ProxyLog));
    }

    [Fact]
    public void DaemonQueryFailureDoesNotEstablishAbsence()
    {
        using var t = new TempDir();
        var session = Create(t, docker: true);
        using var fake = new FakeDocker(t, "echo 'daemon unavailable' >&2; exit 1\n");
        Assert.Throws<PenException>(() => SessionTeardown.End(session));
        Assert.True(session.IsOpen());
        Assert.True(File.Exists(Path.Combine(t.Path, "repo", ".git", "commondir")));
    }

    /// <summary>
    /// Both removals are tried whichever fails, the agent's first; one that fails (and whose absence ps doesn't
    /// establish) throws once both were tried, naming only it, before Git is checked or the stack stopped.
    /// </summary>
    [Theory]
    [InlineData("none")]
    [InlineData("dind")]
    [InlineData("agent")]
    public void BothWritersMustStopBeforeGitCheck(string fail)
    {
        using var t = new TempDir();
        var session = Create(t, docker: true);
        using var fake = new FakeDocker(t, fail switch
        {
            "dind" => """
                case "$1" in
                  rm) case "$3" in *-dind) exit 1 ;; *) exit 0 ;; esac ;;
                  ps) echo dind-still-present ;;
                  *) exit 99 ;;
                esac
                """,
            "agent" => """
                case "$1" in
                  rm) case "$3" in *-agent) exit 1 ;; *) exit 0 ;; esac ;;
                  ps) echo agent-still-present ;;
                  *) exit 99 ;;
                esac
                """,
            _ => """
                case "$1" in rm|compose) exit 0 ;; *) exit 99 ;; esac
                """,
        });
        if (fail != "none")
        {
            var error = Assert.Throws<PenException>(() => SessionTeardown.End(session));
            Assert.True(File.Exists(Path.Combine(t.Path, "repo", ".git", "commondir")));
            var (failed, removed) = fail == "dind"
                ? (session.DindContainer, session.Container)
                : (session.Container, session.DindContainer);
            Assert.Contains($"can't establish that {failed} has stopped", error.Message);
            Assert.DoesNotContain(removed, error.Message);
            Assert.EndsWith($"session {session.Id} is retained for retry by the next pen command here; " +
                "Git has not been checked", error.Message);
            Assert.True(session.IsOpen());
            Assert.True(File.Exists(session.EnvFile));
        }
        else
        {
            SessionTeardown.End(session);
            Assert.False(session.IsOpen());
            Assert.True(File.Exists(Path.Combine(t.Path, "repo", ".git", "commondir.pen-quarantine")));
        }
        var removals = File.ReadAllLines(Path.Combine(t.Path, "calls"))
            .Where(call => call.StartsWith("rm ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["rm -f " + session.Container, "rm -f " + session.DindContainer], removals);
        Assert.Equal(fail == "none",
            File.ReadAllLines(Path.Combine(t.Path, "calls")).Any(call => call.StartsWith("compose ", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A fake docker that succeeds at everything, and at each writer removal, compose run and compose down records
    /// whether another process could take each lock: lines "phase lock mode held|free" in probes, the phase being
    /// "rm:container" or "run:project"/"down:project".
    /// </summary>
    private static string ProbingLocks(string runtime, string worktreeLock) => $$"""
        probe() {
          set -- "$1" project ex '{{runtime}}/project.lock' project sh '{{runtime}}/project.lock' \
            worktree ex '{{worktreeLock}}' worktree sh '{{worktreeLock}}' dind ex '{{runtime}}/dind.lock'
          for lock in '{{runtime}}'/sessions/*/session.lock; do
            [ ! -e "$lock" ] || set -- "$@" "session:$(basename -- "$(dirname -- "$lock")")" sh "$lock"
          done
          python3 -c '
        import fcntl, sys
        args = sys.argv[2:]
        for i in range(0, len(args), 3):
            name, mode, path = args[i:i + 3]
            with open(path, "a") as f:
                try:
                    fcntl.flock(f, (fcntl.LOCK_EX if mode == "ex" else fcntl.LOCK_SH) | fcntl.LOCK_NB)
                    state = "free"
                except OSError:
                    state = "held"
            print(sys.argv[1], name, mode, state)
        ' "$@" >> probes
        }
        case "$1" in
          rm) probe "rm:$3" ;;
          compose)
            project=; previous=; sub=
            for arg; do
              if [ "$previous" = -p ]; then project=$arg; fi
              case "$arg" in run|down) [ -n "$sub" ] || sub=$arg ;; esac
              previous=$arg
            done
            [ -z "$sub" ] || probe "$sub:$project"
            ;;
        esac
        exit 0

        """;

    private static List<string> ReadProbes(TempDir t, string phase) =>
        [.. File.ReadAllLines(Path.Combine(t.Path, "probes"))
            .Where(line => line.StartsWith(phase + " ", StringComparison.Ordinal))
            .Select(line => line[(phase.Length + 1)..])];

    /// <summary>
    /// The live session's project and worktree locks (shared), its dind lock and its own session lock are all held
    /// while it runs, at each writer removal, and at compose down, which comes after the .git check; then released.
    /// </summary>
    [Fact]
    public void ALiveSessionsLocksAreHeldThroughItsTeardownAndCheck()
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var project = Project.Resolve(repo, host);
        var runtime = host.GetRuntimeDir(project.Key);
        var worktreeLock = SessionRegistry.GetWorktreeLockFile(runtime, project.Dir);
        using var docker = new FakeDocker(t, ProbingLocks(runtime, worktreeLock));
        var run = new Session(new ProjectContext(host, project, UserConfiguration.Empty, ProjectConfiguration.Empty),
            new Answers(new StringReader(""), IsTerminal: false), RecipeCatalog.Embedded, TimeProvider.System);

        Assert.Equal(0, run.Run(new(["true"]) { Docker = true }));

        var session = new SessionDir(project.Key, Assert.Single(Directory.GetDirectories(Path.Combine(runtime, "sessions"))));
        var phases = File.ReadAllLines(Path.Combine(t.Path, "probes")).Select(line => line.Split(' ')[0]).Distinct().ToList();
        Assert.Equal(["run:" + session.ComposeProject, "rm:" + session.Container, "rm:" + session.DindContainer,
            "down:" + session.ComposeProject], phases);
        foreach (var phase in phases)
        {
            Assert.Equal([
                "project ex held", "project sh free", "worktree ex held", "worktree sh free", "dind ex held",
                $"session:{session.Id} sh held",
            ], ReadProbes(t, phase));
        }
        Assert.False(session.IsOpen());
        foreach (var path in new[] { SessionRegistry.GetProjectLockFile(runtime), worktreeLock, Path.Combine(runtime, "dind.lock") })
        {
            using var free = FileLock.TryTake(path, exclusive: true);
            Assert.NotNull(free);
        }
    }

    /// <summary>
    /// An abandoned session is ended holding its session lock, under the locks the command holds at the time: a run's
    /// project and worktree locks shared, pen sign's worktree lock exclusive, pen clean's project lock exclusive.
    /// </summary>
    [Theory]
    [InlineData("run")]
    [InlineData("sign")]
    [InlineData("clean")]
    [InlineData("allow")]
    public void AnAbandonedSessionIsEndedUnderTheCommandsLocks(string command)
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var project = Project.Resolve(repo, host);
        var runtime = host.GetRuntimeDir(project.Key);
        var worktreeLock = SessionRegistry.GetWorktreeLockFile(runtime, project.Dir);
        var dead = new SessionDir(project.Key, Path.Combine(runtime, "sessions", "12345678"));
        Directory.CreateDirectory(dead.Path);
        File.WriteAllText(dead.ComposeFile, "stack");
        File.WriteAllText(dead.EnvFile, $"PROJECT_DIR={repo}\n");
        File.WriteAllText(dead.StartedWithGit, Path.Combine(repo, ".git") + "\n");
        File.WriteAllText(dead.ModulesFile, "");
        File.WriteAllText(dead.ProxyLog, "");
        using var docker = new FakeDocker(t, ProbingLocks(runtime, worktreeLock));
        var answers = new Answers(new StringReader("n\n"), IsTerminal: true);

        var output = CapturingStderr(() => Record.Exception(() =>
        {
            switch (command)
            {
                case "run":
                    new Session(new ProjectContext(host, project, UserConfiguration.Empty, ProjectConfiguration.Empty),
                        answers, RecipeCatalog.Embedded, TimeProvider.System).Run(new(["true"]));
                    break;
                case "sign":
                    new SignCommand(host, project, TimeProvider.System).Run(null);
                    break;
                case "clean":
                    new CleanCommand(host, project, answers, TimeProvider.System).Run();
                    break;
                default:
                    EgressTarget.Resolve(host, new StartDir(repo), global: false).PrepareCurrentProject(host, TimeProvider.System);
                    break;
            }
        }));

        Assert.Contains($"ending session {dead.Id}, whose pen exited without ending it", output);
        Assert.False(dead.IsOpen());
        var expected = command switch
        {
            "run" => new[] { "project ex held", "project sh free", "worktree ex held", "worktree sh free" },
            "sign" => ["project ex held", "project sh free", "worktree ex held", "worktree sh held"],
            "clean" => ["project ex held", "project sh held", "worktree ex free", "worktree sh free"],
            _ => [],
        };
        foreach (var phase in new[] { "rm:" + dead.Container, "down:" + dead.ComposeProject })
        {
            var probes = ReadProbes(t, phase);
            Assert.Contains($"session:{dead.Id} sh held", probes);
            foreach (var line in expected)
                Assert.Contains(line, probes);
        }
    }

    /// <summary>
    /// pen clean, pen allow, pen deny (also with --global) and the blocked listing run no git, so an abandoned
    /// session's check that didn't finish only warns: the session ends, no git runs, and the command does its work.
    /// </summary>
    [Theory]
    [InlineData("clean")]
    [InlineData("allow")]
    [InlineData("allow --global")]
    [InlineData("deny")]
    [InlineData("deny --global")]
    [InlineData("listing")]
    public void AFailedCheckInAnAbandonedSession_OnlyWarns_InCommandsThatRunNoGit(string command)
    {
        using var t = new TempDir();
        var (host, project, dead) = AbandonedInProject(t);
        BreakCheck(dead);
        var (docker, gitCalls) = FakeHost(t, """
            case "$1" in
              rm|compose) exit 0 ;;
              volume)
                case "$2" in
                  inspect) case "$3" in *_home) exit 0 ;; *) exit 1 ;; esac ;;
                  rm) exit 0 ;;
                  *) exit 99 ;;
                esac ;;
              *) exit 99 ;;
            esac

            """);
        using var _ = docker;

        var exit = -1;
        var stdout = "";
        var output = CapturingStderr(() => stdout = CapturingStdout(() => exit = RunCommandWithoutGit(command, host, project)));

        Assert.Equal(0, exit);
        Assert.Contains("git wasn't checked", output);
        Assert.False(dead.IsOpen());
        Assert.False(File.Exists(gitCalls));
        switch (command)
        {
            case "clean":
                Assert.Contains($"volume rm {project.Key}_home", File.ReadAllLines(Path.Combine(t.Path, "calls")));
                Assert.Contains($"removed {project.Key}_home", output);
                break;
            case "listing":
                Assert.Contains("blocked.example", stdout);
                break;
            default:
                Assert.Contains("added.example", File.ReadAllText(EgressConfigPath(command, host, project)));
                break;
        }
    }

    /// <summary>
    /// An abandoned session whose writers can't be removed stops pen clean, pen allow, pen deny and the blocked
    /// listing before they change anything, keeping the session for a retry, and no git runs.
    /// </summary>
    [Theory]
    [InlineData("clean")]
    [InlineData("allow")]
    [InlineData("allow --global")]
    [InlineData("deny")]
    [InlineData("deny --global")]
    [InlineData("listing")]
    public void AFailedRemovalInAnAbandonedSession_StopsCommandsThatRunNoGit_BeforeTheyChangeAnything(string command)
    {
        using var t = new TempDir();
        var (host, project, dead) = AbandonedInProject(t);
        var (docker, gitCalls) = FakeHost(t, """
            case "$1" in
              rm) exit 1 ;;
              ps) echo still-present ;;
              *) exit 99 ;;
            esac

            """);
        using var _ = docker;

        var stdout = "";
        PenException? error = null;
        CapturingStderr(() => stdout = CapturingStdout(() =>
            error = Assert.Throws<PenException>(() => RunCommandWithoutGit(command, host, project))));

        Assert.Contains($"session {dead.Id} is retained for retry", error!.Message);
        Assert.True(dead.IsOpen());
        Assert.False(File.Exists(gitCalls));
        Assert.All(File.ReadAllLines(Path.Combine(t.Path, "calls")),
            call => Assert.True(call.StartsWith("rm ", StringComparison.Ordinal) || call.StartsWith("ps ", StringComparison.Ordinal), call));
        Assert.Equal("", stdout);
        if (command is not "clean" and not "listing")
            Assert.False(File.Exists(EgressConfigPath(command, host, project)));
    }

    /// <summary>
    /// An abandoned session whose protected files can't all be restored keeps its files, the good copies among them,
    /// through pen clean, pen allow, pen deny and the blocked listing, and no git runs; the next recovery, once the
    /// way is clear, restores them.
    /// </summary>
    [Theory]
    [InlineData("clean")]
    [InlineData("allow")]
    [InlineData("allow --global")]
    [InlineData("deny")]
    [InlineData("deny --global")]
    [InlineData("listing")]
    public void AnIncompleteRestoreInAnAbandonedSession_KeepsItsFiles_InCommandsThatRunNoGit(string command)
    {
        using var t = new TempDir();
        var (host, project, dead) = AbandonedInProject(t);
        ProtectedFiles.Save(dead, RepoGuard.EnsureMountTargetsAndPlan(project.Dir, "/nonexistent-home"), project.Dir, [], []);
        var config = Path.Combine(project.Dir, ".git", "config");
        var original = File.ReadAllText(config);
        File.AppendAllText(config, "[core]\n fsmonitor = /evil\n");
        var git = Path.GetDirectoryName(config)!;
        var runtime = host.GetRuntimeDir(project.Key);
        var (docker, gitCalls) = FakeHost(t, """
            case "$1" in
              rm|compose) exit 0 ;;
              volume)
                case "$2" in
                  inspect) case "$3" in *_home) exit 0 ;; *) exit 1 ;; esac ;;
                  rm) exit 0 ;;
                  *) exit 99 ;;
                esac ;;
              *) exit 99 ;;
            esac

            """);
        using var _ = docker;

        File.SetUnixFileMode(git, UnixFileMode.None);
        try
        {
            var exit = -1;
            var output = CapturingStderr(() => CapturingStdout(() => exit = RunCommandWithoutGit(command, host, project)));

            Assert.Equal(0, exit);
            Assert.Contains("good copies", output);
            Assert.True(dead.IsOpen());
            Assert.True(File.Exists(dead.ProtectedManifest));
            Assert.True(Directory.Exists(dead.ProtectedDir));
            Assert.False(File.Exists(gitCalls));
            CapturingStderr(() =>
                Assert.False(SessionRecovery.FinishAbandoned(project.Key, runtime, TimeProvider.System).EveryCheckFinished));
        }
        finally { File.SetUnixFileMode(git, (UnixFileMode)0b111_101_101); }

        CapturingStderr(() =>
            Assert.True(SessionRecovery.FinishAbandoned(project.Key, runtime, TimeProvider.System).EveryCheckFinished));
        Assert.False(dead.IsOpen());
        Assert.Equal(original, File.ReadAllText(config));
        Assert.DoesNotContain("fsmonitor", File.ReadAllText(config));
    }

    /// <summary>
    /// A repo with an abandoned session in its runtime dir, which blocked blocked.example; no user config.toml yet.
    /// </summary>
    private static (HostEnv Host, Project Project, SessionDir Dead) AbandonedInProject(TempDir t)
    {
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var repo = Git.Repo(t.Sub("repo"));
        var project = Project.Resolve(repo, host);
        var dead = new SessionDir(project.Key, Path.Combine(host.GetRuntimeDir(project.Key), "sessions", "12345678"));
        Directory.CreateDirectory(dead.Path);
        File.WriteAllText(dead.ComposeFile, "stack");
        File.WriteAllText(dead.EnvFile, $"PROJECT_DIR={repo}\n");
        File.WriteAllText(dead.StartedWithGit, Path.Combine(repo, ".git") + "\n");
        File.WriteAllText(dead.ModulesFile, "");
        File.WriteAllText(dead.ProxyLog, "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT blocked.example:443 -\n");
        return (host, project, dead);
    }

    private static int RunCommandWithoutGit(string command, HostEnv host, Project project)
    {
        var startDir = new StartDir(project.Dir);
        var catalog = RecipeCatalog.Embedded;
        var clock = TimeProvider.System;
        return command switch
        {
            "clean" => new CleanCommand(host, project, new Answers(new StringReader("y\n"), IsTerminal: true), clock).Run(),
            "allow" => new AllowCommand(host, startDir, catalog, clock).Run(["added.example"], global: false),
            "allow --global" => new AllowCommand(host, startDir, catalog, clock).Run(["added.example"], global: true),
            "deny" => new DenyCommand(host, startDir, catalog, clock).Run(["added.example"], global: false),
            "deny --global" => new DenyCommand(host, startDir, catalog, clock).Run(["added.example"], global: true),
            "listing" => new AllowCommand(host, startDir, catalog, clock).ListBlocked(
                new ProjectContext(host, project, UserConfiguration.Empty, ProjectConfiguration.Empty)),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        };
    }

    private static string EgressConfigPath(string command, HostEnv host, Project project) =>
        command.EndsWith("--global", StringComparison.Ordinal) ? host.UserConfigPath : project.ProjectConfigPath;

    private static string CapturingStdout(Action action)
    {
        var stdout = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try { action(); }
        finally { Console.SetOut(stdout); }
        return captured.ToString();
    }

    /// <summary>
    /// A session an older Pen wrote, without the tools, tool-domains, denylist.txt, open-egress.txt or modules records,
    /// still ends: checked, reminded, its files deleted, and what it blocked reported against its allowlist alone.
    /// </summary>
    [Fact]
    public void AnOlderSessionWithoutToolOrEgressRecords_Ends()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        var repo = Path.Combine(t.Path, "repo");
        File.Delete(Path.Combine(repo, ".git", "commondir"));
        File.Delete(session.ModulesFile);
        foreach (var record in new[] { session.ToolsFile, session.ToolDomainsFile, session.DenylistFile, session.OpenEgressFile })
            Assert.False(File.Exists(record));
        File.WriteAllText(session.ProxyLog,
            "2026-11-02T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT blocked.example:443 -\n" +
            "2026-11-02T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT github.com:443 -\n");
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;

        var result = default(SessionEndResult);
        var output = CapturingStderr(() => result = Session.EndAndRemind(session));

        Assert.True(result.CheckFinished);
        Assert.Equal(repo, result.ReminderDir);
        Assert.Single(RepoGitCalls(gitCalls));
        var blocked = Assert.Single(output.Split('\n'), line => line.Contains("blocked.example", StringComparison.Ordinal));
        Assert.Contains("pen allow", blocked);
        Assert.DoesNotContain("github.com", blocked);
        Assert.False(session.IsOpen());
        Assert.Equal([session.ProxyLog], Directory.EnumerateFileSystemEntries(session.Path));
    }

    /// <summary>
    /// A writer removal that fails in one abandoned session stops the batch, with that session retained, before any
    /// reminder: whichever order the sessions are listed in.
    /// </summary>
    [Theory]
    [InlineData("12345678")]
    [InlineData("99999999")]
    public void AFailedRemovalInOneAbandonedSession_StopsTheBatchBeforeAnyReminder(string failing)
    {
        using var t = new TempDir();
        var first = Create(t, docker: false);
        File.Delete(Path.Combine(t.Path, "repo", ".git", "commondir"));
        var second = Another(t, "99999999");
        var (docker, gitCalls) = FakeHost(t, $$"""
            case "$1" in
              rm) case "$3" in *-{{failing}}-agent) exit 1 ;; *) exit 0 ;; esac ;;
              ps) echo still-present ;;
              compose) exit 0 ;;
              *) exit 99 ;;
            esac

            """);
        using var _ = docker;

        var error = Assert.Throws<PenException>(() =>
            SessionRecovery.FinishAbandoned("pen-test-12345678", Path.Combine(t.Path, "runtime"), TimeProvider.System));

        Assert.Contains($"session {failing} is retained for retry", error.Message);
        Assert.True((failing == first.Id ? first : second).IsOpen());
        Assert.False(File.Exists(gitCalls));
    }

    /// <summary>
    /// A compose down that fails leaves the stack, so the files stay for a retry: whether docker can't be started once
    /// the agent is removed (the first rm replaces the fake with a file that can't be run), or compose down starts and
    /// exits non-zero (once).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedStackStop_RetainsTheFilesForARetry(bool startsButExitsNonZero)
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        var docker = Path.Combine(t.Path, "bin", "docker");
        using var fake = new FakeDocker(t, startsButExitsNonZero ? """
            case "$1" in
              rm) exit 0 ;;
              compose)
                if [ ! -f failed ]; then
                  : > failed
                  echo boom >&2
                  exit 1
                fi
                ;;
              *) exit 99 ;;
            esac
            """ : """
            case "$1" in
              rm)
                if [ ! -f broken ]; then
                  : > broken
                  printf 'not a program\n' > bin/unrunnable; chmod 755 bin/unrunnable; mv bin/unrunnable bin/docker
                fi
                ;;
              compose) exit 0 ;;
              *) exit 99 ;;
            esac
            """);
        var working = File.ReadAllText(docker);

        var output = CapturingStderr(() => SessionTeardown.End(session));

        if (startsButExitsNonZero)
        {
            Assert.Contains($"stopping session {session.Id}'s stack failed: boom (it will be retried", output);
        }
        else
        {
            Assert.Contains($"stopping session {session.Id}'s stack failed: can't run docker: ", output);
            Assert.DoesNotContain("compose", File.ReadAllText(Path.Combine(t.Path, "calls")));
        }
        Assert.Contains("(it will be retried by the next pen command here)", output);
        Assert.True(session.IsOpen());
        Assert.True(File.Exists(session.EnvFile));
        Assert.True(File.Exists(session.ModulesFile));
        // The check ran before the stack was stopped, now that the agent is gone.
        Assert.True(File.Exists(Path.Combine(t.Path, "repo", ".git", "commondir.pen-quarantine")));

        File.WriteAllText(docker, working);
        SessionRecovery.FinishAbandoned("pen-test-12345678", Path.Combine(t.Path, "runtime"), TimeProvider.System);
        Assert.False(session.IsOpen());
        Assert.False(File.Exists(session.EnvFile));
        Assert.Contains("compose", File.ReadAllText(Path.Combine(t.Path, "calls")));
    }

    [Fact]
    public void TheEndListsBlockedHostsFromBothLogs_AndKeepsThem()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        File.Delete(Path.Combine(t.Path, "repo", ".git", "commondir"));
        File.WriteAllText(session.PreviousProxyLog,
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT old.example:443 -\n" +
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT both.example:443 -\n");
        File.WriteAllText(session.ProxyLog,
            "2026-11-02T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT both.example:443 -\n" +
            "2026-11-02T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT new.example:443 -\n");
        var (docker, _) = FakeHost(t);
        using var _ = docker;

        var output = CapturingStderr(() => SessionTeardown.End(session));

        Assert.Contains("old.example both.example new.example", output);
        Assert.False(session.IsOpen());
        Assert.True(File.Exists(session.ProxyLog));
        Assert.True(File.Exists(session.PreviousProxyLog));
    }

    [Fact]
    public void TheEndListsDeniedHostsApart_WithNoPenAllowForThem()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        File.Delete(Path.Combine(t.Path, "repo", ".git", "commondir"));
        File.WriteAllText(session.AllowlistFile, "");
        File.WriteAllText(session.DenylistFile, "gist.github.com\n");
        File.WriteAllText(session.OpenEgressFile, ".\n");
        File.WriteAllText(session.ProxyLog,
            "2026-11-02T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT gist.github.com:443 -\n" +
            "2026-11-02T10:00:01Z 172.18.0.3 TCP_TUNNEL/200 CONNECT example.org:443 1.2.3.4\n" +
            "2026-11-02T10:00:02Z 172.18.0.3 TCP_DENIED/403 CONNECT 1.1.1.1:443 -\n");
        var (docker, _) = FakeHost(t);
        using var _ = docker;

        var output = CapturingStderr(() => SessionTeardown.End(session));

        Assert.Contains("pen: denied by config: gist.github.com\n", output);
        Assert.Contains("pen: blocked (raw IP, private address, unresolvable name, another port or blocked earlier): 1.1.1.1\n",
            output);
        Assert.DoesNotContain("pen allow", output);
        Assert.DoesNotContain("example.org", output);
    }

    [Fact]
    public void ReadEgress_ReadsOpenEgressNextToTheNamedDomains()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        File.WriteAllText(session.AllowlistFile, "github.com\n127.0.0.1.nip.io\n");
        File.WriteAllText(session.DenylistFile, "");
        File.WriteAllText(session.OpenEgressFile, ".\n");
        var (allow, deny) = session.ReadEgress();
        Assert.Equal(new[] { "*", "127.0.0.1.nip.io", "github.com" }, allow.Order(StringComparer.Ordinal));
        Assert.Empty(deny);

        File.WriteAllText(session.OpenEgressFile, "");
        Assert.Equal(new[] { "127.0.0.1.nip.io", "github.com" }, session.ReadEgress().Allow.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheEndInOpenEgressWithNoDeny_GroupsWhatsBlocked_AndLeavesOutWhatsAllowedByName()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        File.Delete(Path.Combine(t.Path, "repo", ".git", "commondir"));
        File.WriteAllText(session.AllowlistFile, "github.com\n127.0.0.1.nip.io\n");
        File.WriteAllText(session.DenylistFile, "");
        File.WriteAllText(session.OpenEgressFile, ".\n");
        File.WriteAllText(session.ProxyLog,
            "2026-11-02T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT localhost:443 -\n" +
            "2026-11-02T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT 127.0.0.1.nip.io:443 -\n" +
            "2026-11-02T10:00:02Z 172.18.0.3 TCP_DENIED/403 GET http://nx.invalid/ -\n");
        var (docker, _) = FakeHost(t);
        using var _ = docker;

        var output = CapturingStderr(() => SessionTeardown.End(session));

        // Without the open-egress read, this would be the plain blocked line offering pen allow.
        Assert.Contains(
            "pen: blocked (raw IP, private address, unresolvable name, another port or blocked earlier): localhost, nx.invalid\n",
            output);
        Assert.DoesNotContain("pen allow", output);
        Assert.DoesNotContain("nip.io", output);
    }

    /// <summary>Docker that succeeds (or runs <paramref name="dockerBody"/>), and a git that logs its calls and says commit.gpgsign is on, so a reminder goes on to call git in the repo.</summary>
    private static (FakeDocker Docker, string GitCalls) FakeHost(TempDir t, string? dockerBody = null)
    {
        var fake = new FakeDocker(t, dockerBody ?? """case "$1" in rm|compose) exit 0 ;; *) exit 99 ;; esac""" + "\n");
        var git = Path.Combine(t.Path, "bin", "git");
        var calls = Path.Combine(t.Path, "git-calls");
        File.WriteAllText(git, $"#!/bin/sh\nprintf '%s\\n' \"$*\" >> '{calls}'\ncase \"$*\" in *commit.gpgsign*) echo true; exit 0 ;; esac\nexit 1\n");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return (fake, calls);
    }

    /// <summary>Makes the session's record of its git state unreadable, which stops the check. Deleting it needs only the dir's mode.</summary>
    private static void BreakCheck(SessionDir session) =>
        File.SetUnixFileMode(session.StartedWithGit, UnixFileMode.None);

    private static string CapturingStderr(Action action)
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { action(); }
        finally { Console.SetError(stderr); }
        return captured.ToString();
    }

    /// <summary>A session as Create makes it, without the planted commondir, whose saved config has since changed or gone.</summary>
    private static (SessionDir Session, string Config) WithProtectedConfig(TempDir t)
    {
        var session = Create(t, docker: false);
        var repo = Path.Combine(t.Path, "repo");
        File.Delete(Path.Combine(repo, ".git", "commondir"));
        ProtectedFiles.Save(session, RepoGuard.EnsureMountTargetsAndPlan(repo, "/nonexistent-home"), repo, [], []);
        return (session, Path.Combine(repo, ".git", "config"));
    }

    [Fact]
    public void AChangedProtectedFile_IsRestoredWithAWarning_ItsCopiesRemoved_AndNoGitRuns()
    {
        using var t = new TempDir();
        var (session, config) = WithProtectedConfig(t);
        var original = File.ReadAllText(config);
        File.AppendAllText(config, "[core]\n fsmonitor = /evil\n");
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;

        var output = CapturingStderr(() => Session.EndAndRemind(session));

        Assert.Equal(original, File.ReadAllText(config));
        Assert.Contains("WARNING", output);
        Assert.Contains("config.pen-quarantine", output);
        Assert.False(Directory.Exists(session.ProtectedDir));
        Assert.Empty(RepoGitCalls(gitCalls));
    }

    [Fact]
    public void ADeletedProtectedFile_IsReported_AndNoGitRuns()
    {
        using var t = new TempDir();
        var (session, config) = WithProtectedConfig(t);
        File.Delete(config);
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;

        var output = CapturingStderr(() => Session.EndAndRemind(session));

        Assert.True(File.Exists(config));
        Assert.Contains(".git/config: it was deleted", output.Replace(t.Path + "/repo/", "", StringComparison.Ordinal));
        Assert.Empty(RepoGitCalls(gitCalls));
    }

    [Fact]
    public void ARestoreThatCantFinish_KeepsTheSessionsFiles_AndTheNextEndCompletesIt()
    {
        using var t = new TempDir();
        var (session, config) = WithProtectedConfig(t);
        var original = File.ReadAllText(config);
        File.AppendAllText(config, "[core]\n fsmonitor = /evil\n");
        var git = Path.GetDirectoryName(config)!;
        File.SetUnixFileMode(git, UnixFileMode.None);
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;
        try
        {
            SessionEndResult result = default;
            var output = CapturingStderr(() => result = Session.EndAndRemind(session));
            Assert.False(result.CheckFinished);
            Assert.Contains("good copies", output);
            Assert.True(session.IsOpen());
            Assert.True(File.Exists(session.ProtectedManifest));
            Assert.False(SessionRecovery.FinishAbandoned("pen-test-12345678", Path.Combine(t.Path, "runtime"), TimeProvider.System).EveryCheckFinished);
        }
        finally { File.SetUnixFileMode(git, (UnixFileMode)0b111_101_101); }

        Assert.True(SessionRecovery.FinishAbandoned("pen-test-12345678", Path.Combine(t.Path, "runtime"), TimeProvider.System).EveryCheckFinished);
        Assert.False(session.IsOpen());
        Assert.Equal(original, File.ReadAllText(config));
    }

    /// <summary>The way out Pen prints, run as printed (with Pen's retry, which is what pen true does first), after chmod 000 and a plant.</summary>
    [Fact]
    public void Replay_ThePrintedWayOutAfterChmod000_RestoresTheOriginal()
    {
        using var t = new TempDir();
        var (session, config) = WithProtectedConfig(t);
        var repo = Path.Combine(t.Path, "repo");
        var original = File.ReadAllText(config);
        var marker = Path.Combine(t.Path, "ran");
        var hook = Path.Combine(t.Path, "hook.sh");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
        File.AppendAllText(config, $"[core]\n fsmonitor = {hook}\n");
        var git = Path.GetDirectoryName(config)!;
        File.SetUnixFileMode(git, UnixFileMode.None);
        var (docker, _) = FakeHost(t);
        using var _d = docker;
        string output;
        try { output = CapturingStderr(() => SessionTeardown.End(session)); }
        catch { File.SetUnixFileMode(git, (UnixFileMode)0b111_101_101); throw; }

        var runtime = Path.Combine(t.Path, "runtime");
        Assert.True(session.IsOpen());
        var marked = "as one line: ";
        var line = output.Split('\n').Single(l => l.Contains(marked, StringComparison.Ordinal));
        var command = line[(line.IndexOf(marked, StringComparison.Ordinal) + marked.Length)..];
        Assert.EndsWith(" && " + GitCheckWording.RetryCommand, command);
        Assert.True(output.IndexOf(marked, StringComparison.Ordinal) < output.IndexOf("rm -r", StringComparison.Ordinal));
        Assert.Contains("discards them", output);
        Assert.Contains(session.ProtectedDir, output);

        // pen, here, is what its retry starts with.
        var pen = Path.Combine(t.Path, "bin", "pen");
        File.WriteAllText(pen, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(pen, (UnixFileMode)0b111_101_101);
        var sh = Process.RunAndCaptureText(new ProcessStartInfo("sh", ["-c", command]) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, sh.ExitStatus.ExitCode);
        Assert.True(SessionRecovery.FinishAbandoned("pen-test-12345678", runtime, TimeProvider.System).EveryCheckFinished);

        Assert.False(session.IsOpen());
        Assert.Equal(original, File.ReadAllText(config));
        File.Delete(Path.Combine(t.Path, "bin", "git")); // the real one, from here
        Git.Run(repo, "status");
        Assert.False(File.Exists(marker), "the planted fsmonitor ran");
    }

    [Fact]
    public void AProjectDirThatCantBeEntered_IsAFailure_NotAFileReportedRemoved()
    {
        using var t = new TempDir();
        var (session, config) = WithProtectedConfig(t);
        File.AppendAllText(config, "# planted\n");
        var repo = Path.Combine(t.Path, "repo");
        var (docker, _) = FakeHost(t);
        using var _d = docker;
        File.SetUnixFileMode(repo, UnixFileMode.None);
        try
        {
            var check = GitCheck.RunAtSessionEnd(repo, session);
            Assert.True(check.RestoreIncomplete);
            Assert.NotNull(check.Failure);
            Assert.Contains(repo, check.RestoreBlocked);
            Assert.Empty(check.Restored);
            Assert.True(File.Exists(session.ProtectedManifest));
        }
        finally { File.SetUnixFileMode(repo, (UnixFileMode)0b111_101_101); }
    }

    /// <summary>In a linked worktree whose .git file was forged, its own dir no longer resolves to the session's key: the printed command must go to the main checkout.</summary>
    [Fact]
    public void Replay_ThePrintedWayOutFromALinkedWorktree_RunsPenInTheMainCheckout()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var wt = Path.Combine(t.Path, "w t");
        Git.Run(repo, "worktree", "add", "-q", "-b", "wt", wt);
        var gitFile = Path.Combine(wt, ".git");
        var original = File.ReadAllText(gitFile);
        var key = Project.ComputeKey(repo);
        var runtime = t.Sub("runtime");
        ProjectRuntime.WriteProjectDir(runtime, repo);
        var session = new SessionDir(key, t.Sub("runtime", "sessions", "12345678"));
        File.WriteAllText(session.ComposeFile, "stack");
        File.WriteAllText(session.EnvFile, $"PROJECT_DIR={wt}\n");
        File.WriteAllText(session.StartedWithGit, Path.Combine(repo, ".git") + "\n");
        File.WriteAllText(session.ModulesFile, "");
        File.WriteAllText(session.ProxyLog, "");
        ProtectedFiles.Save(session, RepoGuard.EnsureMountTargetsAndPlan(wt, "/nonexistent-home"), wt, [], []);

        File.WriteAllText(gitFile, "gitdir: /forged\n");
        File.SetUnixFileMode(wt, UnixFileMode.None);
        var host = new HostEnv(t.Sub("home"), 501, 20, "/nuget");
        var (docker, _) = FakeHost(t);
        using var _d = docker;
        string output;
        try { output = CapturingStderr(() => SessionTeardown.End(session)); }
        catch { File.SetUnixFileMode(wt, (UnixFileMode)0b111_101_101); throw; }
        Assert.True(session.IsOpen());

        const string marked = "as one line: ";
        var line = output.Split('\n').Single(l => l.Contains(marked, StringComparison.Ordinal));
        var command = line[(line.IndexOf(marked, StringComparison.Ordinal) + marked.Length)..];
        var where = Path.Combine(t.Path, "pen-ran-in");
        var pen = Path.Combine(t.Path, "bin", "pen");
        File.WriteAllText(pen, $"#!/bin/sh\npwd -P > '{where}'\n");
        File.SetUnixFileMode(pen, (UnixFileMode)0b111_101_101);
        var sh = Process.RunAndCaptureText(new ProcessStartInfo("sh", ["-c", command]) { WorkingDirectory = t.Path, RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, sh.ExitStatus.ExitCode);

        // pen, there, finds this session by its key, and starts by finishing it.
        var ranIn = File.ReadAllText(where).TrimEnd('\n');
        Assert.Equal(repo, ranIn);
        Assert.Equal(key, Project.Resolve(ranIn, host).Key);
        Assert.True(SessionRecovery.FinishAbandoned(key, runtime, TimeProvider.System).EveryCheckFinished);
        Assert.Equal(original, File.ReadAllText(gitFile));
        Assert.False(session.IsOpen());
    }

    [Fact]
    public void AProjectDirThatsGone_IsAFailure_AndNoReminderRuns()
    {
        using var t = new TempDir();
        var (session, _) = WithProtectedConfig(t);
        var repo = Path.Combine(t.Path, "repo");
        Directory.Move(repo, repo + "-moved");
        var (docker, gitCalls) = FakeHost(t);
        using var _d = docker;
        SessionEndResult result = default;
        var output = CapturingStderr(() => result = Session.EndAndRemind(session));
        Assert.False(result.CheckFinished);
        Assert.Contains("isn't there any more", output);
        Assert.Empty(RepoGitCalls(gitCalls));
    }

    [Fact]
    public void AParentThatCantBeEntered_IsAFailure_AndNamesTheParent()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("holder", "p"));
        var holder = Path.Combine(t.Path, "holder");
        var check = default(CheckResult);
        var inner = repo;
        var innerSession = new SessionDir("k", t.Sub("runtime2", "sessions", "1"));
        File.WriteAllText(innerSession.StartedWithGit, Path.Combine(inner, ".git") + "\n");
        File.WriteAllText(innerSession.ModulesFile, "");
        ProtectedFiles.Save(innerSession, RepoGuard.EnsureMountTargetsAndPlan(inner, "/nonexistent-home"), inner, [], []);
        File.SetUnixFileMode(holder, UnixFileMode.None);
        try { check = GitCheck.RunAtSessionEnd(inner, innerSession); }
        finally { File.SetUnixFileMode(holder, (UnixFileMode)0b111_101_101); }
        Assert.True(check.RestoreIncomplete);
        Assert.NotNull(check.Failure);
        Assert.Contains(holder, check.RestoreBlocked);
        Assert.Empty(check.Restored);
        Assert.True(File.Exists(innerSession.ProtectedManifest));
    }

    [Fact]
    public void TheWayOut_QuotesAndSanitizesPaths()
    {
        var advice = GitCheckWording.KeptCopiesAdvice(new SessionDir("k", "/s/x\u001b[2Jy"), ["/p/a b'\n\u001b[2J"]);
        Assert.DoesNotContain('\u001b', advice);
        Assert.Contains("printf", advice);
        var plain = GitCheckWording.KeptCopiesAdvice(new SessionDir("k", "/s/1"), ["/p/a b"]);
        Assert.Contains("chmod u+rwx '/p/a b' && pen true", plain);
        Assert.Contains("deal with what's named above", GitCheckWording.KeptCopiesAdvice(new SessionDir("k", "/s/1"), []));
    }

    [Fact]
    public void AnUnreadableRecordOfTheSessionsGit_KeepsItsCopiesToo()
    {
        using var t = new TempDir();
        var (session, config) = WithProtectedConfig(t);
        File.AppendAllText(config, "# changed\n");
        var (docker, _) = FakeHost(t);
        using var __ = docker;
        BreakCheck(session);
        var result = default(SessionEndResult);
        CapturingStderr(() => result = SessionTeardown.End(session));
        Assert.False(result.CheckFinished);
        Assert.True(File.Exists(session.ProtectedManifest));
        Assert.True(session.IsOpen());
    }

    [Fact]
    public void ADamagedSessionEnv_KeepsTheCopies()
    {
        using var t = new TempDir();
        var (session, _) = WithProtectedConfig(t);
        File.WriteAllText(session.EnvFile, "");
        var (docker, _) = FakeHost(t);
        using var __ = docker;
        var result = default(SessionEndResult);
        var output = CapturingStderr(() => result = SessionTeardown.End(session));
        Assert.False(result.CheckFinished);
        Assert.Contains("good copies", output);
        Assert.True(File.Exists(session.ProtectedManifest));
    }

    [Fact]
    public void ACleanCheckRunsTheReminder_SoTheFakeGitSeesIt()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        File.Delete(Path.Combine(t.Path, "repo", ".git", "commondir"));
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;
        Assert.True(Session.EndAndRemind(session).CheckFinished);
        Assert.Single(RepoGitCalls(gitCalls));
    }

    private static List<string> RepoGitCalls(string gitCalls) =>
        File.Exists(gitCalls) ? File.ReadAllLines(gitCalls).Where(c => c.StartsWith("-C ", StringComparison.Ordinal)).ToList() : [];

    /// <summary>A second dead session in the repo <see cref="Create"/> made.</summary>
    private static SessionDir Another(TempDir t, string id)
    {
        var repo = Path.Combine(t.Path, "repo");
        var session = new SessionDir("pen-test-12345678", t.Sub("runtime", "sessions", id));
        File.WriteAllText(session.ComposeFile, "stack");
        File.WriteAllText(session.EnvFile, $"PROJECT_DIR={repo}\n");
        File.WriteAllText(session.StartedWithGit, Path.Combine(repo, ".git") + "\n");
        File.WriteAllText(session.ModulesFile, "");
        File.WriteAllText(session.ProxyLog, "");
        return session;
    }

    [Fact]
    public void ACheckThatMovedSomethingGetsNoReminder()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;
        Assert.True(Session.EndAndRemind(session).CheckFinished);
        Assert.False(File.Exists(gitCalls));
    }

    // Enumeration order isn't controllable, so the broken session takes each
    // place in turn: whichever order the file system lists them, both arise.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneDeadSessionsFailedCheckStopsEveryReminder(bool breakFirst)
    {
        using var t = new TempDir();
        var first = Create(t, docker: false);
        File.Delete(Path.Combine(t.Path, "repo", ".git", "commondir"));
        var second = Another(t, "99999999");
        BreakCheck(breakFirst ? first : second);
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;

        var ok = true;
        var output = CapturingStderr(() => ok = SessionRecovery.FinishAbandoned("pen-test-12345678", Path.Combine(t.Path, "runtime"), TimeProvider.System).EveryCheckFinished);

        Assert.False(ok);
        Assert.Contains("git wasn't checked", output);
        Assert.False(File.Exists(gitCalls));
        Assert.False(first.IsOpen());
        Assert.False(second.IsOpen());
    }

    [Fact]
    public void CleanDeadSessionsAllGetTheirReminders()
    {
        using var t = new TempDir();
        var first = Create(t, docker: false);
        File.Delete(Path.Combine(t.Path, "repo", ".git", "commondir"));
        var second = Another(t, "99999999");
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;

        Assert.True(SessionRecovery.FinishAbandoned("pen-test-12345678", Path.Combine(t.Path, "runtime"), TimeProvider.System).EveryCheckFinished);

        Assert.Equal(2, RepoGitCalls(gitCalls).Count);
        Assert.False(first.IsOpen());
        Assert.False(second.IsOpen());
    }

    [Fact]
    public void APausedRebaseIsReportedAtSessionEnd_AndLeftInPlace()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        var repo = Path.Combine(t.Path, "repo");
        File.Delete(Path.Combine(repo, ".git", "commondir"));
        var todo = Path.Combine(t.Sub("repo", ".git", "rebase-merge"), "git-rebase-todo");
        File.WriteAllText(todo, "exec evil\n");
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;

        var ok = false;
        var output = CapturingStderr(() => ok = Session.EndAndRemind(session).CheckFinished);

        Assert.True(ok); // it doesn't stop Pen's own git, which continues nothing
        Assert.Contains(".git/rebase-merge: a paused rebase", output);
        Assert.Contains("exec evil", output);
        Assert.Equal("exec evil\n", File.ReadAllText(todo));
        Assert.Single(RepoGitCalls(gitCalls));
    }

    [Fact]
    public void AFailedCheckWarnsRunsNoGitAndStillEndsTheSession()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        File.Delete(Path.Combine(t.Path, "repo", ".git", "commondir"));
        BreakCheck(session);
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;

        var ok = true;
        var output = CapturingStderr(() => ok = Session.EndAndRemind(session).CheckFinished);

        Assert.False(ok);
        Assert.Contains("git wasn't checked: don't run git here", output);
        Assert.Contains("permission denied", output);
        Assert.False(File.Exists(gitCalls));
        Assert.Contains("compose", File.ReadAllText(Path.Combine(t.Path, "calls")));
        Assert.False(session.IsOpen());
        Assert.False(File.Exists(session.EnvFile));
    }

    [Fact]
    public void AFailedCheckInADeadSessionDoesntWedgeTheProject()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        BreakCheck(session);
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;
        var runtime = Path.Combine(t.Path, "runtime");

        var ok = true;
        var output = CapturingStderr(() => ok = SessionRecovery.FinishAbandoned("pen-test-12345678", runtime, TimeProvider.System).EveryCheckFinished);

        Assert.False(ok);
        Assert.Contains("git wasn't checked", output);
        Assert.False(File.Exists(gitCalls));
        Assert.False(session.IsOpen());

        // Nothing is left to retry, so the next command gets through.
        var calls = File.ReadAllText(Path.Combine(t.Path, "calls"));
        Assert.True(SessionRecovery.FinishAbandoned("pen-test-12345678", runtime, TimeProvider.System).EveryCheckFinished);
        Assert.Equal(calls, File.ReadAllText(Path.Combine(t.Path, "calls")));
    }

    [Fact]
    public void SignRefusesAfterADeadSessionsCheckFailed()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        // Nothing for Sign's own check to move, so only its guard can refuse.
        File.Delete(Path.Combine(t.Path, "repo", ".git", "commondir"));
        BreakCheck(session);
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;
        var repo = Path.Combine(t.Path, "repo");
        var host = t.Host();
        var project = Project.Resolve(repo, host);
        var moved = Path.Combine(host.GetRuntimeDir(project.Key), "sessions", "12345678");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(session.Path, moved);

        var error = Assert.Throws<PenException>(() => new SignCommand(host, project, TimeProvider.System).Run(null));

        Assert.Contains("not signing: deal with the warning", error.Message);
        Assert.False(File.Exists(gitCalls));
    }

    [Fact]
    public void SignRefusesAnUnlistableDirectoryAndRunsNoGit()
    {
        using var t = new TempDir();
        Create(t, docker: false);
        var repo = Path.Combine(t.Path, "repo");
        File.Delete(Path.Combine(repo, ".git", "commondir"));
        var sub = t.Sub("repo", "sub");
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;
        var host = t.Host();
        var project = Project.Resolve(repo, host);
        File.SetUnixFileMode(sub, UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            PenException? error = null;
            var output = CapturingStderr(() => error = Assert.Throws<PenException>(() => new SignCommand(host, project, TimeProvider.System).Run(null)));
            Assert.Contains("not signing: deal with the files", error!.Message);
            Assert.Contains("git wasn't checked", output);
            Assert.Contains("sub", output);
            Assert.False(File.Exists(gitCalls));
        }
        finally { File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void WhatCouldntBeMovedGetsTheNotCheckedWarningAndNoGit()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        var git = Path.Combine(t.Path, "repo", ".git");
        File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (docker, gitCalls) = FakeHost(t);
            using var _ = docker;
            var ok = true;
            var output = CapturingStderr(() => ok = Session.EndAndRemind(session).CheckFinished);
            Assert.False(ok);
            Assert.Contains("COULD NOT MOVE", output);
            Assert.Contains("git wasn't checked", output);
            Assert.False(File.Exists(gitCalls));
            Assert.False(session.IsOpen());
        }
        finally { File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void AFailedCheckStillReportsWhatItMovedBeforeStopping()
    {
        // No known file-system state stops the walk midway, so this covers only
        // the printing: both warnings, what moved first.
        var moved = new List<Quarantined> { new Quarantined.Moved("/p/.git/commondir", "/p/.git/commondir.pen-quarantine", "why") };
        var output = CapturingStderr(() => GitCheckReport.Print("/p", new CheckResult { Moved = moved, Failure = "reason" }));
        Assert.Contains("mv '/p/.git/commondir.pen-quarantine' '/p/.git/commondir'", output);
        Assert.Contains("git wasn't checked: don't run git here (/p)", output);
        Assert.Contains("reason", output);
        Assert.True(output.IndexOf("mv '", StringComparison.Ordinal) < output.IndexOf("wasn't checked", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunStopsAfterADeadSessionsCheckFailed_AndTheNextOneProceeds()
    {
        using var t = new TempDir();
        var session = Create(t, docker: false);
        BreakCheck(session);
        var (docker, gitCalls) = FakeHost(t);
        using var _ = docker;
        var repo = Path.Combine(t.Path, "repo");
        var host = t.Host();
        var project = Project.Resolve(repo, host);
        var sessions = Path.Combine(host.GetRuntimeDir(project.Key), "sessions");
        Directory.CreateDirectory(sessions);
        Directory.Move(session.Path, Path.Combine(sessions, "12345678"));
        var run = new Session(new ProjectContext(host, project, UserConfiguration.Empty, ProjectConfiguration.Empty), new Answers(Console.In, Native.IsATty(0)), RecipeCatalog.Embedded, TimeProvider.System);

        var error = Assert.Throws<PenException>(() => run.Run(new(["true"])));

        Assert.Contains("not starting", error.Message);
        Assert.False(File.Exists(gitCalls));
        Assert.Equal(["12345678"], Directory.GetDirectories(sessions).Select(Path.GetFileName));
        var calls = Path.Combine(t.Path, "calls");
        Assert.All(File.ReadAllLines(calls), c => Assert.True(c.StartsWith("rm ", StringComparison.Ordinal) || c.StartsWith("compose ", StringComparison.Ordinal), c));

        // The dead session is gone, so the next run gets past that point. The
        // fake docker has no images, so it stops at the first image call.
        File.Delete(Path.Combine(repo, ".git", "commondir"));
        var next = Record.Exception(() => run.Run(new(["true"])));
        Assert.DoesNotContain("not starting", next?.Message ?? "");
        Assert.Contains(File.ReadAllLines(calls), c => !c.StartsWith("rm ", StringComparison.Ordinal) && !c.StartsWith("compose ", StringComparison.Ordinal));
    }
}
