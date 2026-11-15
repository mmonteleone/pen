using Pen.Platform;

namespace Pen.Signing;

/// <summary>Signs the checked-out branch's unpushed commits, and counts the unsigned ones for the reminder.</summary>
/// <param name="git">The repo's git, which has passed the session-end check if this signs.</param>
/// <remarks>
/// Each commit is re-created with its exact tree, merges included, and parents outside the range stay as they
/// are. Only what verifies under the user's global settings counts as signed (<see cref="SignatureVerifier"/>).
/// </remarks>
internal sealed class BranchSigner(HostGit git)
{
    /// <summary>Formats a commit's line in what pen sign says it will re-sign.</summary>
    /// <remarks>The agent wrote the subject, so it goes through <see cref="Out.Field"/>.</remarks>
    public static string FormatListing(string sha, CommitObject commit, bool verified) =>
        $"  {sha[..12]} {Out.Field(commit.Subject)}{(verified ? " (signed)" : "")}";

    /// <summary>Formats the session-end reminder for the repo in <paramref name="projectDir"/>.</summary>
    /// <returns>The reminder, or null when there's no reminder to give or <c>.git</c> is gone.</returns>
    /// <remarks>It never throws: it's advice, after the real work is done.</remarks>
    public static string? FormatReminderIn(string projectDir)
    {
        if (!Path.Exists(Path.Combine(projectDir, ".git")))
            return null;
        return new BranchSigner(new HostGit(projectDir)).FormatReminder();
    }

    /// <summary>Re-signs the branch's commits after the base, once the repo has passed its checks.</summary>
    /// <param name="baseArg">The base named on the command line, or null for the upstream or origin/HEAD.</param>
    /// <returns>0 once signed or when there's nothing to sign; failures throw.</returns>
    /// <exception cref="PenException">There's no branch or base, a commit is on a remote, or a step fails.</exception>
    public int Sign(string? baseArg)
    {
        var branchRef = git.TryReadOutput("symbolic-ref", "-q", "HEAD")
            ?? throw new PenException("HEAD is detached: check out the branch to sign");
        var head = git.ReadOutput("rev-parse", "--verify", "HEAD^{commit}");
        var resolved = ResolveBase(baseArg)
            ?? throw new PenException($"{Out.Field(branchRef["refs/heads/".Length..])} has no " +
                "upstream and there's no origin/HEAD: name the base, e.g. pen sign origin/main");

        var range = ListRange(resolved.Commit, head);
        if (range.Count == 0)
        {
            Out.WriteLine($"nothing to sign: no commits in {Out.Field(resolved.Name)}..HEAD");
            return 0;
        }
        RefusePushed(resolved.Commit, head, range);

        var objects = new CommitReader(git).Read(range);
        var verified = new SignatureVerifier(git).FindVerified(range);
        if (verified.Count == range.Count)
        {
            Out.WriteLine($"nothing to sign: all {range.Count} commits in {Out.Field(resolved.Name)}..HEAD are signed");
            return 0;
        }

        Out.WriteLine($"re-signing {range.Count} commit{(range.Count == 1 ? "" : "s")} " +
            $"in {Out.Field(resolved.Name)}..HEAD (same trees and authors; hashes change):");
        foreach (var sha in range)
            Out.WriteRaw(FormatListing(sha, objects[sha], verified.Contains(sha)));
        SignRange(branchRef, head, range, verified);
        return 0;
    }

    /// <summary>Resolves the base: <paramref name="arg"/>, else the upstream, else origin/HEAD.</summary>
    /// <returns>The base, or null if neither default resolves.</returns>
    /// <exception cref="PenException"><paramref name="arg"/> is given and isn't a commit.</exception>
    public ResolvedBase? ResolveBase(string? arg)
    {
        foreach (var name in ListBaseCandidates(arg))
        {
            var sha = git.TryReadOutput("rev-parse", "--verify", "-q", "--end-of-options", name + "^{commit}");
            if (!string.IsNullOrEmpty(sha))
                return new ResolvedBase(FormatBaseName(name), sha);
            if (arg != null)
                throw new PenException($"\"{Out.Field(arg)}\" isn't a commit");
        }
        return null;
    }

    /// <summary>Lists the commits in <c>base..head</c>, oldest first, parents before children.</summary>
    /// <exception cref="PenException">git rev-list failed; the message has its error.</exception>
    public List<string> ListRange(string baseCommit, string head) =>
        git.ReadLines("rev-list", "--reverse", "--topo-order", $"{baseCommit}..{head}").ToList();

