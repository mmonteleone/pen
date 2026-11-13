using System.Diagnostics;

namespace Pen.Tests;

/// <summary>
/// Runs alone: a child that another test forks in parallel briefly shares
/// every open fd until it execs, and a lock fd shared that way keeps its lock.
/// </summary>
[CollectionDefinition(nameof(LockAndProcessTests), DisableParallelization = true)]
public sealed class LockCollection;

[Collection(nameof(LockAndProcessTests))]
public class LockAndProcessTests
{
    [Fact]
    public void ExclusiveConflictsWithEverything_SharedWithShared()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "x.lock");

        using (var s1 = FileLock.TryTake(path, exclusive: false))
        using (var s2 = FileLock.TryTake(path, exclusive: false))
        {
            Assert.NotNull(s1);
            Assert.NotNull(s2);
            Assert.Null(FileLock.TryTake(path, exclusive: true));
        }

        using (var x = FileLock.TryTake(path, exclusive: true))
        {
            Assert.NotNull(x);
            Assert.Null(FileLock.TryTake(path, exclusive: true));
            Assert.Null(FileLock.TryTake(path, exclusive: false));
        }

        using var again = FileLock.TryTake(path, exclusive: true);
        Assert.NotNull(again);
    }

    [Fact]
    public void LockHeldByAnotherProcessIsRefused_AndFreeOnceItIsKilled()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "session.lock");
        var psi = new ProcessStartInfo("python3", ["-c",
            "import fcntl, sys, time\n" +
            "f = open(sys.argv[1], 'a')\n" +
            "fcntl.flock(f, fcntl.LOCK_EX)\n" +
            "print('locked', flush=True)\n" +
            "time.sleep(120)\n", path])
        { RedirectStandardOutput = true };
        using var holder = Process.Start(psi)!;
        try
        {
            Assert.Equal("locked", holder.StandardOutput.ReadLine());
            Assert.Null(FileLock.TryTake(path, exclusive: true));
            Assert.Null(FileLock.TryTake(path, exclusive: false));
        }
        finally
        {
            holder.Kill();
            holder.WaitForExit();
        }
        using var l = FileLock.TryTake(path, exclusive: true);
        Assert.NotNull(l);
    }

    [Fact]
    public void ChildrenDontInheritLockFds()
    {
        using var t = new TempDir();
        using var l = FileLock.TryTake(Path.Combine(t.Path, "p.lock"), exclusive: false)!;
        var fd = (int)l.Handle.DangerousGetHandle();
        Assert.True(fd > 2);

        // The lock's fd number isn't open in a child (ls itself opens a few).
        var r = Proc.Capture("sh", ["-c", "ls /dev/fd/"]);
        var fds = r.StandardOutput.Split((char[])['\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        Assert.Contains(0, fds);
        Assert.DoesNotContain(fd, fds);

        // And with lsof (as the plan's check uses), the child holds only 0-2.
        var lsof = Proc.Capture("sh", ["-c", "command -v lsof >/dev/null || exit 77; lsof -a -p $$ -d 0-999 -F f; true"]);
        Assert.SkipWhen(lsof.ExitStatus.ExitCode == 77, "no lsof");
        var open = lsof.StandardOutput.Split('\n').Where(x => x.StartsWith('f')).Select(x => int.Parse(x[1..])).ToList();
        Assert.Equal([0, 1, 2], open);
    }

    [Fact]
    public void ExitCodes()
    {
        Assert.Equal(3, Proc.Run("sh", ["-c", "exit 3"]));
        Assert.Equal(128 + 15, Proc.Run("sh", ["-c", "kill -TERM $$"]));
        var e = Assert.Throws<PenException>(() => Proc.Run("pen-no-such-binary", []));
        Assert.Contains("pen-no-such-binary", e.Message);
    }

    [Fact]
    public void DeadSessionScan_PrunesOldEndedSessions_SkipsLiveOnes()
    {
        using var t = new TempDir();
        var runtime = t.Sub("runtime");
        var oldEnded = t.Sub("runtime", "sessions", "aaaa0001");
        File.WriteAllText(Path.Combine(oldEnded, "proxy.log"), "");
        var recentEnded = t.Sub("runtime", "sessions", "aaaa0002");
        File.WriteAllText(Path.Combine(recentEnded, "proxy.log"), "");
        var live = t.Sub("runtime", "sessions", "aaaa0003");
        File.WriteAllText(Path.Combine(live, "compose.yaml"), "");

        var old = DateTime.UtcNow - Session.KeepEndedSessions - TimeSpan.FromHours(1);
        File.SetLastWriteTimeUtc(Path.Combine(oldEnded, "proxy.log"), old);
        Directory.SetLastWriteTimeUtc(oldEnded, old);

        using (FileLock.TryTake(Path.Combine(live, "session.lock"), exclusive: true))
            Session.FinishDeadSessions("pen-x-00000000", runtime);

        Assert.False(Directory.Exists(oldEnded));
        Assert.True(Directory.Exists(recentEnded));
        Assert.True(File.Exists(Path.Combine(live, "compose.yaml"))); // its lock was held: left alone
    }

    [Fact]
    public void ImageContextAndHash()
    {
        Assert.Equal(Embedded.ComputeImageHash(), Embedded.ComputeImageHash());
        using var ms = new MemoryStream();
        Embedded.WriteImageContext(ms);
        ms.Position = 0;
        using var r = new System.Formats.Tar.TarReader(ms);
        var names = new List<string>();
        while (r.GetNextEntry() is { } e) names.Add(e.Name);
        Assert.Contains("Dockerfile", names);
        Assert.Contains("entrypoint.sh", names);
        Assert.Equal(Embedded.ListUnder("image/").Count, names.Count);
    }
}
