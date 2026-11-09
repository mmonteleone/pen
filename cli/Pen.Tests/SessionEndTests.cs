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
        File.WriteAllText(session.File("allowlist.txt"), "github.com\n");
        File.WriteAllText(session.ProxyLog, "");
        if (docker) File.WriteAllText(session.DockerMarker, "");
        File.WriteAllText(Path.Combine(repo, ".git", "commondir"), "planted\n");
        return session;
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
        var error = Assert.Throws<PenException>(() => Session.End(session));
        Assert.Contains("retained for retry", error.Message);
        Assert.True(session.IsOpen);
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
        Session.FinishDeadSessions("pen-test-12345678", Path.Combine(t.Path, "runtime"));
        Assert.False(session.IsOpen);
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
        Assert.Throws<PenException>(() => Session.End(session));
        Assert.True(session.IsOpen);
        Assert.True(File.Exists(Path.Combine(t.Path, "repo", ".git", "commondir")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothWritersMustStopBeforeGitCheck(bool failDind)
    {
        using var t = new TempDir();
        var session = Create(t, docker: true);
        using var fake = new FakeDocker(t, failDind ? """
            case "$1" in
              rm) case "$3" in *-dind) exit 1 ;; *) exit 0 ;; esac ;;
              ps) echo dind-still-present ;;
              *) exit 99 ;;
            esac
            """ : """
            case "$1" in rm|compose) exit 0 ;; *) exit 99 ;; esac
            """);
        if (failDind)
        {
            Assert.Throws<PenException>(() => Session.End(session));
            Assert.True(File.Exists(Path.Combine(t.Path, "repo", ".git", "commondir")));
        }
        else
        {
            Session.End(session);
            Assert.False(session.IsOpen);
            Assert.True(File.Exists(Path.Combine(t.Path, "repo", ".git", "commondir.pen-quarantine")));
        }
    }

    /// <summary>Docker that succeeds, and a git that logs its calls and says commit.gpgsign is on, so a reminder goes on to call git in the repo.</summary>
    private static (FakeDocker Docker, string GitCalls) FakeHost(TempDir t)
    {
        var fake = new FakeDocker(t, """case "$1" in rm|compose) exit 0 ;; *) exit 99 ;; esac""" + "\n");
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
        var output = CapturingStderr(() => ok = Session.FinishDeadSessions("pen-test-12345678", Path.Combine(t.Path, "runtime")));

        Assert.False(ok);
        Assert.Contains("git wasn't checked", output);
        Assert.False(File.Exists(gitCalls));
        Assert.False(first.IsOpen);
        Assert.False(second.IsOpen);
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

        Assert.True(Session.FinishDeadSessions("pen-test-12345678", Path.Combine(t.Path, "runtime")));

        Assert.Equal(2, RepoGitCalls(gitCalls).Count);
        Assert.False(first.IsOpen);
        Assert.False(second.IsOpen);
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
        Assert.False(session.IsOpen);
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
        var output = CapturingStderr(() => ok = Session.FinishDeadSessions("pen-test-12345678", runtime));

        Assert.False(ok);
        Assert.Contains("git wasn't checked", output);
        Assert.False(File.Exists(gitCalls));
        Assert.False(session.IsOpen);

        // Nothing is left to retry, so the next command gets through.
        var calls = File.ReadAllText(Path.Combine(t.Path, "calls"));
        Assert.True(Session.FinishDeadSessions("pen-test-12345678", runtime));
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
        var moved = Path.Combine(host.RuntimeDir(project.Key), "sessions", "12345678");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(session.Path, moved);

        var error = Assert.Throws<PenException>(() => Sign.Run(host, project, null));

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
            var output = CapturingStderr(() => error = Assert.Throws<PenException>(() => Sign.Run(host, project, null)));
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
            Assert.False(session.IsOpen);
        }
        finally { File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void AFailedCheckStillReportsWhatItMovedBeforeStopping()
    {
        // No known file-system state stops the walk midway, so this covers only
        // the printing: both warnings, what moved first.
        var moved = new List<Quarantined> { new("/p/.git/commondir", "/p/.git/commondir.pen-quarantine", "why") };
        var output = CapturingStderr(() => GitCheck.Report("/p", new CheckResult(moved, "reason")));
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
        var sessions = Path.Combine(host.RuntimeDir(project.Key), "sessions");
        Directory.CreateDirectory(sessions);
        Directory.Move(session.Path, Path.Combine(sessions, "12345678"));
        var run = new Session(host, project, Config.Empty, Config.Empty);

        var error = Assert.Throws<PenException>(() => run.Run(["true"]));

        Assert.Contains("not starting", error.Message);
        Assert.False(File.Exists(gitCalls));
        Assert.Equal(["12345678"], Directory.GetDirectories(sessions).Select(Path.GetFileName));
        var calls = Path.Combine(t.Path, "calls");
        Assert.All(File.ReadAllLines(calls), c => Assert.True(c.StartsWith("rm ", StringComparison.Ordinal) || c.StartsWith("compose ", StringComparison.Ordinal), c));

        // The dead session is gone, so the next run gets past that point. The
        // fake docker has no images, so it stops at the first image call.
        File.Delete(Path.Combine(repo, ".git", "commondir"));
        var next = Record.Exception(() => run.Run(["true"]));
        Assert.DoesNotContain("not starting", next?.Message ?? "");
        Assert.Contains(File.ReadAllLines(calls), c => !c.StartsWith("rm ", StringComparison.Ordinal) && !c.StartsWith("compose ", StringComparison.Ordinal));
    }
}
