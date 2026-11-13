using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Pen.Configuration;

/// <summary>
/// pen allow's edit of a hand-written config.toml: inserts domains into the
/// top-level allow array (or adds one) at positions from the syntax tree,
/// leaving every other byte of the file alone. The result is re-parsed and
/// must equal the old settings plus the new domains, or nothing is written.
/// </summary>
internal static class ConfigEdit
{
    /// <summary>
    /// Returns <paramref name="text"/> with the domains not already allowed
    /// added to allow, and which ones those were. A broken file is reported
    /// (as a PenException) before anything is changed.
    /// </summary>
    public static string AddAllowed(
        string text,
        string displayPath,
        ConfigKind kind,
        IReadOnlyList<string> domains,
        out List<string> added)
    {
        var before = Config.Parse(text, displayPath, kind);
        added = domains.Select(d => d.ToLowerInvariant()).Distinct().Where(d => !before.Allow.Contains(d)).ToList();
        if (added.Count == 0)
            return text;

        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var doc = SyntaxParser.Parse(text, displayPath, validate: true);
        var allow = doc.KeyValues.FirstOrDefault(kv => KeyName(kv) == "allow");
        string result;
        if (allow == null)
        {
            result = Insert(text, NewArrayOffset(text, doc), Block(added, "  ", nl) + nl);
        }
        else if (allow.Value is ArraySyntax array)
        {
            result = array.OpenBracket!.Span.Start.Line == array.CloseBracket!.Span.Start.Line
                ? Rewrite(text, array, added, nl)
                : InsertBeforeClose(text, array, added, nl);
        }
        else
        {
            throw new PenException($"{displayPath}: allow isn't written as a list pen " +
                $"can add to; add {string.Join(", ", added.Select(Quote))} by hand");
        }

        // Belt and braces: the edit must change nothing but allow.
        Config after;
        try
        {
            after = Config.Parse(result, displayPath, kind);
        }
        catch (PenException)
        {
            after = new();
        }
        if (!after.Allow.SequenceEqual(before.Allow.Concat(added))
            || after.GitHubOwner != before.GitHubOwner || after.Docker != before.Docker
            || !after.Ports.SequenceEqual(before.Ports)
            || !after.Env.SequenceEqual(before.Env)
            || !after.GitHubTokens.OrderBy(kv => kv.Key).SequenceEqual(before.GitHubTokens.OrderBy(kv => kv.Key)))
        {
            throw new PenException($"{displayPath}: couldn't add to allow " +
                $"automatically; add {string.Join(", ", added.Select(Quote))} by hand");
        }

        return result;
    }

    private static string? KeyName(KeyValueSyntax kv)
    {
        if (kv.Key is not { } key || key.DotKeys.ChildrenCount > 0)
            return null;
        return key.Key switch
        {
            BareKeySyntax bare => bare.Key?.Text,
            StringValueSyntax str => str.Value,
            _ => null,
        };
    }

    private static string Quote(string domain) => "\"" + domain + "\"";

    private static string Block(IEnumerable<string> domains, string indent, string nl) =>
        "allow = [" + nl + string.Concat(domains.Select(d => indent + Quote(d) + "," + nl)) + "]" + nl;

    private static string Insert(string text, int offset, string s) => text[..offset] + s + text[offset..];

    private static int LineStart(string text, int offset) => offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;

    /// <summary>
    /// Where a new top-level allow goes: above the first [table], and above
    /// the comment block directly over it, since a key after a header would
    /// belong to that table. With no tables, at the end.
    /// </summary>
    private static int NewArrayOffset(string text, DocumentSyntax doc)
    {
        var first = doc.Tables.FirstOrDefault();
        if (first == null)
            return text.Length;
        int at = LineStart(text, first.OpenBracket!.Span.Start.Offset);
        while (at > 0)
        {
            int prev = LineStart(text, at - 1);
            if (!text[prev..at].TrimStart().StartsWith('#'))
                break;
            at = prev;
        }
        return at;
    }

    /// <summary>A one-line array becomes a multi-line one, keeping the values as written.</summary>
    private static string Rewrite(string text, ArraySyntax array, List<string> added, string nl)
    {
        var existing = array.Items.Select(i => i.Value!.ToString()!.Trim());
        var body = string.Concat(existing.Concat(added.Select(Quote)).Select(v => "  " + v + "," + nl));
        int start = array.OpenBracket!.Span.Start.Offset, end = array.CloseBracket!.Span.End.Offset + 1;
        return text[..start] + "[" + nl + body + "]" + text[end..];
    }

    /// <summary>New lines just before the closing bracket, indented like the items above.</summary>
    private static string InsertBeforeClose(string text, ArraySyntax array, List<string> added, string nl)
    {
        var items = array.Items.ToList();
        int close = array.CloseBracket!.Span.Start.Offset;
        var indent = items.Count > 0 ? LeadingSpace(text, items[0].Value!.Span.Start.Offset) : null;
        if (string.IsNullOrEmpty(indent))
        {
            // No items (maybe only comments): match the first comment line inside, else two spaces.
            var inside = text[(array.OpenBracket!.Span.End.Offset + 1)..close].Split('\n').Skip(1)
            .FirstOrDefault(l => l.Trim().Length > 0);
            indent = inside != null ? inside[..(inside.Length - inside.TrimStart().Length)] : "  ";
            if (indent.Length == 0)
                indent = "  ";
        }

        var sb = new StringBuilder(text);
        int lineStart = LineStart(text, close);
        bool closeAlone = text[lineStart..close].Trim().Length == 0;
        string lines = string.Concat(added.Select(d => indent + Quote(d) + "," + nl));
        if (closeAlone)
            sb.Insert(lineStart, lines);
        else
            sb.Insert(close, nl + lines);

        // The last item needs a comma before anything follows it (inserted
        // last, at an earlier offset, so the offsets above stay valid).
        if (items.Count > 0 && items[^1].Comma == null)
            sb.Insert(items[^1].Value!.Span.End.Offset + 1, ",");
        return sb.ToString();
    }

    private static string LeadingSpace(string text, int offset)
    {
        int start = LineStart(text, offset);
        var before = text[start..offset];
        return before.Trim().Length == 0 ? before : "";
    }
}
