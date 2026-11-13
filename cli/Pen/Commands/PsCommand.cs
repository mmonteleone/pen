using System.Text;
using System.Text.RegularExpressions;
using Pen.Platform;
using Pen.Sessions;

namespace Pen.Commands;

/// <summary>Runs pen ps: lists every project's running sessions.</summary>
/// <param name="host">The host user's environment.</param>
internal sealed partial class PsCommand(HostEnv host)
{
    private const int MaxCommand = 80;

    /// <summary>Writes the table of running sessions to stdout, or a note that there are none.</summary>
    /// <returns>0.</returns>
    public int Run()
    {
        var sessions = SessionRegistry.FindRunningEverywhere(host);
        if (sessions.Count == 0)
        {
            Out.WriteLine("no sessions running");
            return 0;
        }
        Out.WriteStdout(Render(host, sessions, DateTime.UtcNow));
        return 0;
    }

    /// <summary>One session's row, and what the rows are sorted by.</summary>
    /// <param name="Project">The project dir the session started in, as shown.</param>
    /// <param name="Started">When the session started, if that's known.</param>
    /// <param name="Cells">The row's cells after the project's.</param>
    private sealed record Row(string Project, DateTime? Started, string[] Cells);

    /// <summary>Renders the table: one row per session, by project, oldest first.</summary>
    /// <returns>The table, with a header row, each line ending in a newline.</returns>
    internal static string Render(HostEnv host, IEnumerable<SessionDir> sessions, DateTime now)
    {
        var rows = sessions
            .Select(session => DescribeRow(host, session, now))
            .OrderBy(row => row.Project, StringComparer.Ordinal)
            .ThenBy(row => row.Started ?? DateTime.MaxValue)
            .Select(row => row.Cells.Prepend(row.Project).ToArray())
            .Prepend(["PROJECT", "SESSION", "DOCKER", "PORTS", "TOOLS", "AGE", "COMMAND"])
            .ToList();

        var widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(row => row[i].Length)).ToArray();
        var table = new StringBuilder();
        foreach (var row in rows)
        {
            var cells = row.Select((cell, i) => i < row.Length - 1 ? cell.PadRight(widths[i]) : cell);
            table.Append(string.Join("  ", cells).TrimEnd()).Append('\n');
        }

        return table.ToString();
    }

    /// <summary>Describes <paramref name="session"/>'s row, from the files its Pen recorded.</summary>
    private static Row DescribeRow(HostEnv host, SessionDir session, DateTime now)
    {
        var started = session.ReadStarted();
        var project = session.ReadEnv().TryGetValue("PROJECT_DIR", out var dir) ? Out.Field(host.Display(dir)) : "?";
        string[] cells =
        [
            Out.Field(session.Id),
            File.Exists(session.DockerMarker) ? "docker" : "",
            string.Join(',', session.ReadPorts()),
            string.Join(',', session.ReadTools().Select(Out.Field)),
            started is { } time ? FormatAge(now - time) : "?",
            Describe(session.ReadCommand()),
        ];
        return new(project, started, cells);
    }

    /// <summary>Formats how long ago: 45s, 12m, 3h05m, 2d04h.</summary>
    internal static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;
        if (age.TotalMinutes < 1)
            return $"{(int)age.TotalSeconds}s";
        if (age.TotalHours < 1)
            return $"{(int)age.TotalMinutes}m";
        if (age.TotalDays < 1)
            return $"{(int)age.TotalHours}h{age.Minutes:00}m";
        return $"{(int)age.TotalDays}d{age.Hours:00}h";
    }

    /// <summary>Describes the command on one line, shell-quoted where needed.</summary>
    /// <returns>
    /// The line, cut at <see cref="MaxCommand"/> characters; "(shell)" when it's empty, "?" when the session didn't
    /// record it.
    /// </returns>
    internal static string Describe(string[]? command)
    {
        if (command == null)
            return "?";
        if (command.Length == 0)
            return "(shell)";
        var line = string.Join(' ', command.Select(Quote));
        line = Out.Field(Whitespace().Replace(line, " "));
        return line.Length <= MaxCommand ? line : line[..(MaxCommand - 1)] + "…";
    }

    /// <summary>Quotes <paramref name="argument"/> for a shell, unless it needs none.</summary>
    private static string Quote(string argument) =>
        argument.Length > 0 && Plain().IsMatch(argument) ? argument : "'" + argument.Replace("'", "'\\''") + "'";

    [GeneratedRegex(@"^[A-Za-z0-9_@%+=:,./-]+$")]
    private static partial Regex Plain();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
