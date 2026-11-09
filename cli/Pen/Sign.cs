using System.Text;

namespace Pen;

/// <summary>A commit object as stored, parsed just enough to re-create it.</summary>
internal sealed record CommitObject(
    string Tree, IReadOnlyList<string> Parents,
    string AuthorName, string AuthorEmail, string AuthorDate,
    string? Encoding, bool Signed, byte[] Message)
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

    /// <summary>Parses the raw bytes of <c>git cat-file commit</c>.</summary>
    public static CommitObject Parse(byte[] raw)
    {
        int split = IndexOf(raw, "\n\n"u8);
        var headerBytes = split >= 0 ? raw.AsSpan(0, split) : raw.AsSpan();
        var message = split >= 0 ? raw[(split + 2)..] : [];

        string? tree = null, encoding = null;
        string? authorName = null, authorEmail = null, authorDate = null;
        var parents = new List<string>();
        bool signed = false;
        foreach (var line in System.Text.Encoding.UTF8.GetString(headerBytes).Split('\n'))
        {
            if (line.StartsWith(' ')) continue; // continuation of a multi-line header (gpgsig, mergetag)
            int sp = line.IndexOf(' ');
            if (sp < 0) continue;
            var key = line[..sp];
            var value = line[(sp + 1)..];
            switch (key)
            {
                case "tree": tree = value; break;
                case "parent": parents.Add(value); break;
                case "encoding": encoding = value; break;
                case "gpgsig" or "gpgsig-sha256": signed = true; break;
                case "author":
                    int lt = value.IndexOf('<');
                    int gt = lt < 0 ? -1 : value.IndexOf('>', lt);
                    if (gt < 0) throw new PenException($"can't parse commit author \"{value}\"");
                    authorName = value[..lt].TrimEnd();
                    authorEmail = value[(lt + 1)..gt];
                    authorDate = value[(gt + 1)..].Trim();
                    break;
            }
        }
        if (tree == null || authorName == null)
            throw new PenException("can't parse a commit object (no tree or author)");
        return new(tree, parents, authorName, authorEmail!, authorDate!, encoding, signed, message);
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) => haystack.AsSpan().IndexOf(needle);
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
            if (arg != null) throw new PenException($"\"{arg}\" isn't a commit");
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
            result[sha] = CommitObject.Parse(bytes[(nl + 1)..(nl + 1 + size)]);
            pos = nl + 1 + size + 1; // content, then a newline
        }
        return result;
    }

    public static int CountUnsigned(HostGit git, IReadOnlyList<string> commits) =>
        Read(git, commits).Values.Count(c => !c.Signed);

    /// <summary>
    /// Re-creates each commit (oldest first) that is unsigned or has a
    /// re-created parent, signed with <c>commit-tree -S</c>: same tree, same
    /// message bytes, same author and date, parents mapped to their new
    /// hashes. Returns the mapping from old to new for every commit given
    /// (unchanged ones map to themselves). Touches no ref, index or worktree.
    /// </summary>
    public static Dictionary<string, string> Rewrite(HostGit git, IReadOnlyList<string> commits)
    {
        var objects = Read(git, commits);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sha in commits)
        {
            var c = objects[sha];
            var parents = c.Parents.Select(p => map.TryGetValue(p, out var n) ? n : p).ToList();
            if (c.Signed && parents.SequenceEqual(c.Parents))
            {
                map[sha] = sha; // already signed, and nothing under it changed
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
                throw new PenException($"signing {sha[..12]} failed: {r.Stderr.Trim()}");
            map[sha] = created;
        }
        return map;
    }

    /// <summary>Moves the branch from <paramref name="oldHead"/> to <paramref name="newHead"/>, refusing if it moved meanwhile.</summary>
    public static void UpdateBranch(HostGit git, string branchRef, string newHead, string oldHead)
    {
        var r = git.Run("update-ref", "-m", "pen sign", branchRef, newHead, oldHead);
        if (!r.Ok)
            throw new PenException($"{branchRef} moved while signing, so nothing was changed (the signed commits are unreferenced): {r.Stderr.Trim()}");
    }

    /// <summary>
    /// The signing settings from the user's global config, passed on the
    /// command line so the repo can't substitute its own signing program.
    /// Where one isn't set globally, git's default is passed instead, since a
    /// missing override overrides nothing.
    /// </summary>
    public static List<string> SigningConfig()
    {
        var config = new List<string>();
        void Pass(string key, string? value) { if (value != null) config.AddRange(["-c", $"{key}={value}"]); }

        var format = HostGit.GlobalConfig("gpg.format") ?? "openpgp";
        Pass("gpg.format", format);
        Pass("gpg.program", HostGit.GlobalConfig("gpg.program") ?? HostGit.GlobalConfig("gpg.openpgp.program") ?? "gpg");
        Pass("gpg.openpgp.program", HostGit.GlobalConfig("gpg.openpgp.program") ?? HostGit.GlobalConfig("gpg.program") ?? "gpg");
        Pass("gpg.x509.program", HostGit.GlobalConfig("gpg.x509.program") ?? "gpgsm");
        Pass("gpg.ssh.program", HostGit.GlobalConfig("gpg.ssh.program") ?? "ssh-keygen");

        var key = HostGit.GlobalConfig("user.signingkey");
        var keyCommand = HostGit.GlobalConfig("gpg.ssh.defaultKeyCommand");
        Pass("user.signingkey", key);
        Pass("gpg.ssh.defaultKeyCommand", keyCommand);
        // With ssh and no key, git runs gpg.ssh.defaultKeyCommand, which a
        // repo could set: so one of the two has to come from global config.
        if (format == "ssh" && key == null && keyCommand == null)
            throw new PenException("gpg.format is ssh but user.signingkey isn't set in your global git config: set it (git config --global user.signingkey ~/.ssh/id_ed25519.pub)");
        return config;
    }

    /// <summary>The whole pen sign command, run in the project dir on the host.</summary>
    public static int Run(HostEnv host, Project project, string? baseArg)
    {
        if (!project.IsRepo) throw new PenException($"{project.Dir} isn't a git repo");
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

        var git = new HostGit(project.Dir);
        var branchRef = git.TryOut("symbolic-ref", "-q", "HEAD")
            ?? throw new PenException("HEAD is detached: check out the branch to sign");
        var head = git.Out("rev-parse", "--verify", "HEAD^{commit}");
        var b = ResolveBase(git, baseArg)
            ?? throw new PenException($"{branchRef["refs/heads/".Length..]} has no upstream and there's no origin/HEAD: name the base, e.g. pen sign origin/main");

        var range = Range(git, b.Commit, head);
        if (range.Count == 0)
        {
            Out.Line($"nothing to sign: no commits in {b.Name}..HEAD");
            return 0;
        }
        var pushed = OnARemote(git, b.Commit, head, range);
        if (pushed.Count > 0)
            throw new PenException($"refusing to rewrite commits that are on a remote branch: {string.Join(", ", pushed.Select(c => c[..12]))} (pass a later base, e.g. pen sign <commit>)");

        var objects = Read(git, range);
        if (objects.Values.All(c => c.Signed))
        {
            Out.Line($"nothing to sign: all {range.Count} commits in {b.Name}..HEAD are signed");
            return 0;
        }

        Out.Line($"re-signing {range.Count} commit{(range.Count == 1 ? "" : "s")} in {b.Name}..HEAD (same trees and authors; hashes change):");
        foreach (var sha in range)
            Out.Raw($"  {sha[..12]} {objects[sha].Subject}{(objects[sha].Signed ? " (signed)" : "")}");

        var signing = git.With(SigningConfig());
        var map = Rewrite(signing, range);
        var newHead = map[head];
        if (newHead != head) UpdateBranch(git, branchRef, newHead, head);
        Out.Line($"signed: {branchRef["refs/heads/".Length..]} is now {newHead[..12]} (was {head[..12]}; the old commits stay in the reflog)");
        return 0;
    }

    /// <summary>
    /// The session-end reminder: when the host's global commit.gpgsign is on,
    /// the number of unsigned commits ahead of the base. Null when there's no
    /// reminder to give. Never throws: it's advice, after the real work is done.
    /// </summary>
    public static string? Reminder(string projectDir)
    {
        try
        {
            if (!Path.Exists(Path.Combine(projectDir, ".git")) || !HostGit.GlobalBool("commit.gpgsign")) return null;
            var git = new HostGit(projectDir);
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
