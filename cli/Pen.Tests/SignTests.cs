using System.Diagnostics;

namespace Pen.Tests;

public class HostGitTests
{
    [Fact]
    public void EveryCallStartsWithTheOverrides()
    {
        Assert.Equal(["-C", "/r", "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "--no-pager", "status"],
            new HostGit("/r").Args(["status"]));
        Assert.Equal(["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "--no-pager", "-c", "a.b=c", "log"],
            HostGit.Global.With(["-c", "a.b=c"]).Args(["log"]));
    }

    [Fact]
    public void RepoConfigCantRunHooksFsmonitorOrAPager()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var marker = Path.Combine(t.Path, "ran");
        var script = Path.Combine(t.Path, "evil.sh");
        File.WriteAllText(script, $"#!/bin/sh\necho \"$0 $*\" >> '{marker}'\ncat >/dev/null\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_101_101);
        var hooks = Path.Combine(repo, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        foreach (var hook in new[] { "reference-transaction", "post-commit", "pre-commit" })
        {
            File.Copy(script, Path.Combine(hooks, hook));
            File.SetUnixFileMode(Path.Combine(hooks, hook), (UnixFileMode)0b111_101_101);
        }
        Git.Run(repo, "config", "core.fsmonitor", script);
        Git.Run(repo, "config", "core.pager", script);

        var git = new HostGit(repo);
        var head = git.Out("rev-parse", "HEAD");
        git.Out("status");
        git.Out("log", "-1");
        git.Out("update-ref", "refs/heads/other", head);
        Assert.False(File.Exists(marker), File.Exists(marker) ? File.ReadAllText(marker) : "");
    }
}

public class SignTests
{
    /// <summary>A repo plus a throwaway SSH signing key, and a HostGit that signs with it.</summary>
    private sealed class Fixture : IDisposable
    {
        public TempDir T { get; } = new();
        public string Repo { get; }
        public string Key { get; }
        public HostGit Git { get; }
        public HostGit Signing { get; }

        public Fixture()
        {
            Repo = Tests.Git.Repo(T.Sub("repo"));
            Key = Path.Combine(T.Path, "key");
            var psi = new ProcessStartInfo("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", "pen-test", "-f", Key])
            { RedirectStandardOutput = true, RedirectStandardError = true };
            Assert.Equal(0, Process.RunAndCaptureText(psi).ExitStatus.ExitCode);
            IReadOnlyList<string> id = ["-c", "user.name=Signer", "-c", "user.email=signer@example.invalid", "-c", "commit.gpgsign=false"];
            Git = new HostGit(Repo, id);
            Signing = Git.With(["-c", "gpg.format=ssh", "-c", $"user.signingkey={Key}", "-c", "gpg.ssh.program=ssh-keygen"]);
        }

        public string Commit(string file, string content, string author, string date, string message)
        {
            File.WriteAllText(Path.Combine(Repo, file), content);
            Tests.Git.Run(Repo, "add", file);
            Tests.Git.Run(Repo, "commit", "-q", "--author", author, "--date", date, "-m", message);
            return Head();
        }

        public string Head() => Git.Out("rev-parse", "HEAD");
        public string Tree(string c) => Git.Out("rev-parse", c + "^{tree}");
        public CommitObject Obj(string c) => CommitObject.Parse(Git.Run("cat-file", "commit", c).Stdout);

        public void Dispose() => T.Dispose();
    }

    private static void AssertSameButSigned(Fixture f, string oldC, string newC)
    {
        var a = f.Obj(oldC);
        var b = f.Obj(newC);
        Assert.NotEqual(oldC, newC);
        Assert.Equal(a.Tree, b.Tree);
        Assert.Equal(a.AuthorName, b.AuthorName);
        Assert.Equal(a.AuthorEmail, b.AuthorEmail);
        Assert.Equal(a.AuthorDate, b.AuthorDate);
        Assert.Equal(a.Message, b.Message);
        Assert.False(a.Signed);
        Assert.True(b.Signed);
        Assert.Contains("committer Signer <signer@example.invalid>", f.Git.Run("cat-file", "commit", newC).Text);
    }

    [Fact]
    public void LinearHistory_KeepsTreesAuthorsDatesAndMessages()
    {
        using var f = new Fixture();
        var b = f.Head();
        var c1 = f.Commit("a", "1", "Ann <ann@example.invalid>", "2026-01-02T03:04:05+0130", "first\n\nbody with  spaces  \n\n");
        var c2 = f.Commit("a", "2", "Bob <bob@example.invalid>", "2026-02-03T04:05:06-0700", "second");
        var range = Sign.Range(f.Git, b, f.Head());
        Assert.Equal([c1, c2], range);

        var map = Sign.Rewrite(f.Signing, range);
        AssertSameButSigned(f, c1, map[c1]);
        AssertSameButSigned(f, c2, map[c2]);
        Assert.Equal([b], f.Obj(map[c1]).Parents);
        Assert.Equal([map[c1]], f.Obj(map[c2]).Parents);
        Assert.Equal(f.Head(), c2); // Rewrite touches no ref

        Sign.UpdateBranch(f.Git, "refs/heads/main", map[c2], c2);
        Assert.Equal(map[c2], f.Head());
        Assert.Equal(0, Sign.CountUnsigned(f.Git, Sign.Range(f.Git, b, f.Head())));
        Assert.Equal("", f.Git.Out("status", "--porcelain")); // index and worktree still match
        Assert.Contains("Good \"git\" signature", Verify(f, map[c2]));
    }

    private static string Verify(Fixture f, string commit)
    {
        var signers = Path.Combine(f.T.Path, "allowed");
        File.WriteAllText(signers, "signer@example.invalid " + File.ReadAllText(f.Key + ".pub"));
        var r = f.Git.With(["-c", "gpg.format=ssh", "-c", $"gpg.ssh.allowedSignersFile={signers}"]).Run("verify-commit", "-v", commit);
        return r.Stderr;
    }

    [Fact]
    public void AMergeWithAConflictResolution_KeepsItsExactTree()
    {
        using var f = new Fixture();
        var b = f.Head();
        f.Commit("x", "base\n", "A <a@x.io>", "2026-01-01T00:00:00Z", "base x");
        Tests.Git.Run(f.Repo, "checkout", "-q", "-b", "side");
        var s1 = f.Commit("x", "side\n", "A <a@x.io>", "2026-01-02T00:00:00Z", "side");
        Tests.Git.Run(f.Repo, "checkout", "-q", "main");
        f.Commit("x", "main\n", "A <a@x.io>", "2026-01-03T00:00:00Z", "main");
        Assert.ThrowsAny<Exception>(() => Tests.Git.Run(f.Repo, "merge", "-q", "side"));
        File.WriteAllText(Path.Combine(f.Repo, "x"), "resolved by hand\n");
        Tests.Git.Run(f.Repo, "add", "x");
        Tests.Git.Run(f.Repo, "commit", "-q", "--no-edit");
        var merge = f.Head();

        var range = Sign.Range(f.Git, b, merge);
        Assert.Equal(4, range.Count);
        Assert.Equal(merge, range[^1]);
        var map = Sign.Rewrite(f.Signing, range);
        foreach (var c in range) AssertSameButSigned(f, c, map[c]);
        var newMerge = f.Obj(map[merge]);
        Assert.Equal(f.Obj(merge).Parents.Select(p => map[p]), newMerge.Parents);
        Assert.Equal(f.Tree(merge), newMerge.Tree);
        Assert.Contains("resolved by hand", f.Git.Out("show", map[merge] + ":x"));
        _ = s1;
    }

    [Fact]
    public void TheUpstreamMovingAfterAFetch_ChangesNothing()
    {
        using var t = new TempDir();
        var origin = Git.Repo(t.Sub("origin"));
        Git.Run(t.Path, "clone", "-q", origin, Path.Combine(t.Path, "clone"));
        var clone = Path.Combine(t.Path, "clone");
        var git = new HostGit(clone, ["-c", "user.name=S", "-c", "user.email=s@x.io"]);
        var oldBase = git.Out("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(clone, "mine"), "m");
        Git.Run(clone, "add", "mine");
        Git.Run(clone, "commit", "-q", "-m", "mine");
        var mine = git.Out("rev-parse", "HEAD");

        File.WriteAllText(Path.Combine(origin, "theirs"), "t");
        Git.Run(origin, "add", "theirs");
        Git.Run(origin, "commit", "-q", "-m", "theirs");
        Git.Run(clone, "fetch", "-q");

        var b = Sign.ResolveBase(git, null)!.Value;
        Assert.Equal("origin/main", b.Name);
        Assert.NotEqual(oldBase, b.Commit);
        var range = Sign.Range(git, b.Commit, mine);
        Assert.Equal([mine], range);
        Assert.Empty(Sign.OnARemote(git, b.Commit, mine, range));

        var key = Path.Combine(t.Path, "key");
        Process.RunAndCaptureText(new ProcessStartInfo("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", key]) { RedirectStandardOutput = true, RedirectStandardError = true });
        var map = Sign.Rewrite(git.With(["-c", "gpg.format=ssh", "-c", $"user.signingkey={key}", "-c", "gpg.ssh.program=ssh-keygen"]), range);
        var signed = CommitObject.Parse(git.Run("cat-file", "commit", map[mine]).Stdout);
        Assert.Equal([oldBase], signed.Parents); // no rebase onto the new upstream
        Assert.Equal(git.Out("rev-parse", mine + "^{tree}"), signed.Tree);
    }

    [Fact]
    public void ABranchThatMovedDuringTheRewrite_IsRefused()
    {
        using var f = new Fixture();
        var b = f.Head();
        var c1 = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");
        var map = Sign.Rewrite(f.Signing, Sign.Range(f.Git, b, c1));
        var moved = f.Commit("a", "2", "A <a@x.io>", "2026-01-02T00:00:00Z", "two, meanwhile");

        var e = Assert.Throws<PenException>(() => Sign.UpdateBranch(f.Git, "refs/heads/main", map[c1], c1));
        Assert.Contains("moved while signing", e.Message);
        Assert.Equal(moved, f.Head());
    }

    [Fact]
    public void CommitsOnAnyRemoteBranchAreFound()
    {
        using var f = new Fixture();
        var b = f.Head();
        var c1 = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");
        var c2 = f.Commit("a", "2", "A <a@x.io>", "2026-01-02T00:00:00Z", "two");
        var range = Sign.Range(f.Git, b, c2);
        Assert.Empty(Sign.OnARemote(f.Git, b, c2, range));
        f.Git.Out("update-ref", "refs/remotes/fork/feature", c1);
        Assert.Equal([c1], Sign.OnARemote(f.Git, b, c2, range));
    }

    [Fact]
    public void SignedCommitsWithUnchangedParentsAreKept_AndCounted()
    {
        using var f = new Fixture();
        var b = f.Head();
        var c1 = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");
        var signedHead = Sign.Rewrite(f.Signing, [c1])[c1];
        f.Git.Out("update-ref", "refs/heads/main", signedHead);
        Tests.Git.Run(f.Repo, "reset", "-q", "--hard", signedHead);
        var c2 = f.Commit("a", "2", "A <a@x.io>", "2026-01-02T00:00:00Z", "two");

        var range = Sign.Range(f.Git, b, c2);
        Assert.Equal(1, Sign.CountUnsigned(f.Git, range));
        var map = Sign.Rewrite(f.Signing, range);
        Assert.Equal(signedHead, map[signedHead]);
        Assert.Equal([signedHead], f.Obj(map[c2]).Parents);
    }

    [Fact]
    public void ResolveBase_FallsBackToOriginHead_AndRejectsNonCommits()
    {
        using var f = new Fixture();
        Assert.Null(Sign.ResolveBase(f.Git, null));
        var head = f.Head();
        f.Git.Out("update-ref", "refs/remotes/origin/main", head);
        f.Git.Out("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        Assert.Equal(("refs/remotes/origin/HEAD", head), Sign.ResolveBase(f.Git, null));
        Assert.Equal(head, Sign.ResolveBase(f.Git, "HEAD")!.Value.Commit);
        Assert.Throws<PenException>(() => Sign.ResolveBase(f.Git, "no-such-ref"));
    }

    [Fact]
    public void ParsesASignedCommitWithAMultiLineSignature()
    {
        var raw = "tree 1234\nparent aa\nparent bb\nauthor A B <a@b.io> 1700000000 +0100\ncommitter C <c@d.io> 1700000001 +0000\ngpgsig -----BEGIN SSH SIGNATURE-----\n abc\n -----END SSH SIGNATURE-----\n\nsubject\n\nbody\n"u8.ToArray();
        var c = CommitObject.Parse(raw);
        Assert.Equal("1234", c.Tree);
        Assert.Equal(["aa", "bb"], c.Parents);
        Assert.Equal("A B", c.AuthorName);
        Assert.Equal("a@b.io", c.AuthorEmail);
        Assert.Equal("1700000000 +0100", c.AuthorDate);
        Assert.True(c.Signed);
        Assert.Equal("subject\n\nbody\n"u8.ToArray(), c.Message);
        Assert.Equal("subject", c.Subject);
        Assert.True(CommitObject.Parse("tree 1\nauthor a <b> 1 +0000\ngpgsig-sha256 x\n\nm"u8.ToArray()).Signed);
    }
}
