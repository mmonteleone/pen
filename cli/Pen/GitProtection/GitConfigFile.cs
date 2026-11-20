using System.Diagnostics.CodeAnalysis;
using System.Text;
using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>Reads git config files directly, never by running git (which could run whatever the file names).</summary>
/// <remarks>
/// This is git's own grammar, from config.c, byte by byte: whatever git would read from a file is what's returned,
/// and a file git would reject, or read in a way Pen can't represent, is a refusal naming its line. A best-effort
/// reading would let a file mean one thing here and another to git. The one thing git accepts that's refused is a
/// NUL in a value or subsection, where git silently cuts the string short.
/// </remarks>
internal static class GitConfigFile
{
    /// <summary>One key read from a config file.</summary>
    /// <param name="Section">The section, lowercased.</param>
    /// <param name="Subsection">The quoted subsection, keeping its case, or null if there's none.</param>
    /// <param name="Key">The key, lowercased.</param>
    /// <param name="Value">The value; a key with no "=" has the value "true".</param>
    internal readonly record struct Entry(string Section, string? Subsection, string Key, string Value);

    /// <summary>Why <see cref="ExpandPath"/> can't give the path a value names.</summary>
    internal enum PathRefusal
    {
        /// <summary>
        /// The value wasn't valid UTF-8: decoded, it has U+FFFD where git has other bytes, so it would name a
        /// different file from the one git reads.
        /// </summary>
        NotUtf8,

        /// <summary>The value is a "~user" or "%(prefix)/" path, which Pen doesn't resolve.</summary>
        UnknownForm,
    }

    /// <summary>
    /// A path value as git would expand it (<see cref="Expanded"/>), or why Pen can't tell (<see cref="Refused"/>),
    /// which are all there are.
    /// </summary>
    internal abstract record ExpandedPath
    {
        private ExpandedPath()
        {
        }

        /// <summary>The path expanded.</summary>
        internal sealed record Expanded(string Path) : ExpandedPath;

        /// <summary>A path Pen can't expand as git would, and why.</summary>
        internal sealed record Refused(PathRefusal Refusal) : ExpandedPath;
    }

    /// <summary>Parses the entries in <paramref name="text"/>.</summary>
    /// <returns>The entries, in file order.</returns>
    /// <exception cref="PenException">It isn't what git would read.</exception>
    public static IReadOnlyList<Entry> Parse(string text) => Parse(Encoding.UTF8.GetBytes(text), "git config");

    /// <summary>Parses the entries in a file's bytes, which <paramref name="source"/> names in the message.</summary>
    /// <returns>The entries, in file order.</returns>
    /// <exception cref="PenException">
    /// It isn't what git would read; the message names <paramref name="source"/>.
    /// </exception>
    public static IReadOnlyList<Entry> Parse(byte[] bytes, string source) =>
        TryParse(bytes, source, out var entries, out var error) ? entries : throw new PenException(error);

    /// <summary>Parses the entries of the file at <paramref name="path"/>.</summary>
    /// <returns>The entries, in file order; none if the file is missing.</returns>
    /// <exception cref="PenException">The file can't be read or parsed.</exception>
    public static IReadOnlyList<Entry> ParseFile(string path) =>
        TryParseFile(path, out var entries, out var error) ? entries : throw new PenException(error);

    /// <summary>
    /// Parses the entries of the file at <paramref name="path"/>, as <see cref="ParseFile"/> does, giving none if
    /// it's missing and otherwise what <see cref="ParseFile"/> refuses with.
    /// </summary>
    /// <returns>Whether it could be read and parsed, or is missing.</returns>
    /// <remarks>Only a read or parse failure is false; any other exception propagates on purpose.</remarks>
    public static bool TryParseFile(
        string path,
        [NotNullWhen(true)] out IReadOnlyList<Entry>? entries,
        [NotNullWhen(false)] out string? error)
    {
        byte[]? bytes;
        try
        {
            bytes = Native.ReadRegularBytes(path);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            entries = [];
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            entries = null;
            error = $"can't read the git config file {Out.Field(path)}: fix its permissions, then rerun";
            return false;
        }
        if (bytes is null)
        {
            entries = null;
            error = "can't read the git config file " +
                $"{Out.Field(path)}: it isn't a regular file of a reasonable size";
            return false;
        }
        return TryParse(bytes, $"the git config file {Out.Field(path)}", out entries, out error);
    }

