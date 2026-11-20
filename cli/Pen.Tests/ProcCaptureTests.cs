using System.Diagnostics;
using System.Text;

namespace Pen.Tests;

/// <summary>
/// <see cref="Proc.CaptureBytes"/> against real children. In the lock collection, since one test checks that a
/// lock's fd doesn't reach the child.
/// </summary>
[Collection(nameof(LockAndProcessTests))]
public class ProcCaptureTests
{
    // Long enough that a deadlocked or unkilled child fails the test rather than hanging it.
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    private static CapturedBytes Sh(string script, byte[]? stdin = null, int? limit = null, TimeSpan? timeout = null,
        params string[] args) =>
        Proc.CaptureBytes(Proc.CreateStartInfo("sh", ["-c", script, "sh", .. args]), stdin, limit, timeout);

    [Fact]
    public void BothStreamsAreReadAtOnce_SoNeitherPipeFillsUp()
    {
        // 2 MB to each, interleaved in 64 KB pieces: far past a pipe's buffer on either.
        var result = Sh("""
            i=0
            while [ $i -lt 32 ]; do
              head -c 65536 /dev/zero
              head -c 65536 /dev/zero | tr '\0' e >&2
              i=$((i + 1))
            done
            """, timeout: Generous);

        Assert.Equal(CaptureEnd.Exited, result.End);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(32 * 65536, result.Stdout.Length);
        Assert.All(result.Stdout, b => Assert.Equal(0, b));
        Assert.Equal(32 * 65536, result.Stderr.Length);
        Assert.All(result.Stderr, b => Assert.Equal((byte)'e', b));
    }

    [Fact]
    public void OutputIsKeptByteForByte()
    {
        // NUL, invalid UTF-8 and no trailing newline survive.
        var result = Sh(@"printf 'a\000b\377\376c'; printf 'x\200y' >&2", timeout: Generous);

        Assert.Equal(CaptureEnd.Exited, result.End);
        Assert.Equal("a\0b"u8.ToArray().Concat(new byte[] { 0xff, 0xfe, (byte)'c' }), result.Stdout);
        Assert.Equal(new byte[] { (byte)'x', 0x80, (byte)'y' }, result.Stderr);
    }

    [Fact]
    public void StdinIsWrittenWhileOutputIsRead()
    {
        // cat echoes as it reads, so 2 MB in deadlocks unless stdout is read while stdin is written.
        var input = new byte[2 << 20];
        new Random(7).NextBytes(input);

        var result = Proc.CaptureBytes(Proc.CreateStartInfo("cat", []), input, timeout: Generous);

        Assert.Equal(CaptureEnd.Exited, result.End);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(input, result.Stdout);
    }

    [Fact]
    public void WithoutBytes_StdinIsClosed()
    {
        // A child that reads stdin sees its end at once, rather than the terminal or a pipe left open.
        var result = Sh("if read -r line; then echo got; else echo eof; fi; cat", timeout: Generous);

        Assert.Equal(CaptureEnd.Exited, result.End);
        Assert.Equal("eof\n", Encoding.ASCII.GetString(result.Stdout));
    }

    [Fact]
    public void AChildThatDoesntReadStdin_IsntAnError()
    {
        var result = Sh("exit 4", new byte[1 << 20], timeout: Generous);

        Assert.Equal(CaptureEnd.Exited, result.End);
        Assert.Equal(4, result.ExitCode);
    }

    [Fact]
    public void ExitCodes_IncludingASignalAsAShellReportsIt()
    {
        Assert.Equal(3, Sh("exit 3").ExitCode);
        Assert.Equal(128 + 15, Sh("kill -TERM $$").ExitCode);
        Assert.Equal(128 + 9, Sh("kill -KILL $$").ExitCode);
        Assert.Equal(128 + 11, Sh("kill -SEGV $$").ExitCode);
        Assert.Equal(CaptureEnd.Exited, Sh("kill -KILL $$").End);
    }

    [Fact]
    public void AMissingProgram_GivesTheSameMessageAsEveryOtherRun()
    {
        var e = Assert.Throws<PenException>(() =>
            Proc.CaptureBytes(Proc.CreateStartInfo("pen-no-such-binary", [])));

        Assert.Equal("pen-no-such-binary not found: is it installed and on PATH?", e.Message);
    }

    [Fact]
    public void AMissingProgram_GivesTheSameMessageWhenCapturedAsText()
    {
        var startInfo = Proc.CreateStartInfo("pen-no-such-binary", []);
        startInfo.RedirectStandardOutput = startInfo.RedirectStandardError = true;

        var e = Assert.Throws<PenException>(() => Proc.CaptureText(startInfo));

        Assert.Equal("pen-no-such-binary not found: is it installed and on PATH?", e.Message);
    }

