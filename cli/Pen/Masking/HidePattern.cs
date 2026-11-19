using System.Diagnostics.CodeAnalysis;
using Pen.Platform;

namespace Pen.Masking;

/// <summary>One hide pattern: the subset of gitignore patterns that hide accepts, matched ignoring case.</summary>
/// <remarks>
/// A pattern with no / except a trailing one matches a name at any depth; a leading or inner / anchors it at the
/// project root. A trailing / matches directories only. * and ? match within one path segment. ** as a whole segment
/// matches zero or more directories, except at the end, where it matches one or more entries inside, as in gitignore;
/// ** inside a segment (a**b) is a plain *. Matching is ordinal and ignores case; on macOS it ignores Unicode
/// normalization form too (both sides compare as form C, through <see cref="Native.NormalizeFormC"/>, since APFS and
/// HFS+ treat NFC and NFD spellings as one name), while on Linux, whose file systems tell them apart, NFC and NFD
/// spellings are different names. On a file system that tells case or form apart where Pen ignores it, a pattern hides
/// more than asked, which is the safe direction. Patterns can't name a .git segment anywhere or start with .pen, and
/// <see cref="IsMatch"/> never matches a path that has a .git segment or starts with .pen, whatever its wildcards would
/// say.
/// </remarks>
internal sealed class HidePattern
{
    private const string AnyDepth = "**";

    private readonly Segment[] _segments;

    private HidePattern(string text, Segment[] segments, bool isAnchored, bool isDirectoryOnly)
    {
        Text = text;
        _segments = segments;
        IsAnchored = isAnchored;
        IsDirectoryOnly = isDirectoryOnly;
        if (isAnchored && segments.All(segment => segment.Kind == SegmentKind.Literal))
            LiteralPath = string.Join('/', segments.Select(segment => segment.Text));
    }

    /// <summary>The pattern as written, for messages.</summary>
    public string Text { get; }

    /// <summary>Whether the pattern is anchored at the project root: it has a leading or inner /.</summary>
    /// <remarks>An anchored pattern that starts with ** still matches at any depth.</remarks>
    public bool IsAnchored { get; }

    /// <summary>Whether the pattern ends in /, so it matches directories only.</summary>
    public bool IsDirectoryOnly { get; }

    /// <summary>Whether the pattern names one path: anchored, with no *, ? or **.</summary>
    /// <remarks>A literal pattern can be checked at <see cref="LiteralPath"/> without walking the project.</remarks>
    [MemberNotNullWhen(true, nameof(LiteralPath))]
    public bool IsLiteral => LiteralPath is not null;

    /// <summary>
    /// The project-relative path a literal pattern names, /-separated with no leading or trailing /, in the pattern's
    /// case; null when the pattern isn't literal.
    /// </summary>
    /// <remarks>
    /// It is in the pattern's spelling (Form C) while <see cref="IsMatch"/> ignores case and normalization, so a lookup
    /// by path must resolve each segment against the directory's listing the way <see cref="IsMatch"/> compares, not by
    /// a plain stat of this string. It never has a .git segment or starts with .pen, but a segment of it may be a
    /// symlink.
    /// </remarks>
    public string? LiteralPath { get; }

    public override string ToString() => Text;

    /// <summary>The longest pattern accepted: normalizing a long decomposed name is quadratic.</summary>
    internal const int MaxLength = 1024;

    /// <summary>
    /// Parses <paramref name="text"/> into <paramref name="pattern"/>, or says in <paramref name="error"/> what's wrong
    /// with it.
    /// </summary>
    public static bool TryParse(
        string text,
        [NotNullWhen(true)] out HidePattern? pattern,
        [NotNullWhen(false)] out string? error)
    {
        pattern = null;
        error = Split(text, out var parts, out var isAnchored, out var isDirectoryOnly);
        if (error is not null)
            return false;
        pattern = new HidePattern(text, BuildSegments(parts), isAnchored, isDirectoryOnly);
        return true;
    }