    /// <summary>Parses the entries in a file's bytes, as <see cref="Parse(byte[], string)"/> does.</summary>
    /// <returns>Whether it's what git would read.</returns>
    private static bool TryParse(
        byte[] bytes,
        string source,
        [NotNullWhen(true)] out IReadOnlyList<Entry>? entries,
        [NotNullWhen(false)] out string? error)
    {
        var reader = new Reader(bytes);
        if (reader.Read() is { } why)
        {
            entries = null;
            error = $"can't parse {source} at line {reader.Line} ({why}), so " +
                "pen can't tell what git would read from it: fix or remove it, then rerun";
            return false;
        }

        entries = reader.Entries;
        error = null;
        return true;
    }

    /// <summary>Finds the value of the last entry with this section, key and subsection.</summary>
    /// <param name="entries">The entries, in file order.</param>
    /// <param name="section">The section, lowercased as <see cref="Entry.Section"/> holds it.</param>
    /// <param name="key">The key, lowercased as <see cref="Entry.Key"/> holds it.</param>
    /// <param name="subsection">The subsection, matched exactly; null matches only entries outside one.</param>
    /// <returns>The value, or null if no entry matches.</returns>
    public static string? FindLast(IReadOnlyList<Entry> entries, string section, string key, string? subsection = null)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            if (entry.Section == section && entry.Subsection == subsection && entry.Key == key)
                return entry.Value;
        }
        return null;
    }

    /// <summary>
    /// Expands a path value the way git does for "~" and "~/", refusing the forms Pen can't resolve.
    /// </summary>
    /// <param name="value">The value, as read from a config file.</param>
    /// <param name="home">The user's home, absolute.</param>
    /// <returns>
    /// The home for "~", the home followed by the rest for "~/…", or any other value as it is (relative and empty
    /// values are left to the caller); otherwise a refusal, checked for invalid UTF-8 first.
    /// </returns>
    public static ExpandedPath ExpandPath(string value, string home)
    {
        if (value.Contains('\ufffd'))
            return new ExpandedPath.Refused(PathRefusal.NotUtf8);
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
            return new ExpandedPath.Expanded(home + value[1..]);
        // ~user needs another user's home looked up; %(prefix) depends on how git was built.
        if (value.StartsWith('~') || value.StartsWith("%(prefix)/", StringComparison.Ordinal))
            return new ExpandedPath.Refused(PathRefusal.UnknownForm);
        return new ExpandedPath.Expanded(value);
    }

    /// <summary>Whether <paramref name="value"/> is true as a git boolean.</summary>
    /// <remarks>
    /// git's false spellings and zero are false. Anything else is true, or git rejects it when it reads the key.
    /// </remarks>
    public static bool IsTrue(string value)
    {
        var lower = value.ToLowerInvariant();
        if (lower is "" or "false" or "no" or "off")
            return false;
        var digits = lower.TrimStart('+', '-').TrimEnd('k', 'm', 'g');
        return digits.Length == 0 || digits.Any(c => c != '0');
    }

    /// <summary>One pass over a file, following git_parse_source and its helpers.</summary>
    /// <remarks>Each method that reads returns why the file can't be read, or null if it can.</remarks>
    private sealed class Reader(byte[] bytes)
    {
        private const int Newline = '\n';

        /// <summary>What <see cref="Unescape"/> returns for a backslash before a newline, which adds nothing.</summary>
        private const int LineContinuation = -1;

        private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

        /// <summary>The full name being read: the section, "." and the subsection's, then the key.</summary>
        private readonly List<byte> _name = [];

        /// <summary>The value being read, trimmed as git trims it once the line ends.</summary>
        private readonly List<byte> _value = [];

        private int _pos;
        private int _nextLine = 1;
        private bool _eof;

        /// <summary>
        /// The length of the current section's part of <see cref="_name"/>, its trailing dot included.
        /// </summary>
        private int _sectionLength;

        public List<Entry> Entries { get; } = [];

        /// <summary>Gets the line of the last character read.</summary>
        public int Line { get; private set; } = 1;

        /// <summary>Reads the whole file.</summary>
        public string? Read()
        {
            if (bytes.AsSpan().StartsWith(Bom))
                _pos = Bom.Length;
            var comment = false;
            while (true)
            {
                var character = ReadChar();
                if (character == Newline)
                {
                    if (_eof)
                        return null;
                    comment = false;
                    continue;
                }
                if (comment || IsSpace(character))
                    continue;
                if (character is '#' or ';')
                {
                    comment = true;
                    continue;
                }
                var problem = character == '[' ? ReadSectionHeader() : ReadEntry(character);
                if (problem is not null)
                    return problem;
            }
        }

        /// <summary>
        /// git's get_next_char: reads one character, CRLF as a newline and the end of the file as newlines.
        /// </summary>
        private int ReadChar()
        {
            Line = _nextLine;
            if (_pos >= bytes.Length)
            {
                _eof = true;
                return Newline;
            }
            int character = bytes[_pos++];
            if (character == '\r' && _pos < bytes.Length && bytes[_pos] == '\n')
            {
                _pos++;
                character = Newline;
            }
            if (character == Newline)
                _nextLine++;
            return character;
        }

        /// <summary>Reads a section header after its "[", making it the current section.</summary>
        private string? ReadSectionHeader()
        {
            _name.Clear();
            if (ReadSectionName() is { } bad)
                return bad;
            if (_name.Count == 0)
                return "an empty section name";
            _name.Add((byte)'.');
            _sectionLength = _name.Count;
            return null;
        }

        /// <summary>Reads a section name up to "]", or up to whitespace and then its subsection.</summary>
        private string? ReadSectionName()
        {
            while (true)
            {
                var character = ReadChar();
                if (_eof)
                    return "a section header that isn't closed";
                if (IsSpace(character))
                    return ReadSubsection(character);
                if (character == ']')
                    return null;
                if (!IsKeyChar(character) && character != '.')
                    return "a character that can't be in a section name";
                _name.Add(Lower(character));
            }
        }

        /// <summary>
        /// Reads a quoted subsection and the "]" after it, from the whitespace <paramref name="character"/> before it.
        /// </summary>
        private string? ReadSubsection(int character)
        {
            do
            {
                if (character == Newline)
                    return "a section header that isn't closed";
                character = ReadChar();
            } while (IsSpace(character));
            if (character != '"')
                return "a section header that isn't [section \"subsection\"]";
            _name.Add((byte)'.');
            while (true)
            {
                character = ReadChar();
                if (character == Newline)
                    return "a section header that isn't closed";
                if (character == '"')
                    break;
                if (character == '\\')
                {
                    character = ReadChar();
                    if (character == Newline)
                        return "a section header that isn't closed";
                }
                if (character == 0)
                    return "a NUL in a subsection";
                _name.Add((byte)character);
            }
            return ReadChar() == ']' ? null : "a section header that isn't [section \"subsection\"]";
        }

        /// <summary>
        /// Reads a key, starting with <paramref name="character"/>, and its value in the current section.
        /// </summary>
        private string? ReadEntry(int character)
        {
            if (!IsAlpha(character))
                return "a line that isn't a section, a key or a comment";
            _name.RemoveRange(_sectionLength, _name.Count - _sectionLength);
            _name.Add(Lower(character));
            while (true)
            {
                character = ReadChar();
                if (_eof || !IsKeyChar(character))
                    break;
                _name.Add(Lower(character));
            }
            while (character is ' ' or '\t')
                character = ReadChar();

            var hasValue = character != Newline;
            if (hasValue)
            {
                if (character != '=')
                    return "a key followed by something other than \"=\"";
                if (ReadValue() is { } problem)
                    return problem;
            }
            AddEntry(hasValue);
            return null;
        }

        /// <summary>git's parse_value: reads a value after its "=", to the end of its line.</summary>
        /// <remarks>
        /// Whitespace is dropped at the start, and at the end only if nothing, not even a pair of quotes or a
        /// backslash, came after it.
        /// </remarks>
        private string? ReadValue()
        {
            _value.Clear();
            var quoted = false;
            var comment = false;
            var trimTo = 0;
            while (true)
            {
                var character = ReadChar();
                if (character == Newline)
                    return EndValue(quoted, trimTo);
                if (comment)
                    continue;
                if (IsSpace(character) && !quoted)
                {
                    if (trimTo == 0)
                        trimTo = _value.Count;
                    if (_value.Count > 0)
                        _value.Add((byte)character);
                    continue;
                }
                if (!quoted && character is ';' or '#')
                {
                    comment = true;
                    continue;
                }
                trimTo = 0;
                if (character == '\\')
                {
                    if (AddEscaped() is { } bad)
                        return bad;
                    continue;
                }
                if (character == '"')
                {
                    quoted = !quoted;
                    continue;
                }
                _value.Add((byte)character);
            }
        }

        /// <summary>
        /// Ends a value at its newline, trimming trailing whitespace from <paramref name="trimTo"/> if set.
        /// </summary>
        private string? EndValue(bool quoted, int trimTo)
        {
            if (quoted)
                return "a quote that isn't closed";
            if (trimTo > 0)
                _value.RemoveRange(trimTo, _value.Count - trimTo);
            if (_value.Contains(0))
                return "a NUL in a value";
            return null;
        }

        /// <summary>Reads the character after a backslash in a value and adds what it stands for.</summary>
        private string? AddEscaped()
        {
            var escaped = Unescape(ReadChar());
            if (escaped is null)
                return "an escape git doesn't know";
            if (escaped != LineContinuation)
                _value.Add((byte)escaped.Value);
            return null;
        }

        /// <summary>Translates the character after a backslash in a value.</summary>
        /// <returns>
        /// The character it stands for, <see cref="LineContinuation"/> for a newline, or null for an escape git doesn't
        /// know.
        /// </returns>
        private static int? Unescape(int character) => character switch
        {
            Newline => LineContinuation,
            't' => '\t',
            'b' => '\b',
            'n' => '\n',
            '\\' or '"' => character,
            _ => null,
        };

        /// <summary>
        /// Adds the entry just read, splitting its full name as git does: the section up to the first dot, the key
        /// after the last, and the subsection between.
        /// </summary>
        /// <param name="hasValue">Whether it had an "=" and a value; a key without one has the value "true".</param>
        private void AddEntry(bool hasValue)
        {
            var full = _name.ToArray().AsSpan();
            var first = full.IndexOf((byte)'.');
            var last = full.LastIndexOf((byte)'.');
            var section = first < 0 ? "" : Encoding.UTF8.GetString(full[..first]);
            var subsection = first == last ? null : Encoding.UTF8.GetString(full[(first + 1)..last]);
            var value = hasValue ? Encoding.UTF8.GetString([.. _value]) : "true";
            Entries.Add(new(section, subsection, Encoding.UTF8.GetString(full[(last + 1)..]), value));
        }

        // git's own ctype, not the locale's or Unicode's: \f, \v and NBSP aren't space, and only ASCII is alphabetic.
        private static bool IsSpace(int character) => character is ' ' or '\t' or '\r' or '\n';
        private static bool IsAlpha(int character) => character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
        private static bool IsKeyChar(int character) => IsAlpha(character) || character is (>= '0' and <= '9') or '-';
        private static byte Lower(int character) => (byte)(character is >= 'A' and <= 'Z' ? character + 32 : character);
    }
}
