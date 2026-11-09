using System.Diagnostics;

namespace Pen.Tests;

/// <summary>
/// The hash of .pen/ pins what docker build reads, and anything hostile means
/// no hash, so nothing can match an approval.
/// </summary>
[Collection(nameof(LockAndProcessTests))] // they start many processes, which briefly hold others' flocks through fork
public class PenDirHashTests
{
    private const UnixFileMode Rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Rwx = Rw | UnixFileMode.UserExecute;

    /// <summary>A .pen/ with a Dockerfile and a helper script.</summary>
    private static string Pen(TempDir t)
    {
        var pen = t.Sub("repo", ".pen");
        File.WriteAllText(Path.Combine(pen, "Dockerfile"), "FROM pen:latest\n");
        File.WriteAllText(Path.Combine(pen, "setup.sh"), "true\n");
        // Whatever the umask, so that each mode change below is a change.
        File.SetUnixFileMode(pen, Rwx);
        File.SetUnixFileMode(Path.Combine(pen, "Dockerfile"), Rw);
        File.SetUnixFileMode(Path.Combine(pen, "setup.sh"), Rw);
        return pen;
    }

    private static string Hash(string pen)
    {
        var r = PenDirHash.Compute(pen);
        Assert.Null(r.Problem);
        return Assert.IsType<string>(r.Hash);
    }

    /// <summary>Fails instead of hanging the suite if something blocks, and returns the problem.</summary>
    private static string Refused(string pen)
    {
        var task = Task.Factory.StartNew(() => PenDirHash.Compute(pen), TaskCreationOptions.LongRunning);
        Assert.True(task.Wait(TimeSpan.FromSeconds(20)), "the hash hung");
        Assert.Null(task.Result.Hash);
        return Assert.IsType<string>(task.Result.Problem);
    }

    private static void Sh(string dir, string script)
    {
        var r = Process.RunAndCaptureText(new ProcessStartInfo("sh", ["-c", script]) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.True(r.ExitStatus.ExitCode == 0, r.StandardError);
    }

    public static TheoryData<string, string> ChangesToTheContext => new()
    {
        { "new content", "printf 'false\\n' >setup.sh" },
        { "renamed", "mv setup.sh setup2.sh" },
        { "made executable", "chmod +x setup.sh" },
        { "group-writable", "chmod g+w setup.sh" },
        { "setuid", "chmod u+s setup.sh" },
        { "removed", "rm setup.sh" },
        { "a new file", "printf x >new" },
        { "a new empty file", ": >new" },
        { "a new empty directory", "mkdir new" },
        { "a directory's mode", "mkdir -m 700 d; chmod 755 d" },
        { "a dot file", "printf '*\\n' >.dockerignore" },
        { "a file in a directory", "mkdir d; printf x >d/f" },
        { "a nested config.toml", "mkdir d; printf x >d/config.toml" },
        { "replaced by a symlink to the same content", "cp setup.sh other; rm setup.sh; ln -s other setup.sh" },
        { "a new symlink", "ln -s setup.sh link" },
    };

    [Theory]
    [MemberData(nameof(ChangesToTheContext))]
    public void AnyChangeToTheContextChangesTheHash(string what, string change)
    {
        using var t = new TempDir();
        var pen = Pen(t);
        var before = Hash(pen);
        Sh(pen, change);
        Assert.True(before != Hash(pen), what);
    }

    [Fact]
    public void RetargetingASymlinkChangesTheHash()
    {
        using var t = new TempDir();
        var pen = Pen(t);
        File.CreateSymbolicLink(Path.Combine(pen, "link"), "setup.sh");
        var before = Hash(pen);
        File.Delete(Path.Combine(pen, "link"));
        File.CreateSymbolicLink(Path.Combine(pen, "link"), "Dockerfile");
        Assert.NotEqual(before, Hash(pen));
    }

    [Fact]
    public void BytesMovedBetweenANameAndAContentOrAcrossFilesChangeTheHash()
    {
        string With(params (string Name, string Content)[] files)
        {
            using var t = new TempDir();
            var pen = t.Sub("repo", ".pen");
            foreach (var (name, content) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(pen, name))!);
                File.WriteAllText(Path.Combine(pen, name), content);
                File.SetUnixFileMode(Path.Combine(pen, name), Rw);
            }
            return Hash(pen);
        }

