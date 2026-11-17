using System.Diagnostics;

namespace Pen.Tests;

public class HostGitTests
{
    [Fact]
    public void EveryCallStartsWithTheOverrides()
    {
        Assert.Equal(["-C", "/r", "-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "--no-pager", "status"],
            new HostGit("/r").BuildArgs(["status"]));
        Assert.Equal(["-c", "core.hooksPath=/dev/null", "-c", "core.fsmonitor=false", "--no-pager", "-c", "a.b=c", "log"],
            HostGit.Global.WithConfig(["-c", "a.b=c"]).BuildArgs(["log"]));
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
        var head = git.ReadOutput("rev-parse", "HEAD");
        git.ReadOutput("status");
        git.ReadOutput("log", "-1");
        git.ReadOutput("update-ref", "refs/heads/other", head);
        Assert.False(File.Exists(marker), File.Exists(marker) ? File.ReadAllText(marker) : "");
    }

    [Fact]
    public void AMissingObjectDoesntFetchFromAPromisorRemote()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var marker = Path.Combine(t.Path, "ran");
        var script = Path.Combine(t.Path, "evil.sh");
        File.WriteAllText(script, $"#!/bin/sh\necho ran >> '{marker}'\n");
        File.SetUnixFileMode(script, (UnixFileMode)0b111_101_101);
        Git.Run(repo, "config", "core.repositoryformatversion", "1");
        Git.Run(repo, "config", "extensions.partialClone", "evil");
        Git.Run(repo, "config", "remote.evil.promisor", "true");
        Git.Run(repo, "config", "remote.evil.url", $"ext::{script} x");
        Git.Run(repo, "config", "protocol.ext.allow", "always");
        var missing = new string('1', 40);

        Assert.Equal($"{missing} missing\n", new HostGit(repo).Run(["cat-file", "--batch"], System.Text.Encoding.ASCII.GetBytes(missing + "\n")).Text);
        Assert.False(File.Exists(marker));
    }
}

public class SignTests
{
    /// <summary>
    /// A repo, plus a temp HOME whose global git config signs with a throwaway
    /// SSH key and trusts it, and a HostGit that uses that HOME. Nothing comes
    /// from this machine's git or SSH setup, or its agent.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        public TempDir T { get; } = new();
        public string Repo { get; }
        public string Key { get; }
        public string AgentKey { get; }
        public string Signers { get; }
        public HostGit Git { get; }
        public HostGit Signing => Git.WithConfig(SignatureConfig.BuildForSigning(Git));
        public Dictionary<string, string> Env { get; }
        private readonly string _globalConfig;

        public Fixture(bool trustKey = true)
        {
            Repo = TestSupport.Git.Repo(T.Sub("repo"));
            Key = NewKey("key");
            AgentKey = NewKey("agent-key");
            Signers = Path.Combine(T.Path, "allowed_signers");
            File.WriteAllText(Signers, "signer@example.invalid " + File.ReadAllText(Key + ".pub"));
            var home = T.Sub("home");
            _globalConfig = Path.Combine(home, ".gitconfig");
            Global("gpg.format", "ssh");
            Global("user.signingkey", Key);
            Global("gpg.ssh.program", "ssh-keygen");
            if (trustKey) Global("gpg.ssh.allowedSignersFile", Signers);
            IReadOnlyList<string> id = ["-c", "user.name=Signer", "-c", "user.email=signer@example.invalid", "-c", "commit.gpgsign=false"];
            Env = new Dictionary<string, string>
            {
                ["HOME"] = home,
                ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"),
                ["GIT_CONFIG_GLOBAL"] = _globalConfig,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["SSH_AUTH_SOCK"] = Path.Combine(T.Path, "no-agent"),
            };
            Git = new HostGit(Repo, id, Env);
        }

