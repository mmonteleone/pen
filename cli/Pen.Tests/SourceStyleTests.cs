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

    // string.Normalize and IsNormalized, which do nothing under the invariant globalization pen is built with.
    private static readonly Regex StringNormalization =
        new("\\bIsNormalized\\s*\\(|\\.Normalize\\s*\\(|\\bNormalizationForm\\b", RegexOptions.Compiled);

    [Fact]
    public void PenNeverNormalizesThroughString_ItDoesNothingUnderInvariantGlobalization()
    {
        var pen = Path.Combine(FindCliDir(), "Pen");

        var offenders = new List<string>();
        var files = Directory.EnumerateFiles(pen, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(Path.GetRelativePath(pen, f)))
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal)
                    && StringNormalization.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(pen, file)}:{i + 1}");
            }
        }

        Assert.True(offenders.Count == 0,
            "string normalization in cli/Pen (use Native.NormalizeFormC):\n" + string.Join("\n", offenders));
    }

    // A global using alias elsewhere (for example, aliasing NativeLibrary) isn't caught.
    private static readonly Regex NativeImport =
        new("\\b(?:DllImport|LibraryImport|NativeLibrary)\\b|delegate\\s*\\*\\s*unmanaged", RegexOptions.Compiled);

    [Fact]
    public void OnlyNativeDeclaresNativeCalls()
    {
        var offenders = FindCodeLines(NativeImport)
            .Where(found => found.File != Path.Combine("Platform", "Native.cs"))
            .Select(found => $"{found.File}:{found.Line}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "native calls outside cli/Pen/Platform/Native.cs:\n" + string.Join("\n", offenders));
    }

    // RepoGuard runs at session start, when the agent may have planted git config and hooks, so it reads files
    // directly and never runs git or any other process.
    private static readonly Regex ProcOrHostGit = new("\\b(?:HostGit|Proc)\\b", RegexOptions.Compiled);

    [Fact]
    public void RepoGuardNeverRunsGit()
    {
        var repoGuard = Path.Combine("GitProtection", "RepoGuard.cs");
        Assert.True(File.Exists(Path.Combine(FindCliDir(), "Pen", repoGuard)),
            "cli/Pen/GitProtection/RepoGuard.cs moved: update this test to follow it");

        var offenders = FindCodeLines(ProcOrHostGit)
            .Where(found => found.File == repoGuard)
            .Select(found => $"{found.File}:{found.Line}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "RepoGuard refers to HostGit or Proc:\n" + string.Join("\n", offenders));
    }

    /// <summary>Finds the lines of cli/Pen's sources, outside line comments, that match <paramref name="pattern"/>.</summary>
    /// <returns>Each file relative to cli/Pen, with its line number.</returns>
    private static List<(string File, int Line)> FindCodeLines(Regex pattern)
    {
        var pen = Path.Combine(FindCliDir(), "Pen");
        var found = new List<(string File, int Line)>();
        var files = Directory.EnumerateFiles(pen, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(Path.GetRelativePath(pen, f)))
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (!lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && pattern.IsMatch(lines[i]))
                    found.Add((Path.GetRelativePath(pen, file), i + 1));
            }
        }
        return found;
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
