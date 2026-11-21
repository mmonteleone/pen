namespace Pen.IntegrationTests.Harness;

/// <summary>Quotes text for sh, so a script can hold any value as one word.</summary>
internal static class ShellQuote
{
    /// <summary>Quotes <paramref name="value"/> in single quotes, with any single quote in it escaped.</summary>
    /// <param name="value">The text.</param>
    /// <returns>A sh word that expands to exactly the text.</returns>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
