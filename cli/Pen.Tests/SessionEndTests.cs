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
}
