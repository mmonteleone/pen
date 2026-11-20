using System.Text;
using Pen.Masking;
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
    /// <summary>
    /// Adds the domains not already in <paramref name="key"/> to the array in <paramref name="text"/>, a config.toml
    /// of the given <paramref name="kind"/>.
    /// </summary>
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
        if (kind == ConfigKind.User)
        {
            var user = ConfigParser.ParseUser(text, displayPath, catalog);
            return AddMissing(text, displayPath, key, domains, SelectList(user.Allow, user.Deny, key),
                (edited, added) => Reparse(edited, displayPath, catalog, ConfigParser.ParseUser) is { } after
                    && IsOnlyAdded(user, after, key, added));
        }

        var project = ConfigParser.ParseProject(text, displayPath, catalog);
        return AddMissing(text, displayPath, key, domains, SelectList(project.Allow, project.Deny, key),
            (edited, added) => Reparse(edited, displayPath, catalog, ConfigParser.ParseProject) is { } after
                && IsOnlyAdded(project, after, key, added));
    }

    /// <summary>
    /// Adds the domains not already in <paramref name="existing"/>, the list's domains before the edit, to the text's
    /// array.
    /// </summary>
    /// <remarks>
    /// <paramref name="isOnlyAdded"/> tells whether the edited text parses to the old settings with only the domains
    /// given added to the list.
    /// </remarks>
    private static ListEdit AddMissing(
        string text,
        string displayPath,
        ConfigListKey key,
        IReadOnlyList<string> domains,
        IReadOnlyList<string> existing,
        Func<string, List<string>, bool> isOnlyAdded)
    {
        var added = domains.Select(domain => domain.ToLowerInvariant())
            .Distinct()
            .Where(domain => !existing.Contains(domain))
            .ToList();
        if (added.Count == 0)
            return new(text, added);

        var result = InsertDomains(text, displayPath, key, added);
        // Belt and braces: the edit must change nothing but that list.
        if (!isOnlyAdded(result, added))
        {
            throw new PenException($"{displayPath}: couldn't add to {Name(key)} " +
                $"automatically; add {string.Join(", ", added.Select(Quote))} by hand");
        }

        return new(result, added);
    }

    private static IReadOnlyList<string> SelectList(
        IReadOnlyList<string> allow,
        IReadOnlyList<string> deny,
        ConfigListKey key) =>
        key == ConfigListKey.Allow ? allow : deny;

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

    /// <summary>Parses the edited text; null if it doesn't parse, which the comparison then rejects.</summary>
    private static T? Reparse<T>(
        string text,
        string displayPath,
        RecipeCatalog catalog,
        Func<string, string, RecipeCatalog, T> parse)
        where T : class
    {
        try
        {
            return parse(text, displayPath, catalog);
        }
        catch (PenException)
        {
            // The parser's refusals are the only statement of a valid config, so an edit it refuses is the wanted
            // "not only an addition" result; the caller then refuses the edit with its own message.
            return null;
        }
    }

    /// <summary>
    /// Tells whether <paramref name="after"/> is <paramref name="before"/> with only the domains added to
    /// <paramref name="key"/>'s list.
    /// </summary>
    /// <remarks>
    /// Every property of <see cref="UserConfiguration"/> is compared, by contents and in order (tools in canonical
    /// order, the tokens by owner), so a new property has to be added here too.
    /// </remarks>
    internal static bool IsOnlyAdded(
        UserConfiguration before,
        UserConfiguration after,
        ConfigListKey key,
        IReadOnlyList<string> added) =>
        IsOnlyAdded(before.Allow, after.Allow, ConfigListKey.Allow, key, added)
            && IsOnlyAdded(before.Deny, after.Deny, ConfigListKey.Deny, key, added)
            && after.Tools.SequenceEqual(before.Tools)
            && IsSameHide(before.Hide, after.Hide)
            && after.Env.SequenceEqual(before.Env)
            && after.GitHubTokens.Count == before.GitHubTokens.Count
            && before.GitHubTokens.All(token =>
                after.GitHubTokens.TryGetValue(token.Key, out var value) && value == token.Value);

    /// <summary>
    /// Tells whether <paramref name="after"/> is <paramref name="before"/> with only the domains added to
    /// <paramref name="key"/>'s list.
    /// </summary>
    /// <remarks>
    /// Every property of <see cref="ProjectConfiguration"/> is compared, by contents and in order (tools in
    /// canonical order), so a new property has to be added here too.
    /// </remarks>
    internal static bool IsOnlyAdded(
        ProjectConfiguration before,
        ProjectConfiguration after,
        ConfigListKey key,
        IReadOnlyList<string> added) =>
        IsOnlyAdded(before.Allow, after.Allow, ConfigListKey.Allow, key, added)
            && IsOnlyAdded(before.Deny, after.Deny, ConfigListKey.Deny, key, added)
            && after.Tools.SequenceEqual(before.Tools)
            && IsSameHide(before.Hide, after.Hide)
            && after.GitHubOwner == before.GitHubOwner
            && after.Docker == before.Docker
            && after.Ports.SequenceEqual(before.Ports)
            && after.Replacements.SequenceEqual(before.Replacements);

    /// <summary>
    /// Tells whether the list <paramref name="list"/> names went from <paramref name="before"/> to
    /// <paramref name="after"/> by adding <paramref name="added"/> when it's the one edited, or not at all.
    /// </summary>
    private static bool IsOnlyAdded(
        IReadOnlyList<string> before,
        IReadOnlyList<string> after,
        ConfigListKey list,
        ConfigListKey key,
        IReadOnlyList<string> added) =>
        after.SequenceEqual(list == key ? before.Concat(added) : before);

    private static bool IsSameHide(IReadOnlyList<HidePattern> before, IReadOnlyList<HidePattern> after) =>
        after.Select(pattern => pattern.Text).SequenceEqual(before.Select(pattern => pattern.Text));

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

        // The last item needs a comma before anything follows it (inserted last, at an earlier offset, so the
        // offsets above stay valid).
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