        Assert.NotEqual(With(("ab", "c")), With(("a", "bc")));
        Assert.NotEqual(With(("a", "bc"), ("d", "")), With(("a", "b"), ("d", "c")));
        Assert.NotEqual(With(("a", ""), ("b", "x")), With(("a", "b"), ("x", "")));
        Assert.NotEqual(With(("d/f", "x")), With(("df", "x")));
    }

    [Fact]
    public void TheHashDoesntDependOnOrderTimesOrConfigToml()
    {
        using var t = new TempDir();
        var a = t.Sub("a", ".pen");
        var b = t.Sub("b", ".pen");
        foreach (var (dir, names) in new[] { (a, new[] { "x", "y", "z" }), (b, new[] { "z", "y", "x" }) })
        {
            File.SetUnixFileMode(dir, Rwx);
            foreach (var name in names)
            {
                File.WriteAllText(Path.Combine(dir, name), name);
                File.SetUnixFileMode(Path.Combine(dir, name), Rw);
            }
        }
        Assert.Equal(Hash(a), Hash(b));

        var before = Hash(a);
        File.SetLastWriteTimeUtc(Path.Combine(a, "x"), DateTime.UnixEpoch);
        File.WriteAllText(Path.Combine(a, Config.FileName), "allow = [\"a.example\"]\n");
        Assert.Equal(before, Hash(a));
        File.WriteAllText(Path.Combine(a, Config.FileName), "# edited\n");
        Assert.Equal(before, Hash(a));
    }

    [Fact]
    public void ACopyHashesLikeTheOriginal_AndKeepsWhatABuildSees()
    {
        using var t = new TempDir();
        var pen = Pen(t);
        Sh(pen, """
            mkdir -p d/empty ro; printf x >ro/f; chmod 755 d; chmod 555 ro
            printf 'secret\n' >.dockerignore; printf s >secret; printf c >config.toml
            chmod 755 setup.sh; chmod u+s setup.sh; chmod 640 secret
            ln -s setup.sh link; ln -s ../setup.sh d/uplink
            touch -t 200102030405 setup.sh; touch -t 200203040506 d
            """);
        var copy = Path.Combine(t.Sub("private"), "context");

        var hash = PenDirHash.Snapshot(pen, copy);
        try
        {
            Assert.Equal(Hash(pen), hash.Hash);
            Assert.Equal(Hash(pen), Hash(copy));
            Assert.False(Path.Exists(Path.Combine(copy, "config.toml")));
            Assert.Equal("secret\n", File.ReadAllText(Path.Combine(copy, ".dockerignore")));
            Assert.True(Directory.Exists(Path.Combine(copy, "d", "empty")));
            Assert.Equal("setup.sh", new FileInfo(Path.Combine(copy, "link")).LinkTarget);
            Assert.Equal("../setup.sh", new FileInfo(Path.Combine(copy, "d", "uplink")).LinkTarget);
            Assert.Equal(File.GetUnixFileMode(Path.Combine(pen, "setup.sh")), File.GetUnixFileMode(Path.Combine(copy, "setup.sh")));
            Assert.Equal(File.GetUnixFileMode(Path.Combine(pen, "ro")), File.GetUnixFileMode(Path.Combine(copy, "ro")));
            Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(pen, "setup.sh")), File.GetLastWriteTimeUtc(Path.Combine(copy, "setup.sh")));
            Assert.Equal(Directory.GetLastWriteTimeUtc(Path.Combine(pen, "d")), Directory.GetLastWriteTimeUtc(Path.Combine(copy, "d")));
        }
        finally
        {
            Sh(t.Path, "chmod -R u+rwx repo private");
        }
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public async Task HostileContentIsntCopied(string what, string plant)
    {
        using var t = new TempDir();
        var pen = Pen(t);
        Sh(pen, plant);
        var copy = Path.Combine(t.Sub("private"), "context");
        var result = await Task.Factory.StartNew(() => PenDirHash.Snapshot(pen, copy), TaskCreationOptions.LongRunning)
            .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken); // throws if it hung
        Assert.Null(result.Hash);
        Assert.False(File.Exists(Path.Combine(copy, "huge")), what);
    }

    public static TheoryData<string, string> Hostile => new()
    {
        { "a FIFO", "mkfifo pipe" },
        { "a FIFO in a directory", "mkdir d; mkfifo d/pipe" },
        { "a FIFO as config.toml", "mkfifo config.toml" },
        { "a directory as config.toml, which isn't hashed", "mkdir config.toml; printf x >config.toml/evil" },
        { "a symlink as config.toml", "ln -s setup.sh config.toml" },
        { "a sparse file far over the limit", "dd if=/dev/null of=huge bs=1 seek=1099511627776 2>/dev/null" },
        { "a symlink to itself", "ln -s loop loop" },
        { "a pair of symlinks", "ln -s b a; ln -s a b" },
        { "a dangling symlink", "ln -s nothing link" },
        { "a symlink out of .pen/", "ln -s ../outside link" },
        { "an absolute symlink", "ln -s /etc/passwd link" },
        { "a symlink chain", "ln -s setup.sh one; ln -s one two" },
        { "a symlink through a symlink, to get out", "mkdir s; ln -s .. s/up; ln -s s/up/.. out" },
        { "a Dockerfile symlink to outside", "rm Dockerfile; printf 'FROM evil\\n' >../Dockerfile.real; ln -s ../Dockerfile.real Dockerfile" },
        { "a Dockerfile symlink to config.toml, which isn't hashed", "rm Dockerfile; : >config.toml; ln -s config.toml Dockerfile" },
    };

    [Theory]
    [MemberData(nameof(Hostile))]
    public void HostileContentHasNoHash(string what, string plant)
    {
        using var t = new TempDir();
        var pen = Pen(t);
        Sh(pen, plant);
        Assert.False(string.IsNullOrEmpty(Refused(pen)), what);
    }

    [Fact]
    public void ALargeTreeHasNoHash()
    {
        using var t = new TempDir();
        var pen = Pen(t);
        Sh(pen, "mkdir d; cd d; i=0; while [ $i -lt 10010 ]; do : >$i; i=$((i+1)); done");
        Assert.Contains("over", Refused(pen));
    }

    [Fact]
    public void ADeepTreeHasNoHash()
    {
        using var t = new TempDir();
        var pen = Pen(t);
        Sh(pen, "i=0; while [ $i -lt 40 ]; do mkdir d; cd d; i=$((i+1)); done");
        Assert.Contains("deeply", Refused(pen));
    }

    [Fact]
    public void ASymlinkedPenDirHasNoHash()
    {
        using var t = new TempDir();
        var real = t.Sub("elsewhere");
        File.WriteAllText(Path.Combine(real, "Dockerfile"), "FROM pen:latest\n");
        var repo = t.Sub("repo");
        var link = Path.Combine(repo, ".pen");
        File.CreateSymbolicLink(link, real);
        Assert.Contains("symlink", Refused(link));
    }

    [Fact]
    public void AMissingPenDirHasNoHash_RatherThanAnException()
    {
        using var t = new TempDir();
        Refused(Path.Combine(t.Path, "nothing"));
    }

    [Fact]
    public void UnreadableFilesAndDirectoriesHaveNoHash()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads everything");
        using var t = new TempDir();
        var pen = Pen(t);
        var script = Path.Combine(pen, "setup.sh");
        File.SetUnixFileMode(script, UnixFileMode.None);
        Refused(pen);
        File.SetUnixFileMode(script, Rw);

        var d = t.Sub("repo", ".pen", "d");
        File.SetUnixFileMode(d, UnixFileMode.None);
        try { Refused(pen); }
        finally { File.SetUnixFileMode(d, Rwx); }

        File.WriteAllText(Path.Combine(pen, Config.FileName), "");
        File.SetUnixFileMode(Path.Combine(pen, Config.FileName), UnixFileMode.None);
        Refused(pen);
    }

    /// <summary>Linux only: APFS refuses such names, and these skip there.</summary>
    [Theory]
    [InlineData("touch \"$b\"")]
    [InlineData("mkdir \"$b\"")]
    [InlineData("mkdir d; touch \"d/$b\"")]
    [InlineData("ln -s setup.sh \"$b\"")]
    public void ANameThatIsntValidUtf8HasNoHash(string plant)
    {
        using var t = new TempDir();
        var pen = Pen(t);
        var psi = new ProcessStartInfo("sh", ["-c", $"b=$(printf 'bad\\377name') && {plant}"]) { WorkingDirectory = pen, RedirectStandardOutput = true, RedirectStandardError = true };
        var r = Process.RunAndCaptureText(psi);
        Assert.SkipWhen(r.ExitStatus.ExitCode != 0 && r.StandardError.Contains("llegal byte sequence", StringComparison.Ordinal), "the file system refuses names that aren't valid UTF-8");
        Assert.True(r.ExitStatus.ExitCode == 0, r.StandardError);
        Assert.Contains("UTF-8", Refused(pen));
    }

    [Fact]
    public void ASymlinkInsidePenIsFine()
    {
        using var t = new TempDir();
        var pen = Pen(t);
        Sh(pen, "mkdir -p d/e; ln -s ../setup.sh d/link; ln -s d dirlink; ln -s ../.. d/e/up");
        Hash(pen);
    }
}
