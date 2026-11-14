using System.Diagnostics;
using System.Globalization;

namespace Pen.SmokeTests;

/// <summary>The nested-repo scan over a 100k-file node_modules and obj/ trees: it finds a repo at the bottom, and
/// quickly.</summary>
/// <remarks>
/// The planted repo makes pen refuse right after the scan, before any Docker call, so the refusal's time is the
/// scan's (plus pen's own startup).
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
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"a whole session with that tree (start and end scans included): {fixture.SessionSeconds}s");

        Assert.True(
            fixture.RefusalTime.TotalSeconds < 1.2, $"...in under about 1 s with 100k+ files (took {seconds}s)");
    }

    /// <summary>Fills the project with the trees, times a refused session with a repo planted at the bottom, then a
    /// whole session without it (only noted, as smoke.sh did).</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal CommandResult Refusal { get; private set; } = null!;

        internal TimeSpan RefusalTime { get; private set; }

        /// <summary>Gets how long a whole session with the trees took, in seconds, for the note.</summary>
        internal string SessionSeconds { get; private set; } = null!;

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
            SessionSeconds = clock.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);
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
