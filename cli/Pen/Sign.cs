using System.Text;

namespace Pen;

/// <summary>A commit object as stored, parsed just enough to re-create it.</summary>
internal sealed record CommitObject(
    string Tree, IReadOnlyList<string> Parents,
    string AuthorName, string AuthorEmail, string AuthorDate,
    string? Encoding, byte[] Message)
{
    public string Subject
    {
        get
        {
            var text = System.Text.Encoding.UTF8.GetString(Message);
            int nl = text.IndexOf('\n');
            return (nl >= 0 ? text[..nl] : text).Trim();
        }
    }

    /// <summary>
    /// Parses the raw bytes of <c>git cat-file commit</c>, refusing any header
    /// shape git doesn't write. git reads the first of a duplicated header and
    /// stops collecting parents at the first other line, so a laxer parser could
    /// sign a tree the user never saw in <c>git show</c>. Headers after the
    /// committer are allowed, since other tools add them, and aren't kept.
    /// </summary>
    public static CommitObject Parse(byte[] raw)
    {
        int split = raw.AsSpan().IndexOf("\n\n"u8);
        if (split < 0) throw new PenException("no blank line after its headers");
        var header = raw.AsSpan(0, split);
        if (header.Contains((byte)0)) throw new PenException("a NUL in its headers");
        if (header.Contains((byte)'\r')) throw new PenException("a carriage return in its headers");

        // Latin-1 keeps each byte as one char, so nothing is lost before the strict decoding below.
        var lines = System.Text.Encoding.Latin1.GetString(header).Split('\n');
        int i = 0;
        string? Take(string key) =>
            i < lines.Length && lines[i].StartsWith(key + " ", StringComparison.Ordinal) ? lines[i++][(key.Length + 1)..] : null;

        var tree = Take("tree");
        if (tree == null || !IsHash(tree)) throw new PenException("no valid tree as its first header");
        var parents = new List<string>();
        while (Take("parent") is { } parent)
        {
            if (!IsHash(parent) || parent.Length != tree.Length) throw new PenException("an invalid parent header");
            parents.Add(parent);
        }
        var (authorName, authorEmail, authorDate) = Ident(Take("author") ?? throw new PenException("no author header after its parents"), "author");
        Ident(Take("committer") ?? throw new PenException("no committer header after its author"), "committer");

        string? encoding = null;
        string? current = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.StartsWith(' '))
            {
                // A tag's message inside a mergetag is free text. Anywhere else, a
                // continuation that reads as a core header has no honest purpose.
                if (current == null || (current != "mergetag" && IsCoreHeader(line[1..].Split(' ')[0])))
                    throw new PenException("a header continuation line where none belongs");
                continue;
            }
            int sp = line.IndexOf(' ');
            if (sp <= 0) throw new PenException("a malformed header");
            current = line[..sp];
            if (IsCoreHeader(current)) throw new PenException($"a second \"{current}\" header, or one out of place");
            if (current is "encoding" or "gpgsig" or "gpgsig-sha256" && !seen.Add(current))
                throw new PenException($"a second \"{current}\" header");
            if (current == "encoding") encoding = Utf8(line[(sp + 1)..], "encoding header");
        }
        return new(tree, parents, authorName, authorEmail, authorDate, encoding, raw[(split + 2)..]);
    }

    private static bool IsCoreHeader(string key) => key is "tree" or "parent" or "author" or "committer";

    private static bool IsHash(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);

    /// <summary>The parts of <c>Name &lt;email&gt; 1700000000 +0000</c>, the only form git writes.</summary>
    private static (string Name, string Email, string Date) Ident(string value, string key)
    {
        int lt = value.IndexOf('<');
        int gt = value.IndexOf('>');
        if (lt < 0 || gt < lt || value.LastIndexOf('<') != lt || value.LastIndexOf('>') != gt)
            throw new PenException($"a malformed {key} header");
        var date = value[(gt + 1)..].Trim();
        if (date.Split(' ') is not [var seconds, var zone]
            || seconds.Length == 0 || !seconds.All(char.IsAsciiDigit)
            || zone.Length != 5 || zone[0] is not ('+' or '-') || !zone[1..].All(char.IsAsciiDigit))
            throw new PenException($"a malformed date in its {key} header");
        return (Utf8(value[..lt].TrimEnd(), key + " name"), Utf8(value[(lt + 1)..gt], key + " email"), date);
    }

    /// <summary>
    /// Header text that goes to git as a string (in the environment or an argument),
    /// which has to be valid UTF-8 to arrive unchanged: .NET can't pass other bytes.
    /// </summary>
    private static string Utf8(string latin1, string what)
    {
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(System.Text.Encoding.Latin1.GetBytes(latin1)); }
        catch (DecoderFallbackException) { throw new NotUtf8Exception(what); }
    }
}

