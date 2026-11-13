using System.Text;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Pen.Configuration;

/// <summary>Edits a hand-written config.toml for pen allow, adding domains to its top-level allow array.</summary>
/// <remarks>
/// Domains are inserted into the array (or a new one is added) at positions from the syntax tree, leaving every
/// other byte of the file alone. The result is re-parsed and must equal the old settings plus the new domains, or
/// nothing is written.
/// </remarks>
internal static class ConfigEdit
{
    /// <summary>Adds the domains not already allowed to <paramref name="text"/>'s allow array.</summary>
    /// <param name="text">The config.toml text.</param>
    /// <param name="displayPath">Names the file in errors.</param>
    /// <param name="kind">Which keys the file may hold.</param>
    /// <param name="domains">The domains to allow.</param>
    /// <param name="added">The domains that weren't allowed yet, lowercased, in order.</param>
    /// <returns>The edited text, or <paramref name="text"/> itself if nothing needed adding.</returns>
    /// <remarks>A broken file is reported (as a PenException) before anything is changed.</remarks>
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

        var result = InsertDomains(text, displayPath, added);
        // Belt and braces: the edit must change nothing but allow.
        if (!IsOnlyAllowAdded(before, Reparse(result, displayPath, kind), added))
        {
            throw new PenException($"{displayPath}: couldn't add to allow " +
                $"automatically; add {string.Join(", ", added.Select(Quote))} by hand");
        }

