using System.Diagnostics.CodeAnalysis;
using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>Whether git reads the file an include entry names.</summary>
internal enum IncludeReading
{
    /// <summary>include.path, with no subsection: git reads the file wherever the entry is.</summary>
    Always,

    /// <summary>
    /// includeIf.&lt;condition&gt;.path: git reads the file only when the condition holds, which may differ in another
    /// worktree or branch sharing the config. Conditions aren't evaluated, and one git doesn't know (or an empty one)
    /// is taken as maybe too, conservatively.
    /// </summary>
    Maybe,

    /// <summary>
    /// A form git ignores: include with a subsection ([include "x"], [include.x]) or includeIf without one. Only a
    /// caller protecting more than git reads follows it.
    /// </summary>
    Never,
}

/// <summary>An include entry and the file it names.</summary>
/// <param name="Reading">Whether git reads the file.</param>
/// <param name="Value">The entry's value, as read from the config file.</param>
/// <param name="Target">
/// The path git opens: the value expanded (<see cref="GitConfigFile.ExpandPath"/>) and, if relative, joined to the
/// directory of the including file's path as it was named, so the OS resolves ".." physically after a symlink; or why
/// Pen can't tell.
/// </param>
internal sealed record Include(IncludeReading Reading, string Value, GitConfigFile.ExpandedPath Target);

/// <summary>One entry of a config file, in file order, with the include it makes if it's an include entry.</summary>
internal readonly record struct ConfigLine(GitConfigFile.Entry Entry, Include? Include);

/// <summary>
/// The git config files reachable through include.path and includeIf.*.path, read directly (never by running git,
/// which could run whatever a file names), each file read and parsed at most once.
/// </summary>
/// <remarks>
/// <para>
/// A file is cached by its path as the caller names it, and its includes resolve from that name, as git resolves them
/// from the name it opened. Callers walk it their own way: <see cref="MountPlanner"/> visits each file once per walk;
/// <see cref="HooksPath"/> replays git's order, reading a file again each time it's included.
/// </para>
/// <para>
/// The agent can write some of these files, so a walk is capped at <see cref="MaxFiles"/> files: cycles and fan-outs
/// would otherwise take time exponential in git's depth limit of 10.
/// </para>
/// </remarks>
internal sealed class GitConfigIncludes(string home)
{
    /// <summary>How many config files one walk of includes follows before it's refused or given up.</summary>
    internal const int MaxFiles = 100;

    private readonly Dictionary<string, (IReadOnlyList<ConfigLine>? Lines, string? Error)> _files =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Reads the config file at <paramref name="path"/> (<see cref="GitConfigFile.TryParseFile"/>), once.
    /// </summary>
    /// <returns>
    /// Whether it could be read and parsed, or is missing (no lines); otherwise the refusal
    /// <see cref="GitConfigFile.ParseFile"/> would give.
    /// </returns>
    public bool TryRead(
        string path,
        [NotNullWhen(true)] out IReadOnlyList<ConfigLine>? lines,
        [NotNullWhen(false)] out string? error)
    {
        if (!_files.TryGetValue(path, out var read))
        {
            read = GitConfigFile.TryParseFile(path, out var entries, out var parseError)
                ? ([.. entries.Select(entry => new ConfigLine(entry, Match(path, entry)))], null)
                : (null, parseError);
            _files.Add(path, read);
        }
        lines = read.Lines;
        error = read.Error;
        return lines is not null;
    }

    /// <summary>Matches an include entry of the file at <paramref name="from"/>, resolving the file it names.</summary>
    /// <returns>The include, or null if the entry isn't one (an empty value names no file).</returns>
    /// <remarks>
    /// Section and key are matched lowercased, as git matches them; a subsection keeps its case. The home for "~" is
    /// always absolute (HostEnv.FromProcess refuses anything else), so the rooted check takes the expanded path as
    /// written.
    /// </remarks>
    private Include? Match(string from, GitConfigFile.Entry entry)
    {
        if (entry.Section is not ("include" or "includeif") || entry.Key != "path" || entry.Value.Length == 0)
            return null;
        var reading = (entry.Section, entry.Subsection) switch
        {
            ("include", null) => IncludeReading.Always,
            ("includeif", not null) => IncludeReading.Maybe,
            _ => IncludeReading.Never,
        };
        var target = GitConfigFile.ExpandPath(entry.Value, home) switch
        {
            GitConfigFile.ExpandedPath.Expanded { Path: var path } when !Path.IsPathRooted(path) =>
                new GitConfigFile.ExpandedPath.Expanded(Path.Combine(Paths.GetParent(from), path)),
            var expanded => expanded,
        };
        return new(reading, entry.Value, target);
    }
}
