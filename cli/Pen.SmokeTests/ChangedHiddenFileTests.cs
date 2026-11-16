namespace Pen.SmokeTests;

/// <summary>A hidden file that's gone by the time the session's mounts are written stops the session, and Docker
/// makes nothing in its place on the host.</summary>
/// <remarks>
/// What to hide is found before the image is ensured, which can take minutes; a docker shim deletes the hidden
/// file's directory when Pen creates the session's volumes, which comes after that and before the session's files
/// are written.
/// </remarks>
public sealed class ChangedHiddenFileTests(ChangedHiddenFileTests.Fixture fixture)
    : IClassFixture<ChangedHiddenFileTests.Fixture>
{
    private const string Refusal = "pen: not starting: apps/web/.env, which this session hides, is gone: the " +
        "project changed while the session was being prepared, so run pen again";

    [Fact(DisplayName = "a hidden file gone since pen looked stops the session, saying what changed")]
    public void RefusesTheSession() =>
        Assert.True(fixture.Session.ExitCode != 0 && fixture.Session.OutputLines.Contains(Refusal),
            $"no refusal \"{Refusal}\"; {fixture.Session.Describe()}");

    [Fact(DisplayName = "...the shim deleted it after pen looked")]
    public void ShimDeletedTheDirectory() => Assert.True(fixture.ShimRan, fixture.Session.Describe());

    [Fact(DisplayName = "...nothing is made in its place on the host")]
    public void NothingIsMadeOnTheHost() => Assert.False(fixture.AppsExists);

    [Fact(DisplayName = "...the agent never ran")]
    public void AgentNeverRan() =>
        Assert.DoesNotContain("agent ran", fixture.Session.OutputLines);

    [Fact(DisplayName = "...no session is left open, and its files are gone")]
    public void NoSessionIsLeft() => Assert.Equal([], fixture.SessionEntries);

    [Fact(DisplayName = "...nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    /// <summary>Hides apps/web/.env and starts a session through a shim that deletes apps/ before the mounts are
    /// written.</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal CommandResult Session { get; private set; } = null!;

        /// <summary>Gets whether the shim deleted apps/.</summary>
        internal bool ShimRan { get; private set; }

        /// <summary>Gets whether apps/ exists on the host after the session.</summary>
        internal bool AppsExists { get; private set; }

        /// <summary>Gets what's left in the sessions dir, besides proxy logs, after the session.</summary>
        internal IReadOnlyList<string> SessionEntries { get; private set; } = [];

        internal int ContainersAtEnd { get; private set; }

        protected override void Prepare()
        {
            var apps = Path.Combine(Project.Dir, "apps");
            Directory.CreateDirectory(Path.Combine(apps, "web"));
            File.WriteAllText(Path.Combine(apps, "web", ".env"), "WEB_SECRET=host-only\n");
            Directory.CreateDirectory(Path.Combine(Project.Dir, ".pen"));
            File.WriteAllText(Path.Combine(Project.Dir, ".pen", "config.toml"), "hide = [\"apps/web/.env\"]\n");

            var shimDir = Path.Combine(Scratch, "rm-shim");
            Directory.CreateDirectory(shimDir);
            var marker = Path.Combine(Scratch, "shim-ran");
            var shim = Path.Combine(shimDir, "docker");
            File.WriteAllText(shim, $$"""
                #!/bin/sh
                if [ "$1" = volume ] && [ "$2" = create ] && [ -d {{ShellQuote.Quote(apps)}} ]; then
                  rm -rf {{ShellQuote.Quote(apps)}} && : >{{ShellQuote.Quote(marker)}}
                fi
                exec {{ShellQuote.Quote(SmokeEnvironment.DockerPath)}} "$@"

                """);
            File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            Session = Cli.WithPathPrefix(shimDir).Run("run", "sh", "-c", "echo 'agent ran'");

            ShimRan = File.Exists(marker);
            AppsExists = Directory.Exists(apps) || File.Exists(apps);
            var sessions = Path.Combine(RuntimeDir, "sessions");
            SessionEntries = Directory.Exists(sessions)
                ?
                [
                    .. Directory.EnumerateFileSystemEntries(sessions, "*", SearchOption.AllDirectories)
                        .Where(path => File.Exists(path)
                            && Path.GetFileName(path) is not ("proxy.log" or "proxy.log.1"))
                        .Select(path => Path.GetRelativePath(sessions, path))
                        .Order(StringComparer.Ordinal),
                ]
                : [];
            ContainersAtEnd = Docker.CountContainers(Key);
        }
    }
}