        return result;
    }

    /// <summary>Inserts <paramref name="added"/> into the text's top-level allow array, or adds one.</summary>
    private static string InsertDomains(string text, string displayPath, List<string> added)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var document = SyntaxParser.Parse(text, displayPath, validate: true);
        var allow = document.KeyValues.FirstOrDefault(kv => KeyName(kv) == "allow");
        if (allow == null)
            return Insert(text, NewArrayOffset(text, document), Block(added, "  ", newline) + newline);
        if (allow.Value is ArraySyntax array)
        {
            return array.OpenBracket!.Span.Start.Line == array.CloseBracket!.Span.Start.Line
                ? Rewrite(text, array, added, newline)
                : InsertBeforeClose(text, array, added, newline);
        }

        throw new PenException($"{displayPath}: allow isn't written as a list pen " +
            $"can add to; add {string.Join(", ", added.Select(Quote))} by hand");
    }

    /// <summary>Parses the edited text.</summary>
    /// <returns>Its settings, or empty ones if it doesn't parse (which the comparison then rejects).</returns>
    private static Config Reparse(string text, string displayPath, ConfigKind kind)
    {
        try
        {
            return Config.Parse(text, displayPath, kind);
        }
        catch (PenException)
        {
            return new();
        }
    }

    /// <summary>
    /// Tells whether <paramref name="after"/> is <paramref name="before"/> with only the domains added.
    /// </summary>
    private static bool IsOnlyAllowAdded(Config before, Config after, List<string> added) =>
        after.Allow.SequenceEqual(before.Allow.Concat(added))
            && after.GitHubOwner == before.GitHubOwner
            && after.Docker == before.Docker
            && after.Ports.SequenceEqual(before.Ports)
            && after.Env.SequenceEqual(before.Env)
            && after.GitHubTokens.OrderBy(kv => kv.Key).SequenceEqual(before.GitHubTokens.OrderBy(kv => kv.Key));

    /// <summary>Gets a key-value's key, if it's a single (undotted) bare or quoted key.</summary>
    private static string? KeyName(KeyValueSyntax keyValue)
    {
        if (keyValue.Key is not { } key || key.DotKeys.ChildrenCount > 0)
            return null;
        return key.Key switch
        {
            BareKeySyntax bare => bare.Key?.Text,
            StringValueSyntax quoted => quoted.Value,
            _ => null,
        };
    }

    private static string Quote(string domain) => "\"" + domain + "\"";

    /// <summary>Formats a new multi-line allow array holding <paramref name="domains"/>.</summary>
    private static string Block(IEnumerable<string> domains, string indent, string newline) =>
        "allow = [" + newline + string.Concat(domains.Select(d => indent + Quote(d) + "," + newline)) + "]" + newline;

    private static string Insert(string text, int offset, string insertion) =>
        text[..offset] + insertion + text[offset..];

    /// <summary>Finds the start of the line holding <paramref name="offset"/>.</summary>
    private static int LineStart(string text, int offset) =>
        offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;

    /// <summary>Finds where a new top-level allow goes.</summary>
    /// <returns>
    /// The start of the comment block directly above the first [table], or of the table's header line; with no
    /// tables, the end of the text.
    /// </returns>
    /// <remarks>A key after a table header would belong to that table.</remarks>
    private static int NewArrayOffset(string text, DocumentSyntax document)
    {
        var first = document.Tables.FirstOrDefault();
        if (first == null)
            return text.Length;
        int at = LineStart(text, first.OpenBracket!.Span.Start.Offset);
        while (at > 0)
        {
            int previous = LineStart(text, at - 1);
            if (!text[previous..at].TrimStart().StartsWith('#'))
                break;
            at = previous;
        }
        return at;
    }

    /// <summary>
    /// Rewrites a one-line array as a multi-line one with the domains added, keeping the values as written.
    /// </summary>
    private static string Rewrite(string text, ArraySyntax array, List<string> added, string newline)
    {
        var existing = array.Items.Select(i => i.Value!.ToString()!.Trim());
        var body = string.Concat(existing.Concat(added.Select(Quote)).Select(v => "  " + v + "," + newline));
        int start = array.OpenBracket!.Span.Start.Offset;
        int end = array.CloseBracket!.Span.End.Offset + 1;
        return text[..start] + "[" + newline + body + "]" + text[end..];
    }

    /// <summary>
    /// Inserts the domains as new lines just before the closing bracket, indented like the items above.
    /// </summary>
    private static string InsertBeforeClose(string text, ArraySyntax array, List<string> added, string newline)
    {
        var items = array.Items.ToList();
        int close = array.CloseBracket!.Span.Start.Offset;
        var indent = items.Count > 0 ? LeadingSpace(text, items[0].Value!.Span.Start.Offset) : null;
        if (string.IsNullOrEmpty(indent))
            indent = CommentIndent(text, array, close);

        var builder = new StringBuilder(text);
        int lineStart = LineStart(text, close);
        bool closeAlone = text[lineStart..close].Trim().Length == 0;
        string lines = string.Concat(added.Select(d => indent + Quote(d) + "," + newline));
        if (closeAlone)
            builder.Insert(lineStart, lines);
        else
            builder.Insert(close, newline + lines);

        // The last item needs a comma before anything follows it (inserted
        // last, at an earlier offset, so the offsets above stay valid).
        if (items.Count > 0 && items[^1].Comma == null)
            builder.Insert(items[^1].Value!.Span.End.Offset + 1, ",");
        return builder.ToString();
    }

    /// <summary>
    /// Gets the indent for an array whose items give none (maybe it holds only comments): the first non-blank line's
    /// inside it, else two spaces.
    /// </summary>
    private static string CommentIndent(string text, ArraySyntax array, int close)
    {
        var inside = text[(array.OpenBracket!.Span.End.Offset + 1)..close]
            .Split('\n')
            .Skip(1)
            .FirstOrDefault(l => l.Trim().Length > 0);
        var indent = inside != null ? inside[..(inside.Length - inside.TrimStart().Length)] : "  ";
        return indent.Length == 0 ? "  " : indent;
    }

    /// <summary>Gets the whitespace before <paramref name="offset"/> on its line.</summary>
    /// <returns>That whitespace, or "" if anything else comes before it on the line.</returns>
    private static string LeadingSpace(string text, int offset)
    {
        int start = LineStart(text, offset);
        var before = text[start..offset];
        return before.Trim().Length == 0 ? before : "";
    }
}
