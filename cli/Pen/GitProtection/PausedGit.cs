using System.Text;
using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// Finds rebase, am, cherry-pick and revert state left in a git dir, which the sandbox can write, and words the
/// warning about it.
/// </summary>
/// <remarks>
/// <para>
/// Continuing a paused operation runs commands on this machine, but it may be the user's own, so it's reported and
/// left in place, apart from what <see cref="PromptHazardScan"/> finds. Reads files directly and never runs git.
/// Every file is hostile input: anything Pen can't read is reported as such, so a planted file can't hang this or
/// silence it.
/// </para>
/// <para>
/// What git does with these files, checked against git 2.56: an exec line runs only from
/// rebase-merge/git-rebase-todo. The todo's other copies and sequencer/todo (where git refuses anything but pick or
/// revert) run nothing, but they're listed too, as nothing here depends on which version of git reads them. Any
/// merge strategy other than the stock ones is run as a program named git-merge-&lt;strategy&gt;, and "x/../evil"
/// reaches a planted file through a directory named git-merge-x, so rebase-merge/strategy and sequencer/opts count
/// too. rebase-apply runs nothing itself, but rebase-apply/apply-opt passes options to git apply, and
/// --build-fake-ancestor writes a file anywhere.
/// </para>
/// <para>
/// One thing can't wait for the user to look: git's own git-prompt.sh puts the first line of rebase-merge/msgnum and
/// end, and rebase-apply/next and last, into PS1 unprotected. Where the shell expands PS1 (bash's PROMPT_COMMAND
/// mode, zsh's precmd with PROMPT_SUBST), "$(cmd)" there runs at the next prompt, with no git command typed. So
/// <see cref="PromptHazardScan"/> finds what git wouldn't have written for the caller to quarantine. git-prompt.sh
/// protects head-name, HEAD and describe output from expansion, and only tests for the rest (MERGE_HEAD, BISECT_LOG,
/// sequencer/todo): those replays run nothing. HEAD is left alone in any case: a branch may legally be named
/// "$(cmd)", so its content can't tell a planted one from the user's.
/// </para>
/// </remarks>
internal static class PausedGit
{
    /// <summary>The most lines listed from one file.</summary>
    internal const int MaxLines = RepoGuard.MaxListed;

    /// <summary>The most characters shown of one line.</summary>
    /// <remarks>So a line can't flood the terminal or push its end out of sight unnoticed.</remarks>
    internal const int MaxLineLength = 200;

    private static readonly HashSet<string> StockStrategies = new(StringComparer.Ordinal)
    {
        "ort", "recursive", "resolve", "octopus", "ours", "subtree"
    };

    /// <summary>
    /// Finds the paused operations in <paramref name="gitDir"/>, its worktree entries, and its module repos and their
    /// worktree entries.
    /// </summary>
    /// <returns>
    /// What it found, in order; if it couldn't finish, last an entry for the git dir that says so. Never throws.
    /// </returns>
    public static List<PausedOp> Find(string gitDir) => new PausedOpSearch(gitDir).Run();

    /// <summary>
    /// Tells whether a state file holds git's msgnum, end, next or last: a decimal number and a newline.
    /// </summary>
    /// <remarks>Digits and line ends alone can't expand, whatever else is odd about them.</remarks>
    internal static bool IsCount(byte[] content) =>
        content.All(b => b is (>= (byte)'0' and <= (byte)'9') or (byte)'\n' or (byte)'\r');

    /// <summary>
    /// Tells whether a state file holds what git writes as head-name: "detached HEAD", or the ref being rebased with
    /// a newline. Empty passes too.
    /// </summary>
    /// <remarks>
    /// Empty can't expand. A ref name may hold "$(cmd)", which the prompt protects from expansion, so this can only
    /// rule out what git couldn't have written.
    /// </remarks>
    internal static bool IsHeadName(byte[] content)
    {
        var value = Encoding.Latin1.GetString(content).TrimEnd('\n', '\r');
        return value.Length == 0
            || value == "detached HEAD"
            || (value.StartsWith("refs/", StringComparison.Ordinal) && IsRefName(value));
    }

