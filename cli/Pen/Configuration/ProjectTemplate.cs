using System.Globalization;
using System.Text;
using Pen.Platform;

namespace Pen.Configuration;

/// <summary>Writes a project's .pen/config.toml text from the embedded template and what detection found.</summary>
/// <remarks>
/// The template comments out every key. A placeholder line (<c>{{tools}}</c>, <c>{{hide}}</c>, one
/// <c>{{…-note}}</c> per commented hint) becomes the detected setting or hint, or goes when there's nothing.
/// Values are only recipe-validated specs and validated patterns, written through <see cref="Quote"/>; comments
/// hold only what the detection kept as safe for them (sources, Docker files, replace hints, unknown versions).
/// No key is written as a table, so a key added at the end of the file (as <see cref="ConfigEdit"/> does when
/// there's no table) stays top-level.
/// </remarks>
internal static class ProjectTemplate
{
    public const string ResourceName = "templates/project/config.toml";

    /// <summary>The most paths a comment lists for one finding before "and N more".</summary>
    public const int MaxListed = 3;

    /// <summary>Renders the template with <paramref name="detection"/>'s settings and hints filled in.</summary>
    /// <returns>The config.toml text; <see cref="ProjectDetection.None"/> gives the all-commented template.</returns>
    /// <exception cref="InvalidOperationException">The template has a placeholder this doesn't know.</exception>
    public static string Render(ProjectDetection detection)
    {
        var template = Embedded.ReadText(ResourceName);
        var builder = new StringBuilder(template.Length);
        foreach (var line in template.TrimEnd('\n').Split('\n'))
        {
            if (!line.StartsWith("{{", StringComparison.Ordinal))
            {
                builder.Append(line).Append('\n');
                continue;
            }

            foreach (var rendered in ExpandPlaceholder(line, detection))
                builder.Append(rendered).Append('\n');
        }
        return builder.ToString();
    }

    /// <summary>Writes <paramref name="value"/> as a TOML basic string, in double quotes.</summary>
    /// <remarks>Escapes quotes, backslashes and control characters; anything else is written as it is.</remarks>
    /// <exception cref="ArgumentException">A lone surrogate in <paramref name="value"/>.</exception>
    internal static string Quote(string value)
    {
        var builder = new StringBuilder(value.Length + 2).Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
                continue;
            }
            if (char.IsSurrogate(c))
                throw new ArgumentException("A lone surrogate can't be written as TOML.", nameof(value));
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '\b':
                    builder.Append(@"\b");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\f':
                    builder.Append(@"\f");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                default:
                    if (c < ' ' || c == '\u007f')
                        builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
                    else
                        builder.Append(c);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>Gets the lines a placeholder line stands for.</summary>
    private static IEnumerable<string> ExpandPlaceholder(string line, ProjectDetection detection) => line switch
    {
        "{{tools}}" => RenderTools(detection),
        "{{base-tools-note}}" => RenderBaseToolsNote(detection),
        "{{hide}}" => RenderHide(detection),
        "{{replace-note}}" => RenderReplaceNote(detection),
        "{{docker-note}}" => RenderDockerNote(detection),
        _ => throw new InvalidOperationException($"{ResourceName}: unknown placeholder {line}"),
    };

    /// <summary>Writes the detected tools as one tools line, with a comment line per tool.</summary>
    private static IEnumerable<string> RenderTools(ProjectDetection detection)
    {
        if (detection.Tools.Count == 0)
            yield break;
        yield return "";
        yield return "# Found in this project:";
        foreach (var tool in detection.Tools)
        {
            var line = $"# {FormatTool(tool)}";
            if (tool.Sources.Count > 0)
                line += $", from {CappedList.Join(tool.Sources, MaxListed)}";
            if (tool.HasUnknownVersion)
                line += $" ({DescribeUnknownVersion(tool)}, so it's at the recipe's default)";
            yield return line;
        }
        yield return $"tools = [{string.Join(", ", detection.Tools.Select(tool => Quote(FormatTool(tool))))}]";
    }

    /// <summary>Writes a tool as its bare name when it follows the recipe's default, else as name:version.</summary>
    internal static string FormatTool(DetectedTool tool) => tool.IsPinned ? tool.Tool.ToString() : tool.Tool.Name;

    private static string DescribeUnknownVersion(DetectedTool tool) => tool.UnknownVersions.Count switch
    {
        0 => "a version found isn't one its recipe knows",
        1 => $"{tool.UnknownVersions[0]} isn't a version its recipe knows",
        _ => $"{CappedList.Join(tool.UnknownVersions, MaxListed)} aren't versions its recipe knows",
    };

    /// <summary>Notes that Node and Python, when found, come with the base image.</summary>
    private static IEnumerable<string> RenderBaseToolsNote(ProjectDetection detection)
    {
        if (detection.HasNode || detection.HasPython)
            yield return "";
        if (detection.HasNode)
            yield return "# Found package.json: Node is in the base image, so it needs no tool.";
        if (detection.HasPython)
        {
            yield return "# Found pyproject.toml or requirements.txt: Python is in the base image, " +
                "so it needs no tool.";
        }
    }

    /// <summary>Writes the detected hide patterns as a hide list, each with what it matched.</summary>
    private static IEnumerable<string> RenderHide(ProjectDetection detection)
    {
        if (detection.Hide.Count == 0)
            yield break;
        yield return "";
        yield return "# Found in this project, with what each matched:";
        yield return "hide = [";
        foreach (var pattern in detection.Hide)
        {
            var line = $"  {Quote(pattern.Pattern)},";
            if (pattern.Sources.Count > 0)
                line += $" # {CappedList.Join(pattern.Sources, MaxListed)}";
            yield return line;
        }
        yield return "]";
    }

    /// <summary>Suggests, commented, replacing each .env found with the .env.example beside it.</summary>
    private static IEnumerable<string> RenderReplaceNote(ProjectDetection detection)
    {
        var hints = detection.ReplaceHints;
        if (hints.Count == 0)
            yield break;
        yield return "";
        yield return "# Found a .env with a .env.example beside it; to show the agent the example in its place:";
        var entries = hints.Take(MaxListed).Select(hint => $"{Quote(hint.Target)} = {Quote(hint.Source)}");
        yield return $"# replace = {{ {string.Join(", ", entries)} }}";
        if (hints.Count > MaxListed)
            yield return $"# (and {hints.Count - MaxListed} more .env files with an example beside them)";
    }

    /// <summary>Notes the Docker files found, above the commented docker setting.</summary>
    private static IEnumerable<string> RenderDockerNote(ProjectDetection detection)
    {
        if (detection.DockerFiles.Count == 0)
            yield break;
        var found = CappedList.Join(detection.DockerFiles, MaxListed);
        yield return $"# Found {found}: if sessions need Docker, uncomment this.";
    }
}
