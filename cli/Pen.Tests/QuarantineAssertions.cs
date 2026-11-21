namespace Pen.Tests;

internal static class QuarantineAssertions
{
    internal static Quarantined DirectoryWithoutChangingMode(string repo, string path, string[]? modulesAtStart = null)
    {
        var mode = File.GetUnixFileMode(path);
        var target = path + GitQuarantine.Suffix;
        var canRename = CanRename(path, target);

        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart);

        Assert.Empty(check.ModeChanges);
        var entry = Assert.Single(check.Moved);
        Assert.Equal(path, entry.From);
        if (canRename)
        {
            var moved = Assert.IsType<Quarantined.Moved>(entry);
            Assert.Equal(target, moved.To);
            Assert.False(Path.Exists(path));
            Assert.Equal(mode, File.GetUnixFileMode(target));
            Assert.Null(check.Failure);
        }
        else
        {
            Assert.IsType<Quarantined.Failed>(entry);
            Assert.Contains("COULD NOT MOVE IT", entry.Why);
            Assert.False(Path.Exists(target));
            Assert.Equal(mode, File.GetUnixFileMode(path));
            Assert.NotNull(check.Failure);
        }
        return entry;
    }

    private static bool CanRename(string from, string to)
    {
        // Filesystems differ on whether the source directory must be writable for a rename in the same parent.
        try { Native.Rename(from, to); }
        catch (IOException) { return false; }
        Native.Rename(to, from);
        return true;
    }
}