        public string NewKey(string name)
        {
            var key = Path.Combine(T.Path, name);
            var psi = new ProcessStartInfo("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", "pen-test", "-f", key])
            { RedirectStandardOutput = true, RedirectStandardError = true };
            Assert.Equal(0, Process.RunAndCaptureText(psi).ExitStatus.ExitCode);
            return key;
        }

        public void Global(string key, string value) => TestSupport.Git.Run(T.Path, "config", "--file", _globalConfig, key, value);

        /// <summary>Re-creates what the range needs, as pen sign would.</summary>
        public Dictionary<string, string> Rewrite(IReadOnlyList<string> commits) =>
            new CommitRewriter(Signing).Rewrite(commits, new SignatureVerifier(Git).FindVerified(commits));

        /// <summary>The branch moves to a new empty commit signed with <paramref name="key"/>.</summary>
        public string SignedBy(string key, string message)
        {
            TestSupport.Git.Run(Repo, "-c", "gpg.format=ssh", "-c", "gpg.ssh.program=ssh-keygen", "-c", $"user.signingkey={key}", "commit", "-q", "-S", "--allow-empty", "-m", message);
            return Head();
        }

        /// <summary>The branch moves to a new commit with this armor in its signature header, which nothing checked.</summary>
        public string Forged(string armorType, string message = "forged") =>
            ForgedHeader($"gpgsig -----BEGIN {armorType}-----\n Zm9v\n -----END {armorType}-----", message);

        public string ForgedHeader(string header, string message = "forged")
        {
            var raw = Git.Run("cat-file", "commit", "HEAD").Text;
            var lines = raw[..raw.IndexOf("\n\n", StringComparison.Ordinal)].Split('\n');
            var tree = lines.First(l => l.StartsWith("tree "));
            var people = lines.Where(l => l.StartsWith("author ") || l.StartsWith("committer "));
            var commit = $"{tree}\nparent {Head()}\n{string.Join('\n', people)}\n{header}\n\n{message}\n";
            var written = Git.Run(["hash-object", "-t", "commit", "-w", "--stdin"], System.Text.Encoding.UTF8.GetBytes(commit));
            Assert.True(written.Ok, written.Stderr);
            TestSupport.Git.Run(Repo, "update-ref", "HEAD", written.Text.Trim());
            return Head();
        }

        public string ObjectPath(string sha) => Path.Combine(Repo, ".git", "objects", sha[..2], sha[2..]);

        /// <summary>Runs what a hint says to run, in a shell, with this fixture's HOME.</summary>
        public void RunHintCommands(string hint)
        {
            foreach (var line in hint.Split('\n').Where(l => l.StartsWith("    ")))
            {
                var psi = new ProcessStartInfo("sh", ["-c", line.Trim()]) { WorkingDirectory = T.Path, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var (k, v) in Env) psi.Environment[k] = v;
                var r = Process.RunAndCaptureText(psi);
                Assert.True(r.ExitStatus.ExitCode == 0, line + ": " + r.StandardError);
            }
        }

        public string Commit(string file, string content, string author, string date, string message)
        {
            File.WriteAllText(Path.Combine(Repo, file), content);
            TestSupport.Git.Run(Repo, "add", file);
            TestSupport.Git.Run(Repo, "commit", "-q", "--author", author, "--date", date, "-m", message);
            return Head();
        }

        public string Head() => Git.ReadOutput("rev-parse", "HEAD");
        public string Tree(string c) => Git.ReadOutput("rev-parse", c + "^{tree}");
        public CommitObject Obj(string c) => CommitObject.Parse(Git.Run("cat-file", "commit", c).Stdout);
        public bool HasSignatureHeader(string c) => Git.Run("cat-file", "commit", c).Text.Contains("\ngpgsig ");

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
        Assert.False(f.HasSignatureHeader(oldC));
        Assert.True(f.HasSignatureHeader(newC));
        Assert.Contains("committer Signer <signer@example.invalid>", f.Git.Run("cat-file", "commit", newC).Text);
    }

    [Fact]
    public void LinearHistory_KeepsTreesAuthorsDatesAndMessages()
    {
        using var f = new Fixture();
        var b = f.Head();
        var c1 = f.Commit("a", "1", "Ann <ann@example.invalid>", "2026-01-02T03:04:05+0130", "first\n\nbody with  spaces  \n\n");
        var c2 = f.Commit("a", "2", "Bob <bob@example.invalid>", "2026-02-03T04:05:06-0700", "second");
        var range = new BranchSigner(f.Git).ListRange(b, f.Head());
        Assert.Equal([c1, c2], range);

        var map = f.Rewrite(range);
        AssertSameButSigned(f, c1, map[c1]);
        AssertSameButSigned(f, c2, map[c2]);
        Assert.Equal([b], f.Obj(map[c1]).Parents);
        Assert.Equal([map[c1]], f.Obj(map[c2]).Parents);
        Assert.Equal(f.Head(), c2); // Rewrite touches no ref

        new BranchSigner(f.Git).UpdateBranch("refs/heads/main", map[c2], c2);
        Assert.Equal(map[c2], f.Head());
        Assert.Equal(0, new SignatureVerifier(f.Git).CountUnsigned(new BranchSigner(f.Git).ListRange(b, f.Head())));
        Assert.Equal("", f.Git.ReadOutput("status", "--porcelain")); // index and worktree still match
        Assert.Contains("Good \"git\" signature", Verify(f, map[c2]));
    }

    private static string Verify(Fixture f, string commit)
    {
        var signers = Path.Combine(f.T.Path, "allowed");
        File.WriteAllText(signers, "signer@example.invalid " + File.ReadAllText(f.Key + ".pub"));
        var r = f.Git.WithConfig(["-c", "gpg.format=ssh", "-c", $"gpg.ssh.allowedSignersFile={signers}"]).Run("verify-commit", "-v", commit);
        return r.Stderr;
    }

    [Fact]
    public void AMergeWithAConflictResolution_KeepsItsExactTree()
    {
        using var f = new Fixture();
        var b = f.Head();
        f.Commit("x", "base\n", "A <a@x.io>", "2026-01-01T00:00:00Z", "base x");
        TestSupport.Git.Run(f.Repo, "checkout", "-q", "-b", "side");
        var s1 = f.Commit("x", "side\n", "A <a@x.io>", "2026-01-02T00:00:00Z", "side");
        TestSupport.Git.Run(f.Repo, "checkout", "-q", "main");
        f.Commit("x", "main\n", "A <a@x.io>", "2026-01-03T00:00:00Z", "main");
        Assert.ThrowsAny<Exception>(() => TestSupport.Git.Run(f.Repo, "merge", "-q", "side"));
        File.WriteAllText(Path.Combine(f.Repo, "x"), "resolved by hand\n");
        TestSupport.Git.Run(f.Repo, "add", "x");
        TestSupport.Git.Run(f.Repo, "commit", "-q", "--no-edit");
        var merge = f.Head();

        var range = new BranchSigner(f.Git).ListRange(b, merge);
        Assert.Equal(4, range.Count);
        Assert.Equal(merge, range[^1]);
        var map = f.Rewrite(range);
        foreach (var c in range) AssertSameButSigned(f, c, map[c]);
        var newMerge = f.Obj(map[merge]);
        Assert.Equal(f.Obj(merge).Parents.Select(p => map[p]), newMerge.Parents);
        Assert.Equal(f.Tree(merge), newMerge.Tree);
        Assert.Contains("resolved by hand", f.Git.ReadOutput("show", map[merge] + ":x"));
        _ = s1;
    }

    [Fact]
    public void TheUpstreamMovingAfterAFetch_ChangesNothing()
    {
        using var t = new TempDir();
        var origin = Git.Repo(t.Sub("origin"));
        Git.Run(t.Path, "clone", "-q", origin, Path.Combine(t.Path, "clone"));
        var clone = Path.Combine(t.Path, "clone");
        var git = new HostGit(clone, ["-c", "user.name=S", "-c", "user.email=s@x.io"], null);
        var oldBase = git.ReadOutput("rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(clone, "mine"), "m");
        Git.Run(clone, "add", "mine");
        Git.Run(clone, "commit", "-q", "-m", "mine");
        var mine = git.ReadOutput("rev-parse", "HEAD");

        File.WriteAllText(Path.Combine(origin, "theirs"), "t");
        Git.Run(origin, "add", "theirs");
        Git.Run(origin, "commit", "-q", "-m", "theirs");
        Git.Run(clone, "fetch", "-q");

        var b = new BranchSigner(git).ResolveBase(null)!;
        Assert.Equal("origin/main", b.Name);
        Assert.NotEqual(oldBase, b.Commit);
        var range = new BranchSigner(git).ListRange(b.Commit, mine);
        Assert.Equal([mine], range);
        Assert.Empty(new BranchSigner(git).FindOnARemote(b.Commit, mine, range));

        var key = Path.Combine(t.Path, "key");
        Process.RunAndCaptureText(new ProcessStartInfo("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-f", key]) { RedirectStandardOutput = true, RedirectStandardError = true });
        var map = new CommitRewriter(git.WithConfig(["-c", "gpg.format=ssh", "-c", $"user.signingkey={key}", "-c", "gpg.ssh.program=ssh-keygen"])).Rewrite(range, new HashSet<string>());
        var signed = CommitObject.Parse(git.Run("cat-file", "commit", map[mine]).Stdout);
        Assert.Equal([oldBase], signed.Parents); // no rebase onto the new upstream
        Assert.Equal(git.ReadOutput("rev-parse", mine + "^{tree}"), signed.Tree);
    }

    [Fact]
    public void ABranchThatMovedDuringTheRewrite_IsRefused()
    {
        using var f = new Fixture();
        var b = f.Head();
        var c1 = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");
        var map = f.Rewrite(new BranchSigner(f.Git).ListRange(b, c1));
        var moved = f.Commit("a", "2", "A <a@x.io>", "2026-01-02T00:00:00Z", "two, meanwhile");

        var e = Assert.Throws<PenException>(() => new BranchSigner(f.Git).UpdateBranch("refs/heads/main", map[c1], c1));
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
        var range = new BranchSigner(f.Git).ListRange(b, c2);
        Assert.Empty(new BranchSigner(f.Git).FindOnARemote(b, c2, range));
        f.Git.ReadOutput("update-ref", "refs/remotes/fork/feature", c1);
        Assert.Equal([c1], new BranchSigner(f.Git).FindOnARemote(b, c2, range));
    }

    [Fact]
    public void SignedCommitsWithUnchangedParentsAreKept_AndCounted()
    {
        using var f = new Fixture();
        var b = f.Head();
        var c1 = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");
        var signedHead = f.Rewrite([c1])[c1];
        f.Git.ReadOutput("update-ref", "refs/heads/main", signedHead);
        TestSupport.Git.Run(f.Repo, "reset", "-q", "--hard", signedHead);
        var c2 = f.Commit("a", "2", "A <a@x.io>", "2026-01-02T00:00:00Z", "two");

        var range = new BranchSigner(f.Git).ListRange(b, c2);
        Assert.Equal(1, new SignatureVerifier(f.Git).CountUnsigned(range));
        var map = f.Rewrite(range);
        Assert.Equal(signedHead, map[signedHead]);
        Assert.Equal([signedHead], f.Obj(map[c2]).Parents);
    }

    [Fact]
    public void ResolveBase_FallsBackToOriginHead_AndRejectsNonCommits()
    {
        using var f = new Fixture();
        Assert.Null(new BranchSigner(f.Git).ResolveBase(null));
        var head = f.Head();
        f.Git.ReadOutput("update-ref", "refs/remotes/origin/main", head);
        f.Git.ReadOutput("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        Assert.Equal(new ResolvedBase("refs/remotes/origin/HEAD", head), new BranchSigner(f.Git).ResolveBase(null));
        Assert.Equal(head, new BranchSigner(f.Git).ResolveBase("HEAD")!.Commit);
        Assert.Throws<PenException>(() => new BranchSigner(f.Git).ResolveBase("no-such-ref"));
    }

    [Fact]
    public void ParsesASignedCommitWithAMultiLineSignature()
    {
        string t = new('1', 40), p1 = new('a', 40), p2 = new('b', 40);
        var raw = System.Text.Encoding.UTF8.GetBytes($"tree {t}\nparent {p1}\nparent {p2}\nauthor A B <a@b.io> 1700000000 +0100\ncommitter C <c@d.io> 1700000001 +0000\ngpgsig -----BEGIN SSH SIGNATURE-----\n abc\n -----END SSH SIGNATURE-----\n\nsubject\n\nbody\n");
        var c = CommitObject.Parse(raw);
        Assert.Equal(t, c.Tree);
        Assert.Equal([p1, p2], c.Parents);
        Assert.Equal("A B", c.AuthorName);
        Assert.Equal("a@b.io", c.AuthorEmail);
        Assert.Equal("1700000000 +0100", c.AuthorDate);
        Assert.Equal("subject\n\nbody\n"u8.ToArray(), c.Message);
        Assert.Equal("subject", c.Subject);
    }
    private static readonly string[] Armors = ["SSH SIGNATURE", "PGP SIGNATURE", "SIGNED MESSAGE"];

    /// <summary>Sets the branch's base here, so <see cref="BranchSigner.Sign"/> and the reminder have a range.</summary>
    private static string SetBase(Fixture f)
    {
        var b = f.Head();
        f.Git.ReadOutput("update-ref", "refs/remotes/origin/main", b);
        f.Git.ReadOutput("symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        return b;
    }

    [Theory]
    [InlineData("SSH SIGNATURE")]
    [InlineData("PGP SIGNATURE")]
    [InlineData("SIGNED MESSAGE")]
    public void AForgedSignatureHeader_IsReSigned(string armor)
    {
        using var f = new Fixture();
        var b = SetBase(f);
        var forged = f.Forged(armor);
        Assert.Empty(new SignatureVerifier(f.Git).FindVerified([forged]));

        new BranchSigner(f.Git).Sign(null);

        var head = f.Head();
        Assert.NotEqual(forged, head);
        Assert.Equal([b], f.Obj(head).Parents);
        Assert.Equal([head], new SignatureVerifier(f.Git).FindVerified([head]));
    }

    [Fact]
    public void AValidlySignedCommit_IsSkipped()
    {
        using var f = new Fixture();
        SetBase(f);
        var valid = f.SignedBy(f.Key, "valid");

        new BranchSigner(f.Git).Sign(null);

        Assert.Equal(valid, f.Head());
    }

    [Fact]
    public void TheWholeRangeIsRead_KeepingValidCommitsBelowAForgedOne()
    {
        using var f = new Fixture();
        SetBase(f);
        var valid = f.SignedBy(f.Key, "valid");
        var forged = f.Forged("SSH SIGNATURE");
        var aboveForged = f.SignedBy(f.Key, "valid, above the forged one");
        Assert.Equal(new HashSet<string> { valid, aboveForged }, new SignatureVerifier(f.Git).FindVerified([valid, forged, aboveForged]));

        new BranchSigner(f.Git).Sign(null);

        var head = f.Head();
        Assert.NotEqual(aboveForged, head);
        var newForged = f.Obj(head).Parents.Single();
        Assert.NotEqual(forged, newForged);
        Assert.Equal([valid], f.Obj(newForged).Parents);
        Assert.Equal(2, new SignatureVerifier(f.Git).FindVerified([head, newForged]).Count);
    }

    /// <summary>The repo's own config trusts the agent's key, set directly, through an include or in config.worktree.</summary>
    [Theory]
    [InlineData("config")]
    [InlineData("include")]
    [InlineData("worktree")]
    public void AllowedSignersFromTheRepo_AreIgnored(string how)
    {
        using var f = new Fixture();
        SetBase(f);
        var agentSigners = Path.Combine(f.T.Path, "agent_signers");
        File.WriteAllText(agentSigners, "agent@example.invalid " + File.ReadAllText(f.AgentKey + ".pub"));
        if (how == "config")
            TestSupport.Git.Run(f.Repo, "config", "gpg.ssh.allowedSignersFile", agentSigners);
        else if (how == "include")
        {
            var included = Path.Combine(f.T.Path, "included");
            File.WriteAllText(included, $"[gpg \"ssh\"]\n\tallowedSignersFile = {agentSigners}\n");
            TestSupport.Git.Run(f.Repo, "config", "include.path", included);
        }
        else
        {
            TestSupport.Git.Run(f.Repo, "config", "extensions.worktreeConfig", "true");
            TestSupport.Git.Run(f.Repo, "config", "--worktree", "gpg.ssh.allowedSignersFile", agentSigners);
        }
        var forged = f.SignedBy(f.AgentKey, "signed by the agent");
        Assert.Equal("G", f.Git.ReadOutput("log", "-1", "--format=%G?", forged)); // what trusting the repo's config would say

        Assert.Empty(new SignatureVerifier(f.Git).FindVerified([forged]));
        new BranchSigner(f.Git).Sign(null);

        Assert.NotEqual(forged, f.Head());
        Assert.Equal([f.Head()], new SignatureVerifier(f.Git).FindVerified([f.Head()]));
    }

    /// <summary>pen sign doesn't quarantine a config.worktree (it has no record of a start), so what it runs must not take its settings.</summary>
    [Fact]
    public void AnUnvouchedConfigWorktree_RunsNothingInPenSign()
    {
        using var f = new Fixture();
        SetBase(f);
        var marker = Path.Combine(f.T.Path, "ran");
        var hook = Path.Combine(f.T.Path, "hook.sh");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
        TestSupport.Git.Run(f.Repo, "config", "extensions.worktreeConfig", "true");
        var planted = Path.Combine(f.Repo, ".git", "config.worktree");
        File.WriteAllText(planted, $"[core]\n\tfsmonitor = {hook}\n");

        new BranchSigner(f.Git).Sign(null);

        Assert.False(File.Exists(marker), "the planted fsmonitor ran");
        Assert.True(File.Exists(planted));
    }

    [Fact]
    public void ARepoFileIsntTrusted_EvenWithNoGlobalSetting_OrARelativeGlobalOne()
    {
        using var f = new Fixture(trustKey: false);
        File.WriteAllText(Path.Combine(f.Repo, "signers"), "agent@example.invalid " + File.ReadAllText(f.AgentKey + ".pub"));
        var forged = f.SignedBy(f.AgentKey, "signed by the agent");
        Assert.Empty(new SignatureVerifier(f.Git).FindVerified([forged]));

        f.Global("gpg.ssh.allowedSignersFile", "signers");
        Assert.Equal("G", f.Git.ReadOutput("log", "-1", "--format=%G?", forged)); // what git makes of that path
        Assert.Empty(new SignatureVerifier(f.Git).FindVerified([forged]));
    }

    /// <summary>To verify, git picks the program by the signature's armor, whatever gpg.format says.</summary>
    [Theory]
    [InlineData("gpg.program", "PGP SIGNATURE")]
    [InlineData("gpg.openpgp.program", "PGP SIGNATURE")]
    [InlineData("gpg.x509.program", "SIGNED MESSAGE")]
    [InlineData("gpg.ssh.program", "SSH SIGNATURE")]
    public void ARepoProgramDoesntRun_AndCantVouchForASignature(string setting, string armor)
    {
        using var f = new Fixture();
        SetBase(f);
        var marker = Path.Combine(f.T.Path, "ran");
        var evil = Path.Combine(f.T.Path, "evil.sh");
        File.WriteAllText(evil, $"""
            #!/bin/sh
            echo "$0 $*" >> '{marker}'
            echo '[GNUPG:] GOODSIG 1234 forged'
            echo '[GNUPG:] VALIDSIG AAAA 2020-01-01 0 4 0 1 8 00 AAAA'
            echo '[GNUPG:] TRUST_ULTIMATE 0 pgp'
            """);
        File.SetUnixFileMode(evil, (UnixFileMode)0b111_101_101);
        TestSupport.Git.Run(f.Repo, "config", setting, evil);
        TestSupport.Git.Run(f.Repo, "config", "gpg.format", "x509");
        TestSupport.Git.Run(f.Repo, "config", "gpg.ssh.allowedSignersFile", Path.Combine(f.T.Path, "allowed_signers"));
        var forged = f.Forged(armor);
        f.Git.ReadOutput("log", "-1", "--format=%G?", forged);
        Assert.True(File.Exists(marker)); // git runs it, left to the repo's config
        File.Delete(marker);

        Assert.Empty(new SignatureVerifier(f.Git).FindVerified([forged]));
        Assert.False(File.Exists(marker));

        new BranchSigner(f.Git).Sign(null);
        Assert.False(File.Exists(marker));
        Assert.NotEqual(forged, f.Head());
    }

    [Fact]
    public void ARepoDefaultKeyCommandDoesntRun()
    {
        using var f = new Fixture();
        SetBase(f);
        var marker = Path.Combine(f.T.Path, "ran");
        var evil = Path.Combine(f.T.Path, "evil.sh");
        File.WriteAllText(evil, $"#!/bin/sh\necho ran >> '{marker}'\necho key::ssh-ed25519 AAAA\n");
        File.SetUnixFileMode(evil, (UnixFileMode)0b111_101_101);
        TestSupport.Git.Run(f.Repo, "config", "gpg.ssh.defaultKeyCommand", evil);
        foreach (var armor in Armors) f.Forged(armor, armor);

        new BranchSigner(f.Git).Sign(null);

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void AReplaceRefCantLendAForgedCommitAValidOnesSignature()
    {
        using var f = new Fixture();
        SetBase(f);
        var valid = f.SignedBy(f.Key, "valid");
        var forged = f.Commit("evil", "agent's change", "A <a@x.io>", "2026-01-01T00:00:00Z", "agent's commit");
        TestSupport.Git.Run(f.Repo, "replace", forged, valid);
        TestSupport.Git.Run(f.Repo, "config", "core.useReplaceRefs", "true");
        Assert.Equal("G", TestSupport.Git.Run(f.Repo, "-c", "gpg.ssh.allowedSignersFile=" + f.Signers, "log", "-1", "--format=%G?", forged).Trim());

        Assert.DoesNotContain(forged, new SignatureVerifier(f.Git).FindVerified([forged]));
        new BranchSigner(f.Git).Sign(null);

        var head = f.Head();
        Assert.NotEqual(forged, head);
        Assert.Equal(f.Tree(forged), f.Obj(head).Tree); // the agent's real commit, which the user gets to review
        Assert.Equal("agent's commit\n", System.Text.Encoding.UTF8.GetString(f.Obj(head).Message));
    }

    [Fact]
    public void TheRepoCantDistortTheRead_WithShowSignatureOrItsOwnTrustSettings()
    {
        using var f = new Fixture();
        var valid = f.SignedBy(f.Key, "valid");
        TestSupport.Git.Run(f.Repo, "config", "log.showSignature", "true");
        TestSupport.Git.Run(f.Repo, "config", "gpg.minTrustLevel", "ultimate");
        TestSupport.Git.Run(f.Repo, "config", "gpg.ssh.revocationFile", Path.Combine(f.T.Path, "no-such-file"));
        Assert.Equal([valid], new SignatureVerifier(f.Git).FindVerified([valid]));
    }

    [Fact]
    public void TheUsersOwnRevocationsAndTrustLevelApply()
    {
        using var f = new Fixture();
        var valid = f.SignedBy(f.Key, "valid");
        Assert.Equal([valid], new SignatureVerifier(f.Git).FindVerified([valid]));

        f.Global("gpg.minTrustLevel", "ultimate"); // SSH signers only reach "fully"
        Assert.Empty(new SignatureVerifier(f.Git).FindVerified([valid]));
        f.Global("gpg.minTrustLevel", "fully");
        Assert.Equal([valid], new SignatureVerifier(f.Git).FindVerified([valid]));

        var krl = Path.Combine(f.T.Path, "krl");
        var psi = new ProcessStartInfo("ssh-keygen", ["-k", "-f", krl, f.Key + ".pub"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        Assert.Equal(0, Process.RunAndCaptureText(psi).ExitStatus.ExitCode);
        f.Global("gpg.ssh.revocationFile", krl);
        Assert.Empty(new SignatureVerifier(f.Git).FindVerified([valid]));
    }

    [Fact]
    public void TheReminderCountsWhatDoesntVerify()
    {
        using var f = new Fixture();
        SetBase(f);
        f.Global("commit.gpgsign", "true");
        Assert.Null(new BranchSigner(f.Git).FormatReminder());
        f.SignedBy(f.Key, "valid");
        Assert.Null(new BranchSigner(f.Git).FormatReminder());
        f.Forged("SSH SIGNATURE");
        Assert.Equal("1 unsigned commit; review, then run pen sign", new BranchSigner(f.Git).FormatReminder());
    }

    [Fact]
    public void NoAllowedSignersFile_LeavesNewSignaturesUnverified_AndGivesTheHint()
    {
        using var f = new Fixture(trustKey: false);
        SetBase(f);
        var first = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");

        new BranchSigner(f.Git).Sign(null);
        var signed = f.Head();
        Assert.NotEqual(first, signed);
        Assert.True(f.HasSignatureHeader(signed));
        var hint = new SignatureVerifier(f.Git).FormatHint([signed]);
        Assert.Contains("re-sign", hint);
        Assert.Contains("gpg.ssh.allowedSignersFile isn't set", hint);
        Assert.Contains("git config --global gpg.ssh.allowedSignersFile", hint);

        Assert.Empty(new SignatureVerifier(f.Git).FindVerified([signed])); // so the next pen sign re-signs it

        f.Global("gpg.ssh.allowedSignersFile", f.Signers);
        Assert.Null(new SignatureVerifier(f.Git).FormatHint([f.Head()]));
    }

    [Fact]
    public void TheHintNamesARelativeFile_AndAFileWithoutTheKey()
    {
        using var f = new Fixture(trustKey: false);
        var signed = f.SignedBy(f.Key, "signed");

        f.Global("gpg.ssh.allowedSignersFile", "signers");
        Assert.Contains("relative path", new SignatureVerifier(f.Git).FormatHint([signed]));

        var other = Path.Combine(f.T.Path, "other_signers");
        File.WriteAllText(other, "someone@example.invalid " + File.ReadAllText(f.AgentKey + ".pub"));
        f.Global("gpg.ssh.allowedSignersFile", other);
        var hint = new SignatureVerifier(f.Git).FormatHint([signed]);
        Assert.Contains($"may not be listed in \"{other}\"", hint);
        Assert.Contains(">> \"$(git config --global --type=path gpg.ssh.allowedSignersFile)\"", hint);
    }

    [Fact]
    public void TheHintDoesntThrow_OnAnObjectGitCantRead()
    {
        using var f = new Fixture(trustKey: false);
        Assert.Null(new SignatureVerifier(f.Git).FormatHint([new string('1', 40)]));
    }

    public static TheoryData<string> KeyForms() => new("private", "public", "key::", "ssh-");

    private static string KeyInForm(Fixture f, string form) => form switch
    {
        "private" => f.Key,
        "public" => f.Key + ".pub",
        "key::" => "key::" + File.ReadAllText(f.Key + ".pub").Trim(),
        _ => File.ReadAllText(f.Key + ".pub").Trim(),
    };

    [Theory]
    [MemberData(nameof(KeyForms))]
    public void TheHintsCommandsWorkAsPrinted_WithNoSignersFileSet(string form)
    {
        using var f = new Fixture(trustKey: false);
        var signed = f.SignedBy(f.Key, "signed");
        f.Global("user.email", "signer@example.invalid");
        f.Global("user.signingkey", KeyInForm(f, form));

        f.RunHintCommands(new SignatureVerifier(f.Git).FormatHint([signed])!);

        Assert.Equal([signed], new SignatureVerifier(f.Git).FindVerified([signed]));
    }

    /// <summary>A path git expands, which a shell wouldn't in double quotes.</summary>
    [Theory]
    [MemberData(nameof(KeyForms))]
    public void TheHintsCommandWorksAsPrinted_ForASignersFileUnderHome(string form)
    {
        using var f = new Fixture(trustKey: false);
        var signed = f.SignedBy(f.Key, "signed");
        File.WriteAllText(Path.Combine(f.Env["HOME"], "signers with space"), "");
        f.Global("gpg.ssh.allowedSignersFile", "~/signers with space");
        f.Global("user.signingkey", KeyInForm(f, form)); // and no user.email

        f.RunHintCommands(new SignatureVerifier(f.Git).FormatHint([signed])!);

        Assert.Equal([signed], new SignatureVerifier(f.Git).FindVerified([signed]));
    }

    [Fact]
    public void TheHintDoesntGuessAKey_WhenADefaultKeyCommandGivesIt()
    {
        using var f = new Fixture(trustKey: false);
        var signed = f.SignedBy(f.Key, "signed");
        TestSupport.Git.Run(f.T.Path, "config", "--file", f.Env["GIT_CONFIG_GLOBAL"], "--unset", "user.signingkey");
        f.Global("gpg.ssh.defaultKeyCommand", "ssh-add -L");
        Assert.Contains("<your public key>", new SignatureVerifier(f.Git).FormatHint([signed]));
    }

    /// <summary>ssh-keygen gives up on these before reading the message, and git dies of the SIGPIPE.</summary>
    [Fact]
    public void AForgedSignatureThatKillsTheRead_IsReSigned()
    {
        using var f = new Fixture();
        SetBase(f);
        f.Global("commit.gpgsign", "true");
        var valid = f.SignedBy(f.Key, "valid");
        var forged = f.Forged("SSH SIGNATURE", new string('x', 2_000_000));

        Assert.Equal("1 unsigned commit; review, then run pen sign", new BranchSigner(f.Git).FormatReminder());
        Assert.Equal([valid], new SignatureVerifier(f.Git).FindVerified([valid, forged]));

        new BranchSigner(f.Git).Sign(null);

        var head = f.Head();
        Assert.NotEqual(forged, head);
        Assert.Equal([valid], f.Obj(head).Parents);
        Assert.Equal([head], new SignatureVerifier(f.Git).FindVerified([head]));
    }

    /// <summary>Headers git itself refuses: its exit code can't tell these from a broken repo.</summary>
    [Theory]
    [InlineData("gpgsig garbage")]
    [InlineData("gpgsig -----BEGIN FOO SIGNATURE-----\n a\n -----END FOO SIGNATURE-----")]
    public void ASignatureHeaderGitRefuses_IsReSigned(string header)
    {
        using var f = new Fixture();
        SetBase(f);
        var valid = f.SignedBy(f.Key, "valid");
        var forged = f.ForgedHeader(header);

        Assert.Equal([valid], new SignatureVerifier(f.Git).FindVerified([valid, forged]));

        new BranchSigner(f.Git).Sign(null);

        Assert.NotEqual(forged, f.Head());
        Assert.Equal([valid], f.Obj(f.Head()).Parents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACommitThatCantBeRead_IsAClearError_NotARewrite(bool garbled)
    {
        using var f = new Fixture();
        SetBase(f);
        var valid = f.SignedBy(f.Key, "valid");
        var broken = f.Forged("SSH SIGNATURE");
        var path = f.ObjectPath(broken);
        File.Delete(path);
        if (garbled) File.WriteAllText(path, "not a git object");

        var e = Assert.Throws<PenException>(() => new SignatureVerifier(f.Git).FindVerified([valid, broken]));
        Assert.Contains($"can't read commit {broken[..12]}", e.Message);
        Assert.Null(new BranchSigner(f.Git).FormatReminder()); // advice only: it says nothing, rather than miscounting
        Assert.Throws<PenException>(() => new BranchSigner(f.Git).Sign(null));
        Assert.Equal(broken, TestSupport.Git.Run(f.Repo, "rev-parse", "HEAD").Trim());
    }

    [Fact]
    public void ACommitWhoseTreeIsMissing_SaysSo()
    {
        using var f = new Fixture();
        SetBase(f);
        var commit = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");
        var tree = f.Tree(commit);
        File.Delete(f.ObjectPath(tree));

        var e = Assert.Throws<PenException>(() => new BranchSigner(f.Git).Sign(null));

        Assert.Contains($"its tree {tree} isn't in this repo", e.Message);
        Assert.Contains($"git cat-file -e {tree}", e.Message);
        Assert.Equal(commit, f.Head());
    }

    [Fact]
    public void AMissingCommitInTheRange_FailsWithGitsError()
    {
        using var f = new Fixture();
        SetBase(f);
        var middle = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");
        var head = f.Commit("a", "2", "A <a@x.io>", "2026-01-02T00:00:00Z", "two");
        File.Delete(f.ObjectPath(middle));

        var e = Assert.Throws<PenException>(() => new BranchSigner(f.Git).Sign(null));

        Assert.StartsWith("git rev-list failed: ", e.Message);
        Assert.Contains(middle, e.Message); // git names the commit it couldn't read
        Assert.Equal(head, f.Head());
    }

    [Fact]
    public void ALevelOfUndefinedStillOnlyAcceptsAGoodVerifiedSignature()
    {
        using var f = new Fixture();
        f.Global("gpg.minTrustLevel", "undefined");
        var valid = f.SignedBy(f.Key, "valid");
        var untrusted = f.SignedBy(f.AgentKey, "good signature, key not trusted"); // U
        var forged = f.Forged("SSH SIGNATURE"); // N or a failed read
        Assert.Equal("U", f.Git.WithConfig(SignatureConfig.BuildForVerifying(f.Git)).ReadOutput("log", "-1", "--format=%G?", untrusted));

        Assert.Equal([valid], new SignatureVerifier(f.Git).FindVerified([valid, untrusted, forged]));
    }

    [Fact]
    public void ARevocationFileInTheRepo_IsIgnored()
    {
        using var f = new Fixture();
        var valid = f.SignedBy(f.Key, "valid");
        var krl = Path.Combine(f.T.Path, "krl");
        var psi = new ProcessStartInfo("ssh-keygen", ["-k", "-f", krl, f.Key + ".pub"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        Assert.Equal(0, Process.RunAndCaptureText(psi).ExitStatus.ExitCode);
        TestSupport.Git.Run(f.Repo, "config", "gpg.ssh.revocationFile", krl);
        Assert.Equal("B", TestSupport.Git.Run(f.Repo, "-c", "gpg.ssh.program=ssh-keygen", "-c", "gpg.ssh.allowedSignersFile=" + f.Signers, "log", "-1", "--format=%G?", valid).Trim());

        Assert.Equal([valid], new SignatureVerifier(f.Git).FindVerified([valid]));
    }

    private const string Tree1 = "1111111111111111111111111111111111111111";
    private const string Tree2 = "2222222222222222222222222222222222222222";
    private const string Author = "author A <a@x.io> 1700000000 +0000";
    private const string Committer = "committer A <a@x.io> 1700000000 +0000";

    /// <summary>Each of these is a header block git wouldn't write, ending before the blank line.</summary>
    public static TheoryData<string> BadHeaders() => new(
        $"tree {Tree1}\n{Author}\n{Committer}\ntree {Tree2}",
        $"tree {Tree1}\ntree {Tree2}\n{Author}\n{Committer}",
        $"{Author}\ntree {Tree1}\n{Committer}",
        $"parent {Tree2}\ntree {Tree1}\n{Author}\n{Committer}",
        $"tree {Tree1}\n{Author}\n{Committer}\nparent {Tree2}",
        $"tree {Tree1}\n{Author}\nparent {Tree2}\n{Committer}",
        $"tree {Tree1}\nparent {Tree2}\n{Author}\n{Committer}\nparent {Tree2}",
        $"tree {Tree1}\n{Author}\n{Author}\n{Committer}",
        $"tree {Tree1}\n{Author}\n{Committer}\n{Author}",
        $"tree {Tree1}\n{Author}\n{Committer}\n{Committer}",
        $"tree {Tree1}\n{Committer}\n{Author}",
        $"tree {Tree1}\n{Author}",
        $"tree {Tree1}\n tree {Tree2}\n{Author}\n{Committer}",
        $"tree {Tree1}\n parent {Tree2}\nparent {Tree2}\n{Author}\n{Committer}",
        $"tree {Tree1}\n{Author}\n{Committer}\n tree {Tree2}",
        $"tree {Tree1}\n{Author}\n{Committer}\ngpgsig x\n tree {Tree2}",
        $"tree {Tree1}\n{Author}\n{Committer}\nencoding UTF-8\nencoding ISO-8859-1",
        $"tree {Tree1}\n{Author}\n{Committer}\ngpgsig a\ngpgsig b",
        $"tree {Tree1}\n{Author}\n{Committer}\ngpgsig-sha256 a\ngpgsig-sha256 b",
        $"tree {Tree1}\n{Author}\n{Committer}\nnospace",
        $"tree {Tree1}\0\n{Author}\n{Committer}",
        $"tree {Tree1}\nauthor A\0B <a@x.io> 1700000000 +0000\n{Committer}",
        $"tree {Tree1}\n{Author}\n{Committer}\nchange-id a\0b",
        $"tree {Tree1}\r\n{Author}\n{Committer}",
        $"tree {Tree1}\n{Author}\n{Committer}\ngpgsig a\r",
        $"tree nothex\n{Author}\n{Committer}",
        $"tree {Tree1.ToUpperInvariant().Replace('1', 'A')}\n{Author}\n{Committer}",
        $"tree {Tree1}\nparent short\n{Author}\n{Committer}",
        $"tree {Tree1}\nauthor A <a@x.io>\n{Committer}",
        $"tree {Tree1}\nauthor A <a> <b> 1 +0000\n{Committer}",
        $"tree {Tree1}\nauthor A <a@x.io> 1 +0000 extra\n{Committer}");

    [Theory]
    [MemberData(nameof(BadHeaders))]
    public void HeadersGitWouldntWrite_AreRefused(string headers) =>
        Assert.Throws<CommitShapeException>(() => CommitObject.Parse(System.Text.Encoding.UTF8.GetBytes(headers + "\n\nmessage\n")));

    [Theory]
    [InlineData("\n")]
    [InlineData("")]
    public void NoBlankLineAfterTheHeaders_IsRefused(string end) =>
        Assert.Throws<CommitShapeException>(() => CommitObject.Parse(System.Text.Encoding.UTF8.GetBytes($"tree {Tree1}\n{Author}\n{Committer}{end}")));

    [Theory]
    [InlineData("author José <a@x.io> 1 +0000\ncommitter C <c@x.io> 1 +0000", "author name")]
    [InlineData("author A <josé@x.io> 1 +0000\ncommitter C <c@x.io> 1 +0000", "author email")]
    [InlineData("author A <a@x.io> 1 +0000\ncommitter José <c@x.io> 1 +0000", "committer name")]
    [InlineData("author A <a@x.io> 1 +0000\ncommitter C <c@x.io> 1 +0000\nencoding é", "encoding header")]
    public void AHeaderThatIsntUtf8_IsRefused_SinceItWouldChangeOnTheWay(string headers, string what)
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes($"tree {Tree1}\n{headers}\n\nm\n");
        Assert.Equal(what, Assert.Throws<NotUtf8Exception>(() => CommitObject.Parse(bytes)).What);
    }

    [Fact]
    public void ACommitWithANonUtf8Author_IsRefusedByNameWithoutItsBytes()
    {
        using var f = new Fixture();
        SetBase(f);
        var raw = System.Text.Encoding.Latin1.GetBytes($"tree {f.Tree("HEAD")}\nparent {f.Head()}\nauthor Jos\u00e9\u001b[31m <a@x.io> 1 +0000\n{Committer}\nencoding ISO-8859-1\n\nm\n");
        var written = f.Git.Run(["hash-object", "-t", "commit", "-w", "--stdin"], raw);
        Assert.True(written.Ok, written.Stderr);
        var commit = written.Text.Trim();
        TestSupport.Git.Run(f.Repo, "update-ref", "HEAD", commit);

        var e = Assert.Throws<PenException>(() => new BranchSigner(f.Git).Sign(null));

        Assert.Equal($"refusing commit {commit[..12]}: its author name isn't valid UTF-8, and pen can't pass that to git unchanged, so it can't be re-created with the same author name", e.Message);
        Assert.Equal(commit, f.Head());
    }

    [Theory]
    [InlineData("gpgsig a\n b")]
    [InlineData("gpgsig a\ngpgsig-sha256 b")]
    [InlineData("encoding ISO-8859-1\ngpgsig a")]
    [InlineData("mergetag object 1\n tree in a tag's message\n parent too\nmergetag object 2")]
    [InlineData("change-id abc\nchange-id def\nHG:extra x")]
    public void HeadersOtherToolsWrite_AreAccepted(string extras)
    {
        var c = CommitObject.Parse(System.Text.Encoding.UTF8.GetBytes($"tree {Tree1}\nparent {Tree2}\n{Author}\n{Committer}\n{extras}\n\nm\n"));
        Assert.Equal(Tree1, c.Tree);
        Assert.Equal([Tree2], c.Parents);
    }

    /// <summary>Writes a commit object as given, and moves the branch there.</summary>
    private static string WriteRaw(Fixture f, string commit)
    {
        var written = f.Git.Run(["hash-object", "-t", "commit", "-w", "--stdin", "--literally"], System.Text.Encoding.UTF8.GetBytes(commit));
        Assert.True(written.Ok, written.Stderr);
        TestSupport.Git.Run(f.Repo, "update-ref", "HEAD", written.Text.Trim());
        return f.Head();
    }

    /// <summary>git show displays the first tree; a parser taking the last would sign the second.</summary>
    [Theory]
    [InlineData("tree")]
    [InlineData("parent")]
    public void ALaterDuplicateHeader_CantGetSigned(string kind)
    {
        using var f = new Fixture();
        SetBase(f);
        var evilParent = f.Commit("evil", "evil", "A <a@x.io>", "2026-01-01T00:00:00Z", "evil");
        var evilTree = f.Tree(evilParent);
        TestSupport.Git.Run(f.Repo, "reset", "-q", "--hard", "HEAD~1");
        var good = f.Commit("good", "good", "A <a@x.io>", "2026-01-02T00:00:00Z", "good");
        var goodTree = f.Tree(good);
        var extra = kind == "tree" ? $"tree {evilTree}" : $"parent {evilParent}";
        var forged = WriteRaw(f, $"tree {goodTree}\nparent {good}\n{Author}\n{Committer}\n{extra}\n\nreviewed content\n");
        Assert.Equal(goodTree, f.Tree(forged)); // what the user reviews

        var e = Assert.Throws<PenException>(() => new BranchSigner(f.Git).Sign(null));

        Assert.Contains($"refusing commit {forged[..12]}", e.Message);
        Assert.Equal(forged, f.Head());
        Assert.Equal(goodTree, f.Tree("HEAD"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhereTheParserAndGitDisagree_ItsRefused(bool wrongTree)
    {
        using var f = new Fixture();
        var head = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");
        var c = f.Obj(head);
        new CommitReader(f.Git).CheckMatchesGit([head], new Dictionary<string, CommitObject> { [head] = c });

        var other = wrongTree ? c with { Tree = Tree1 } : c with { Parents = [Tree1] };
        var e = Assert.Throws<PenException>(() => new CommitReader(f.Git).CheckMatchesGit([head], new Dictionary<string, CommitObject> { [head] = other }));
        Assert.Contains($"refusing commit {head[..12]}", e.Message);
    }

    [Fact]
    public void AGraftOrShallowEntryDoesntStopACommitBeingRead()
    {
        using var f = new Fixture();
        var b = f.Head();
        var c1 = f.Commit("a", "1", "A <a@x.io>", "2026-01-01T00:00:00Z", "one");
        var c2 = f.Commit("a", "2", "A <a@x.io>", "2026-01-02T00:00:00Z", "two");
        Directory.CreateDirectory(Path.Combine(f.Repo, ".git", "info"));
        File.WriteAllText(Path.Combine(f.Repo, ".git", "info", "grafts"), $"{c2} {b}\n");
        File.WriteAllText(Path.Combine(f.Repo, ".git", "shallow"), $"{c1}\n");

        Assert.Equal([c1], new CommitReader(f.Git).Read([c2])[c2].Parents);
        Assert.Equal([b], new CommitReader(f.Git).Read([c1])[c1].Parents);
    }

    [Fact]
    public void AnEncodingHeader_IsKeptWhenReSigning()
    {
        using var f = new Fixture();
        var b = SetBase(f);
        File.WriteAllText(Path.Combine(f.Repo, "a"), "1");
        TestSupport.Git.Run(f.Repo, "add", "a");
        var message = Path.Combine(f.T.Path, "message");
        File.WriteAllBytes(message, System.Text.Encoding.Latin1.GetBytes("café\n"));
        TestSupport.Git.Run(f.Repo, "-c", "i18n.commitEncoding=ISO-8859-1", "commit", "-q", "-F", message);
        var old = f.Head();

        new BranchSigner(f.Git).Sign(null);

        var head = f.Head();
        Assert.NotEqual(old, head);
        Assert.Equal(f.Tree(old), f.Tree(head));
        Assert.Equal([b], f.Obj(head).Parents);
        Assert.Equal("ISO-8859-1", f.Obj(head).Encoding);
        Assert.Equal(System.Text.Encoding.Latin1.GetBytes("café\n"), f.Obj(head).Message);
    }

    [Fact]
    public void AMergeOfASignedTagWithItsMergetag_KeepsItsTreeAndParents()
    {
        using var f = new Fixture();
        SetBase(f);
        TestSupport.Git.Run(f.Repo, "checkout", "-q", "-b", "side");
        f.Commit("s", "side", "A <a@x.io>", "2026-01-01T00:00:00Z", "side");
        TestSupport.Git.Run(f.Repo, "-c", "gpg.format=ssh", "-c", "gpg.ssh.program=ssh-keygen", "-c", $"user.signingkey={f.Key}", "tag", "-s", "-m", "tag message", "v1");
        TestSupport.Git.Run(f.Repo, "checkout", "-q", "main");
        f.Commit("m", "main", "A <a@x.io>", "2026-01-02T00:00:00Z", "main");
        TestSupport.Git.Run(f.Repo, "merge", "-q", "--no-ff", "--no-edit", "v1");
        var merge = f.Head();
        Assert.Contains("\nmergetag ", f.Git.Run("cat-file", "commit", merge).Text);

        new BranchSigner(f.Git).Sign(null);

        var head = f.Head();
        Assert.NotEqual(merge, head);
        Assert.Equal(f.Tree(merge), f.Tree(head));
        Assert.Equal(2, f.Obj(head).Parents.Count);
    }

    [Fact]
    public void AnUnknownExtraHeader_StillWorks()
    {
        using var f = new Fixture();
        var b = SetBase(f);
        var extra = WriteRaw(f, $"tree {f.Tree("HEAD")}\nparent {b}\n{Author}\n{Committer}\nchange-id abc\n\nmessage\n");

        new BranchSigner(f.Git).Sign(null);

        Assert.NotEqual(extra, f.Head());
        Assert.Equal(f.Tree(extra), f.Tree("HEAD"));
    }
}
