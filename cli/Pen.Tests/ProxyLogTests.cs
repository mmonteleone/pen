using System.Text;

namespace Pen.Tests;

[Collection(nameof(LockAndProcessTests))]
public class ProxyLogTests
{
    private static SessionDir NewSession(TempDir t) =>
        new("pen-test-12345678", t.Sub("runtime", "sessions", "12345678"));

    private static string Line(int i) => $"2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT host{i}.example:443 -\n";

    /// <summary>A log over the limit, of numbered lines, written in one go.</summary>
    private static int FillOverLimit(string log)
    {
        using var w = new StreamWriter(log, append: false, Encoding.ASCII);
        int i = 0;
        long size = 0;
        while (size <= ProxyLogTrimmer.Limit)
        {
            var l = Line(i++);
            w.Write(l);
            size += l.Length;
        }
        return i;
    }

    /// <summary>What the proxy container does: the last Keep bytes, then a truncate in place (same file, so squid's fd stays valid).</summary>
    private static byte[] TailAndTruncate(string log)
    {
        using var f = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        long from = Math.Max(0, f.Length - ProxyLogTrimmer.Keep);
        f.Position = from;
        var tail = new byte[f.Length - from];
        f.ReadExactly(tail);
        f.SetLength(0);
        return tail;
    }

    [Fact]
    public void OverTheLimit_TheTailGoesToProxyLog1AndTheLogIsTruncatedInPlace()
    {
        using var t = new TempDir();
        var s = NewSession(t);
        int lines = FillOverLimit(s.ProxyLog);
        using var trimmer = new ProxyLogTrimmer(s, () => TailAndTruncate(s.ProxyLog), Timeout.InfiniteTimeSpan);

        Assert.True(trimmer.TrimIfOver());

        Assert.Equal(0, new FileInfo(s.ProxyLog).Length);
        var kept = File.ReadAllText(s.PreviousProxyLog);
        Assert.True(kept.Length <= ProxyLogTrimmer.Keep);
        Assert.True(kept.Length > ProxyLogTrimmer.Keep - Line(lines).Length * 2);
        // Whole lines only: the one the cut went through is dropped, and the last one is there.
        var keptLines = kept.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(keptLines, l => Assert.Matches(@"^2026-11-01T10:00:00Z 172\.18\.0\.3 TCP_DENIED/403 CONNECT host\d+\.example:443 -$", l));
        Assert.EndsWith(Line(lines - 1), kept);
        Assert.False(File.Exists(s.PreviousProxyLog + ".tmp"));

        // squid goes on appending, and the listing reads both files, oldest first.
        File.AppendAllText(s.ProxyLog, Line(lines));
        var read = s.ProxyLogLines().ToList();
        Assert.Equal(keptLines.Append(Line(lines).TrimEnd('\n')), read);
    }

    [Fact]
    public void ASecondTrimReplacesTheFirstTail_AndUnderTheLimitNothingHappens()
    {
        using var t = new TempDir();
        var s = NewSession(t);
        using var trimmer = new ProxyLogTrimmer(s, () => TailAndTruncate(s.ProxyLog), Timeout.InfiniteTimeSpan);
        File.WriteAllText(s.ProxyLog, Line(1));
        Assert.False(trimmer.TrimIfOver());
        Assert.False(File.Exists(s.PreviousProxyLog));
        Assert.Equal(Line(1), File.ReadAllText(s.ProxyLog));

        FillOverLimit(s.ProxyLog);
        Assert.True(trimmer.TrimIfOver());
        File.WriteAllText(s.ProxyLog, "");
        using (var w = new StreamWriter(s.ProxyLog))
            for (long size = 0; size <= ProxyLogTrimmer.Limit; size += Line(-1).Length) w.Write(Line(-1));
        Assert.True(trimmer.TrimIfOver());
        Assert.DoesNotContain("host1.example", File.ReadAllText(s.PreviousProxyLog));
        Assert.False(File.Exists(Path.Combine(s.Path, "proxy.log.2")));
    }

