using System.Text;

namespace Pen.Signing;

/// <summary>A commit object as stored, parsed just enough to re-create it.</summary>
internal sealed record CommitObject
{
    /// <summary>The tree's hash.</summary>
    public required string Tree { get; init; }

    /// <summary>The parents' hashes, in header order.</summary>
    public required IReadOnlyList<string> Parents { get; init; }

    /// <summary>The author's name, as written.</summary>
    public required string AuthorName { get; init; }

    /// <summary>The author's email, without the angle brackets.</summary>
    public required string AuthorEmail { get; init; }

    /// <summary>The author date as written: seconds since the epoch and a zone such as +0130.</summary>
    public required string AuthorDate { get; init; }

    /// <summary>The value of the encoding header, or null if there's none.</summary>
    public required string? Encoding { get; init; }

    /// <summary>The message bytes, exactly as stored after the blank line.</summary>
    public required byte[] Message { get; init; }

    /// <summary>The first line of the message, decoded as UTF-8 and trimmed.</summary>
    public string Subject
    {
        get
        {
            var text = System.Text.Encoding.UTF8.GetString(Message);
            var newline = text.IndexOf('\n');
            return (newline >= 0 ? text[..newline] : text).Trim();
        }
    }

    /// <summary>
    /// Parses the raw bytes of <c>git cat-file commit</c>, refusing any header shape git doesn't write.
    /// </summary>
    /// <returns>The parsed commit.</returns>
    /// <exception cref="PenException">A header git doesn't write; the message says what the commit has.</exception>
    /// <exception cref="NotUtf8Exception">A header field that must go to git as a string isn't valid UTF-8.</exception>
    /// <remarks>
    /// git reads the first of a duplicated header and stops collecting parents at the first other line, so a laxer
    /// parser could sign a tree the user never saw in <c>git show</c>. Headers after the committer are allowed,
    /// since other tools add them, and aren't kept.
    /// </remarks>
    public static CommitObject Parse(byte[] raw)
    {
        var split = raw.AsSpan().IndexOf("\n\n"u8);
        if (split < 0)
            throw new PenException("no blank line after its headers");
        var headers = new HeaderLines(DecodeHeaders(raw.AsSpan(0, split)));

        var tree = headers.Take("tree");
        if (tree == null || !IsHash(tree))
            throw new PenException("no valid tree as its first header");
        var parents = ReadParents(headers, tree);
        var author = ParseIdent(
            headers.Take("author") ?? throw new PenException("no author header after its parents"), "author");
        ParseIdent(
            headers.Take("committer") ?? throw new PenException("no committer header after its author"), "committer");
        var encoding = ReadExtraHeaders(headers.Rest);
        return new CommitObject
        {
            Tree = tree,
            Parents = parents,
            AuthorName = author.Name,
            AuthorEmail = author.Email,
            AuthorDate = author.Date,
            Encoding = encoding,
            Message = raw[(split + 2)..],
        };
    }

    /// <summary>Splits the header bytes into lines, refusing a NUL or a carriage return.</summary>
    /// <returns>The lines, decoded as Latin-1.</returns>
    private static string[] DecodeHeaders(ReadOnlySpan<byte> header)
    {
        if (header.Contains((byte)0))
            throw new PenException("a NUL in its headers");
        if (header.Contains((byte)'\r'))
            throw new PenException("a carriage return in its headers");

        // Latin-1 keeps each byte as one char, so nothing is lost before the strict decoding below.
        return System.Text.Encoding.Latin1.GetString(header).Split('\n');
    }

    /// <summary>Takes the parent headers that follow the tree, each a hash of the same length as the tree's.</summary>
    private static List<string> ReadParents(HeaderLines headers, string tree)
    {
        var parents = new List<string>();
        while (headers.Take("parent") is { } parent)
        {
            if (!IsHash(parent) || parent.Length != tree.Length)
                throw new PenException("an invalid parent header");
            parents.Add(parent);
        }
        return parents;
    }