/// <summary>A commit header field that isn't valid UTF-8, so it can't be passed to git unchanged.</summary>
internal sealed class NotUtf8Exception(string what) : Exception(what)
{
    public string What { get; } = what;
}

/// <summary>
/// pen sign: re-creates unpushed commits with the same trees, signed.
/// Each commit keeps its exact tree,
/// merges included, and parents outside the range stay as they are.
/// </summary>
internal static class Sign
{
    /// <summary>The base: <paramref name="arg"/>, else the upstream, else origin/HEAD. Null if none resolves.</summary>
    public static (string Name, string Commit)? ResolveBase(HostGit git, string? arg)
    {
        foreach (var name in arg != null ? [arg] : new[] { "@{upstream}", "refs/remotes/origin/HEAD" })
        {
            var sha = git.TryOut("rev-parse", "--verify", "-q", "--end-of-options", name + "^{commit}");
            if (!string.IsNullOrEmpty(sha)) return (name == "@{upstream}" ? git.TryOut("rev-parse", "--abbrev-ref", name) ?? name : name, sha);
            if (arg != null) throw new PenException($"\"{Out.Field(arg)}\" isn't a commit");
        }
        return null;
    }

    /// <summary><c>base..head</c>, oldest first, parents before children.</summary>
    public static List<string> Range(HostGit git, string baseCommit, string head) =>
        git.Run("rev-list", "--reverse", "--topo-order", $"{baseCommit}..{head}") is { Ok: true } r
            ? r.Lines.ToList()
            : throw new PenException($"git rev-list failed");

    /// <summary>Commits in the range that are also on some remote branch (refs/remotes/*).</summary>
    public static List<string> OnARemote(HostGit git, string baseCommit, string head, IReadOnlyList<string> range)
    {
        var r = git.Run("rev-list", $"{baseCommit}..{head}", "--not", "--remotes");
        if (!r.Ok) throw new PenException($"git rev-list failed: {r.Stderr.Trim()}");
        var unpushed = r.Lines.ToHashSet(StringComparer.Ordinal);
        return range.Where(c => !unpushed.Contains(c)).ToList();
    }

    /// <summary>The commit objects, read in one <c>git cat-file --batch</c> call.</summary>
    public static Dictionary<string, CommitObject> Read(HostGit git, IReadOnlyList<string> commits)
    {
        var result = new Dictionary<string, CommitObject>(StringComparer.Ordinal);
        if (commits.Count == 0) return result;
        var r = git.Run(["cat-file", "--batch"], Encoding.ASCII.GetBytes(string.Join('\n', commits) + "\n"));
        if (!r.Ok) throw new PenException($"git cat-file failed: {r.Stderr.Trim()}");
        var bytes = r.Stdout;
        int pos = 0;
        foreach (var sha in commits)
        {
            int nl = Array.IndexOf(bytes, (byte)'\n', pos);
            if (nl < 0) throw new PenException("git cat-file --batch: truncated output");
            var header = Encoding.ASCII.GetString(bytes, pos, nl - pos).Split(' ');
            if (header.Length != 3 || header[1] != "commit")
                throw new PenException($"{sha} isn't a commit ({string.Join(' ', header)})");
            int size = int.Parse(header[2]);
            try { result[sha] = CommitObject.Parse(bytes[(nl + 1)..(nl + 1 + size)]); }
            catch (PenException e) { throw new PenException($"refusing commit {sha[..12]}: it has {e.Message}, which git doesn't write, so it could be re-created wrongly"); }
            catch (NotUtf8Exception e) { throw new PenException($"refusing commit {sha[..12]}: its {e.What} isn't valid UTF-8, and pen can't pass that to git unchanged, so it can't be re-created with the same {e.What}"); }
            pos = nl + 1 + size + 1; // content, then a newline
        }
        MatchesGit(git, commits, result);
        return result;
    }

