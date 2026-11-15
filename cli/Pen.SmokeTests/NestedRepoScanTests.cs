using System.Diagnostics;
using System.Globalization;

namespace Pen.SmokeTests;

/// <summary>The nested-repo scan over a 100k-file node_modules and obj/ trees: it finds a repo at the bottom, and
/// quickly. The session-end scans also find a repo the agent puts inside the git dir, with a symlink to it in the
/// project.</summary>
/// <remarks>
/// The planted repo makes pen refuse right after the scan, before any Docker call, so the refusal's time is the
/// scan's (plus pen's own startup). The repo in the git dir sets core.fsmonitor to a host script that touches a
/// canary, so "git there runs it" is seen, not guessed.
/// </remarks>
public sealed class NestedRepoScanTests(NestedRepoScanTests.Fixture fixture)
    : IClassFixture<NestedRepoScanTests.Fixture>
{
    [Fact(DisplayName = "the nested-repo scan finds a repo deep in node_modules")]
    public void ScanFindsTheDeepRepo() => SmokeAssert.Contains(fixture.Refusal, "node_modules/pkg4999/dist/deep");

    [Fact(DisplayName = "...in under about 1 s with 100k+ files (took …s)")]
    public void ScanIsQuick()
    {
        var seconds = fixture.RefusalTime.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);
        var sessionSeconds = fixture.SessionTime.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"a whole session with that tree (start and end scans included): {sessionSeconds}s");

        Assert.True(
            fixture.RefusalTime.TotalSeconds < 1.2, $"...in under about 1 s with 100k+ files (took {seconds}s)");
    }

    [Fact(DisplayName = "a repo in .git/foo/bar, docs linked to it: host git in docs runs it (the replay is real)")]
    public void GitDirPlantFiresOnTheHost() => Assert.True(fixture.GitDirPlantFiresBefore);

    [Fact(DisplayName = "...the agent planting it: warned at session end")]
    public void GitDirPlantWarns() =>
        SmokeAssert.ContainsAll(
            fixture.GitDirPlant, "pen: WARNING", "inside the git dir", "a symlink into the git dir",
            "docs (a symlink to .git/foo/bar) removed", "ln -s '.git/foo/bar' ");

    [Fact(DisplayName = "...with .git/foo/bar/.git moved aside and docs removed, not renamed")]
    public void GitDirPlantIsMoved() =>
        Assert.True(fixture.GitDirPlantMoved, fixture.GitDirPlant.Describe());

    [Fact(DisplayName = "...and host git there no longer runs it")]
    public void GitDirPlantNoLongerFires() => Assert.False(fixture.GitDirPlantFiresAfter);

    /// <summary>Fills the project with the trees, times a refused session with a repo planted at the bottom, then a
    /// whole session without it (only noted); then has the agent put a repo in the git dir.</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal CommandResult Refusal { get; private set; } = null!;

        internal TimeSpan RefusalTime { get; private set; }

        /// <summary>Gets how long a whole session with the trees took, for the note.</summary>
        internal TimeSpan SessionTime { get; private set; }

        /// <summary>Gets whether host git in docs ran the repo's fsmonitor when the host planted it.</summary>
        internal bool GitDirPlantFiresBefore { get; private set; }

        /// <summary>Gets the session whose agent planted the repo in the git dir and docs.</summary>
        internal CommandResult GitDirPlant { get; private set; } = null!;

        /// <summary>Gets whether .git/foo/bar/.git was gone and its quarantined name there after it, and docs gone
        /// with no quarantined name.</summary>
        internal bool GitDirPlantMoved { get; private set; }

        /// <summary>Gets whether host git in .git/foo/bar then ran it.</summary>
        internal bool GitDirPlantFiresAfter { get; private set; }

        private string Bar => Path.Combine(Project.Dir, ".git", "foo", "bar");

        private string Docs => Path.Combine(Project.Dir, "docs");

        protected override void Prepare()
        {
            CreateTree("node_modules", 5000, 20);
            CreateTree(Path.Combine("src", "obj"), 500, 10);
            var deep = Path.Combine("node_modules", "pkg4999", "dist", "deep");
            Project.RunGit("init", "-q", deep);

            var clock = Stopwatch.StartNew();
            Refusal = Cli.Run("run", "true");
            RefusalTime = clock.Elapsed;

            Files.DeleteTree(Path.Combine(Project.Dir, deep, ".git"));
            clock.Restart();
            Cli.Run("run", "true").RequireSuccess();
            SessionTime = clock.Elapsed;

            RunGitDirPlant();
        }

        /// <summary>Plants a repo in .git/foo/bar, with docs linked to it, on the host to see git run it; then has the
        /// agent plant the same.</summary>
        private void RunGitDirPlant()
        {
            var hook = Path.Combine(Scratch, "hook-gitdir");
            var canary = Path.Combine(Scratch, "canary-gitdir");
            File.WriteAllText(hook, $"#!/bin/sh\ntouch {canary}\n");
            File.SetUnixFileMode(hook, Files.Mode755);
            var plant = $"git init -q .git/foo/bar && git -C .git/foo/bar config core.fsmonitor '{hook}' " +
                "&& ln -s .git/foo/bar docs";

            new HostCommand("sh", ["-c", $"cd '{Project.Dir}' && {plant}"]).Run().RequireSuccess();
            GitDirPlantFiresBefore = Fires(Docs, canary);
            Files.DeleteTree(Path.Combine(Project.Dir, ".git", "foo"));
            File.Delete(Docs);

            GitDirPlant = Cli.Run("run", "bash", "-c", plant);
            GitDirPlantMoved = !Files.Exists(Path.Combine(Bar, ".git")) && !Files.Exists(Docs)
                && Files.IsDirectory(Path.Combine(Bar, ".git.pen-quarantine"))
                && !Files.Exists(Docs + ".pen-quarantine");
            GitDirPlantFiresAfter = Fires(Bar, canary);
            Files.DeleteTree(Path.Combine(Project.Dir, ".git", "foo"));
        }

        /// <summary>Runs host git status in <paramref name="dir"/> and checks whether it touched the canary.</summary>
        private static bool Fires(string dir, string canary)
        {
            File.Delete(canary);
            SmokeGit.CreateCommand(dir, "status").Run();
            return Files.Exists(canary);
        }

        /// <summary>Creates top/pkgN/lib (odd N) or top/pkgN/dist (even N), each with empty files f0.js….</summary>
        private void CreateTree(string top, int dirs, int files)
        {
            for (var d = 0; d < dirs; d++)
            {
                var dir = Path.Combine(Project.Dir, top, $"pkg{d}", d % 2 == 1 ? "lib" : "dist");
                Directory.CreateDirectory(dir);
                for (var f = 0; f < files; f++)
                    File.Create(Path.Combine(dir, $"f{f}.js")).Dispose();
            }
        }
    }
}