    /// <summary>Splits <paramref name="text"/> into its normalized segments, unless it's refused.</summary>
    /// <returns>
    /// What's wrong with the pattern, or null when it's accepted. The outs are meaningful only when this returns null.
    /// </returns>
    private static string? Split(
        string text,
        out string[] parts,
        out bool isAnchored,
        out bool isDirectoryOnly)
    {
        parts = [];
        isAnchored = false;
        isDirectoryOnly = false;
        if (text.Contains('\0'))
            return "a pattern can't contain a NUL character";
        if (text.AsSpan().IndexOfAny("[]\\") >= 0)
            return "[, ] and \\ aren't supported in patterns (use * or ?)";
        if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[^1])))
            return "a pattern can't start or end with a space";
        if (text.StartsWith('!'))
            return "! (negation) isn't supported";
        if (text.StartsWith('#'))
            return "# isn't a comment here, and a pattern can't start with it";
        if (text.Length > MaxLength)
            return $"a pattern can't be longer than {MaxLength} characters";
        var body = text;
        isDirectoryOnly = body.EndsWith('/');
        if (isDirectoryOnly)
            body = body[..^1];
        var hasLeadingSlash = body.StartsWith('/');
        if (hasLeadingSlash)
            body = body[1..];
        if (body.Length == 0)
            return "a pattern can't be empty";

        parts = [.. body.Split('/').Select(Native.NormalizeFormC)];
        isAnchored = hasLeadingSlash || parts.Length > 1;
        return FindProblem(parts);
    }

    /// <summary>Says what's wrong with the pattern's segments, or gives null if they're accepted.</summary>
    private static string? FindProblem(string[] parts)
    {
        if (parts.Any(part => part.Length == 0))
            return "a pattern can't have an empty segment (//)";
        if (parts.Any(part => part is "." or ".."))
            return "a pattern can't have . or .. segments";
        if (parts.Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            return "a pattern can't reach into .git";
        if (parts[0].Equals(".pen", StringComparison.OrdinalIgnoreCase))
            return "a pattern can't start with .pen, Pen's own directory";
        if (parts.All(part => part is AnyDepth or "*") && parts.Count(part => part == "*") <= 1)
            return "this pattern would hide the whole project";
        return null;
    }

    /// <summary>Turns the pattern's segments into the matcher's.</summary>
    /// <remarks>
    /// Consecutive ** collapse into one. A trailing ** becomes * then **, so it needs at least one entry
    /// inside. An unanchored pattern is its one segment, which <see cref="IsMatch"/> tries on the last name.
    /// </remarks>
    private static Segment[] BuildSegments(string[] parts)
    {
        var segments = new List<Segment>();
        foreach (var part in parts)
        {
            if (part == AnyDepth)
            {
                if (segments.Count == 0 || segments[^1].Kind != SegmentKind.AnyDepth)
                    segments.Add(new(SegmentKind.AnyDepth, part));
            }
            else
            {
                var kind = part.AsSpan().IndexOfAny('*', '?') >= 0 ? SegmentKind.Glob : SegmentKind.Literal;
                segments.Add(new(kind, part));
            }
        }
        if (segments[^1].Kind == SegmentKind.AnyDepth)
            segments.Insert(segments.Count - 1, new(SegmentKind.Glob, "*"));
        return [.. segments];
    }

    /// <summary>Tells whether the pattern matches an entry.</summary>
    /// <param name="relativePath">The entry's path in the project: /-separated, with no leading or trailing /.</param>
    /// <param name="isDirectory">Whether the entry is a directory (not a symlink to one).</param>
    /// <remarks>
    /// Only the entry itself is tested: a path inside a matching directory doesn't match unless the pattern
    /// matches it too, since a walk doesn't descend into a directory it hides.
    /// </remarks>
    public bool IsMatch(string relativePath, bool isDirectory)
    {
        if (relativePath.Length == 0 || (IsDirectoryOnly && !isDirectory) || IsOffLimits(relativePath))
            return false;
        relativePath = Native.NormalizeFormC(relativePath);
        if (!IsAnchored)
            return _segments[0].Matches(relativePath.AsSpan(relativePath.LastIndexOf('/') + 1));
        return MatchesSegments(relativePath);
    }

    /// <summary>
    /// Tells whether a path in the project has a .git segment or starts with .pen, ignoring case: no pattern may
    /// match it.
    /// </summary>
    internal static bool IsOffLimits(string path)
    {
        var first = true;
        foreach (var range in path.AsSpan().Split('/'))
        {
            var segment = path.AsSpan(range);
            if (segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
                || (first && segment.Equals(".pen", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            first = false;
        }
        return false;
    }

    /// <summary>Matches the whole path, segment by segment, with ** as any run of segments.</summary>
    /// <remarks>
    /// The usual greedy wildcard match with one backtrack point (the last **), one segment at a time, so it
    /// takes no allocation and at most (pattern segments × path segments) steps.
    /// </remarks>
    private bool MatchesSegments(string path)
    {
        var end = path.Length;
        var index = 0;
        var start = 0;
        var anyDepthIndex = -1;
        var anyDepthStart = 0;
        while (start <= end)
        {
            var next = path.IndexOf('/', start);
            var segmentEnd = next < 0 ? end : next;
            if (index < _segments.Length && _segments[index].Kind == SegmentKind.AnyDepth)
            {
                anyDepthIndex = index;
                anyDepthStart = start;
                index++;
            }
            else if (index < _segments.Length && _segments[index].Matches(path.AsSpan(start, segmentEnd - start)))
            {
                index++;
                start = segmentEnd + 1;
            }
            else if (anyDepthIndex >= 0)
            {
                // Let the last ** take one more segment, and try the rest of the pattern after it.
                index = anyDepthIndex + 1;
                var skipped = path.IndexOf('/', anyDepthStart);
                anyDepthStart = skipped < 0 ? end + 1 : skipped + 1;
                start = anyDepthStart;
            }
            else
            {
                return false;
            }
        }
        while (index < _segments.Length && _segments[index].Kind == SegmentKind.AnyDepth)
            index++;
        return index == _segments.Length;
    }

    private enum SegmentKind
    {
        Literal,
        Glob,
        AnyDepth,
    }

    /// <summary>One /-separated segment of a pattern.</summary>
    private readonly record struct Segment(SegmentKind Kind, string Text)
    {
        /// <summary>Tells whether the segment matches one name, ignoring case.</summary>
        public bool Matches(ReadOnlySpan<char> name) => Kind switch
        {
            SegmentKind.Literal => name.Equals(Text, StringComparison.OrdinalIgnoreCase),
            SegmentKind.Glob => MatchesGlob(Text, name),
            _ => false,
        };

        /// <summary>Matches a name against * (any run, including none) and ? (one character).</summary>
        /// <remarks>
        /// The greedy match with one backtrack point (the last *). A surrogate pair counts as one character.
        /// </remarks>
        private static bool MatchesGlob(ReadOnlySpan<char> glob, ReadOnlySpan<char> name)
        {
            var globIndex = 0;
            var nameIndex = 0;
            var starIndex = -1;
            var starName = 0;
            while (nameIndex < name.Length)
            {
                var width = CharWidth(name, nameIndex);
                if (globIndex < glob.Length && glob[globIndex] == '*')
                {
                    starIndex = globIndex++;
                    starName = nameIndex;
                }
                else if (globIndex < glob.Length && glob[globIndex] == '?')
                {
                    globIndex++;
                    nameIndex += width;
                }
                else if (globIndex < glob.Length && IsSameChar(glob, globIndex, name, nameIndex))
                {
                    globIndex += CharWidth(glob, globIndex);
                    nameIndex += width;
                }
                else if (starIndex >= 0)
                {
                    globIndex = starIndex + 1;
                    starName += CharWidth(name, starName);
                    nameIndex = starName;
                }
                else
                {
                    return false;
                }
            }
            while (globIndex < glob.Length && glob[globIndex] == '*')
                globIndex++;
            return globIndex == glob.Length;
        }

        private static bool IsSameChar(
            ReadOnlySpan<char> glob, int globIndex, ReadOnlySpan<char> name, int nameIndex)
        {
            var width = CharWidth(glob, globIndex);
            return nameIndex + width <= name.Length
                && glob.Slice(globIndex, width)
                    .Equals(name.Slice(nameIndex, width), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Gets how many chars the character at <paramref name="index"/> takes.</summary>
        /// <returns>2 for a surrogate pair, otherwise 1.</returns>
        private static int CharWidth(ReadOnlySpan<char> text, int index) =>
            index + 1 < text.Length && char.IsSurrogatePair(text[index], text[index + 1]) ? 2 : 1;
    }
}
