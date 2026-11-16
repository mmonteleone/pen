using System.Diagnostics;
using System.Net.Sockets;

namespace Pen.Tests;

/// <summary>
/// Native.ReadInsideProject reads a regular file below a root without following a symlink anywhere on the way, and
/// refuses anything else, a FIFO included, without blocking.
/// </summary>
public class ReadInsideProjectTests
{
    private static void Run(string command, params string[] args)
    {
        var r = Process.RunAndCaptureText(new ProcessStartInfo(command, args) { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, r.ExitStatus.ExitCode);
    }

    /// <summary>Fails instead of hanging the suite if the read blocks opening a FIFO.</summary>
    private static Exception? ThrowsWithin10s(Action action)
    {
        var task = Task.Factory.StartNew(() => Record.Exception(action), TaskCreationOptions.LongRunning);
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "blocked: a FIFO was opened");
        return task.Result;
    }

    [Fact]
    public void RefusesAUnixSocketAsNotARegularFile()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "s.sock");
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));

        var e = Assert.Throws<IOException>(() => Native.ReadInsideProject(t.Path, "s.sock"));

        Assert.Contains("isn't a regular file", e.Message);
    }

    [Fact]
    public void ReadsANestedRegularFilesBytesAndMode()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Sub("a", "b"), "agent.env");
        File.WriteAllBytes(file, [1, 2, 0, 255]);
        var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(file, mode);

        var read = Native.ReadInsideProject(t.Path, "a/b/agent.env");

        Assert.Equal(new byte[] { 1, 2, 0, 255 }, read.Bytes);
        Assert.Equal(mode, read.Mode);
    }

    [Fact]
    public void ReadsAFileAtTheTopAndAnEmptyFile()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, ".env"), "");
        Assert.Empty(Native.ReadInsideProject(t.Path, ".env").Bytes);
    }

    [Fact]
    public void RefusesASymlinkAsTheFile()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "real"), "x");
        File.CreateSymbolicLink(Path.Combine(t.Path, "link"), "real");

        var e = Assert.Throws<IOException>(() => Native.ReadInsideProject(t.Path, "link"));
        Assert.Equal($"{t.Path}/link is a symlink", e.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RefusesASymlinkedDirectoryOnTheWay_InsideTheRootOrNot(bool inside)
    {
        using var t = new TempDir();
        var root = t.Sub("root");
        var target = inside ? t.Sub("root", "real") : t.Sub("outside");
        File.WriteAllText(Path.Combine(target, "f"), "secret");
        t.Sub("root", "a");
        File.CreateSymbolicLink(Path.Combine(root, "a", "link"), target);

        var e = Assert.Throws<IOException>(() => Native.ReadInsideProject(root, "a/link/f"));
        Assert.Equal($"{root}/a/link isn't a directory (a symlink, say)", e.Message);
    }

    [Fact]
    public void RefusesAFileWhereADirectoryShouldBe()
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "a"), "x");
        var e = Assert.Throws<IOException>(() => Native.ReadInsideProject(t.Path, "a/f"));
        Assert.Equal($"{t.Path}/a isn't a directory (a symlink, say)", e.Message);
    }

    [Fact]
    public void RefusesADirectory()
    {
        using var t = new TempDir();
        t.Sub("d");
        var e = Assert.Throws<IOException>(() => Native.ReadInsideProject(t.Path, "d"));
        Assert.Equal($"{t.Path}/d isn't a regular file", e.Message);
    }

    [Fact]
    public void RefusesAFifoWithoutBlocking()
    {
        using var t = new TempDir();
        var fifo = Path.Combine(t.Sub("d"), "pipe");
        Run("mkfifo", fifo);

        var e = ThrowsWithin10s(() => Native.ReadInsideProject(t.Path, "d/pipe"));
        Assert.Equal($"{fifo} isn't a regular file", Assert.IsType<IOException>(e).Message);
    }

    [Fact]
    public void ReadsExactly1MbAndRefusesMore()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "f");
        File.WriteAllBytes(file, new byte[1024 * 1024]);
        Assert.Equal(1024 * 1024, Native.ReadInsideProject(t.Path, "f").Bytes.Length);

        File.AppendAllText(file, "x");
        var e = Assert.Throws<IOException>(() => Native.ReadInsideProject(t.Path, "f"));
        Assert.Equal($"{file} is over 1 MB or changed while it was read", e.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../f")]
    [InlineData("a/../../f")]
    [InlineData("a/../f")]
    [InlineData(".")]
    [InlineData("./f")]
    [InlineData("a//f")]
    [InlineData("a/")]
    [InlineData("/etc/passwd")]
    [InlineData("f\0x")]
    public void RefusesPathsThatArentPlainlyBelowTheRoot(string relative)
    {
        using var t = new TempDir();
        File.WriteAllText(Path.Combine(t.Path, "f"), "x");
        t.Sub("a");
        var e = Assert.Throws<IOException>(() => Native.ReadInsideProject(t.Sub("a"), relative));
        Assert.Equal($"{relative} isn't a path below {t.Path}/a", e.Message);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("gone/f")]
    public void AMissingFileOrDirectoryIsNotFound(string relative)
    {
        using var t = new TempDir();
        var e = Assert.Throws<FileNotFoundException>(() => Native.ReadInsideProject(t.Path, relative));
        Assert.EndsWith(" doesn't exist", e.Message);
    }

    [Fact]
    public void AShutDirectoryIsUnauthorized()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads any directory");
        using var t = new TempDir();
        var shut = t.Sub("shut");
        File.WriteAllText(Path.Combine(shut, "f"), "x");
        File.SetUnixFileMode(shut, UnixFileMode.None);
        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Native.ReadInsideProject(t.Path, "shut/f"));
        }
        finally
        {
            File.SetUnixFileMode(shut, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
