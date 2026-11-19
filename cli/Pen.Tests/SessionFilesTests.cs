using Pen.Masking;

namespace Pen.Tests;

/// <summary>
/// What a starting session writes to its dir and in what order, and what's left when a write fails: compose.yaml,
/// whose presence marks the session open, goes last, and a failed write deletes what was written, the masks too.
/// </summary>
/// <remarks>
/// The sessions run for real against a fake docker. The clock is asked for the start time once the env and the other
/// records are written, which is where <see cref="AtStarted"/> looks at the new session's dir and changes it.
/// </remarks>
[Collection(nameof(LockAndProcessTests))]
public class SessionFilesTests
{
    private const UnixFileMode Mode700 = (UnixFileMode)0b111_000_000;
    private const UnixFileMode Mode500 = (UnixFileMode)0b101_000_000;

    /// <summary>A clock that runs a hook the first time it's asked the time while a new session's files are written.</summary>
    private sealed class AtStarted(string projectKey, string sessions, Action<SessionDir> hook) : TimeProvider
    {
        private bool _ran;

        public override DateTimeOffset GetUtcNow()
        {
            if (!_ran && Directory.Exists(sessions))
            {
                foreach (var dir in Directory.GetDirectories(sessions))
                {
                    var session = new SessionDir(projectKey, dir);
                    if (File.Exists(session.EnvFile) && !File.Exists(session.StartedFile))
                    {
                        _ran = true;
                        hook(session);
                    }
                }
            }
            return base.GetUtcNow();
        }
    }

    private sealed record Started(Exception? Error, SessionDir Session, string Calls);

    private static string[] List(string dir) =>
        [.. Directory.EnumerateFileSystemEntries(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!];

    private static UserConfiguration Hiding(string pattern)
    {
        Assert.True(HidePattern.TryParse(pattern, out var hide, out _));
        return new([], [], [], [hide], [], []);
    }

    /// <summary>Runs a session of true in a new repo with secret.env in it.</summary>
    private static Started Run(
        TempDir t,
        bool docker,
        UserConfiguration? user = null,
        Action<SessionDir>? atStarted = null,
        Func<string, string>? dockerBody = null)
    {
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(repo, "secret.env"), "TOKEN=1\n");
        var project = Project.Resolve(repo, host);
        var sessions = Path.Combine(host.GetRuntimeDir(project.Key), "sessions");
        using var fake = new FakeDocker(t, dockerBody?.Invoke(sessions) ?? "exit 0\n");
        TimeProvider clock = atStarted is null ? TimeProvider.System : new AtStarted(project.Key, sessions, atStarted);
        var run = new Session(new ProjectContext(host, project, user ?? UserConfiguration.Empty, ProjectConfiguration.Empty),
            new Answers(new StringReader(""), IsTerminal: false), RecipeCatalog.Embedded, clock);

        var error = Record.Exception(() => run.Run(new(["true"]) { Docker = docker }));

        var session = new SessionDir(project.Key, Assert.Single(Directory.GetDirectories(sessions)));
        return new(error, session, File.ReadAllText(Path.Combine(t.Path, "calls")));
    }

    private static void AssertAgentNeverRan(Started started) =>
        Assert.DoesNotContain(" run --rm ", started.Calls, StringComparison.Ordinal);

    private static string[] Records(bool docker) =>
    [
        "command", .. docker ? ["docker"] : Array.Empty<string>(), "modules", "ports", "protected", "session.env",
        "session.lock", "started-with-git", "tool-domains", "tools",
    ];

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ASessionHasEveryFileWhenItsAgentRuns_AndOnlyItsProxyLogAfter(bool docker, bool hide)
    {
        using var t = new TempDir();
        var seen = t.Sub("seen");
        string Body(string sessions) => $$"""
            case "$*" in *" run --rm "*)
              for d in '{{sessions}}'/*; do
                ls -A "$d" > '{{seen}}/entries'
                cat "$d/session.env" > '{{seen}}/env'
                ls -A "$d/protected" > '{{seen}}/protected'
              done
              ;;
            esac
            exit 0

            """;

        var started = Run(t, docker, hide ? Hiding("secret.env") : null, dockerBody: Body);

        Assert.Null(started.Error);
        Assert.Contains(" run --rm ", started.Calls, StringComparison.Ordinal);
        string[] expected =
        [
            "allowlist.txt", "command", "compose.yaml", "denylist.txt", .. docker ? ["docker"] : Array.Empty<string>(),
            .. hide ? ["masks"] : Array.Empty<string>(), "modules", "open-egress.txt", "ports", "protected",
            "proxy.log", "session.env", "session.lock", "session.yaml", "squid.conf", "started", "started-with-git",
            "tool-domains", "tools",
        ];
        Assert.Equal(expected, File.ReadAllLines(Path.Combine(seen, "entries")).Order(StringComparer.Ordinal));
        Assert.Contains("manifest.json", File.ReadAllLines(Path.Combine(seen, "protected")));
        var host = t.Host();
        var env = File.ReadAllLines(Path.Combine(seen, "env"));
        Assert.Equal(["PROJECT_DIR", "PEN_PROJECT", "PEN_UID", "PEN_GID", "NUGET_PACKAGES", "PEN_IMAGE", "PEN_SEED"],
            env.Select(line => line[..line.IndexOf('=')]));
        Assert.Equal([
            $"PROJECT_DIR={Path.Combine(t.Path, "repo")}", $"PEN_PROJECT={started.Session.ProjectKey}", "PEN_UID=501",
            "PEN_GID=20", $"NUGET_PACKAGES={host.NuGetPackages}",
        ], env[..5]);
        Assert.Equal($"PEN_SEED={host.SeedDir}", env[6]);
        Assert.Equal([started.Session.ProxyLog], Directory.EnumerateFileSystemEntries(started.Session.Path));
        Assert.Equal((UnixFileMode)0b110_110_110, File.GetUnixFileMode(started.Session.ProxyLog));
        Assert.Equal(Mode700, File.GetUnixFileMode(started.Session.Path) & (UnixFileMode)0b111_111_111);
    }

