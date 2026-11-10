using System.Text;

namespace Pen;

/// <summary>A detail of a paused operation: a heading, and lines already safe to print.</summary>
internal sealed record PausedNote(string Text, List<string> Lines, int More = 0);

/// <summary>State for a paused operation in a git dir, as found: Path is the state directory.</summary>
internal sealed record PausedOp(string Path, string Kind, List<PausedNote> Notes);

/// <summary>
/// A file or state directory a shell prompt would run. StateDir is the directory
/// holding it, which can go aside in its place, if it's a file.
/// </summary>
internal sealed record PromptHazard(string Path, string? StateDir, string Why);

/// <summary>
/// Finds rebase, am, cherry-pick and revert state left in a git dir, which the
/// sandbox can write. Continuing it runs commands on this machine, but it may be
/// the user's own, so it's reported and left in place, apart from what
/// <see cref="PromptHazards"/> finds. Reads files directly
/// and never runs git. Every file is hostile input: anything Pen can't read is
/// reported as such, so a planted file can't hang this or silence it.
///
/// What git does with these files, checked against git 2.56: an exec line runs
/// only from rebase-merge/git-rebase-todo. The todo's other copies and
/// sequencer/todo (where git refuses anything but pick or revert) run nothing,
/// but they're listed too, as nothing here depends on which version of git
/// reads them. Any merge strategy other than the stock ones is run as a program
/// named git-merge-&lt;strategy&gt;, and "x/../evil" reaches a planted file through a
/// directory named git-merge-x, so rebase-merge/strategy and sequencer/opts
/// count too. rebase-apply runs nothing itself, but rebase-apply/apply-opt
/// passes options to git apply, and --build-fake-ancestor writes a file anywhere.
///
/// One thing can't wait for the user to look: git's own git-prompt.sh puts the
/// first line of rebase-merge/msgnum and end, and rebase-apply/next and last,
/// into PS1 unprotected. Where the shell expands PS1 (bash's PROMPT_COMMAND mode,
/// zsh's precmd with PROMPT_SUBST), "$(cmd)" there runs at the next prompt, with
/// no git command typed. So <see cref="PromptHazards"/> finds what git wouldn't
/// have written for the caller to quarantine. git-prompt.sh protects head-name,
/// HEAD and describe output from expansion, and only tests for the rest
/// (MERGE_HEAD, BISECT_LOG, sequencer/todo): those replays run nothing. HEAD is
/// left alone in any case: a branch may legally be named "$(cmd)", so its content
/// can't tell a planted one from the user's.
/// </summary>
internal static class PausedGit
{
    /// <summary>The most lines listed from one file.</summary>
    internal const int MaxLines = RepoGuard.MaxListed;

    /// <summary>The most characters shown of one line, so a line can't flood the terminal or push its end out of sight unnoticed.</summary>
    internal const int MaxLineLength = 200;

    private static readonly HashSet<string> StockStrategies = new(StringComparer.Ordinal) { "ort", "recursive", "resolve", "octopus", "ours", "subtree" };

    /// <summary>The paused operations in <paramref name="gitDir"/>, its worktree entries, and its module repos and their worktree entries. Never throws.</summary>
    public static List<PausedOp> Find(string gitDir)
    {
        var found = new List<PausedOp>();
        try
        {
            if (!Directory.Exists(gitDir)) return found;
            LookWithWorktrees(gitDir, found);
            foreach (var repo in RepoScan.FindModuleRepos(gitDir))
                if (!RepoGuard.IsLink(repo)) LookWithWorktrees(repo, found);
        }
        catch (Exception e)
        {
            found.Add(new(gitDir, "a git dir pen couldn't finish looking through", [new($"it may hold more paused state: {Out.Describe(e)}", [])]));
        }
        return found;
    }

