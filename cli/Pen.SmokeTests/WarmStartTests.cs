using System.Diagnostics;
using System.Globalization;

namespace Pen.SmokeTests;

/// <summary>How long pen run true takes once the image and the project are set up.</summary>
public sealed class WarmStartTests(WarmStartTests.Fixture fixture) : IClassFixture<WarmStartTests.Fixture>
{
    [Fact(DisplayName = "warm start: pen run true in under 5 s (best of 3: …s; target about 2-3 s)")]
    public void WarmStartIsQuick()
    {
        var best = fixture.Best.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture);

        Assert.True(
            fixture.Best.TotalSeconds < 5,
            $"warm start: pen run true in under 5 s (best of 3: {best}s; target about 2-3 s)");
    }

    /// <summary>Times three sessions of pen run true after the first.</summary>
    public sealed class Fixture : SmokeFixture
    {
        /// <summary>Gets the quickest of the three.</summary>
        internal TimeSpan Best { get; private set; } = TimeSpan.MaxValue;

        protected override void Prepare()
        {
            for (var i = 0; i < 3; i++)
            {
                var clock = Stopwatch.StartNew();
                Cli.Run("run", "true").RequireSuccess();
                if (clock.Elapsed < Best)
                    Best = clock.Elapsed;
            }
        }
    }
}