    /// <summary>
    /// Compares each parsed commit with what git itself reads as its tree and
    /// parents, in case the two parsers differ in some way nobody foresaw. Grafts
    /// and .git/shallow are left out, since re-creating uses the object's own parents.
    /// </summary>
    internal static void MatchesGit(HostGit git, IReadOnlyList<string> commits, IReadOnlyDictionary<string, CommitObject> parsed)
    {
        var r = git.Run(
            ["log", "--no-walk=unsorted", "--stdin", "--no-show-signature", "--no-notes", "--format=%H %T %P"],
            Encoding.ASCII.GetBytes(string.Join('\n', commits) + "\n"),
            new Dictionary<string, string> { ["GIT_GRAFT_FILE"] = "/dev/null", ["GIT_SHALLOW_FILE"] = "/dev/null" });
        if (!r.Ok) throw new PenException($"git can't read these commits: {r.Stderr.Trim()}");
        var fromGit = r.Lines.Select(l => l.TrimEnd()).ToHashSet(StringComparer.Ordinal);
        foreach (var sha in commits)
        {
            var c = parsed[sha];
            if (!fromGit.Contains($"{sha} {c.Tree} {string.Join(' ', c.Parents)}".TrimEnd()))
                throw new PenException($"refusing commit {sha[..12]}: git reads a different tree or parents than its headers show, so it could be re-created wrongly");
        }
    }

    /// <summary>
    /// The commits whose signature verifies under the user's global settings.
    /// A gpgsig header proves nothing, since the agent can write any header,
    /// and neither does a verification the repo's config had a say in. Only
    /// %G? = G counts: U is a good signature from a key the user doesn't trust.
    /// A forged signature can make git fail, so a failed read of a commit that
    /// can be read otherwise just leaves it unverified. One that can't be read
    /// at all is an error.
    /// </summary>
    public static HashSet<string> Verified(HostGit git, IReadOnlyList<string> commits)
    {
        var verified = new HashSet<string>(StringComparer.Ordinal);
        if (commits.Count == 0) return verified;
        var verifying = git.With(VerificationConfig(git));

        var whole = ReadSignatures(verifying, commits);
        var lines = whole.Ok ? whole.Lines.ToList() : [];
        if (!whole.Ok)
        {
            // Some commit killed that read: ssh-keygen giving up on a forged
            // signature before reading the message (git takes the SIGPIPE), or
            // git refusing a signature header. Its output could be cut short.
            foreach (var commit in commits)
            {
                var one = ReadSignatures(verifying, [commit]);
                if (one.Ok)
                {
                    lines.AddRange(one.Lines);
                    continue;
                }
                var read = git.Run("cat-file", "commit", commit);
                if (!read.Ok) throw new PenException($"can't read commit {commit[..12]}: {read.Stderr.Trim()}");
            }
        }

        // %G? leaves gpg.minTrustLevel to verify-commit, so it's applied here.
        int minTrust = TrustRank(MinTrust(git));
        var wanted = commits.ToHashSet(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var fields = line.Split(' ');
            if (fields is [var sha, "G", var trust] && wanted.Contains(sha) && minTrust >= 0 && TrustRank(trust) >= minTrust)
                verified.Add(sha);
        }
        return verified;
    }

    private static GitResult ReadSignatures(HostGit verifying, IReadOnlyList<string> commits) => verifying.Run(
        ["log", "--no-walk=unsorted", "--stdin", "--no-show-signature", "--no-notes", "--format=%H %G? %GT"],
        Encoding.ASCII.GetBytes(string.Join('\n', commits) + "\n"));

    private static readonly string[] TrustLevels = ["undefined", "never", "marginal", "fully", "ultimate"];

    private static int TrustRank(string level) => Array.IndexOf(TrustLevels, level.ToLowerInvariant());

    private static string MinTrust(HostGit git) => git.GlobalValue("gpg.minTrustLevel") ?? "marginal";

    public static int CountUnsigned(HostGit git, IReadOnlyList<string> commits) =>
        commits.Count - Verified(git, commits).Count;

