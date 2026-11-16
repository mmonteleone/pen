using System.Diagnostics;

namespace Pen.Tests;

/// <summary>Native.Stat reports a file's link count.</summary>
public class FileStatusTests
{
    [Fact]
    public void APlainFileHasOneLinkAndAHardLinkedOneTwo()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "f");
        File.WriteAllText(file, "x");
        Assert.Equal(1UL, Native.Stat(file, follow: false)!.Value.Links);

        var other = Path.Combine(t.Path, "other");
        var r = Process.RunAndCaptureText(new ProcessStartInfo("ln", [file, other]) { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, r.ExitStatus.ExitCode);

        Assert.Equal(2UL, Native.Stat(file, follow: false)!.Value.Links);
        Assert.Equal(2UL, Native.Stat(other, follow: false)!.Value.Links);
        Assert.Equal(Native.Stat(file, follow: false)!.Value.Inode, Native.Stat(other, follow: false)!.Value.Inode);
    }

    [Fact]
    public void ASymlinkCountsItsOwnLinksUnlessFollowed()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "f");
        File.WriteAllText(file, "x");
        var r = Process.RunAndCaptureText(new ProcessStartInfo("ln", [file, Path.Combine(t.Path, "g")]) { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, r.ExitStatus.ExitCode);
        var link = Path.Combine(t.Path, "link");
        File.CreateSymbolicLink(link, file);

        Assert.Equal(1UL, Native.Stat(link, follow: false)!.Value.Links);
        Assert.Equal(2UL, Native.Stat(link, follow: true)!.Value.Links);
    }
}