    /// <summary>Tells whether git's check_refname_format with no flags accepts a name, on one char per byte.</summary>
    internal static bool IsRefName(string name)
    {
        if (name == "@" || name.EndsWith('.'))
            return false;
        var parts = name.Split('/');
        return parts.Length >= 2 && parts.All(IsRefNameComponent);
    }

    private static bool IsRefNameComponent(string part)
    {
        if (part.Length == 0 || part[0] == '.' || part.EndsWith(".lock", StringComparison.Ordinal))
            return false;
        if (part.Contains("..", StringComparison.Ordinal) || part.Contains("@{", StringComparison.Ordinal))
            return false;
        return !part.Any(c => c is < ' ' or '\x7f' or ' ' or '*' or ':' or '?' or '[' or '\\' or '^' or '~');
    }

    private static string FormatNotRunHeading(string file) =>
        $"exec lines in {file} (git doesn't run them from this file):";

    /// <summary>Reads the exec lines in a todo file into a note.</summary>
    /// <returns>The note, or null if the file isn't there or has none.</returns>
    private static PausedNote? ReadExecNote(string dir, string file, string heading)
    {
        var (text, problem) = ReadText(Path.Combine(dir, file));
        if (problem != null)
            return new($"couldn't read {file}, so pen can't say what it would run: {problem}", []);
        return text == null ? null : CreateLinesNote(heading, ExecLines(text));
    }

    /// <summary>Reads rebase-merge/strategy into a note if it names a strategy other than git's own.</summary>
    private static PausedNote? ReadStrategyFileNote(string dir)
    {
        var (text, problem) = ReadText(Path.Combine(dir, "strategy"));
        if (problem != null)
            return new($"couldn't read strategy, which can name a program for git to run: {problem}", []);
        var value = text?.TrimEnd('\n', '\r');
        if (string.IsNullOrEmpty(value) || StockStrategies.Contains(value))
            return null;
        return CreateLinesNote(FormatStrategyHeading("strategy"), [value]);
    }

    /// <summary>Reads the lines of sequencer/opts that could set a strategy other than git's own into a note.</summary>
    private static PausedNote? ReadOptsNote(string dir)
    {
        var (text, problem) = ReadText(Path.Combine(dir, "opts"));
        if (problem != null)
            return new($"couldn't read opts, which can name a program for git to run: {problem}", []);
        return text == null ? null : CreateLinesNote(FormatStrategyHeading("opts"), StrategyLines(text));
    }

    private static string FormatStrategyHeading(string file) => $"a merge strategy in {file} that isn't " +
        "one of git's own: git runs it as a program on this machine when the operation continues:";

    /// <summary>Reads rebase-apply/apply-opt into a note if it holds options.</summary>
    private static PausedNote? ReadApplyOptNote(string dir)
    {
        var (text, problem) = ReadText(Path.Combine(dir, "apply-opt"));
        if (problem != null)
            return new($"couldn't read apply-opt, which holds options for git apply: {problem}", []);
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
            return null;
        return CreateLinesNote("options for git apply in apply-opt, one of " +
            "which can overwrite any file (--build-fake-ancestor):", [value]);
    }

    /// <summary>Creates a note listing the first lines, each made safe to print.</summary>
    /// <returns>The note, or null if there are no lines.</returns>
    private static PausedNote? CreateLinesNote(string heading, List<string> lines) =>
        lines.Count == 0 ? null
            : new(heading, lines.Take(MaxLines).Select(FormatLine).ToList(), Math.Max(0, lines.Count - MaxLines));

    /// <summary>Formats a line of hostile text as one printable line of bounded length.</summary>
    internal static string FormatLine(string line) =>
        line.Length <= MaxLineLength ? Out.Field(line)
            : $"{Out.Field(line[..MaxLineLength])} … ({line.Length - MaxLineLength} more characters)";