    /// <summary>
    /// The start time is the last record written before the stack's files. A write that fails there leaves no
    /// compose.yaml, and what was written by then, the lock and the protected files' copies among it, is deleted.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailedRecordWrite_LeavesNoComposeYaml_AndDeletesWhatWasWritten(bool docker)
    {
        using var t = new TempDir();
        string[]? seen = null;
        string[]? copies = null;
        var failure = new IOException("planted failure");

        var started = Run(t, docker, atStarted: session =>
        {
            seen = List(session.Path);
            copies = List(session.ProtectedDir);
            throw failure;
        });

        Assert.Same(failure, started.Error);
        Assert.Equal(Records(docker), seen);
        Assert.Contains("manifest.json", copies!);
        Assert.False(started.Session.IsOpen());
        Assert.Empty(Directory.EnumerateFileSystemEntries(started.Session.Path));
        Assert.Empty(SessionRegistry.FindOpen(started.Session.ProjectKey, Paths.GetParent(Paths.GetParent(started.Session.Path))));
        AssertAgentNeverRan(started);
    }

    /// <summary>
    /// When what was written can't be deleted either (the session dir can't be written to), the write's failure is
    /// what's thrown, and the records are all that's there: no compose.yaml, nor any of the stack's files.
    /// </summary>
    [Fact]
    public void AFailedWriteWhoseFilesCantBeDeleted_ThrowsTheWritesFailure_AndLeavesTheSessionNotOpen()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root writes anywhere, so this would prove nothing");
        using var t = new TempDir();
        SessionDir? locked = null;
        try
        {
            var started = Run(t, docker: false, atStarted: session =>
            {
                locked = session;
                File.SetUnixFileMode(session.Path, Mode500);
            });

            var error = Assert.IsType<UnauthorizedAccessException>(started.Error);
            Assert.Contains(started.Session.StartedFile, error.Message, StringComparison.Ordinal);
            Assert.Equal(Records(docker: false), List(started.Session.Path));
            Assert.False(started.Session.IsOpen());
            AssertAgentNeverRan(started);
        }
        finally
        {
            if (locked is not null)
                File.SetUnixFileMode(locked.Path, Mode700);
        }
    }

    /// <summary>
    /// Each of the stack's files is written before compose.yaml: with compose.yaml a symlink to a file outside, and
    /// the stack's file a directory, that write fails, nothing reaches the file outside, and the session's files,
    /// the masks among them, are deleted (proxy.log stays, as it always does).
    /// </summary>
    [Theory]
    [InlineData("squid.conf")]
    [InlineData("allowlist.txt")]
    [InlineData("denylist.txt")]
    [InlineData("open-egress.txt")]
    [InlineData("masks")]
    [InlineData("session.yaml")]
    [InlineData("proxy.log")]
    public void ComposeYamlIsWrittenAfter(string file)
    {
        using var t = new TempDir();
        var outside = Path.Combine(t.Path, "outside");
        File.WriteAllText(outside, "");

        var started = Run(t, docker: true, Hiding("secret.env"), session =>
        {
            File.CreateSymbolicLink(session.ComposeFile, outside);
            Directory.CreateDirectory(session.GetPath(file));
        });

        Assert.True(started.Error is IOException or UnauthorizedAccessException, started.Error?.ToString());
        Assert.Equal("", File.ReadAllText(outside));
        Assert.False(started.Session.IsOpen());
        Assert.Equal(file == "proxy.log" ? ["proxy.log"] : [], List(started.Session.Path));
        AssertAgentNeverRan(started);
    }

    /// <summary>
    /// The masks' plan is looked at again after the ACL files are written and just before the mask files are: a
    /// hidden file gone by then stops the start before a planted masks dir would, but after a planted ACL file.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("masks")]
    [InlineData("open-egress.txt")]
    public void TheLateMaskCheckComesAfterTheAclFiles_AndJustBeforeTheMasks(string? planted)
    {
        using var t = new TempDir();
        var outside = Path.Combine(t.Path, "outside");
        File.WriteAllText(outside, "");

        var started = Run(t, docker: false, Hiding("secret.env"), session =>
        {
            File.Delete(Path.Combine(t.Path, "repo", "secret.env"));
            File.CreateSymbolicLink(session.ComposeFile, outside);
            if (planted is not null)
                Directory.CreateDirectory(session.GetPath(planted));
        });

        if (planted == "open-egress.txt")
        {
            Assert.True(started.Error is IOException or UnauthorizedAccessException, started.Error?.ToString());
        }
        else
        {
            var error = Assert.IsType<PenException>(started.Error);
            Assert.Equal("not starting: secret.env, which this session hides, is gone: the project changed while the " +
                "session was being prepared, so run pen again", error.Message);
        }
        Assert.Equal("", File.ReadAllText(outside));
        Assert.False(started.Session.IsOpen());
        Assert.Empty(Directory.EnumerateFileSystemEntries(started.Session.Path));
        AssertAgentNeverRan(started);
    }
}
