using System.Text;
using Pen.Tools;
using Tomlyn.Parsing;
using Tomlyn.Syntax;

namespace Pen.Configuration;

/// <summary>
/// Edits a hand-written config.toml for pen allow and pen deny, adding domains to its top-level allow or deny
/// array.
/// </summary>
/// <remarks>
/// Domains are inserted into the array (or a new one is added) at positions from the syntax tree, leaving every
/// other byte of the file alone. The result is re-parsed and must equal the old settings plus the new domains in
/// that list, or nothing is written.
/// </remarks>
internal static class ConfigEdit
{
    /// <summary>Adds the domains not already in <paramref name="key"/> to <paramref name="text"/>'s array.</summary>
    /// <param name="text">The config.toml text.</param>
    /// <param name="displayPath">Names the file in errors.</param>
    /// <param name="kind">Which keys the file may hold.</param>
    /// <param name="key">The list to add to.</param>
    /// <param name="domains">The domains to add.</param>
    /// <param name="catalog">The recipes the file's tools come from.</param>
    /// <returns>
    /// The edited text, or <paramref name="text"/> itself if nothing needed adding, and the domains added.
    /// </returns>
    /// <remarks>A broken file is reported (as a PenException) before anything is changed.</remarks>
    public static ListEdit AddToList(
        string text,
        string displayPath,
        ConfigKind kind,
        ConfigListKey key,
        IReadOnlyList<string> domains,
        RecipeCatalog catalog)
    {
        var before = Parse(text, displayPath, kind, catalog);
        var added = domains.Select(domain => domain.ToLowerInvariant())
            .Distinct()
            .Where(domain => !SelectList(before, key).Contains(domain))
            .ToList();
        if (added.Count == 0)
            return new(text, added);

        var result = InsertDomains(text, displayPath, key, added);
        // Belt and braces: the edit must change nothing but that list.
        if (!IsOnlyAdded(before, Reparse(result, displayPath, kind, catalog), key, added))
        {
            throw new PenException($"{displayPath}: couldn't add to {Name(key)} " +
                $"automatically; add {string.Join(", ", added.Select(Quote))} by hand");
        }

        return new(result, added);
    }

    /// <summary>Gets the list <paramref name="key"/> names in <paramref name="config"/>.</summary>
    private static List<string> SelectList(Config config, ConfigListKey key) =>
        key == ConfigListKey.Allow ? config.Allow : config.Deny;

    /// <summary>Gets the key <paramref name="key"/> is written as in config.toml.</summary>
    private static string Name(ConfigListKey key) => key == ConfigListKey.Allow ? "allow" : "deny";

    /// <summary>
    /// Inserts <paramref name="added"/> into the text's top-level <paramref name="key"/> array, or adds one.
    /// </summary>
    private static string InsertDomains(string text, string displayPath, ConfigListKey key, List<string> added)
    {
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        // AddToList's Parse already refused this text if it had any syntax error, so the tree is complete:
        // every array has both brackets, every item a value and every table header its bracket. This method and the
        // ones it calls rely on that.
        var document = SyntaxParser.Parse(text, displayPath, validate: true);
        var name = Name(key);
        var list = document.KeyValues.FirstOrDefault(kv => KeyName(kv) == name);
        if (list is null)
            return Insert(text, NewArrayOffset(text, document), Block(name, added, "  ", newline) + newline);
        if (list.Value is ArraySyntax array)
        {
            return array.OpenBracket!.Span.Start.Line == array.CloseBracket!.Span.Start.Line
                ? Rewrite(text, array, added, newline)
                : InsertBeforeClose(text, array, added, newline);
        }

        throw new PenException($"{displayPath}: {name} isn't written as a list pen " +
            $"can add to; add {string.Join(", ", added.Select(Quote))} by hand");
    }

    /// <summary>Parses the edited text.</summary>
    /// <returns>Its settings, or empty ones if it doesn't parse (which the comparison then rejects).</returns>
    private static Config Reparse(string text, string displayPath, ConfigKind kind, RecipeCatalog catalog)
    {
        try
        {
            return Parse(text, displayPath, kind, catalog);
        }
        catch (PenException)
        {
            return new();
        }
    }

    private static Config Parse(string text, string displayPath, ConfigKind kind, RecipeCatalog catalog) =>
        kind == ConfigKind.User
            ? ConfigParser.ParseUser(text, displayPath, catalog)
            : ConfigParser.ParseProject(text, displayPath, catalog);

