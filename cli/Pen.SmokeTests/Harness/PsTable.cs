using System.Text.RegularExpressions;

namespace Pen.SmokeTests.Harness;

/// <summary>Builds the parts of pen ps lines that the checks match.</summary>
internal static class PsTable
{
    /// <summary>Builds the regex for a session's TOOLS cell and the spaces after it, from the tools its session
    /// dir recorded, as smoke.sh's tools_cell did.</summary>
    /// <param name="sessionDir">The session's dir.</param>
    /// <returns>The tools joined by commas and the spaces after them, or nothing if it recorded none.</returns>
    public static string BuildToolsCell(string sessionDir)
    {
        var file = Path.Combine(sessionDir, "tools");
        var tools = File.Exists(file) ? string.Join(',', File.ReadAllLines(file)) : "";
        return tools.Length == 0 ? "" : Regex.Escape(tools) + " +";
    }

    /// <summary>Formats a project dir under the real HOME as pen ps shows it, with ~ for the HOME.</summary>
    /// <param name="dir">The project's real path, under the real HOME.</param>
    /// <returns>~ and the rest of the path.</returns>
    public static string FormatProjectDir(string dir) => "~" + dir[SmokeEnvironment.RealHome.Length..];
}
