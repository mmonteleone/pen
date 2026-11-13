using System.Text;
using Pen.Commands;

namespace Pen.GitProtection;

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
        if (split < 0)
            throw new PenException("no blank line after its headers");
        var header = raw.AsSpan(0, split);
        if (header.Contains((byte)0))
            throw new PenException("a NUL in its headers");
        if (header.Contains((byte)'\r'))
            throw new PenException("a carriage return in its headers");

        // Latin-1 keeps each byte as one char, so nothing is lost before the strict decoding below.
        var lines = System.Text.Encoding.Latin1.GetString(header).Split('\n');
        int i = 0;
        string? Take(string key) =>
            i < lines.Length && lines[i].StartsWith(key + " ", StringComparison.Ordinal)
                ? lines[i++][(key.Length + 1)..]
                : null;

        var tree = Take("tree");
        if (tree == null || !IsHash(tree))
            throw new PenException("no valid tree as its first header");
        var parents = new List<string>();
        while (Take("parent") is { } parent)
        {
            if (!IsHash(parent) || parent.Length != tree.Length)
                throw new PenException("an invalid parent header");
            parents.Add(parent);
        }
        var (authorName, authorEmail, authorDate) = Ident(Take("author") ?? throw new PenException(
            "no author header after its parents"), "author");
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
            if (sp <= 0)
                throw new PenException("a malformed header");
            current = line[..sp];
            if (IsCoreHeader(current))
                throw new PenException($"a second \"{current}\" header, or one out of place");
            if (current is "encoding" or "gpgsig" or "gpgsig-sha256" && !seen.Add(current))
                throw new PenException($"a second \"{current}\" header");
            if (current == "encoding")
                encoding = Utf8(line[(sp + 1)..], "encoding header");
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
        {
            throw new PenException($"a malformed date in its {key} header");
        }

        return (Utf8(value[..lt].TrimEnd(), key + " name"), Utf8(value[(lt + 1)..gt], key + " email"), date);
    }

    /// <summary>
    /// Header text that goes to git as a string (in the environment or an argument),
    /// which has to be valid UTF-8 to arrive unchanged: .NET can't pass other bytes.
    /// </summary>
    private static string Utf8(string latin1, string what)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(System.Text.Encoding.Latin1.GetBytes(latin1));
        }
        catch (DecoderFallbackException)
        {
            throw new NotUtf8Exception(what);
        }
    }
}
