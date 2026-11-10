using System.Text;

namespace Pen;

/// <summary>
/// Reads git config files directly, never by running git (which could run
/// whatever the file names). This is git's own grammar, from config.c, byte by
/// byte: whatever git would read from a file is what's returned, and a file git
/// would reject, or read in a way Pen can't represent, is a refusal naming its
/// line. A best-effort reading would let a file mean one thing here and another
/// to git. The one thing git accepts that's refused is a NUL in a value or
/// subsection, where git silently cuts the string short.
/// </summary>
internal static class GitConfigFile
{
    /// <summary>One key. Section and key are lowercased; a quoted subsection keeps its case. A key with no "=" has the value "true".</summary>
    internal readonly record struct Entry(string Section, string? Subsection, string Key, string Value);

    /// <summary>The entries in <paramref name="text"/>, or a <see cref="PenException"/> if it isn't what git would read.</summary>
    public static List<Entry> Parse(string text) => Parse(Encoding.UTF8.GetBytes(text), "git config");

    /// <summary>The entries in a file's bytes, or a <see cref="PenException"/> naming <paramref name="source"/> if it isn't what git would read.</summary>
    public static List<Entry> Parse(byte[] bytes, string source)
    {
        var reader = new Reader(bytes);
        if (reader.Read() is { } why)
            throw new PenException($"can't parse {source} at line {reader.Line} ({why}), so pen can't tell what git would read from it: fix or remove it, then rerun");
        return reader.Entries;
    }

    /// <summary>The entries of the file at <paramref name="path"/>: none if it's missing, and a <see cref="PenException"/> if it can't be read or parsed.</summary>
    public static List<Entry> ParseFile(string path)
    {
        try
        {
            return Parse(Native.ReadRegularBytes(path)
                ?? throw new PenException($"can't read the git config file {Out.Field(path)}: it isn't a regular file of a reasonable size"),
                $"the git config file {Out.Field(path)}");
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PenException($"can't read the git config file {Out.Field(path)}: fix its permissions, then rerun");
        }
    }

    /// <summary>git's boolean: its false spellings and zero are false. Anything else is true, or git rejects it when it reads the key.</summary>
    public static bool IsTrue(string value)
    {
        var v = value.ToLowerInvariant();
        if (v is "" or "false" or "no" or "off") return false;
        var digits = v.TrimStart('+', '-').TrimEnd('k', 'm', 'g');
        return digits.Length == 0 || digits.Any(c => c != '0');
    }

    /// <summary>One pass over a file, following git_parse_source and its helpers.</summary>
    private sealed class Reader(byte[] bytes)
    {
        private const int Newline = '\n';
        private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

        private int _pos;
        private int _nextLine = 1;
        private bool _eof;

        public List<Entry> Entries { get; } = [];

        /// <summary>The line of the last character read.</summary>
        public int Line { get; private set; } = 1;

        /// <summary>Reads the whole file. Returns why it can't be read, if it can't.</summary>
        public string? Read()
        {
            if (bytes.AsSpan().StartsWith(Bom)) _pos = Bom.Length;
            var name = new List<byte>();
            int baseLength = 0;
            bool comment = false;
            while (true)
            {
                int c = Next();
                if (c == Newline)
                {
                    if (_eof) return null;
                    comment = false;
                    continue;
                }
                if (comment || IsSpace(c)) continue;
                if (c is '#' or ';')
                {
                    comment = true;
                    continue;
                }
                if (c == '[')
                {
                    name.Clear();
                    if (BaseName(name) is { } bad) return bad;
                    if (name.Count == 0) return "an empty section name";
                    name.Add((byte)'.');
                    baseLength = name.Count;
                    continue;
                }
                if (!IsAlpha(c)) return "a line that isn't a section, a key or a comment";
                name.RemoveRange(baseLength, name.Count - baseLength);
                name.Add(Lower(c));
                if (KeyAndValue(name) is { } wrong) return wrong;
            }
        }

        /// <summary>git's get_next_char: CRLF is a newline, and the end of the file reads as newlines.</summary>
        private int Next()
        {
            Line = _nextLine;
            if (_pos >= bytes.Length)
            {
                _eof = true;
                return Newline;
            }
            int c = bytes[_pos++];
            if (c == '\r' && _pos < bytes.Length && bytes[_pos] == '\n')
            {
                _pos++;
                c = Newline;
            }
            if (c == Newline) _nextLine++;
            return c;
        }