    /// <summary>
    /// Re-creates each commit (oldest first) that isn't in <paramref name="verified"/>
    /// or has a re-created parent, signed with <c>commit-tree -S</c>: same tree, same
    /// message bytes, same author and date, parents mapped to their new
    /// hashes. Returns the mapping from old to new for every commit given
    /// (unchanged ones map to themselves). Touches no ref, index or worktree.
    /// </summary>
    public static Dictionary<string, string> Rewrite(HostGit git, IReadOnlyList<string> commits, IReadOnlySet<string> verified)
    {
        var objects = Read(git, commits);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sha in commits)
        {
            var c = objects[sha];
            var parents = c.Parents.Select(p => map.TryGetValue(p, out var n) ? n : p).ToList();
            if (verified.Contains(sha) && parents.SequenceEqual(c.Parents))
            {
                map[sha] = sha; // already verifiably signed, and nothing under it changed
                continue;
            }

            var args = new List<string>();
            if (c.Encoding != null) args.AddRange(["-c", $"i18n.commitEncoding={c.Encoding}"]);
            args.AddRange(["commit-tree", c.Tree]);
            foreach (var p in parents) args.AddRange(["-p", p]);
            args.AddRange(["-S", "-F", "-"]);
            var env = new Dictionary<string, string>
            {
                ["GIT_AUTHOR_NAME"] = c.AuthorName,
                ["GIT_AUTHOR_EMAIL"] = c.AuthorEmail,
                ["GIT_AUTHOR_DATE"] = c.AuthorDate,
            };
            var r = git.Run(args, c.Message, env);
            var created = r.Text.Trim();
            if (!r.Ok || created.Length == 0)
            {
                if (!git.Run("cat-file", "-e", c.Tree).Ok)
                    throw new PenException($"can't re-create {sha[..12]}: its tree {c.Tree} isn't in this repo. In a partial clone, this fetches it: git cat-file -e {c.Tree}");
                throw new PenException($"signing {sha[..12]} failed: {r.Stderr.Trim()}");
            }
            map[sha] = created;
        }
        return map;
    }

    /// <summary>Moves the branch from <paramref name="oldHead"/> to <paramref name="newHead"/>, refusing if it moved meanwhile.</summary>
    public static void UpdateBranch(HostGit git, string branchRef, string newHead, string oldHead)
    {
        var r = git.Run("update-ref", "-m", "pen sign", branchRef, newHead, oldHead);
        if (!r.Ok)
            throw new PenException($"{Out.Field(branchRef)} moved while signing, so nothing was changed (the signed commits are unreferenced): {r.Stderr.Trim()}");
    }

    /// <summary>
    /// The format and every program git could run for it, from the user's
    /// global config. All of them, whatever the format: to verify, git picks the
    /// program by the type of the signature, which the agent chooses. Where one
    /// isn't set globally, git's default is passed instead, since a missing
    /// override overrides nothing.
    /// </summary>
    private static List<string> ProgramConfig(HostGit git)
    {
        var config = new List<string>();
        void Pass(string key, string value) => config.AddRange(["-c", $"{key}={value}"]);

        Pass("gpg.format", git.GlobalValue("gpg.format") ?? "openpgp");
        Pass("gpg.program", git.GlobalValue("gpg.program") ?? git.GlobalValue("gpg.openpgp.program") ?? "gpg");
        Pass("gpg.openpgp.program", git.GlobalValue("gpg.openpgp.program") ?? git.GlobalValue("gpg.program") ?? "gpg");
        Pass("gpg.x509.program", git.GlobalValue("gpg.x509.program") ?? "gpgsm");
        Pass("gpg.ssh.program", git.GlobalValue("gpg.ssh.program") ?? "ssh-keygen");
        return config;
    }

    /// <summary>
    /// Command-line settings for reading %G?, so that only the user's own
    /// settings decide what verifies. <c>/dev/null</c> stands in for a file
    /// that isn't set or that the agent could write: no signers are trusted
    /// and no keys are revoked.
    /// </summary>
    public static List<string> VerificationConfig(HostGit git)
    {
        var config = ProgramConfig(git);
        void Pass(string key, string value) => config.AddRange(["-c", $"{key}={value}"]);

        Pass("gpg.ssh.allowedSignersFile", OutsideTheRepo(git.GlobalValue("gpg.ssh.allowedSignersFile")) ?? "/dev/null");
        Pass("gpg.ssh.revocationFile", OutsideTheRepo(git.GlobalValue("gpg.ssh.revocationFile")) ?? "/dev/null");
        Pass("gpg.minTrustLevel", MinTrust(git));
        Pass("gpg.ssh.defaultKeyCommand", git.GlobalValue("gpg.ssh.defaultKeyCommand") ?? "");
        Pass("log.showSignature", "false");
        return config;
    }

    /// <summary>
    /// <paramref name="path"/> unless git would resolve it against the working
    /// tree: the agent writes there, so it could list its own key in the file.
    /// </summary>
    private static string? OutsideTheRepo(string? path)
    {
        if (path == null) return null;
        var name = path.StartsWith(":(optional)", StringComparison.Ordinal) ? path[":(optional)".Length..] : path;
        return name.StartsWith('/') || name.StartsWith('~') || name.StartsWith("%(prefix)/", StringComparison.Ordinal) ? path : null;
    }

    /// <summary>
    /// The signing settings from the user's global config, passed on the
    /// command line so the repo can't substitute its own signing program.
    /// </summary>
    public static List<string> SigningConfig(HostGit git)
    {
        var config = ProgramConfig(git);
        void Pass(string key, string? value) { if (value != null) config.AddRange(["-c", $"{key}={value}"]); }

        var format = git.GlobalValue("gpg.format") ?? "openpgp";
        var key = git.GlobalValue("user.signingkey");
        var keyCommand = git.GlobalValue("gpg.ssh.defaultKeyCommand");
        Pass("user.signingkey", key);
        Pass("gpg.ssh.defaultKeyCommand", keyCommand);
        // With ssh and no key, git runs gpg.ssh.defaultKeyCommand, which a
        // repo could set: so one of the two has to come from global config.
        if (format == "ssh" && key == null && keyCommand == null)
            throw new PenException("gpg.format is ssh but user.signingkey isn't set in your global git config: set it (git config --global user.signingkey ~/.ssh/id_ed25519.pub)");
        return config;
    }

    /// <summary>A commit's line in what pen sign says it will re-sign. The agent wrote the subject.</summary>
    internal static string Listing(string sha, CommitObject commit, bool verified) =>
        $"  {sha[..12]} {Out.Field(commit.Subject)}{(verified ? " (signed)" : "")}";

    /// <summary>
    /// How to fix it when commits pen just signed don't verify, or null when
    /// they do. Until it's fixed, each pen sign re-signs them. Never throws:
    /// the signing is done by now, and this is advice.
    /// </summary>
    internal static string? VerificationHint(HostGit git, IReadOnlyList<string> created)
    {
        try
        {
            if (Verified(git, created).Count == created.Count) return null;
            var first = "warning: git can't verify the signatures just made, so each pen sign will re-sign these commits and change their hashes.\n";
            if (git.GlobalValue("gpg.format") != "ssh")
                return first + "  Check that your key is in your keyring, and that you trust it.";

            var signers = git.GlobalValue("gpg.ssh.allowedSignersFile");
            var addKey = $"echo \"{EmailInHint(git)} {PublicKeyInHint(git)}\" >> ";
            if (OutsideTheRepo(signers) != null)
                return first + $"  Your key may not be listed in \"{Out.Field(signers!)}\". To add it:\n    {addKey}\"$(git config --global --type=path gpg.ssh.allowedSignersFile)\"";
            var reason = signers == null
                ? "gpg.ssh.allowedSignersFile isn't set in your global git config"
                : $"gpg.ssh.allowedSignersFile (\"{Out.Field(signers)}\") is a relative path, which pen ignores since the project could write to it";
            return first
                + $"  {reason}. To set it up:\n"
                + "    mkdir -p ~/.config/git\n"
                + $"    {addKey}~/.config/git/allowed_signers\n"
                + "    git config --global gpg.ssh.allowedSignersFile ~/.config/git/allowed_signers";
        }
        catch (PenException) { return null; }
    }

    private static string EmailInHint(HostGit git) =>
        git.GlobalValue("user.email") != null ? "$(git config --global user.email)" : "you@example.com";

    /// <summary>Shell text that gives the public key of the configured signing key, which can be given in several forms.</summary>
    private static string PublicKeyInHint(HostGit git)
    {
        const string path = "\"$(git config --global --type=path user.signingkey)\"";
        return git.GlobalValue("user.signingkey") switch
        {
            null => "<your public key>", // it comes from gpg.ssh.defaultKeyCommand
            var k when k.StartsWith("key::", StringComparison.Ordinal) => "$(git config --global user.signingkey | cut -c6-)",
            var k when k.StartsWith("ssh-", StringComparison.Ordinal) => "$(git config --global user.signingkey)",
            var k when k.EndsWith(".pub", StringComparison.Ordinal) => $"$(cat {path})",
            _ => $"$(ssh-keygen -y -f {path})",
        };
    }

    /// <summary>The whole pen sign command, run in the project dir on the host.</summary>
    public static int Run(HostEnv host, Project project, string? baseArg)
    {
        if (!project.IsRepo) throw new PenException($"{Out.Field(project.Dir)} isn't a git repo");
        Session.EnsureRuntimeDir(host, project);
        using var locks = Sessions.TakeForSign(host, project);
        // This command's own check below takes the module repos there now, so
        // it can't stand in for an ended session's that didn't finish.
        if (!Session.FinishDeadSessions(project.Key, host.RuntimeDir(project.Key)))
            throw new PenException("not signing: deal with the warning above first");

        var check = GitCheck.RunWithoutOpening(project.Dir, startedWithGit: true, project.CommonGitDir);
        if (check.Moved.Count > 0 || check.Failure != null)
        {
            GitCheck.Report(project.Dir, check);
            throw new PenException("not signing: deal with the files above first");
        }
        // Signing doesn't continue these, so they're not a reason to refuse.
        GitCheck.Report(project.Dir, check);

        return SignBranch(new HostGit(project.Dir), baseArg);
    }

    /// <summary>Signs the checked-out branch's unpushed commits, once the repo has passed its checks.</summary>
    internal static int SignBranch(HostGit git, string? baseArg)
    {
        var branchRef = git.TryOut("symbolic-ref", "-q", "HEAD")
            ?? throw new PenException("HEAD is detached: check out the branch to sign");
        var head = git.Out("rev-parse", "--verify", "HEAD^{commit}");
        var b = ResolveBase(git, baseArg)
            ?? throw new PenException($"{Out.Field(branchRef["refs/heads/".Length..])} has no upstream and there's no origin/HEAD: name the base, e.g. pen sign origin/main");

        var range = Range(git, b.Commit, head);
        if (range.Count == 0)
        {
            Out.Line($"nothing to sign: no commits in {Out.Field(b.Name)}..HEAD");
            return 0;
        }
        var pushed = OnARemote(git, b.Commit, head, range);
        if (pushed.Count > 0)
            throw new PenException($"refusing to rewrite commits that are on a remote branch: {string.Join(", ", pushed.Select(c => c[..12]))} (pass a later base, e.g. pen sign <commit>)");

        var objects = Read(git, range);
        var verified = Verified(git, range);
        if (verified.Count == range.Count)
        {
            Out.Line($"nothing to sign: all {range.Count} commits in {Out.Field(b.Name)}..HEAD are signed");
            return 0;
        }

        Out.Line($"re-signing {range.Count} commit{(range.Count == 1 ? "" : "s")} in {Out.Field(b.Name)}..HEAD (same trees and authors; hashes change):");
        foreach (var sha in range)
            Out.Raw(Listing(sha, objects[sha], verified.Contains(sha)));

        var signing = git.With(SigningConfig(git));
        var map = Rewrite(signing, range, verified);
        var newHead = map[head];
        if (newHead != head) UpdateBranch(git, branchRef, newHead, head);
        Out.Line($"signed: {Out.Field(branchRef["refs/heads/".Length..])} is now {newHead[..12]} (was {head[..12]}; the old commits stay in the reflog)");
        if (VerificationHint(git, range.Where(c => map[c] != c).Select(c => map[c]).ToList()) is { } hint)
            Out.Line(hint);
        return 0;
    }

    /// <summary>
    /// The session-end reminder: when the host's global commit.gpgsign is on,
    /// the number of commits ahead of the base that don't verify. Null when
    /// there's no reminder to give. Never throws: it's advice, after the real
    /// work is done.
    /// </summary>
    public static string? Reminder(string projectDir) =>
        Path.Exists(Path.Combine(projectDir, ".git")) ? Reminder(new HostGit(projectDir)) : null;

    internal static string? Reminder(HostGit git)
    {
        try
        {
            if (!git.GlobalFlag("commit.gpgsign")) return null;
            var head = git.TryOut("rev-parse", "--verify", "-q", "HEAD^{commit}");
            if (string.IsNullOrEmpty(head)) return null;
            var b = ResolveBase(git, null);
            if (b == null) return null;
            var range = Range(git, b.Value.Commit, head);
            int n = CountUnsigned(git, range);
            return n == 0 ? null : $"{n} unsigned commit{(n == 1 ? "" : "s")}; review, then run pen sign";
        }
        catch (PenException) { return null; }
    }
}