    /// <summary>
    /// The files and state directories to quarantine because a shell prompt would
    /// run them, each with why, in the same git dirs as <see cref="Find"/>. A real
    /// rebase's own files always pass: see <see cref="IsCount"/> and <see cref="IsHeadName"/>.
    /// A planted symlink counts whatever it points to, and a state directory Pen
    /// can't enter counts too, since the prompt reads it once the user fixes its mode.
    /// Every known name is tried directly, so a directory that can be searched but
    /// not listed (git and the prompt can read through one too) hides nothing. A
    /// worktree entry or worktrees dir still closed after <paramref name="tryOpen"/>
    /// goes in <paramref name="unreachable"/>: what's in it can't be vouched for.
    /// <para>
    /// With <paramref name="atStart"/> (a session's end), so does each config.worktree
    /// it doesn't vouch for, wherever git would read one. git takes settings from it
    /// that run programs, as soon as anything runs git in that worktree, the prompt
    /// included. Whether extensions.worktreeConfig is on now doesn't matter: one left
    /// there takes effect whenever it's turned on. So does a symlink in place
    /// of worktrees/ or of an entry in it, which git follows to a config.worktree nothing
    /// checks, and which was refused at the start so can't have been there then.
    /// </para>
    /// </summary>
    public static List<PromptHazard> PromptHazards(string gitDir, Func<string, bool>? tryOpen, List<string> unreachable, ProtectedManifest? atStart = null)
    {
        var found = new List<PromptHazard>();
        if (!Directory.Exists(gitDir)) return found;
        void Visit(string dir)
        {
            LookForHazards(Path.Combine(dir, "rebase-merge"), ["msgnum", "end"], found);
            LookForHazards(Path.Combine(dir, "rebase-apply"), ["next", "last"], found);
            if (atStart == null) return;
            var config = Path.Combine(dir, "config.worktree");
            if (RepoGuard.Exists(config) && !atStart.Vouches(config))
                found.Add(new(config, null, "a config.worktree that wasn't there at the session's start, or that another session could have made: git reads it as config, so a setting in it can run code on this machine. git worktree add copies one into a new worktree, so it may be harmless"));
        }
        void CouldntLook(string path)
        {
            if (!RepoGuard.IsLink(path)) unreachable.Add(path); // git won't follow a symlink here to a project's worktree: a warning covers it
            else if (atStart != null) found.Add(new(path, null, "a symlink where git keeps a worktree's files: it points git at a config.worktree in another directory"));
        }
        WithWorktrees(gitDir, Visit, CouldntLook, tryOpen);
        foreach (var repo in RepoScan.FindModuleRepos(gitDir))
            if (!RepoGuard.IsLink(repo)) WithWorktrees(repo, Visit, CouldntLook, tryOpen);
        return found;
    }

    private static void LookForHazards(string stateDir, string[] counts, List<PromptHazard> found)
    {
        const string prompt = "git's shell prompt (git-prompt.sh) can run what's in it, with no git command typed";
        if (RepoGuard.IsLink(stateDir))
        {
            found.Add(new(stateDir, null, $"a symlink where git keeps a paused rebase: {prompt}"));
            return;
        }
        if (!Directory.Exists(stateDir)) return;
        if (!Native.CanEnter(stateDir))
        {
            found.Add(new(stateDir, null, $"a paused rebase's directory that can't be read, so pen can't check it: {prompt}, once its permissions are fixed"));
            return;
        }
        foreach (var name in counts)
        {
            var path = Path.Combine(stateDir, name);
            if (!HoldsWhatGitWrites(path, IsCount))
                found.Add(new(path, stateDir, $"git only writes a number here: {prompt}"));
        }
        var head = Path.Combine(stateDir, "head-name");
        if (!HoldsWhatGitWrites(head, IsHeadName))
            found.Add(new(head, stateDir, $"git only writes a ref name here: {prompt}"));
    }

