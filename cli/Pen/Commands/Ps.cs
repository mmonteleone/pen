using System.Text;
using System.Text.RegularExpressions;
using Pen.Platform;
using Pen.Sessions;

namespace Pen.Commands;

/// <summary>pen ps: every project's running sessions.</summary>
internal static partial class Ps
{
    private const int MaxCommand = 80;

    public static int Run(HostEnv host)
    {
        var sessions = SessionRegistry.RunningEverywhere(host);
        if (sessions.Count == 0)
        {
            Out.WriteLine("no sessions running");
            return 0;
        }
        Out.WriteStdout(Render(host, sessions, DateTime.UtcNow));
        return 0;
    }

    /// <summary>The table: one row per session, by project, oldest first.</summary>
    internal static string Render(HostEnv host, IEnumerable<SessionDir> sessions, DateTime now)
    {
        var rows = sessions
            .Select(s =>
            {
                var started = s.ReadStarted();
                return (
                    Project: s.ReadEnv().TryGetValue("PROJECT_DIR", out var dir) ? Out.Field(host.Display(dir)) : "?",
                    Started: started,
                    Cells: new[]
                    {
                        "",
                        Out.Field(s.Id),
                        File.Exists(s.DockerMarker) ? "docker" : "",
                        string.Join(',', s.ReadPorts()),
                        string.Join(',', s.ReadTools().Select(Out.Field)),
                        started is { } t ? Age(now - t) : "?",
                        Describe(s.ReadCommand()),
                    });
            })
            .OrderBy(r => r.Project, StringComparer.Ordinal)
            .ThenBy(r => r.Started ?? DateTime.MaxValue)
            .Select(r =>
            {
                r.Cells[0] = r.Project;
                return r.Cells;
            })
            .Prepend(["PROJECT", "SESSION", "DOCKER", "PORTS", "TOOLS", "AGE", "COMMAND"])
            .ToList();

        var widths = Enumerable.Range(0, rows[0].Length).Select(i => rows.Max(r => r[i].Length)).ToArray();
        var sb = new StringBuilder();
        foreach (var r in rows)
        {
            sb.Append(string.Join("  ", r.Select((c, i) => i < r.Length - 1 ? c.PadRight(widths[i]) : c)).TrimEnd())
                .Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>How long ago: 45s, 12m, 3h05m, 2d04h.</summary>
    internal static string Age(TimeSpan d)
    {
        if (d < TimeSpan.Zero)
            d = TimeSpan.Zero;
        if (d.TotalMinutes < 1)
            return $"{(int)d.TotalSeconds}s";
        if (d.TotalHours < 1)
            return $"{(int)d.TotalMinutes}m";
        if (d.TotalDays < 1)
            return $"{(int)d.TotalHours}h{d.Minutes:00}m";
        return $"{(int)d.TotalDays}d{d.Hours:00}h";
    }

    /// <summary>
    /// The command on one line, shell-quoted where needed, cut at <see cref="MaxCommand"/>
    /// characters. A shell when it's empty; ? when the session didn't record it.
    /// </summary>
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

    private static string Quote(string a) =>
        a.Length > 0 && Plain().IsMatch(a) ? a : "'" + a.Replace("'", "'\\''") + "'";

    [GeneratedRegex(@"^[A-Za-z0-9_@%+=:,./-]+$")]
    private static partial Regex Plain();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
