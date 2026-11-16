using Pen.Platform;

namespace Pen.Masking;

/// <summary>Formats what a session says at its start about what it hides and replaces.</summary>
internal static class MaskMessages
{
    /// <summary>The most hidden paths, and the most replaced ones, the summary names; the rest are counted.</summary>
    public const int MaxNamed = 5;

    /// <summary>
    /// The warning given where a hidden or replaced file may be reached under another spelling: the probe found the
    /// filesystem ignores case, or couldn't tell.
    /// </summary>
    internal const string CaseWarning = "warning: this project's filesystem isn't known to be case-sensitive: the " +
        "agent may read a hidden or replaced file under another spelling (.ENV for .env); hide its directory to " +
        "keep it out of reach";

    /// <summary>
    /// The warning given on macOS where a hidden or replaced file's name isn't ASCII: another Unicode spelling (NFC
    /// or NFD) of it reaches the file past its mount on any APFS volume, and the case probe can't see that.
    /// </summary>
    internal const string UnicodeWarning = "warning: on macOS, another Unicode spelling of a hidden or replaced " +
        "file's name reaches the file: hide its directory to keep it out of reach";

    /// <summary>The warning given in a docker session that hides or replaces anything.</summary>
    internal const string DockerWarning = "warning: hidden and replaced files aren't enforced with --docker: " +
        "the agent can use the privileged Docker daemon to undo their mounts";

    /// <summary>Formats the line saying what the agent doesn't see and what it sees replaced.</summary>
    /// <param name="plan">What the session hides and replaces.</param>
    /// <returns>
    /// The line, e.g. "hidden from the agent: .env, secrets/ and 3 more; replaced: .env", or null for an empty plan.
    /// </returns>
    /// <remarks>
    /// Hidden files and directories are named together, sorted, each directory with a trailing /. Names come from
    /// the disk, so each goes through <see cref="Out.Field"/>.
    /// </remarks>
    public static string? FormatSummary(MaskPlan plan)
    {
        var parts = new List<string>();
        var hidden = plan.HiddenFiles.Select(file => (file.RelativePath, Name: Out.Field(file.RelativePath)))
            .Concat(plan.HiddenDirs.Select(dir => (dir.RelativePath, Name: Out.Field(dir.RelativePath) + "/")))
            .OrderBy(path => path.RelativePath, StringComparer.Ordinal)
            .Select(path => path.Name)
            .ToList();
        if (hidden.Count > 0)
            parts.Add("hidden from the agent: " + JoinCapped(hidden));
        if (plan.Replacements.Count > 0)
        {
            var replaced = plan.Replacements.Select(replacement => Out.Field(replacement.Target.RelativePath));
            parts.Add("replaced: " + JoinCapped([.. replaced]));
        }
        return parts.Count == 0 ? null : string.Join("; ", parts);
    }

    /// <summary>Lists the warnings about what a session hides and replaces.</summary>
    /// <param name="plan">What the session hides and replaces.</param>
    /// <param name="penDir">The project's .pen dir, where the case probe goes.</param>
    /// <param name="docker">Whether it's a docker session.</param>
    /// <param name="isMacOS">Whether the host is macOS: <see cref="OperatingSystem.IsMacOS"/> in production.</param>
    /// <param name="probe">
    /// Whether a directory may fold case: <see cref="GitProtection.RepoGuard.MayFoldCase(string)"/> in production.
    /// </param>
    /// <returns>
    /// <see cref="CaseWarning"/> if a file is hidden or replaced and <paramref name="penDir"/> may fold case, or
    /// else <see cref="UnicodeWarning"/> on macOS for a non-ASCII name, then
    /// the plan's own warnings, then <see cref="DockerWarning"/> in a docker session that hides or replaces anything.
    /// </returns>
    /// <remarks>
    /// The probe runs only when a file is hidden or replaced: another spelling of a hidden directory's name reaches
    /// the same mount. .pen is Pen's own dir, which always exists by now, so probing it adds nothing to the user's
    /// tree.
    /// </remarks>
    public static List<string> ListWarnings(MaskPlan plan, string penDir, bool docker, bool isMacOS,
        Func<string, bool> probe)
    {
        var warnings = new List<string>();
        var folds = plan.HiddenFiles.Count + plan.Replacements.Count > 0 && probe(penDir);
        if (folds)
        {
            warnings.Add(CaseWarning);
        }
        else if (isMacOS && plan.HiddenFiles.Select(file => file.RelativePath)
            .Concat(plan.Replacements.Select(replacement => replacement.Target.RelativePath))
            .Any(path => path.Any(character => character > 127)))
        {
            warnings.Add(UnicodeWarning);
        }
        warnings.AddRange(plan.Warnings);
        if (docker && !plan.IsEmpty)
            warnings.Add(DockerWarning);
        return warnings;
    }

    /// <summary>Joins up to <see cref="MaxNamed"/> names with commas, then "and N more" for the rest.</summary>
    private static string JoinCapped(IReadOnlyList<string> names)
    {
        if (names.Count <= MaxNamed)
            return string.Join(", ", names);
        return string.Join(", ", names.Take(MaxNamed)) + $" and {names.Count - MaxNamed} more";
    }
}
