namespace Pen.Signing;

/// <summary>Re-creates commits signed, with the same trees, messages and authors.</summary>
/// <param name="signing">The repo's git with the signing settings added, which every call here uses.</param>
internal sealed class CommitRewriter(HostGit signing)
{
    /// <summary>Re-creates each commit that needs it, oldest first, signed with <c>commit-tree -S</c>.</summary>
    /// <param name="commits">The commits, parents before children.</param>
    /// <param name="verified">The commits whose signature already verifies.</param>
    /// <returns>The mapping from old hash to new for every commit given; unchanged ones map to themselves.</returns>
    /// <exception cref="PenException">A commit can't be read or re-created exactly, or signing fails.</exception>
    /// <remarks>
    /// A commit is re-created when it isn't in <paramref name="verified"/> or has a re-created parent: same tree,
    /// same message bytes, same author and date, parents mapped to their new hashes. Parents outside the range stay
    /// as they are, and merges are re-created too. It touches no ref, index or worktree.
    /// </remarks>
    public Dictionary<string, string> Rewrite(IReadOnlyList<string> commits, IReadOnlySet<string> verified)
    {
        var objects = new CommitReader(signing).Read(commits);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sha in commits)
        {
            var commit = objects[sha];
            var parents = commit.Parents.Select(parent => map.TryGetValue(parent, out var mapped) ? mapped : parent)
                .ToList();
            if (verified.Contains(sha) && parents.SequenceEqual(commit.Parents))
            {
                map[sha] = sha; // already verifiably signed, and nothing under it changed
                continue;
            }
            map[sha] = CreateSigned(sha, commit, parents);
        }
        return map;
    }

    /// <summary>
    /// Creates a signed copy of <paramref name="commit"/> with <paramref name="parents"/>, returning the new commit's
    /// hash.
    /// </summary>
    /// <exception cref="PenException">Its tree is missing, or git couldn't create or sign it.</exception>
    private string CreateSigned(string sha, CommitObject commit, List<string> parents)
    {
        var args = new List<string>();
        if (commit.Encoding is not null)
            args.AddRange(["-c", $"i18n.commitEncoding={commit.Encoding}"]);
        args.AddRange(["commit-tree", commit.Tree]);
        foreach (var parent in parents)
            args.AddRange(["-p", parent]);
        args.AddRange(["-S", "-F", "-"]);
        var env = new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = commit.AuthorName,
            ["GIT_AUTHOR_EMAIL"] = commit.AuthorEmail,
            ["GIT_AUTHOR_DATE"] = commit.AuthorDate,
        };
        var result = signing.Run(args, commit.Message, env);
        var created = result.Text.Trim();
        if (result.Ok && created.Length > 0)
            return created;
        if (!signing.Run("cat-file", "-e", commit.Tree).Ok)
        {
            throw new PenException($"can't re-create {sha[..12]}: its tree {commit.Tree} isn't in " +
                $"this repo. In a partial clone, this fetches it: git cat-file -e {commit.Tree}");
        }

        throw new PenException($"signing {sha[..12]} failed: {result.Stderr.Trim()}");
    }
}