    /// <summary>Whether a state file is missing or holds what git writes. A file that can't be vouched for doesn't.</summary>
    private static bool HoldsWhatGitWrites(string path, Func<byte[], bool> valid)
    {
        try
        {
            if (RepoGuard.IsLink(path)) return false;
            var bytes = Native.ReadProtected(path)?.Bytes;
            return bytes != null && valid(bytes);
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>git's msgnum, end, next or last: a decimal number and a newline. Digits and line ends alone can't expand, whatever else is odd about them.</summary>
    internal static bool IsCount(byte[] content) => content.All(b => b is (>= (byte)'0' and <= (byte)'9') or (byte)'\n' or (byte)'\r');

    /// <summary>
    /// git's head-name: "detached HEAD", or the ref being rebased with a newline.
    /// Empty can't expand. A ref name may hold "$(cmd)", which the prompt protects
    /// from expansion, so this can only rule out what git couldn't have written.
    /// </summary>
    internal static bool IsHeadName(byte[] content)
    {
        var value = Encoding.Latin1.GetString(content).TrimEnd('\n', '\r');
        return value.Length == 0 || value == "detached HEAD" || (value.StartsWith("refs/", StringComparison.Ordinal) && IsRefName(value));
    }

    /// <summary>git's check_refname_format with no flags, on one char per byte.</summary>
    internal static bool IsRefName(string name)
    {
        if (name == "@" || name.EndsWith('.')) return false;
        var parts = name.Split('/');
        return parts.Length >= 2 && parts.All(IsRefNameComponent);
    }

    private static bool IsRefNameComponent(string part)
    {
        if (part.Length == 0 || part[0] == '.' || part.EndsWith(".lock", StringComparison.Ordinal)) return false;
        if (part.Contains("..", StringComparison.Ordinal) || part.Contains("@{", StringComparison.Ordinal)) return false;
        return !part.Any(c => c is < ' ' or '\x7f' or ' ' or '*' or ':' or '?' or '[' or '\\' or '^' or '~');
    }

    private static void LookWithWorktrees(string gitDir, List<PausedOp> found) =>
        WithWorktrees(gitDir, dir => Look(dir, found), path => found.Add(CouldntLook(path)), tryOpen: null);

    /// <summary>Visits a git dir and its worktree entries, naming what it can't look in.</summary>
    private static void WithWorktrees(string gitDir, Action<string> visit, Action<string> couldntLook, Func<string, bool>? tryOpen)
    {
        visit(gitDir);
        var worktrees = Path.Combine(gitDir, "worktrees");
        if (!RepoGuard.Exists(worktrees)) return;
        if (!RepoGuard.IsLink(worktrees) && Directory.Exists(worktrees) && !Native.CanList(worktrees)) tryOpen?.Invoke(worktrees);
        if (RepoGuard.IsLink(worktrees) || !Directory.Exists(worktrees) || !Native.CanList(worktrees))
        {
            couldntLook(worktrees);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(worktrees).Order(StringComparer.Ordinal))
        {
            // git keeps only directories here. One it can't enter is what it would
            // read once the user fixes the mode, so it can't pass as empty.
            if (!RepoGuard.IsLink(entry) && Directory.Exists(entry) && !Native.CanEnter(entry)) tryOpen?.Invoke(entry);
            if (RepoGuard.IsLink(entry) || !Path.Exists(entry) || (Directory.Exists(entry) && !Native.CanEnter(entry)))
                couldntLook(entry);
            else if (Directory.Exists(entry))
                visit(entry);
        }
    }

    private static PausedOp CouldntLook(string path) =>
        new(path, "a worktree's git files pen couldn't look in", [new("it may hold paused state", [])]);

    private static void Look(string gitDir, List<PausedOp> found)
    {
        LookAt(Path.Combine(gitDir, "rebase-merge"), "a paused rebase", found, dir => [
            ExecNote(dir, "git-rebase-todo", "exec lines in git-rebase-todo, which run on this machine at git rebase --continue or --skip:"),
            ExecNote(dir, "git-rebase-todo.backup", NotRun("git-rebase-todo.backup")),
            ExecNote(dir, "done", NotRun("done")),
            StrategyFileNote(dir),
        ]);
        LookAt(Path.Combine(gitDir, "rebase-apply"), "a paused rebase or git am", found, dir => [ApplyOptNote(dir)]);
        LookAt(Path.Combine(gitDir, "sequencer"), "a paused cherry-pick or revert", found, dir => [
            ExecNote(dir, "todo", NotRun("todo")),
            OptsNote(dir),
        ]);
    }

    private static string NotRun(string file) => $"exec lines in {file} (git doesn't run them from this file):";

    private static void LookAt(string dir, string kind, List<PausedOp> found, Func<string, PausedNote?[]> notes)
    {
        if (!RepoGuard.Exists(dir)) return;
        if (RepoGuard.IsLink(dir))
            found.Add(new(dir, kind, [new("couldn't read the todo in it: it's a symlink", [])]));
        else if (!Directory.Exists(dir))
            found.Add(new(dir, kind, [new("couldn't read the todo in it: it isn't a directory", [])]));
        else
            found.Add(new(dir, kind, notes(dir).OfType<PausedNote>().ToList()));
    }

    private static PausedNote? ExecNote(string dir, string file, string heading)
    {
        var (text, problem) = Read(Path.Combine(dir, file));
        if (problem != null) return new($"couldn't read {file}, so pen can't say what it would run: {problem}", []);
        return text == null ? null : LinesNote(heading, ExecLines(text));
    }

    private static PausedNote? StrategyFileNote(string dir)
    {
        var (text, problem) = Read(Path.Combine(dir, "strategy"));
        if (problem != null) return new($"couldn't read strategy, which can name a program for git to run: {problem}", []);
        var value = text?.TrimEnd('\n', '\r');
        return string.IsNullOrEmpty(value) || StockStrategies.Contains(value) ? null
            : LinesNote(StrategyHeading("strategy"), [value]);
    }

    private static PausedNote? OptsNote(string dir)
    {
        var (text, problem) = Read(Path.Combine(dir, "opts"));
        if (problem != null) return new($"couldn't read opts, which can name a program for git to run: {problem}", []);
        return text == null ? null : LinesNote(StrategyHeading("opts"), StrategyLines(text));
    }

    private static string StrategyHeading(string file) => $"a merge strategy in {file} that isn't one of git's own: git runs it as a program on this machine when the operation continues:";

    private static PausedNote? ApplyOptNote(string dir)
    {
        var (text, problem) = Read(Path.Combine(dir, "apply-opt"));
        if (problem != null) return new($"couldn't read apply-opt, which holds options for git apply: {problem}", []);
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) ? null
            : LinesNote("options for git apply in apply-opt, one of which can overwrite any file (--build-fake-ancestor):", [value]);
    }

    private static PausedNote? LinesNote(string heading, List<string> lines) =>
        lines.Count == 0 ? null
            : new(heading, lines.Take(MaxLines).Select(Shown).ToList(), Math.Max(0, lines.Count - MaxLines));

    /// <summary>A line of hostile text as one printable line of bounded length.</summary>
    internal static string Shown(string line) =>
        line.Length <= MaxLineLength ? Out.Field(line)
            : $"{Out.Field(line[..MaxLineLength])} … ({line.Length - MaxLineLength} more characters)";

    /// <summary>
    /// A file's text; (null, null) if it isn't there; a problem if it can't be
    /// read. As bytes, since a byte-order mark must not change how the lines are
    /// seen, and git splits and parses bytes too.
    /// </summary>
    private static (string? Text, string? Problem) Read(string path)
    {
        try
        {
            if (RepoGuard.IsLink(path)) return (null, "it's a symlink");
            var bytes = Native.ReadProtected(path)?.Bytes;
            return bytes == null ? (null, "it isn't a regular file of a reasonable size") : (Encoding.UTF8.GetString(bytes), null);
        }
        catch (FileNotFoundException) { return (null, null); }
        catch (DirectoryNotFoundException) { return (null, null); }
        catch (Exception e) { return (null, e is UnauthorizedAccessException ? "permission denied" : Out.Describe(e)); }
    }

    /// <summary>
    /// The lines git parses as exec, without their leading blanks: "exec" or "x",
    /// case-sensitive, after any spaces and tabs, then a space, tab, CR or the
    /// line's end. Comments need no case of their own, whatever core.commentChar
    /// says, since git tries the commands first. git stops at a NUL and this
    /// doesn't, which only lists more.
    /// </summary>
    internal static List<string> ExecLines(string text)
    {
        var found = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimStart(' ', '\t');
            if (line.EndsWith('\r')) line = line[..^1];
            if (IsCommand(line, "exec") || IsCommand(line, "x")) found.Add(line);
        }
        return found;
    }