    [Fact]
    public void OutputUpToTheLimit_IsKept()
    {
        var result = Sh("head -c 100000 /dev/zero; head -c 100000 /dev/zero >&2", limit: 100000, timeout: Generous);

        Assert.Equal(CaptureEnd.Exited, result.End);
        Assert.Equal(100000, result.Stdout.Length);
        Assert.Equal(100000, result.Stderr.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData(">&2")]
    public void OutputPastTheLimit_KillsTheChildAtOnce(string redirect)
    {
        using var t = new TempDir();
        var pidFile = Path.Combine(t.Path, "pid");
        var watch = Stopwatch.StartNew();
        try
        {
            // The grandchild is in the tree, so it's gone only if the over-limit path killed the tree.
            var result = Sh($"sleep 60 & echo $! > \"$1\"; head -c 100001 /dev/zero {redirect}; sleep 60",
                limit: 100000, timeout: Generous, args: pidFile);

            Assert.Equal(CaptureEnd.OverLimit, result.End);
            Assert.Equal(-1, result.ExitCode);
            Assert.Empty(result.Stdout);
            Assert.Empty(result.Stderr);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
            var grandchild = int.Parse(File.ReadAllText(pidFile).Trim());
            Assert.True(WaitUntilGone(grandchild), $"sleep {grandchild} still runs");
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var survivor))
                Proc.CaptureBytes(Proc.CreateStartInfo("kill", [survivor.ToString()]), timeout: Generous);
        }
    }

    [Fact]
    public void AChildPastTheTimeout_IsKilledWithItsProcessTree()
    {
        using var t = new TempDir();
        var pidFile = Path.Combine(t.Path, "pid");
        var watch = Stopwatch.StartNew();

        // The grandchild holds stdout open too, so the capture ends only if it's killed as well.
        var result = Sh("""
            sleep 60 &
            echo $! > "$1"
            echo started
            wait
            """, timeout: TimeSpan.FromSeconds(1), args: pidFile);

        Assert.Equal(CaptureEnd.TimedOut, result.End);
        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"took {watch.Elapsed}");
        var grandchild = int.Parse(File.ReadAllText(pidFile).Trim());
        Assert.True(WaitUntilGone(grandchild), $"sleep {grandchild} still runs");
    }

    [Theory]
    [InlineData("(sleep 20 & echo $! > \"$1\"); sleep 60", 0)]
    [InlineData("sleep 20 & echo $! > \"$1\"; exit 0", 0)]
    [InlineData("sleep 20 <&0 & echo $! > \"$1\"; exit 0", 8 << 20)]
    public void ADescendantThatLeftTheProcessTree_IsntWaitedFor(string script, int stdinBytes)
    {
        // The orphaned sleep holds stdout and stderr (and in the last case stdin, with far more queued than a pipe
        // holds) open past the kill, which can't reach it.
        using var t = new TempDir();
        var pidFile = Path.Combine(t.Path, "pid");
        var watch = Stopwatch.StartNew();
        try
        {
            var result = Sh(script, stdinBytes > 0 ? new byte[stdinBytes] : null, timeout: TimeSpan.FromSeconds(1),
                args: pidFile);

            Assert.Equal(CaptureEnd.TimedOut, result.End);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
        }
        finally
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var orphan))
                Proc.CaptureBytes(Proc.CreateStartInfo("kill", [orphan.ToString()]), timeout: Generous);
        }
    }

    [Fact]
    public void AChildThatClosesItsOutputButDoesntExit_TimesOut()
    {
        var result = Sh("exec >&- 2>&-; sleep 60", timeout: TimeSpan.FromSeconds(1));

        Assert.Equal(CaptureEnd.TimedOut, result.End);
    }

    [Fact]
    public void ChildrenDontInheritLockFds()
    {
        using var t = new TempDir();
        using var l = FileLock.TryTake(Path.Combine(t.Path, "p.lock"), exclusive: false)!;
        var fd = (int)l.Handle.DangerousGetHandle();
        Assert.True(fd > 2);

        var r = Sh("ls /dev/fd/", timeout: Generous);
        var fds = Encoding.ASCII.GetString(r.Stdout)
            .Split((char[])['\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        Assert.Contains(0, fds);
        Assert.DoesNotContain(fd, fds);

        var lsof = Sh("command -v lsof >/dev/null || exit 77; lsof -a -p $$ -d 0-999 -F f; true", timeout: Generous);
        Assert.SkipWhen(lsof.ExitCode == 77, "no lsof");
        var open = Encoding.ASCII.GetString(lsof.Stdout).Split('\n')
            .Where(x => x.StartsWith('f')).Select(x => int.Parse(x[1..])).ToList();
        Assert.Equal([0, 1, 2], open);
    }

    /// <summary>Waits a little for <paramref name="pid"/> to be gone, since a kill isn't instant.</summary>
    private static bool WaitUntilGone(int pid)
    {
        for (var i = 0; i < 50; i++)
        {
            var probe = Proc.CaptureBytes(Proc.CreateStartInfo("kill", ["-0", pid.ToString()]), timeout: Generous);
            if (probe.ExitCode != 0)
                return true;
            Thread.Sleep(100);
        }

        return false;
    }
}
