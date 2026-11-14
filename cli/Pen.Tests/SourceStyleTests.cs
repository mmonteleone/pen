using System.Text.RegularExpressions;

namespace Pen.Tests;

public class SourceStyleTests
{
    private const int MaxLineLength = 120;

    // The project whose lines may hold nothing but a long string literal.
    private const string LoneLiteralProject = "Pen.SmokeTests";

    // A run of three or more quotes, with any $ in front: opens a raw string literal, or closes one.
    private static readonly Regex RawDelimiter = new("\\$*(\"{3,})", RegexOptions.Compiled);

    // A line that holds nothing but one regular string literal, and the brackets, comma or semicolon that close
    // it: a smoke check's description is one literal, and some are longer than a line. Only the smoke tests'
    // sources may have one.
    private static readonly Regex LoneStringLiteral =
        new("^\\s*\"(?:[^\"\\\\]|\\\\.)*\"[)\\]]*[,;]?$", RegexOptions.Compiled);

    // The projects held to the code style in CONTRIBUTING.md (Pen.Tests isn't).
    private static readonly string[] StyledProjects = ["Pen", "Pen.SmokeTests", "Pen.TestSupport"];

    [Fact]
    public void NoLineInTheCliSourcesIsOverMaxLength()
    {
        var cli = FindCliDir();

        var offenders = new List<string>();
        var files = StyledProjects
            .SelectMany(project => Directory
                .EnumerateFiles(Path.Combine(cli, project), "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(Path.GetRelativePath(Path.Combine(cli, project), f)))
                .Select(f => (Project: project, File: f)))
            .OrderBy(entry => entry.File, StringComparer.Ordinal);
        foreach (var (project, file) in files)
        {
            var loneLiteralAllowed = project == LoneLiteralProject;
            var lines = File.ReadAllLines(file);
            // The closing quotes of the raw string the line starts in, or null outside one.
            string? closing = null;
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Lines that start inside a raw string (its text and closing line) are exempt.
                if (closing == null && line.Length > MaxLineLength
                    && !(loneLiteralAllowed && LoneStringLiteral.IsMatch(line)))
                    offenders.Add($"{Path.GetRelativePath(cli, file)}:{i + 1}");
                closing = FindClosingAfter(line, closing);
            }
        }

        Assert.True(offenders.Count == 0,
            $"lines over {MaxLineLength} characters in cli/:\n" + string.Join("\n", offenders));
    }

    /// <summary>Finds the raw string literal a line ends inside, given the one it starts inside.</summary>
    /// <param name="line">The line.</param>
    /// <param name="closing">The closing quotes of the raw string the line starts inside, or null if none.</param>
    /// <returns>The closing quotes of the raw string the line ends inside, or null if it ends outside one.</returns>
    private static string? FindClosingAfter(string line, string? closing)
    {
        var position = 0;
        while (position < line.Length)
        {
            if (closing != null)
            {
                var end = line.IndexOf(closing, position, StringComparison.Ordinal);
                if (end < 0)
                    return closing;
                position = end + closing.Length;
                closing = null;
                continue;
            }
            var open = RawDelimiter.Match(line, position);
            if (!open.Success)
                return null;
            closing = open.Groups[1].Value;
            position = open.Index + open.Length;
        }
        return closing;
    }

    private static bool IsBuildOutput(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }

    /// <summary>Finds cli/ by walking up from the test assembly (cli/Pen.Tests/bin/…).</summary>
    private static string FindCliDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pen", "Pen.csproj")))
                return dir.FullName;
        }
        throw new InvalidOperationException($"no Pen/Pen.csproj above {AppContext.BaseDirectory}");
    }
}