    [Fact]
    public void AFailedTailAndTruncate_LeavesBothFilesAlone()
    {
        using var t = new TempDir();
        var s = NewSession(t);
        FillOverLimit(s.ProxyLog);
        File.WriteAllText(s.PreviousProxyLog, "previous\n");
        var size = new FileInfo(s.ProxyLog).Length;
        using var trimmer = new ProxyLogTrimmer(s, () => null, Timeout.InfiniteTimeSpan);
        Assert.False(trimmer.TrimIfOver());
        Assert.Equal(size, new FileInfo(s.ProxyLog).Length);
        Assert.Equal("previous\n", File.ReadAllText(s.PreviousProxyLog));
    }

    [Fact]
    public void TheTimerTrims_AndStopsOnDispose_WaitingForATrimInProgress()
    {
        using var t = new TempDir();
        var s = NewSession(t);
        FillOverLimit(s.ProxyLog);
        int calls = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var trimmer = new ProxyLogTrimmer(s, () =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait();
            return TailAndTruncate(s.ProxyLog);
        }, TimeSpan.FromMilliseconds(10));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var disposed = new Thread(trimmer.Dispose);
        disposed.Start();
        Assert.False(disposed.Join(TimeSpan.FromMilliseconds(200))); // the session end waits for the trim
        Assert.False(File.Exists(s.PreviousProxyLog));
        release.Set();
        Assert.True(disposed.Join(TimeSpan.FromSeconds(10)));
        Assert.True(File.Exists(s.PreviousProxyLog));

        FillOverLimit(s.ProxyLog);
        int after = Volatile.Read(ref calls);
        Thread.Sleep(100);
        Assert.Equal(1, after);
        Assert.Equal(after, Volatile.Read(ref calls));
    }

    /// <summary>The command run in the proxy container, run here against a local file by a fake docker.</summary>
    [Fact]
    public void TheProxyContainerTailsThenTruncates()
    {
        using var t = new TempDir();
        var s = NewSession(t);
        File.WriteAllText(s.ComposeFile, "stack");
        var log = Path.Combine(t.Path, "log");
        File.WriteAllText(log, "first\nsecond\n");
        // compose ... exec -T proxy sh -c SCRIPT sh /var/log/squid/access.log
        using var fake = new FakeDocker(t, $$"""
            while [ "$1" != "-c" ]; do shift; done
            exec sh -c "$2" sh '{{log}}'
            """);
        var tail = ProxyLogTrimmer.TailAndTruncateInProxy(s, new Dictionary<string, string>());
        Assert.Equal("first\nsecond\n", Encoding.ASCII.GetString(tail!));
        Assert.Equal(0, new FileInfo(log).Length);
        var calls = File.ReadAllText(Path.Combine(t.Path, "calls"));
        Assert.Contains($"-p {s.ComposeProject} exec -T proxy sh -c", calls);
        Assert.EndsWith("sh /var/log/squid/access.log\n", calls);
    }

    [Fact]
    public void AFailingProxyCommandGivesNoTail()
    {
        using var t = new TempDir();
        var s = NewSession(t);
        File.WriteAllText(s.ComposeFile, "stack");
        using var fake = new FakeDocker(t, "echo partial; exit 1\n");
        Assert.Null(ProxyLogTrimmer.TailAndTruncateInProxy(s, new Dictionary<string, string>()));
    }

    [Fact]
    public void BlockedListingsReadBothFiles_Deduplicated()
    {
        using var t = new TempDir();
        var s = NewSession(t);
        File.WriteAllText(s.PreviousProxyLog,
            "2026-11-01T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT old.example:443 -\n" +
            "2026-11-01T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT both.example:443 -\n");
        File.WriteAllText(s.ProxyLog,
            "2026-11-02T10:00:00Z 172.18.0.3 TCP_DENIED/403 CONNECT both.example:443 -\n" +
            "2026-11-02T10:00:01Z 172.18.0.3 TCP_DENIED/403 CONNECT new.example:443 -\n");
        Assert.Equal(["old.example", "both.example", "new.example"], Allowlist.BlockedHosts(s.ProxyLogLines()));
        Assert.Equal(["new.example", "both.example", "old.example"], AllowCommand.BlockedAcross([s.ProxyLogLines()]).Select(b => b.Host));

        File.Delete(s.ProxyLog);
        Assert.Equal(["old.example", "both.example"], Allowlist.BlockedHosts(s.ProxyLogLines()));
    }
}
