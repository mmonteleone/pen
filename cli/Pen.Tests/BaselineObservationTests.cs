namespace Pen.Tests;

/// <summary>
/// Which open sessions a starting session goes by for its module baseline: those open just before the session dir is
/// written, as well as those open before the module scan, whether or not their Pen is still running.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public class BaselineObservationTests
{
    [Fact]
    public void ASessionThatOpensDuringPrepare_AndWhosePenDies_IsOneTheStartGoesBy()
    {
        using var t = new TempDir();
        var host = t.Host();
        var repo = Git.Repo(t.Sub("repo"));
        var project = Project.Resolve(repo, host);
        var other = Path.Combine(host.GetRuntimeDir(project.Key), "sessions", "zzzzzzzz");
        // The first docker call comes from SessionPreparer's EnsureImage, after its first look at the open sessions:
        // a session opens then (no module list recorded) and its Pen is gone (its lock is free).
        using var docker = new FakeDocker(t, $$"""
            if [ ! -e opened ]; then
              mkdir -p '{{other}}'
              : > '{{other}}/compose.yaml'
              : > opened
            fi
            exit 0

            """);
        var run = new Session(new ProjectContext(host, project, UserConfiguration.Empty, ProjectConfiguration.Empty),
            new Answers(new StringReader(""), IsTerminal: false), RecipeCatalog.Embedded, TimeProvider.System);

        var error = Record.Exception(() => run.Run(new(["true"])));

        Assert.True(File.Exists(Path.Combine(t.Path, "opened")), "no docker call before the session dir");
        var pen = Assert.IsType<PenException>(error);
        Assert.Contains("pen has no record of which submodule repos", pen.Message);
    }
}
