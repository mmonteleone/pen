using System.Text;

namespace Pen;

/// <summary>
/// Reads git config files directly, never by running git (which could run
/// whatever the file names). Handles the syntax git writes and people
/// commonly hand-write: [section], [section "sub"], [section.sub], quoted
/// values, escapes, comments and backslash continuations.
/// </summary>
internal static class GitConfigFile
{
    /// <summary>One key. Section and key are lowercased; a quoted subsection keeps its case.</summary>
    internal readonly record struct Entry(string Section, string? Subsection, string Key, string Value);

    public static List<Entry> Parse(string text)
    {
        var entries = new List<Entry>();
        string section = "";
        string? sub = null;
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            while (EndsInContinuation(line) && i + 1 < lines.Length) line = line[..^1] + lines[++i];
            var t = line.Trim();
            if (t.Length == 0 || t[0] is '#' or ';') continue;

            if (t[0] == '[')
            {
                int close = HeaderEnd(t);
                if (close < 0) { section = ""; sub = null; continue; }
                (section, sub) = ParseHeader(t[1..close]);
                t = t[(close + 1)..].Trim(); // git allows "[core] bare = true"
                if (t.Length == 0 || t[0] is '#' or ';') continue;
            }

            int eq = t.IndexOf('=');
            string key, value;
            if (eq < 0)
            {
                int end = t.IndexOfAny([' ', '\t', '#', ';']);
                key = end < 0 ? t : t[..end];
                value = "true"; // a bare key is a boolean true
            }
            else
            {
                key = t[..eq].Trim();
                value = Unquote(t[(eq + 1)..].Trim());
            }
            if (key.Length > 0) entries.Add(new(section, sub, key.ToLowerInvariant(), value));
        }
        return entries;
    }

    /// <summary>The entries of the file at <paramref name="path"/>: none if it's missing, and a <see cref="PenException"/> if it can't be read.</summary>
    public static List<Entry> ParseFile(string path)
    {
        try
        {
            return Parse(Native.ReadRegularFile(path)
                ?? throw new PenException($"can't read the git config file {path}: it isn't a regular file of a reasonable size"));
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PenException($"can't read the git config file {path}: fix its permissions, then rerun");
        }
    }

    /// <summary>git's boolean spellings; anything else is false.</summary>
    public static bool IsTrue(string value) =>
        value.ToLowerInvariant() is "true" or "yes" or "on" or "1";

    private static bool EndsInContinuation(string line)
    {
        int n = 0;
        for (int i = line.Length - 1; i >= 0 && line[i] == '\\'; i--) n++;
        return n % 2 == 1;
    }

    private static int HeaderEnd(string t)
    {
        bool quoted = false;
        for (int i = 1; i < t.Length; i++)
        {
            if (quoted && t[i] == '\\') { i++; continue; }
            if (t[i] == '"') quoted = !quoted;
            else if (t[i] == ']' && !quoted) return i;
        }
        return -1;
    }

    private static (string, string?) ParseHeader(string h)
    {
        int q = h.IndexOf('"');
        if (q >= 0)
        {
            var name = h[..q].Trim().ToLowerInvariant();
            var sb = new StringBuilder();
            for (int i = q + 1; i < h.Length && h[i] != '"'; i++)
            {
                if (h[i] == '\\' && i + 1 < h.Length) i++;
                sb.Append(h[i]);
            }
            return (name, sb.ToString());
        }
        h = h.Trim();
        int dot = h.IndexOf('.');
        // Old-style [section.sub]: git lowercases the subsection too.
        return dot < 0 ? (h.ToLowerInvariant(), null) : (h[..dot].ToLowerInvariant(), h[(dot + 1)..].ToLowerInvariant());
    }

    /// <summary>A value: quotes removed, escapes applied, a trailing comment dropped.</summary>
    internal static string Unquote(string value)
    {
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (c == '\\' && i + 1 < value.Length)
            {
                char n = value[++i];
                sb.Append(n switch { 'n' => '\n', 't' => '\t', 'b' => '\b', _ => n });
                continue;
            }
            if (!quoted && (c == '#' || c == ';')) break;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }
}
