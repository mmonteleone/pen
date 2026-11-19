using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Pen.Platform;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;

namespace Pen.Configuration;

/// <summary>
/// Reads TOML the way every Pen file does (config.toml, a recipe's tool.toml, a detected toolchain file), and
/// checks the values Pen's TOML files share.
/// </summary>
internal static partial class TomlReader
{
    // Names Pen sets itself, or that would steer the sandbox around its proxy.
    private static readonly HashSet<string> ReservedEnv = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROJECT_DIR", "NUGET_PACKAGES", "GH_TOKEN", "GITHUB_TOKEN", "HOME", "USER", "LOGNAME",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY", "DOCKER_HOST",
    };

    /// <summary>Parses TOML text into its untyped table; errors call the file <paramref name="displayPath"/>.</summary>
    internal static TomlTable ParseToml(string text, string displayPath) =>
        TryParseToml(text, displayPath, out var table, out var error) ? table : throw new PenException(error);

    /// <summary>
    /// Parses TOML text into its untyped table, or says why it can't, calling the file
    /// <paramref name="displayPath"/>. Diagnostics and model errors come back as false; a <c>TomlException</c> from
    /// the syntax parser itself could still escape, so callers that must not throw catch it.
    /// </summary>
    internal static bool TryParseToml(
        string text,
        string displayPath,
        [NotNullWhen(true)] out TomlTable? table,
        [NotNullWhen(false)] out string? error)
    {
        // Validating the syntax tree first enforces the whole TOML spec (a key or table defined twice, say), with
        // line numbers; the model built afterwards would quietly let the last definition win.
        var document = SyntaxParser.Parse(text, displayPath, validate: true);
        foreach (var diagnostic in document.Diagnostics)
        {
            if (diagnostic.Kind == Tomlyn.Syntax.DiagnosticMessageKind.Error)
            {
                table = null;
                error = $"{displayPath}:{diagnostic.Span.Start.Line + 1}: {Out.Field(diagnostic.Message)}";
                return false;
            }
        }
        try
        {
            table = TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? [];
            error = null;
            return true;
        }
        catch (TomlException exception)
        {
            // Tomlyn reports a model it can't build only by throwing.
            table = null;
            error = $"{displayPath}: {Out.Field(exception.Message)}";
            return false;
        }
    }

    /// <summary>Tells whether an environment variable is Pen's own, so no config may set it.</summary>
    internal static bool IsReservedEnv(string name) =>
        ReservedEnv.Contains(name)
            || name.StartsWith("PEN_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns <paramref name="value"/> as a table, or throws <paramref name="error"/>'s exception.</summary>
    internal static TomlTable RequireTable(object value, string key, Func<string, PenException> error) =>
        value as TomlTable ?? throw error($"{key} must be a table ([{key}])");

    /// <summary>
    /// Returns <paramref name="value"/> as a non-empty string, or throws <paramref name="error"/>'s exception.
    /// </summary>
    internal static string RequireString(object value, string key, Func<string, PenException> error) =>
        value is string text && text.Length > 0
            ? text
            : throw error($"{key} must be a non-empty string (in quotes)");

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})\z")]
    internal static partial Regex OwnerRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    internal static partial Regex EnvNameRegex();
}