        private string? BaseName(List<byte> name)
        {
            while (true)
            {
                int c = Next();
                if (_eof) return "a section header that isn't closed";
                if (IsSpace(c)) return Subsection(name, c);
                if (c == ']') return null;
                if (!IsKeyChar(c) && c != '.') return "a character that can't be in a section name";
                name.Add(Lower(c));
            }
        }

        private string? Subsection(List<byte> name, int c)
        {
            do
            {
                if (c == Newline) return "a section header that isn't closed";
                c = Next();
            } while (IsSpace(c));
            if (c != '"') return "a section header that isn't [section \"subsection\"]";
            name.Add((byte)'.');
            while (true)
            {
                c = Next();
                if (c == Newline) return "a section header that isn't closed";
                if (c == '"') break;
                if (c == '\\')
                {
                    c = Next();
                    if (c == Newline) return "a section header that isn't closed";
                }
                if (c == 0) return "a NUL in a subsection";
                name.Add((byte)c);
            }
            return Next() == ']' ? null : "a section header that isn't [section \"subsection\"]";
        }

        private string? KeyAndValue(List<byte> name)
        {
            int c;
            while (true)
            {
                c = Next();
                if (_eof || !IsKeyChar(c)) break;
                name.Add(Lower(c));
            }
            while (c is ' ' or '\t') c = Next();

            byte[]? value = null;
            if (c != Newline)
            {
                if (c != '=') return "a key followed by something other than \"=\"";
                var problem = Value(out value);
                if (problem != null) return problem;
            }
            Add(name, value);
            return null;
        }

        /// <summary>git's parse_value, with its trimming: whitespace is dropped at the start, and at the end only if nothing, not even a pair of quotes or a backslash, came after it.</summary>
        private string? Value(out byte[]? result)
        {
            result = null;
            var value = new List<byte>();
            bool quoted = false, comment = false;
            int trimTo = 0;
            while (true)
            {
                int c = Next();
                if (c == Newline)
                {
                    if (quoted) return "a quote that isn't closed";
                    if (trimTo > 0) value.RemoveRange(trimTo, value.Count - trimTo);
                    if (value.Contains(0)) return "a NUL in a value";
                    result = [.. value];
                    return null;
                }
                if (comment) continue;
                if (IsSpace(c) && !quoted)
                {
                    if (trimTo == 0) trimTo = value.Count;
                    if (value.Count > 0) value.Add((byte)c);
                    continue;
                }
                if (!quoted && c is ';' or '#')
                {
                    comment = true;
                    continue;
                }
                trimTo = 0;
                if (c == '\\')
                {
                    c = Next();
                    switch (c)
                    {
                        case Newline: continue;
                        case 't': c = '\t'; break;
                        case 'b': c = '\b'; break;
                        case 'n': c = '\n'; break;
                        case '\\' or '"': break;
                        default: return "an escape git doesn't know";
                    }
                    value.Add((byte)c);
                    continue;
                }
                if (c == '"')
                {
                    quoted = !quoted;
                    continue;
                }
                value.Add((byte)c);
            }
        }

        /// <summary>Splits a full name as git does: the section up to the first dot, the key after the last, and the subsection between.</summary>
        private void Add(List<byte> name, byte[]? value)
        {
            var full = name.ToArray().AsSpan();
            int first = full.IndexOf((byte)'.'), last = full.LastIndexOf((byte)'.');
            string section = first < 0 ? "" : Encoding.UTF8.GetString(full[..first]);
            string? subsection = first == last ? null : Encoding.UTF8.GetString(full[(first + 1)..last]);
            Entries.Add(new(section, subsection, Encoding.UTF8.GetString(full[(last + 1)..]), value == null ? "true" : Encoding.UTF8.GetString(value)));
        }

        // git's own ctype, not the locale's or Unicode's: \f, \v and NBSP aren't space, and only ASCII is alphabetic.
        private static bool IsSpace(int c) => c is ' ' or '\t' or '\r' or '\n';
        private static bool IsAlpha(int c) => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z');
        private static bool IsKeyChar(int c) => IsAlpha(c) || c is (>= '0' and <= '9') or '-';
        private static byte Lower(int c) => (byte)(c is >= 'A' and <= 'Z' ? c + 32 : c);
    }
}