    /// <summary>
    /// Tells whether <paramref name="after"/> is <paramref name="before"/> with only the domains added to
    /// <paramref name="key"/>'s list.
    /// </summary>
    /// <remarks>
    /// Every property of <see cref="Config"/> is compared, by contents and in order (tools in canonical order, the
    /// tokens by owner), so a new property has to be added here too.
    /// </remarks>
    internal static bool IsOnlyAdded(Config before, Config after, ConfigListKey key, List<string> added) =>
        after.Allow.SequenceEqual(key == ConfigListKey.Allow ? before.Allow.Concat(added) : before.Allow)
            && after.Deny.SequenceEqual(key == ConfigListKey.Deny ? before.Deny.Concat(added) : before.Deny)
            && after.Tools.SequenceEqual(before.Tools)
            && after.GitHubOwner == before.GitHubOwner
            && after.Docker == before.Docker
            && after.Ports.SequenceEqual(before.Ports)
            && after.Env.SequenceEqual(before.Env)
            && after.GitHubTokens.Count == before.GitHubTokens.Count
            && before.GitHubTokens.All(token =>
                after.GitHubTokens.TryGetValue(token.Key, out var value) && value == token.Value)
            && after.Hide.Select(pattern => pattern.Text).SequenceEqual(before.Hide.Select(pattern => pattern.Text))
            && after.Replace.SequenceEqual(before.Replace);

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

    /// <summary>Formats a new multi-line <paramref name="key"/> array holding <paramref name="domains"/>.</summary>
    private static string Block(string key, IEnumerable<string> domains, string indent, string newline) =>
        key + " = [" + newline + string.Concat(domains.Select(d => indent + Quote(d) + "," + newline)) + "]" + newline;

    private static string Insert(string text, int offset, string insertion) =>
        text[..offset] + insertion + text[offset..];

    /// <summary>Finds the start of the line holding <paramref name="offset"/>.</summary>
    private static int LineStart(string text, int offset) =>
        offset == 0 ? 0 : text.LastIndexOf('\n', offset - 1) + 1;

    /// <summary>Finds where a new top-level array goes.</summary>
    /// <returns>
    /// The start of the comment block directly above the first [table], or of the table's header line; with no
    /// tables, the end of the text.
    /// </returns>
    /// <remarks>A key after a table header would belong to that table.</remarks>
    private static int NewArrayOffset(string text, DocumentSyntax document)
    {
        var first = document.Tables.FirstOrDefault();
        if (first is null)
            return text.Length;
        var at = LineStart(text, first.OpenBracket!.Span.Start.Offset);
        while (at > 0)
        {
            var previous = LineStart(text, at - 1);
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
        var start = array.OpenBracket!.Span.Start.Offset;
        var end = array.CloseBracket!.Span.End.Offset + 1;
        return text[..start] + "[" + newline + body + "]" + text[end..];
    }

    /// <summary>
    /// Inserts the domains as new lines just before the closing bracket, indented like the items above.
    /// </summary>
    private static string InsertBeforeClose(string text, ArraySyntax array, List<string> added, string newline)
    {
        var items = array.Items.ToList();
        var close = array.CloseBracket!.Span.Start.Offset;
        var indent = items.Count > 0 ? LeadingSpace(text, items[0].Value!.Span.Start.Offset) : null;
        if (string.IsNullOrEmpty(indent))
            indent = CommentIndent(text, array, close);

        var builder = new StringBuilder(text);
        var lineStart = LineStart(text, close);
        var closeAlone = text[lineStart..close].Trim().Length == 0;
        var lines = string.Concat(added.Select(d => indent + Quote(d) + "," + newline));
        if (closeAlone)
            builder.Insert(lineStart, lines);
        else
            builder.Insert(close, newline + lines);

        // The last item needs a comma before anything follows it (inserted
        // last, at an earlier offset, so the offsets above stay valid).
        if (items.Count > 0 && items[^1].Comma is null)
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
        var indent = inside is not null ? inside[..(inside.Length - inside.TrimStart().Length)] : "  ";
        return indent.Length == 0 ? "  " : indent;
    }

    /// <summary>Gets the whitespace before <paramref name="offset"/> on its line.</summary>
    /// <returns>That whitespace, or "" if anything else comes before it on the line.</returns>
    private static string LeadingSpace(string text, int offset)
    {
        var start = LineStart(text, offset);
        var before = text[start..offset];
        return before.Trim().Length == 0 ? before : "";
    }
}

/// <summary>The top-level lists <see cref="ConfigEdit.AddToList"/> can add to.</summary>
internal enum ConfigListKey
{
    Allow,
    Deny,
}
