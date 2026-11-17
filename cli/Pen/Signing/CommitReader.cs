using System.Globalization;
using System.Text;

namespace Pen.Signing;

/// <summary>Reads commit objects through host git and checks that git reads them the same way.</summary>
/// <param name="git">The repo's git; its extra settings, if any, go on every call.</param>
internal sealed class CommitReader(HostGit git)
{
    /// <summary>Reads and parses <paramref name="commits"/> in one <c>git cat-file --batch</c> call.</summary>
    /// <returns>Each commit's parsed object, by hash.</returns>
    /// <exception cref="PenException">
    /// git can't read them, one isn't a commit, or one has a header Pen can't re-create exactly.
    /// </exception>
    public Dictionary<string, CommitObject> Read(IReadOnlyList<string> commits)
    {
        if (commits.Count == 0)
            return new Dictionary<string, CommitObject>(StringComparer.Ordinal);
        var batch = git.Run(["cat-file", "--batch"], HostGit.EncodeLines(commits));
        if (!batch.Ok)
            throw new PenException($"git cat-file failed: {batch.Stderr.Trim()}");
        var result = ParseBatch(commits, batch.Stdout);
        CheckMatchesGit(commits, result);
        return result;
    }

    /// <summary>Splits <c>git cat-file --batch</c> output into <paramref name="commits"/>, and parses each.</summary>
    /// <param name="commits">The commits asked for, in the order they were asked for.</param>
    /// <param name="output">git's output: for each commit, a header line, its content, then a newline.</param>
    /// <returns>Each commit's parsed object, by hash.</returns>
    /// <exception cref="PenException">
    /// The output is shorter than its headers say, one isn't a commit, or one has a header Pen can't re-create
    /// exactly.
    /// </exception>
    public static Dictionary<string, CommitObject> ParseBatch(IReadOnlyList<string> commits, byte[] output)
    {
        var result = new Dictionary<string, CommitObject>(StringComparer.Ordinal);
        var position = 0;
        foreach (var sha in commits)
        {
            var newline = Array.IndexOf(output, (byte)'\n', position);
            if (newline < 0)
                throw new PenException("git cat-file --batch: truncated output");
            var size = ReadCommitSize(sha, Encoding.ASCII.GetString(output, position, newline - position));
            result[sha] = Parse(sha, output[(newline + 1)..(newline + 1 + size)]);
            position = newline + 1 + size + 1; // content, then a newline
        }
        return result;
    }

    /// <summary>Checks that git reads the same tree and parents in each commit as <paramref name="parsed"/>.</summary>
    /// <exception cref="PenException">git can't read the commits, or reads one differently.</exception>
    /// <remarks>
    /// This is in case the two parsers differ in some way nobody foresaw. Grafts and .git/shallow are left out,
    /// since re-creating uses the object's own parents.
    /// </remarks>
    public void CheckMatchesGit(IReadOnlyList<string> commits, IReadOnlyDictionary<string, CommitObject> parsed)
    {
        var result = git.Run(
            [.. HostGit.LogEachArgs, "--format=%H %T %P"],
            HostGit.EncodeLines(commits),
            new Dictionary<string, string> { ["GIT_GRAFT_FILE"] = "/dev/null", ["GIT_SHALLOW_FILE"] = "/dev/null" });
        if (!result.Ok)
            throw new PenException($"git can't read these commits: {result.Stderr.Trim()}");
        var fromGit = result.Lines.Select(line => line.TrimEnd()).ToHashSet(StringComparer.Ordinal);
        foreach (var sha in commits)
        {
            var commit = parsed[sha];
            if (!fromGit.Contains($"{sha} {commit.Tree} {string.Join(' ', commit.Parents)}".TrimEnd()))
            {
                throw new PenException($"refusing commit {sha[..12]}: git reads a different " +
                    "tree or parents than its headers show, so it could be re-created wrongly");
            }
        }
    }

    /// <summary>Reads the object's size from its <c>git cat-file --batch</c> header line.</summary>
    /// <exception cref="PenException">The object isn't a commit.</exception>
    private static int ReadCommitSize(string sha, string line)
    {
        var header = line.Split(' ');
        if (header.Length != 3 || header[1] != "commit")
            throw new PenException($"{sha} isn't a commit ({string.Join(' ', header)})");
        return int.Parse(header[2], CultureInfo.InvariantCulture);
    }

    /// <summary>Parses one commit's bytes, turning a refusal into a message that names the commit.</summary>
    /// <exception cref="PenException">It has a header shape git doesn't write, or a field that isn't UTF-8.</exception>
    private static CommitObject Parse(string sha, byte[] content)
    {
        try
        {
            return CommitObject.Parse(content);
        }
        catch (CommitShapeException refusal)
        {
            throw new PenException($"refusing commit {sha[..12]}: it has {refusal.Shape}, " +
                "which git doesn't write, so it could be re-created wrongly");
        }
        catch (NotUtf8Exception notUtf8)
        {
            throw new PenException($"refusing commit {sha[..12]}: its {notUtf8.What} isn't valid UTF-8, and " +
                $"pen can't pass that to git unchanged, so it can't be re-created with the same {notUtf8.What}");
        }
    }
}
