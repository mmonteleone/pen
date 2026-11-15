using System.Text;

namespace Pen.Tests;

/// <summary>B1: a repo modeled apart from its dir (RepoScan.ResolveWorktree, Project's repo fields). B4: sessions in a linked worktree.</summary>
public class WorktreeTests
{
    /// <summary>A repo with a linked worktree outside it: (repo, worktree dir, the worktree's admin dir).</summary>
    private static (string Repo, string Wt, string Admin) RepoWithWorktree(TempDir t, string name = "wt")
    {
        var repo = Git.Repo(t.Sub("repo"));
        var wt = Path.Combine(t.Path, name);
        Git.Run(repo, "worktree", "add", "-q", wt);
        return (repo, wt, Path.Combine(repo, ".git", "worktrees", name));
    }

    private static string? Resolve(string wt) => RepoScan.ResolveWorktree(Path.Combine(wt, ".git"));

    [Fact]
    public void AValidWorktreeResolvesToTheCommonDir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        Assert.Equal(Path.Combine(repo, ".git"), Resolve(wt));
    }

    [Fact]
    public void AWorktreeInsideTheRepoResolves_AndIsTheProjects()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", ".claude/worktrees/x");
        var gitFile = Path.Combine(repo, ".claude", "worktrees", "x", ".git");
        Assert.Equal(Path.Combine(repo, ".git"), RepoScan.ResolveWorktree(gitFile));
        Assert.True(NestedScans.IsProjectWorktree(repo, gitFile));
        Assert.False(NestedScans.IsProjectWorktree(t.Sub("other"), gitFile));
    }

    [Fact]
    public void RelativePathsResolve()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: ../repo/.git/worktrees/wt\n");
        File.WriteAllText(Path.Combine(admin, "gitdir"), "../../../../wt/.git\n");
        Assert.Equal(Path.Combine(t.Path, "repo", ".git"), Resolve(wt));
    }

    /// <summary>Writes "gitdir: <paramref name="value"/>" with {repo} and {admin} filled in, as wt's .git file.</summary>
    private static string WriteGitFile(string repo, string wt, string admin, string value)
    {
        var gitFile = Path.Combine(wt, ".git");
        File.WriteAllText(gitFile, $"gitdir: {value.Replace("{repo}", repo).Replace("{admin}", admin)}\n");
        return gitFile;
    }

    // Each is where git goes too: the value's realpath is the entry.
    [Theory]
    [InlineData("{admin}")]
    [InlineData("{admin}/")]
    [InlineData("{repo}//.git/./worktrees//wt")]
    [InlineData("../repo/.git/worktrees/wt")]
    [InlineData("./../repo/.git/worktrees/./wt/")]
    [InlineData("back/../../repo/.git/worktrees/wt")] // back leads to a dir beside it, so ".." lands where it reads
    public void GitFileValuesThatResolveWhereTheyRead_Resolve(string value)
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        Directory.CreateSymbolicLink(Path.Combine(wt, "back"), t.Sub("wt", "beside"));
        WriteGitFile(repo, wt, admin, value);
        Assert.Equal(admin, Git.Run(wt, "rev-parse", "--absolute-git-dir").TrimEnd('\n'));
        Assert.Equal(Path.Combine(repo, ".git"), Resolve(wt));
    }

    [Theory]
    [InlineData("l/../../repo/.git/worktrees/wt")] // git goes to <l's target>/../../repo/.git/worktrees/wt
    [InlineData("gone/../../repo/.git/worktrees/wt")] // git can't get through gone
    [InlineData("{link}/.git/worktrees/wt")]
    public void GitFileValuesThatResolveElsewhereOrNowhere_AreRefused(string value)
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        Directory.CreateSymbolicLink(Path.Combine(wt, "l"), t.Sub("x", "y", "z"));
        var link = Path.Combine(t.Path, "link");
        Directory.CreateSymbolicLink(link, repo);
        WriteGitFile(repo, wt, admin, value.Replace("{link}", link));
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesAGitdirWhoseParentIsntWorktrees()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        var moved = Path.Combine(repo, ".git", "elsewhere", "wt");
        Directory.CreateDirectory(Path.GetDirectoryName(moved)!);
        Directory.Move(admin, moved);
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {moved}\n");
        Assert.Null(Resolve(wt));
    }

    [Theory]
    [InlineData("..\n")]
    [InlineData("../../\n")]
    [InlineData("/tmp/evil\n")]
    [InlineData("")]
    public void RefusesACommondirOtherThanDotDotDotDot(string commondir)
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(admin, "commondir"), commondir);
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesAMissingCommondir()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.Delete(Path.Combine(admin, "commondir"));
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesAMissingBackPointer()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.Delete(Path.Combine(admin, "gitdir"));
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesAWrongBackPointer()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        // Another worktree's gitfile copied here: its admin dir points back to the other one.
        var other = Path.Combine(t.Path, "other");
        Git.Run(repo, "worktree", "add", "-q", other);
        File.WriteAllText(Path.Combine(admin, "gitdir"), Path.Combine(other, ".git") + "\n");
        Assert.Null(Resolve(wt));
        Assert.NotNull(Resolve(other));
    }

    [Fact]
    public void RefusesASymlinkedAdminDir()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        // The same files under another worktrees/ dir, reached through a symlink.
        var elsewhere = Path.Combine(t.Sub("evil", ".git", "worktrees"), "wt");
        Directory.Move(admin, elsewhere);
        Directory.CreateSymbolicLink(admin, elsewhere);
        Assert.Null(Resolve(wt));
        Assert.True(Directory.Exists(Path.Combine(repo, ".git", "worktrees", "wt")));
    }

    [Fact]
    public void RefusesASymlinkedWorktreesDir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var worktrees = Path.Combine(repo, ".git", "worktrees");
        var elsewhere = Path.Combine(t.Sub("evil"), "worktrees");
        Directory.Move(worktrees, elsewhere);
        Directory.CreateSymbolicLink(worktrees, elsewhere);
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesASymlinkedAncestorInTheGitdir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var link = Path.Combine(t.Path, "link");
        Directory.CreateSymbolicLink(link, repo);
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {Path.Combine(link, ".git", "worktrees", "wt")}\n");
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesSymlinkedLinkFiles()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        var gitFile = Path.Combine(wt, ".git");
        var copy = Path.Combine(t.Path, "gitfile-copy");
        File.Move(gitFile, copy);
        File.CreateSymbolicLink(gitFile, copy);
        Assert.Null(Resolve(wt));

        File.Delete(gitFile);
        File.Move(copy, gitFile);
        Assert.NotNull(Resolve(wt));
        var commondir = Path.Combine(admin, "commondir");
        File.Move(commondir, Path.Combine(t.Path, "commondir-copy"));
        File.CreateSymbolicLink(commondir, Path.Combine(t.Path, "commondir-copy"));
        Assert.Null(Resolve(wt));
    }

    [Fact]
    public void RefusesASymlinkedGitdirFileInTheEntry()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        Assert.True(RepoScan.IsWorktreeInside(wt, admin));
        var gitdir = Path.Combine(admin, "gitdir");
        var copy = Path.Combine(t.Path, "gitdir-copy");
        File.Move(gitdir, copy);
        File.CreateSymbolicLink(gitdir, copy);
        Assert.Null(Resolve(wt));
        Assert.False(RepoScan.IsWorktreeInside(wt, admin)); // unreadable counts as outside

        File.Delete(gitdir);
        File.Move(copy, gitdir);
        Assert.NotNull(Resolve(wt));
        Assert.True(RepoScan.IsWorktreeInside(wt, admin));
    }

    [Fact]
    public void ThePathFileReadItselfDoesntFollowASymlink()
    {
        using var t = new TempDir();
        var real = Path.Combine(t.Path, "commondir");
        File.WriteAllText(real, "../..\n");
        var link = Path.Combine(t.Path, "link");
        File.CreateSymbolicLink(link, real);
        Assert.Equal("../..\n", RepoScan.ReadPathText(real));
        Assert.Null(RepoScan.ReadPathText(link));
    }

    [Fact]
    public void ReadsLinkFilesWithCrlfOrNoNewlineAndRelativePaths()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: ../repo/.git/worktrees/wt\r\n");
        File.WriteAllText(Path.Combine(admin, "commondir"), "../..");
        File.WriteAllText(Path.Combine(admin, "gitdir"), "../../../../wt/.git\r\n");
        Assert.Equal(Path.Combine(repo, ".git"), Resolve(wt));
        Assert.True(RepoScan.IsWorktreeInside(wt, admin));
    }

    [Theory]
    [InlineData("")]
    [InlineData("gitdir: \n")]
    [InlineData("gitdir:/x/.git/worktrees/wt\n")]
    [InlineData("gitdir: a\nb\n")]
    public void RefusesMalformedGitFiles(string content)
    {
        using var t = new TempDir();
        var (_, wt, _) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(wt, ".git"), content);
        Assert.Null(Resolve(wt));
    }

    /// <summary>The bytes of <paramref name="text"/> in <paramref name="encoding"/>, after its byte order mark.</summary>
    internal static byte[] WithBom(string encoding, string text)
    {
        Encoding e = encoding switch
        {
            "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            "utf-16" => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
            "utf-32BE" => new UTF32Encoding(bigEndian: true, byteOrderMark: true),
            _ => throw new ArgumentException(encoding),
        };
        return [.. e.GetPreamble(), .. e.GetBytes(text)];
    }

    [Fact]
    public void ThePathFileReadKeepsAByteOrderMark_AndRefusesBytesThatArentUtf8()
    {
        using var t = new TempDir();
        var path = Path.Combine(t.Path, "commondir");
        File.WriteAllBytes(path, WithBom("utf-8", "../..\n"));
        Assert.Equal("\uFEFF../..\n", RepoScan.ReadPathText(path));
        File.WriteAllBytes(path, [.. "../"u8, 0xFF, .. "\n"u8]);
        Assert.Null(RepoScan.ReadPathText(path));
        File.WriteAllBytes(path, WithBom("utf-32BE", "../..\n"));
        Assert.Null(RepoScan.ReadPathText(path));
        File.WriteAllBytes(path, Encoding.Unicode.GetBytes("../..\n")); // UTF-16 without one: NULs
        Assert.Equal("../..\n", RepoScan.ReadPathText(path)!.Replace("\0", ""));
    }

    // Git reads these files as bytes. After a UTF-8 byte order mark, commondir names "<entry>/\uFEFF../..", which a
    // directory named "\uFEFF.." makes the entry itself; after UTF-32BE's, a NUL comes first, so it names the entry.
    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-32BE")]
    public void RefusesACommondirWithAByteOrderMark(string encoding)
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        Assert.NotNull(Resolve(wt));
        File.WriteAllBytes(Path.Combine(admin, "commondir"), WithBom(encoding, "../..\n"));
        Assert.Null(Resolve(wt));
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-32BE")]
    public void RefusesAGitFileWithAByteOrderMark(string encoding)
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        var gitFile = Path.Combine(wt, ".git");
        File.WriteAllBytes(gitFile, WithBom(encoding, $"gitdir: {admin}\n"));
        Assert.Null(Resolve(wt));
        Assert.Null(RepoScan.ReadWorktreeEntry(gitFile));
        File.WriteAllText(gitFile, $"gitdir: {admin}\n");
        Assert.NotNull(Resolve(wt));
        Assert.Equal(admin, RepoScan.ReadWorktreeEntry(gitFile));
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16")]
    [InlineData("utf-32BE")]
    public void AGitdirFileWithAByteOrderMark_DoesntPointBack_AndCountsAsOutside(string encoding)
    {
        // A worktree inside the repo, so a path relative to its entry would be inside too.
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "wt");
        var wt = Path.Combine(repo, "wt");
        var admin = Path.Combine(repo, ".git", "worktrees", "wt");
        Assert.True(RepoScan.IsWorktreeInside(repo, admin));
        File.WriteAllBytes(Path.Combine(admin, "gitdir"), WithBom(encoding, Path.Combine(wt, ".git") + "\n"));
        Assert.Null(Resolve(wt));
        Assert.False(RepoScan.IsWorktreeInside(repo, admin));
    }

    [Fact]
    public void RefusesAGitFileThatIsntUtf8_RatherThanReadAnotherEntry()
    {
        // Decoded with replacement, "wt\xFF" would read as the entry named "wt\uFFFD", which git never opens.
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        var lookalike = Path.Combine(repo, ".git", "worktrees", "wt\uFFFD");
        Directory.Move(admin, lookalike);
        var gitFile = Path.Combine(wt, ".git");
        File.WriteAllText(gitFile, $"gitdir: {lookalike}\n");
        Assert.NotNull(Resolve(wt));
        File.WriteAllBytes(gitFile, [.. Encoding.UTF8.GetBytes($"gitdir: {admin}"), 0xFF, .. "\n"u8]);
        Assert.Null(Resolve(wt));
        Assert.Null(RepoScan.ReadWorktreeEntry(gitFile));
    }

    [Fact]
    public void AGitdirFileThatIsntUtf8_CountsAsOutside()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", "wt");
        var admin = Path.Combine(repo, ".git", "worktrees", "wt");
        File.WriteAllBytes(Path.Combine(admin, "gitdir"), [.. Encoding.UTF8.GetBytes(Path.Combine(repo, "wt")), 0xFF, .. "/.git\n"u8]);
        Assert.False(RepoScan.IsWorktreeInside(repo, admin));
    }

    // --- Project ------------------------------------------------------------------

    [Fact]
    public void ProjectForAMainCheckout()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var p = Project.Resolve(repo, t.Host());
        Assert.True(p.IsRepo);
        Assert.False(p.IsWorktree);
        Assert.Equal(Path.Combine(repo, ".git"), p.CommonGitDir);
        Assert.Equal(repo, p.RepoDir);
    }

    [Fact]
    public void ProjectForALinkedWorktree_KeyFromTheMainCheckout()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var p = Project.Resolve(wt, t.Host());
        Assert.True(p.IsRepo);
        Assert.True(p.IsWorktree);
        Assert.Equal(Path.Combine(repo, ".git"), p.CommonGitDir);
        Assert.Equal(repo, p.RepoDir);
        Assert.Equal(wt, p.Dir);
        Assert.Equal(Project.ComputeKey(repo), p.Key);
        Assert.Equal(Project.Resolve(repo, t.Host()).Key, p.Key);
    }

    [Fact]
    public void ProjectForAPlainDirOrAForgedGitFile()
    {
        using var t = new TempDir();
        var plain = Project.Resolve(t.Sub("plain"), t.Host());
        Assert.False(plain.IsRepo);
        Assert.Null(plain.CommonGitDir);
        Assert.Equal(plain.Dir, plain.RepoDir);

        var forged = t.Sub("forged");
        File.WriteAllText(Path.Combine(forged, ".git"), "gitdir: /tmp/nowhere/.git/worktrees/x\n");
        var p = Project.Resolve(forged, t.Host());
        Assert.False(p.IsRepo);
        Assert.Equal(forged, p.RepoDir);
    }

    [Fact]
    public void GitHubOwnerOfAWorktreeComesFromTheCommonConfig()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        Git.Run(repo, "remote", "add", "origin", "git@github.com:acme/widgets.git");
        Assert.Equal("acme", GitHub.FindProjectOwner(Project.Resolve(wt, t.Host()), Config.Empty));
        Assert.Null(GitHub.FindProjectOwner(Project.Resolve(t.Sub("plain"), t.Host()), Config.Empty));
    }

    [Fact]
    public void SignRunsInAWorktree_AndRefusesAPlainDir()
    {
        using var t = new TempDir();
        var (_, wt, _) = RepoWithWorktree(t);
        var host = t.Host();
        // Past the repo checks and the locks, to the base: there's no upstream here.
        Assert.Contains("has no upstream", Assert.Throws<PenException>(() => new SignCommand(host, Project.Resolve(wt, host)).Run(null)).Message);
        Assert.Contains("isn't a git repo", Assert.Throws<PenException>(() => new SignCommand(host, Project.Resolve(t.Sub("plain"), host)).Run(null)).Message);
    }

    // --- B4: running in a linked worktree ------------------------------------------

    [Fact]
    public void AWorktreeInsideTheMainCheckoutIsAllowed_WithTheMainKey()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", ".claude/worktrees/x");
        var wt = Path.Combine(repo, ".claude", "worktrees", "x");
        var p = Project.Resolve(wt, t.Host()); // CheckAllowed: not "inside the git repo at …"
        Assert.Equal(wt, p.Dir);
        Assert.Equal(repo, p.RepoDir);
        Assert.Equal(Project.ComputeKey(repo), p.Key);
        RepoGuard.CheckAtStart(wt);
    }

    [Fact]
    public void CheckAtStartRefusesAForgedGitFile()
    {
        using var t = new TempDir();
        var (_, wt, admin) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(admin, "gitdir"), "/elsewhere/.git\n");
        Assert.Contains("isn't a valid linked worktree", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
    }

    [Fact]
    public void CheckAtStartRefusesAWorktreeOfABareRepo()
    {
        using var t = new TempDir();
        var source = Git.Repo(t.Sub("source"));
        var bare = Path.Combine(t.Path, "bare.git");
        Git.Run(t.Path, "clone", "-q", "--bare", source, bare);
        var wt = Path.Combine(t.Path, "wt");
        Git.Run(bare, "worktree", "add", "-q", wt);
        Assert.Contains("bare repo", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
        Assert.Equal(wt, Project.Resolve(wt, t.Host()).RepoDir); // no main checkout: the dir stands in

        // A bare repo named .git (core.bare = true) counts as bare too.
        var dotgitParent = t.Sub("dotgit");
        var dotgit = Path.Combine(dotgitParent, ".git");
        Git.Run(t.Path, "clone", "-q", "--bare", source, dotgit);
        var wt2 = Path.Combine(t.Path, "wt2");
        Git.Run(dotgit, "worktree", "add", "-q", wt2);
        Assert.False(RepoGuard.IsMainCheckoutGitDir(dotgit));
        Assert.Contains("bare repo", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt2)).Message);
    }

    [Fact]
    public void ABareKeyInACoreSubsection_LeavesTheMainCheckoutNonBare()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var dotgit = Path.Combine(repo, ".git");
        var config = Path.Combine(dotgit, "config");
        File.AppendAllText(config, "[core \"x\"]\n\tbare = true\n[core.y]\n\tbare = true\n");
        Assert.Equal("false\n", Git.Run(repo, "rev-parse", "--is-bare-repository")); // git ignores them too
        Assert.True(RepoGuard.IsMainCheckoutGitDir(dotgit));
        RepoGuard.CheckAtStart(wt);
        Assert.Equal(repo, Project.Resolve(wt, t.Host()).RepoDir);

        // core.bare itself still counts, after them as before them.
        File.AppendAllText(config, "[core]\n\tbare = true\n");
        Assert.False(RepoGuard.IsMainCheckoutGitDir(dotgit));
        Assert.Contains("bare repo", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);

        // A later subsection key doesn't unset it: git still reads core.bare = true.
        File.AppendAllText(config, "[core \"z\"]\n\tbare = false\n");
        Assert.Equal("true\n", Git.Run(dotgit, "rev-parse", "--is-bare-repository"));
        Assert.False(RepoGuard.IsMainCheckoutGitDir(dotgit));
        Assert.Contains("bare repo", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
    }

    [Fact]
    public void CheckAtStartRunsTheGitDirChecksOnTheMainRepo()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var commondir = Path.Combine(repo, ".git", "commondir");
        File.WriteAllText(commondir, "/tmp/evil\n");
        Assert.Contains("commondir exists", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
        File.Delete(commondir);
        RepoGuard.CheckAtStart(wt);
    }

    [Fact]
    public void CheckAtStartScansTheWorktree_AcceptingWorktreesOfTheSameRepo()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        // A worktree of the main repo inside this worktree is the project's own.
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(wt, ".claude", "worktrees", "y"));
        RepoGuard.CheckAtStart(wt);

        Git.Run(wt, "init", "-q", "libs/sub");
        Assert.Contains("libs/sub", Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(wt)).Message);
    }

    private static List<(string, bool)> Mounts(string dir) =>
        RepoGuard.PrepareMounts(dir, "/nonexistent-home").Select(m => (m.Path, m.ReadOnly)).ToList();

    [Fact]
    public void MountsForAWorktree()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "sibling"));
        var git = Path.Combine(repo, ".git");
        var m = Mounts(wt);
        Assert.Equal(
            [
                (git, false), // the common dir, read-write: objects, refs, this worktree's entry
                (Path.Combine(git, "config"), true),
                (Path.Combine(git, "hooks"), true),
                (Path.Combine(git, "worktrees"), false),
                (Path.Combine(git, "worktrees", "sibling"), true), // not visible in the sandbox
                (Path.Combine(admin, "commondir"), true), // this worktree's entry: writable but for commondir
                (Path.Combine(git, "HEAD"), true), // the main checkout's, which isn't visible either
                (Path.Combine(git, "index"), true),
                (Path.Combine(wt, ".git"), true), // the gitfile
                (Path.Combine(wt, ".pen"), true),
            ],
            m);
        Assert.DoesNotContain(m, x => x.Item1 == repo); // the main checkout's files aren't mounted
        Assert.True(Directory.Exists(Path.Combine(wt, ".pen")));
        Assert.False(Directory.Exists(Path.Combine(repo, ".pen")));
    }

    [Fact]
    public void MountsForAWorktree_GoToDindToo()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var yaml = SessionCompose.GenerateOverride(RepoGuard.PrepareMounts(wt, "/nonexistent-home"), readOnlyRoot: false, dindContainer: "x-dind", ports: null);
        var dind = yaml[yaml.IndexOf("\"dind\"", StringComparison.Ordinal)..];
        Assert.Contains($"\"source\": \"{Path.Combine(repo, ".git")}\"", dind);
        Assert.Contains($"\"source\": \"{Path.Combine(wt, ".git")}\"", dind);
    }

    [Fact]
    public void MountsCreateTheMainReposHooksWhenMissing()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var hooks = Path.Combine(repo, ".git", "hooks");
        Directory.Delete(hooks, recursive: true);
        Assert.Contains((hooks, true), Mounts(wt));
        Assert.True(Directory.Exists(hooks));
    }

    [Fact]
    public void EndingAWorktreeSessionDoesntQuarantineItsGitFile()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var common = Path.Combine(repo, ".git");
        Git.Run(wt, "commit", "-q", "--allow-empty", "-m", "in wt");
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(wt, ".claude", "worktrees", "y"));
        Assert.Empty(GitCheck.RunWithoutOpening(wt, startedWithGit: true, common, modulesAtStart: null).Moved);
        Assert.True(File.Exists(Path.Combine(wt, ".git")));
        // Without the common dir, the inner worktree would look foreign.
        Assert.Single(NestedScans.Find(wt));
    }

    [Fact]
    public void TheEndCheckRunsTheGitDirChecksOnTheCommonDir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var common = Path.Combine(repo, ".git");
        File.WriteAllText(Path.Combine(common, "commondir"), "x\n");
        var m = Directory.CreateDirectory(Path.Combine(common, "modules", "m")).FullName;
        Git.Run(m, "init", "-q", "--bare");
        Git.Run(wt, "init", "-q", "sub");
        var moved = GitCheck.RunWithoutOpening(wt, startedWithGit: true, common, modulesAtStart: []).Moved.Select(q => q.From).ToList();
        Assert.Equal([Path.Combine(common, "commondir"), Path.Combine(common, "modules", "m"), Path.Combine(wt, "sub", ".git")], moved);
        Assert.True(File.Exists(Path.Combine(wt, ".git")));
    }

    [Fact]
    public void SessionDirSavesTheCommonDir()
    {
        using var t = new TempDir();
        var s = new SessionDir("pen-x-00000000", t.Sub("s"));
        Assert.Equal((false, null), s.ReadGit());
        File.WriteAllText(s.StartedWithGit, ""); // written by an older Pen
        Assert.Equal((true, null), s.ReadGit());
        File.WriteAllText(s.StartedWithGit, "/r/.git\n");
        Assert.Equal((true, "/r/.git"), s.ReadGit());
    }

    [Fact]
    public void ProjectLayerNamePerDir()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var main = Project.Resolve(repo, t.Host());
        var w = Project.Resolve(wt, t.Host());
        Assert.Equal($"{main.Key}:latest", Image.GetProjectLayerName(main, Image.Name));
        Assert.Equal($"{main.Key}-{Project.HashPath(wt)}:latest", Image.GetProjectLayerName(w, Image.Name));
        Assert.Equal($"{Project.ComputeKey(t.Sub("plain"))}:latest", Image.GetProjectLayerName(Project.Resolve(t.Sub("plain"), t.Host()), Image.Name));
    }

    [Fact]
    public void ProjectLayerNameOnAToolsImage()
    {
        using var t = new TempDir();
        var (repo, wt, _) = RepoWithWorktree(t);
        var main = Project.Resolve(repo, t.Host());
        var w = Project.Resolve(wt, t.Host());
        const string one = "pen-tools:0123456789abcdef", other = "pen-tools:fedcba9876543210";
        Assert.Equal($"{main.Key}:0123456789abcdef", Image.GetProjectLayerName(main, one));
        Assert.Equal($"{main.Key}-{Project.HashPath(wt)}:0123456789abcdef", Image.GetProjectLayerName(w, one));
        Assert.Equal($"{main.Key}:fedcba9876543210", Image.GetProjectLayerName(main, other));
        Assert.Equal($"{main.Key}:latest", Image.GetProjectLayerName(main, Image.Name));
        Assert.True(PruneCommand.IsImageOf(main.Key, Image.GetProjectLayerName(main, one)));
        Assert.True(PruneCommand.IsImageOf(main.Key, Image.GetProjectLayerName(w, one)));
    }

    [Fact]
    public void AGitdirWithATrailingSlashStillResolves()
    {
        using var t = new TempDir();
        var (repo, wt, admin) = RepoWithWorktree(t);
        File.WriteAllText(Path.Combine(wt, ".git"), $"gitdir: {admin}/\n");
        Assert.Equal(Path.Combine(repo, ".git"), Resolve(wt));
    }
}