    /// <summary>Reads a file's text.</summary>
    /// <returns>
    /// The text; (null, null) if it isn't there; or a problem if it can't be read.
    /// </returns>
    /// <remarks>
    /// As bytes, since a byte-order mark must not change how the lines are seen, and git splits and parses bytes too.
    /// </remarks>
    private static (string? Text, string? Problem) ReadText(string path)
    {
        try
        {
            if (RepoGuard.IsLink(path))
                return (null, "it's a symlink");
            var bytes = Native.ReadProtected(path)?.Bytes;
            return bytes == null
                ? (null, "it isn't a regular file of a reasonable size")
                : (Encoding.UTF8.GetString(bytes), null);
        }
        catch (FileNotFoundException)
        {
            return (null, null);
        }
        catch (DirectoryNotFoundException)
        {
            return (null, null);
        }
        catch (Exception exception)
        {
            return (null, exception is UnauthorizedAccessException ? "permission denied" : Out.Describe(exception));
        }
    }

    /// <summary>Finds the lines git parses as exec, without their leading blanks.</summary>
    /// <remarks>
    /// "exec" or "x", case-sensitive, after any spaces and tabs, then a space, tab, CR or the line's end. Comments
    /// need no case of their own, whatever core.commentChar says, since git tries the commands first. git stops at a
    /// NUL and this doesn't, which only lists more.
    /// </remarks>
    internal static List<string> ExecLines(string text)
    {
        var found = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart(' ', '\t');
            if (line.EndsWith('\r'))
                line = line[..^1];
            if (IsCommand(line, "exec") || IsCommand(line, "x"))
                found.Add(line);
        }
        return found;
    }

    private static bool IsCommand(string line, string name) =>
        line.StartsWith(name, StringComparison.Ordinal)
        && (line.Length == name.Length || line[name.Length] is ' ' or '\t' or '\r' or '\0');

    /// <summary>Finds the lines of sequencer/opts that could set a strategy other than git's own.</summary>
    /// <remarks>
    /// Only the plain form git writes passes. Anything else is listed, comments and other spellings included, rather
    /// than parsed the way git might not. A key can't span lines, so each mention is on the line shown.
    /// </remarks>
    internal static List<string> StrategyLines(string text)
    {
        var found = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (MentionsStrategy(line) && !IsStockStrategyLine(line))
                found.Add(line);
        }
        return found;
    }

    private static bool MentionsStrategy(string line)
    {
        const string word = "strategy";
        for (var at = line.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            at >= 0;
            at = line.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + word.Length;
            if (end == line.Length || line[end] != '-')
                return true; // strategy-option only passes arguments to it
        }
        return false;
    }

    private static bool IsStockStrategyLine(string line)
    {
        var equals = line.IndexOf('=');
        return equals > 0 && line[..equals].Trim().Equals("strategy", StringComparison.OrdinalIgnoreCase)
            && StockStrategies.Contains(line[(equals + 1)..].Trim());
    }

    /// <summary>Words the warning for what <see cref="Find"/> returned.</summary>
    /// <param name="dir">The project dir; paths are relative to it where they can be.</param>
    /// <param name="paused">The paused operations.</param>
    public static string Warning(string dir, IReadOnlyList<PausedOp> paused)
    {
        var text = new StringBuilder();
        text.AppendLine("pen: WARNING: git has paused operations here. They may be yours, but the " +
            "sandbox could write to them, and continuing one runs what it says on this machine:");
        foreach (var operation in paused.Take(RepoGuard.MaxListed))
            AppendOperation(text, dir, operation);
        var rest = paused.Skip(RepoGuard.MaxListed).ToList();
        if (rest.Count > 0)
        {
            text.Append("  … and ").Append(rest.Count).Append(" more");
            var withNotes = rest.Count(operation => operation.Notes.Count > 0);
            if (withNotes > 0)
                text.Append(", ").Append(withNotes).Append(" of them with something to look at");
            text.AppendLine();
        }
        text.AppendLine("pen: Inspect each one before git rebase --continue " +
            "or --skip (or the same for git am, cherry-pick or revert).");
        text.Append("pen: A short entry isn't a clean bill: pen parses only the todo files, the merge strategy and " +
            "git apply's options, and can't vouch for what a pick brings in or the text reword opens in your editor.");
        return text.ToString();
    }

    /// <summary>Appends one paused operation and its notes to the warning.</summary>
    private static void AppendOperation(StringBuilder text, string dir, PausedOp operation)
    {
        text.Append("  ").Append(RepoGuard.FormatPath(dir, operation.Path)).Append(": ").AppendLine(operation.Kind);
        foreach (var note in operation.Notes)
        {
            text.Append("      ").AppendLine(note.Text);
            foreach (var line in note.Lines)
                text.Append("        ").AppendLine(line);
            if (note.More > 0)
                text.Append("        … and ").Append(note.More).AppendLine(" more");
        }
    }

    /// <summary>One search of a git dir for paused operations, which keeps what it finds as it goes.</summary>
    /// <param name="gitDir">The git dir.</param>
    private sealed class PausedOpSearch(string gitDir)
    {
        private readonly List<PausedOp> _found = [];

        /// <summary>Searches the git dir, its worktree entries, its module repos and theirs. Call it once.</summary>
        /// <returns>What it found; if it stopped, what it found before, then an entry that says so.</returns>
        public List<PausedOp> Run()
        {
            try
            {
                GitDirWalk.Visit(gitDir, Visit, AddUnreachable, tryOpen: null);
            }
            catch (Exception exception)
            {
                _found.Add(new(gitDir, "a git dir pen couldn't finish looking through",
                    [new($"it may hold more paused state: {Out.Describe(exception)}", [])]));
            }
            return _found;
        }

        private void AddUnreachable(string path) =>
            _found.Add(new(path, "a worktree's git files pen couldn't look in", [new("it may hold paused state", [])]));

        /// <summary>Looks for each kind of paused operation in one git dir or worktree entry.</summary>
        private void Visit(string dir)
        {
            AddIfPresent(Path.Combine(dir, "rebase-merge"), "a paused rebase", state => [
                ReadExecNote(state, "git-rebase-todo", "exec lines in git-rebase-todo, " +
                    "which run on this machine at git rebase --continue or --skip:"),
                ReadExecNote(state, "git-rebase-todo.backup", FormatNotRunHeading("git-rebase-todo.backup")),
                ReadExecNote(state, "done", FormatNotRunHeading("done")),
                ReadStrategyFileNote(state),
            ]);
            AddIfPresent(Path.Combine(dir, "rebase-apply"), "a paused rebase or git am",
                state => [ReadApplyOptNote(state)]);
            AddIfPresent(Path.Combine(dir, "sequencer"), "a paused cherry-pick or revert", state => [
                ReadExecNote(state, "todo", FormatNotRunHeading("todo")),
                ReadOptsNote(state),
            ]);
        }

        /// <summary>Adds the state directory <paramref name="dir"/> as a paused operation, if it's there.</summary>
        /// <param name="dir">The state directory.</param>
        /// <param name="kind">What kind of operation it is, as words.</param>
        /// <param name="readNotes">
        /// Reads its notes, with null where there's nothing to say; called only if it's a real directory.
        /// </param>
        private void AddIfPresent(string dir, string kind, Func<string, PausedNote?[]> readNotes)
        {
            if (!RepoGuard.Exists(dir))
                return;
            if (RepoGuard.IsLink(dir))
                _found.Add(new(dir, kind, [new("couldn't read the todo in it: it's a symlink", [])]));
            else if (!Directory.Exists(dir))
                _found.Add(new(dir, kind, [new("couldn't read the todo in it: it isn't a directory", [])]));
            else
                _found.Add(new(dir, kind, readNotes(dir).OfType<PausedNote>().ToList()));
        }
    }
}

/// <summary>A detail of a paused operation.</summary>
/// <param name="Text">The heading.</param>
/// <param name="Lines">The lines under it, already safe to print.</param>
/// <param name="More">How many more lines there were than are listed.</param>
internal sealed record PausedNote(string Text, List<string> Lines, int More = 0);

/// <summary>State for a paused operation in a git dir, as found.</summary>
/// <param name="Path">The state directory.</param>
/// <param name="Kind">What kind of operation it is, as words.</param>
/// <param name="Notes">What in it is worth a look.</param>
internal sealed record PausedOp(string Path, string Kind, List<PausedNote> Notes);