    private static bool IsCommand(string line, string name) =>
        line.StartsWith(name, StringComparison.Ordinal)
        && (line.Length == name.Length || line[name.Length] is ' ' or '\t' or '\r' or '\0');

    /// <summary>
    /// The lines of sequencer/opts that could set a strategy other than git's own.
    /// Only the plain form git writes passes. Anything else is listed, comments
    /// and other spellings included, rather than parsed the way git might not.
    /// A key can't span lines, so each mention is on the line shown.
    /// </summary>
    internal static List<string> StrategyLines(string text)
    {
        var found = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (MentionsStrategy(line) && !IsStockStrategyLine(line)) found.Add(line);
        }
        return found;
    }

    private static bool MentionsStrategy(string line)
    {
        const string word = "strategy";
        for (int at = line.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = line.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + word.Length;
            if (end == line.Length || line[end] != '-') return true; // strategy-option only passes arguments to it
        }
        return false;
    }

    private static bool IsStockStrategyLine(string line)
    {
        var eq = line.IndexOf('=');
        return eq > 0 && line[..eq].Trim().Equals("strategy", StringComparison.OrdinalIgnoreCase)
            && StockStrategies.Contains(line[(eq + 1)..].Trim());
    }

    /// <summary>The warning for what <see cref="Find"/> returned. Paths are relative to <paramref name="dir"/> where they can be.</summary>
    public static string Warning(string dir, IReadOnlyList<PausedOp> paused)
    {
        var sb = new StringBuilder();
        sb.AppendLine("pen: WARNING: git has paused operations here. They may be yours, but the sandbox could write to them, and continuing one runs what it says on this machine:");
        foreach (var op in paused.Take(RepoGuard.MaxListed))
        {
            sb.Append("  ").Append(RepoGuard.Rel(dir, op.Path)).Append(": ").AppendLine(op.Kind);
            foreach (var note in op.Notes)
            {
                sb.Append("      ").AppendLine(note.Text);
                foreach (var line in note.Lines) sb.Append("        ").AppendLine(line);
                if (note.More > 0) sb.Append("        … and ").Append(note.More).AppendLine(" more");
            }
        }
        var rest = paused.Skip(RepoGuard.MaxListed).ToList();
        if (rest.Count > 0)
        {
            sb.Append("  … and ").Append(rest.Count).Append(" more");
            var withNotes = rest.Count(op => op.Notes.Count > 0);
            if (withNotes > 0) sb.Append(", ").Append(withNotes).Append(" of them with something to look at");
            sb.AppendLine();
        }
        sb.AppendLine("pen: Inspect each one before git rebase --continue or --skip (or the same for git am, cherry-pick or revert).");
        sb.Append("pen: A short entry isn't a clean bill: pen parses only the todo files, the merge strategy and git apply's options, and can't vouch for what a pick brings in or the text reword opens in your editor.");
        return sb.ToString();
    }
}