    /// <summary>Finds the commits in <paramref name="range"/> that are also on a remote branch.</summary>
    /// <returns>Those commits, in range order; a remote branch is any ref under refs/remotes/.</returns>
    /// <exception cref="PenException">git rev-list failed.</exception>
    public List<string> FindOnARemote(string baseCommit, string head, IReadOnlyList<string> range)
    {
        var unpushed = git.ReadLines("rev-list", $"{baseCommit}..{head}", "--not", "--remotes")
            .ToHashSet(StringComparer.Ordinal);
        return range.Where(commit => !unpushed.Contains(commit)).ToList();
    }

    /// <summary>Moves the branch from <paramref name="oldHead"/> to <paramref name="newHead"/>.</summary>
    /// <exception cref="PenException">The branch moved meanwhile, so nothing was changed.</exception>
    public void UpdateBranch(string branchRef, string newHead, string oldHead)
    {
        var result = git.Run("update-ref", "-m", "pen sign", branchRef, newHead, oldHead);
        if (!result.Ok)
        {
            throw new PenException($"{Out.Field(branchRef)} moved while signing, so nothing " +
                $"was changed (the signed commits are unreferenced): {result.Stderr.Trim()}");
        }
    }

    /// <summary>Formats the session-end reminder: how many commits ahead of the base don't verify.</summary>
    /// <returns>The reminder, or null when the host's global commit.gpgsign is off or nothing is unsigned.</returns>
    /// <remarks>It never throws: it's advice, after the real work is done.</remarks>
    public string? FormatReminder()
    {
        try
        {
            if (!git.IsGlobalFlagOn("commit.gpgsign"))
                return null;
            var head = git.TryReadOutput("rev-parse", "--verify", "-q", "HEAD^{commit}");
            if (string.IsNullOrEmpty(head))
                return null;
            var resolved = ResolveBase(null);
            if (resolved == null)
                return null;
            var range = ListRange(resolved.Commit, head);
            int unsignedCount = new SignatureVerifier(git).CountUnsigned(range);
            if (unsignedCount == 0)
                return null;
            return $"{unsignedCount} unsigned commit{(unsignedCount == 1 ? "" : "s")}; review, then run pen sign";
        }
        catch (PenException)
        {
            return null;
        }
    }

    /// <summary>Lists the bases to try: <paramref name="arg"/> if given, else the upstream and origin/HEAD.</summary>
    private static string[] ListBaseCandidates(string? arg) =>
        arg != null ? [arg] : ["@{upstream}", "refs/remotes/origin/HEAD"];

    /// <summary>Formats a base's name for messages: the upstream by its short name, anything else as given.</summary>
    private string FormatBaseName(string name) =>
        name == "@{upstream}" ? git.TryReadOutput("rev-parse", "--abbrev-ref", name) ?? name : name;

    /// <summary>Refuses to rewrite when any commit in <paramref name="range"/> is on a remote branch.</summary>
    /// <exception cref="PenException">Some are.</exception>
    private void RefusePushed(string baseCommit, string head, IReadOnlyList<string> range)
    {
        var pushed = FindOnARemote(baseCommit, head, range);
        if (pushed.Count > 0)
        {
            throw new PenException("refusing to rewrite commits that are on a remote branch: "
                + $"{string.Join(", ", pushed.Select(commit => commit[..12]))} "
                + "(pass a later base, e.g. pen sign <commit>)");
        }
    }

    /// <summary>Re-creates the commits that need it signed, moves the branch, and says how it went.</summary>
    private void SignRange(string branchRef, string head, IReadOnlyList<string> range, IReadOnlySet<string> verified)
    {
        var signing = git.WithConfig(SignatureConfig.BuildForSigning(git));
        var map = new CommitRewriter(signing).Rewrite(range, verified);
        var newHead = map[head];
        if (newHead != head)
            UpdateBranch(branchRef, newHead, head);
        Out.WriteLine($"signed: {Out.Field(branchRef["refs/heads/".Length..])} is now " +
            $"{newHead[..12]} (was {head[..12]}; the old commits stay in the reflog)");
        var created = range.Where(commit => map[commit] != commit).Select(commit => map[commit]).ToList();
        if (new SignatureVerifier(git).FormatHint(created) is { } hint)
            Out.WriteLine(hint);
    }
}

/// <summary>The base that pen sign signs the commits after.</summary>
/// <param name="Name">The base as named on the command line, the upstream's short name, or origin/HEAD's ref.</param>
/// <param name="Commit">The commit it resolves to.</param>
internal sealed record ResolvedBase(string Name, string Commit);