    /// <summary>
    /// Checks the headers after the committer, refusing a core header or a repeat git doesn't write.
    /// </summary>
    /// <returns>The encoding header's value, or null if there's none.</returns>
    private static string? ReadExtraHeaders(IEnumerable<string> lines)
    {
        string? encoding = null;
        string? current = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.StartsWith(' '))
            {
                // A tag's message inside a mergetag is free text. Anywhere else, a
                // continuation that reads as a core header has no honest purpose.
                if (current == null || (current != "mergetag" && IsCoreHeader(line[1..].Split(' ')[0])))
                    throw new PenException("a header continuation line where none belongs");
                continue;
            }
            var space = line.IndexOf(' ');
            if (space <= 0)
                throw new PenException("a malformed header");
            current = line[..space];
            if (IsCoreHeader(current))
                throw new PenException($"a second \"{current}\" header, or one out of place");
            if (current is "encoding" or "gpgsig" or "gpgsig-sha256" && !seen.Add(current))
                throw new PenException($"a second \"{current}\" header");
            if (current == "encoding")
                encoding = DecodeUtf8(line[(space + 1)..], "encoding header");
        }
        return encoding;
    }

    private static bool IsCoreHeader(string key) => key is "tree" or "parent" or "author" or "committer";

    private static bool IsHash(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigitLower);

    /// <summary>Splits <c>Name &lt;email&gt; 1700000000 +0000</c>, the only form git writes, into its parts.</summary>
    /// <param name="value">The header's value.</param>
    /// <param name="key">The header's name, for messages.</param>
    private static (string Name, string Email, string Date) ParseIdent(string value, string key)
    {
        var open = value.IndexOf('<');
        var close = value.IndexOf('>');
        if (open < 0 || close < open || value.LastIndexOf('<') != open || value.LastIndexOf('>') != close)
            throw new PenException($"a malformed {key} header");
        var date = value[(close + 1)..].Trim();
        if (date.Split(' ') is not [var seconds, var zone]
            || seconds.Length == 0 || !seconds.All(char.IsAsciiDigit)
            || zone.Length != 5 || zone[0] is not ('+' or '-') || !zone[1..].All(char.IsAsciiDigit))
        {
            throw new PenException($"a malformed date in its {key} header");
        }

        var name = DecodeUtf8(value[..open].TrimEnd(), key + " name");
        var email = DecodeUtf8(value[(open + 1)..close], key + " email");
        return (name, email, date);
    }

    /// <summary>Decodes header text that goes to git as a string (in the environment or an argument).</summary>
    /// <param name="latin1">The text as decoded from the header bytes as Latin-1.</param>
    /// <param name="what">The field, for the exception.</param>
    /// <returns>The text decoded as UTF-8.</returns>
    /// <remarks>It has to be valid UTF-8 to arrive unchanged: .NET can't pass other bytes.</remarks>
    private static string DecodeUtf8(string latin1, string what)
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

    /// <summary>The header lines of a commit, taken in order.</summary>
    /// <param name="lines">All the header lines.</param>
    private sealed class HeaderLines(string[] lines)
    {
        private int _next;

        /// <summary>The lines not taken yet.</summary>
        public IEnumerable<string> Rest => lines.Skip(_next);

        /// <summary>Takes the next line if it's a <paramref name="key"/> header.</summary>
        /// <returns>The header's value, or null if the next line isn't one or there's none left.</returns>
        public string? Take(string key)
        {
            if (_next >= lines.Length || !lines[_next].StartsWith(key + " ", StringComparison.Ordinal))
                return null;
            return lines[_next++][(key.Length + 1)..];
        }
    }
}

/// <summary>The exception thrown when a commit header field isn't valid UTF-8.</summary>
/// <param name="what">The field, such as "author name".</param>
/// <remarks>Such a field can't be passed to git unchanged, so the commit can't be re-created exactly.</remarks>
internal sealed class NotUtf8Exception(string what) : Exception(what)
{
    /// <summary>The field that isn't valid UTF-8, such as "author name".</summary>
    public string What { get; } = what;
}
