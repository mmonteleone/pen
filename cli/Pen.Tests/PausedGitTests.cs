using System.Diagnostics;

namespace Pen.Tests;

[Collection(nameof(LockAndProcessTests))]
public class PausedGitTests
{
    private const string TodoFile = "git-rebase-todo";

    /// <summary>A real paused rebase: its todo was replaced with a break, which stops it there.</summary>
    private static string PausedRebase(TempDir t)
    {
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "commit", "-q", "--allow-empty", "-m", "two");
        Git.Run(repo, "-c", "sequence.editor=sh -c 'printf \"break\\n\" >\"$1\"' -", "rebase", "-q", "-i", "--keep-empty", "HEAD~1");
        Assert.True(Directory.Exists(RebaseDir(repo)));
        return repo;
    }

    private static string RebaseDir(string repo) => Path.Combine(repo, ".git", "rebase-merge");

    private static string Warning(string repo)
    {
        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);
        Assert.Empty(check.Moved);
        Assert.Null(check.Failure);
        return PausedGit.Warning(repo, check.Paused);
    }

    private static IReadOnlyList<PausedOp> Paused(string repo) => GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Paused;

    private static void MkFifo(string path)
    {
        var r = Process.RunAndCaptureText(new ProcessStartInfo("mkfifo", [path]) { RedirectStandardOutput = true, RedirectStandardError = true });
        Assert.Equal(0, r.ExitStatus.ExitCode);
    }

    /// <summary>Fails instead of hanging the suite if the code under test blocks opening a FIFO.</summary>
    private static T Within10s<T>(Func<T> f)
    {
        var task = Task.Factory.StartNew(f, TaskCreationOptions.LongRunning);
        Assert.True(task.Wait(TimeSpan.FromSeconds(10)), "blocked reading a planted file");
        return task.Result;
    }

    private static string CapturingStderr(Action action)
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { action(); }
        finally { Console.SetError(stderr); }
        return captured.ToString();
    }

    [Fact]
    public void TheReplaysExecLineIsListed_AndStillRunsBecausePenMovedNothing()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        var marker = Path.Combine(t.Path, "ran");
        var todo = Path.Combine(RebaseDir(repo), TodoFile);
        File.AppendAllText(todo, $"exec touch '{marker}'\n");
        var before = File.ReadAllText(todo);

        var warning = Warning(repo);

        Assert.Contains("WARNING", warning);
        Assert.Contains(".git/rebase-merge: a paused rebase", warning);
        Assert.Contains($"exec touch '{marker}'", warning);
        Assert.Contains("git rebase --continue", warning);
        Assert.Equal(before, File.ReadAllText(todo));
        Assert.False(File.Exists(marker));
        Git.Run(repo, "rebase", "--continue");
        Assert.True(File.Exists(marker)); // the line Pen listed is one git really runs
    }

    [Fact]
    public void AUsersOwnPausedRebaseGetsTheShortWarningAndCanContinue()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);

        var warning = Warning(repo);

        Assert.Contains(".git/rebase-merge: a paused rebase", warning);
        Assert.DoesNotContain("exec lines", warning);
        Assert.DoesNotContain("couldn't", warning);
        Git.Run(repo, "rebase", "--continue");
        Assert.False(Directory.Exists(RebaseDir(repo)));
    }

    [Fact]
    public void ACleanRepoHasNothing()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Assert.Empty(Paused(repo));
    }

    [Fact]
    public void NotStartedWithGitIsAboutTheRepoBeingMovedInstead()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        var check = GitCheck.RunWithoutSession(repo, startedWithGit: false, commonGitDir: null, modulesAtStart: null);
        Assert.Single(check.Moved);
        Assert.Empty(check.Paused);
    }

    // What git 2.56 does with each is in the comment on PausedGit: these are the lines it runs, and the ones it doesn't.
    [Theory]
    [InlineData("exec touch f", "exec touch f")]
    [InlineData("x touch f", "x touch f")]
    [InlineData("exec\ttouch f", "exec\ttouch f")]
    [InlineData("x\ttouch f", "x\ttouch f")]
    [InlineData("   \t x touch f", "x touch f")]
    [InlineData("exec touch f\r", "exec touch f")]
    [InlineData("exec touch f # x", "exec touch f # x")]
    [InlineData("exec", "exec")]
    [InlineData("x", "x")]
    [InlineData("exec\r", "exec")]
    [InlineData("x\0", "x\0")]
    [InlineData("# exec touch f", null)]
    [InlineData("#exec touch f", null)]
    [InlineData("pick --exec touch f", null)]
    [InlineData("pick abc123 exec touch f", null)]
    [InlineData("EXEC touch f", null)]
    [InlineData("X touch f", null)]
    [InlineData("execute touch f", null)]
    [InlineData("xtouch f", null)]
    [InlineData("\u000bexec touch f", null)]
    [InlineData("noop\rexec touch f", null)]
    public void ExecLinesMatchHowGitParsesThem(string line, string? listed)
    {
        Assert.Equal(listed == null ? [] : [listed], PausedGit.ExecLines($"pick abc one\n{line}\npick def two\n"));
        Assert.Equal(listed == null ? [] : [listed], PausedGit.ExecLines(line)); // with no newline after it
    }

    [Fact]
    public void AByteOrderMarkDoesntChangeHowTheLinesAreSeen()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        File.WriteAllBytes(Path.Combine(RebaseDir(repo), TodoFile), [0xff, 0xfe, (byte)'\n', .. "exec evil\n"u8]);
        Assert.Contains("exec evil", Warning(repo));
    }

    [Theory]
    [InlineData(TodoFile, "exec lines in git-rebase-todo, which run on this machine")]
    [InlineData("git-rebase-todo.backup", "exec lines in git-rebase-todo.backup (git doesn't run them")]
    [InlineData("done", "exec lines in done (git doesn't run them")]
    public void EachCopyOfTheTodoIsListedAndSaysWhetherItRuns(string file, string heading)
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        File.WriteAllText(Path.Combine(RebaseDir(repo), file), "x evil\n");
        var warning = Warning(repo);
        Assert.Contains(heading, warning);
        Assert.Contains("x evil", warning);
    }

    [Fact]
    public void ASequencerTodoIsListed()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateDirectory(Path.Combine(repo, ".git", "sequencer"));
        File.WriteAllText(Path.Combine(repo, ".git", "sequencer", "todo"), "pick abc\nexec evil\n");
        var warning = Warning(repo);
        Assert.Contains(".git/sequencer: a paused cherry-pick or revert", warning);
        Assert.Contains("exec evil", warning);
    }

    [Fact]
    public void RebaseApplyAloneIsTheShortWarning_AndItsApplyOptionsAreShown()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var apply = t.Sub("repo", ".git", "rebase-apply");
        File.WriteAllText(Path.Combine(apply, "apply-opt"), "");
        var warning = Warning(repo);
        Assert.Contains(".git/rebase-apply: a paused rebase or git am", warning);
        Assert.DoesNotContain("options for git apply", warning);

        File.WriteAllText(Path.Combine(apply, "apply-opt"), "'--build-fake-ancestor=/home/me/.zshrc'\n");
        Assert.Contains("--build-fake-ancestor=/home/me/.zshrc", Warning(repo));
    }

    [Fact]
    public void AMergeStrategyThatRunsAPlantedProgramIsListed_AndARealOneIsnt()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        var marker = Path.Combine(t.Path, "ran");
        var strategy = Path.Combine(RebaseDir(repo), "strategy");

        File.WriteAllText(strategy, "ort\n");
        Assert.DoesNotContain("a merge strategy in", Warning(repo));

        File.WriteAllText(strategy, "x/../evil.sh\n");
        var warning = Warning(repo);
        Assert.Contains("merge strategy in strategy", warning);
        Assert.Contains("x/../evil.sh", warning);

        // The replay: the rebase's next pick runs it, from the top of the worktree.
        t.Sub("repo", "git-merge-x");
        var evil = Path.Combine(repo, "evil.sh");
        File.WriteAllText(evil, $"#!/bin/sh\ntouch '{marker}'\nexit 1\n");
        File.SetUnixFileMode(evil, (UnixFileMode)0b111_101_101);
        var parent = Git.Run(repo, "rev-parse", "HEAD").Trim();
        File.WriteAllText(Path.Combine(repo, "f"), "f");
        Git.Run(repo, "add", "f");
        Git.Run(repo, "commit", "-q", "-m", "f");
        var picked = Git.Run(repo, "rev-parse", "HEAD").Trim();
        Git.Run(repo, "reset", "-q", "--hard", parent);
        Git.Run(repo, "commit", "-q", "--allow-empty", "-m", "so the pick can't fast-forward");
        File.WriteAllText(Path.Combine(RebaseDir(repo), TodoFile), $"pick {picked} f\n");
        Assert.Throws<InvalidOperationException>(() => Git.Run(repo, "rebase", "--continue"));
        Assert.True(File.Exists(marker));
    }

    [Theory]
    [InlineData("[options]\n\tstrategy = x/../evil.sh\n", "strategy = x/../evil.sh")]
    [InlineData("[OPTIONS] STRATEGY = x/../evil.sh\n", "[OPTIONS] STRATEGY = x/../evil.sh")]
    [InlineData("[options]\n\tstrategy = \"x/../ev\\\nil.sh\"\n", "strategy = \"x/../ev\\")]
    [InlineData("[options]\n\tStrategy=evil\n", "Strategy=evil")]
    [InlineData("[options]\n\tstrategy = ort\\\nx\n", "strategy = ort\\")]
    public void SequencerOptionsNamingAnotherStrategyAreListed(string opts, string line)
    {
        Assert.Equal([line], PausedGit.StrategyLines(opts));
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(t.Sub("repo", ".git", "sequencer"), "opts"), opts);
        var warning = Warning(repo);
        Assert.Contains("merge strategy in opts", warning);
    }

    [Theory]
    [InlineData("[options]\n\tno-commit = true\n")]
    [InlineData("[options]\n\tstrategy = ort\n\tstrategy-option = theirs\n")]
    [InlineData("[options]\n\tstrategy = ours\n")]
    public void SequencerOptionsGitWritesAreQuiet(string opts) => Assert.Empty(PausedGit.StrategyLines(opts));

    [Theory]
    [InlineData("symlink", "it's a symlink")]
    [InlineData("dangling", "it's a symlink")]
    [InlineData("fifo", "it isn't a regular file")]
    [InlineData("huge", "it isn't a regular file")]
    [InlineData("directory", "it isn't a regular file")]
    [InlineData("unreadable", "permission denied")]
    public void ATodoPenCantReadSaysSoWithoutHanging(string how, string problem)
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        var todo = Path.Combine(RebaseDir(repo), TodoFile);
        var outside = Path.Combine(t.Path, "outside");
        File.WriteAllText(outside, "exec from the target, never read\n");
        File.Delete(todo);
        switch (how)
        {
            case "symlink": File.CreateSymbolicLink(todo, outside); break;
            case "dangling": File.CreateSymbolicLink(todo, "/nonexistent"); break;
            case "fifo": MkFifo(todo); break;
            case "huge":
                using (var f = File.Create(todo)) f.SetLength(1L << 40);
                break;
            case "directory": Directory.CreateDirectory(todo); break;
            case "unreadable":
                File.WriteAllText(todo, "exec evil\n");
                File.SetUnixFileMode(todo, UnixFileMode.None);
                break;
        }

        var warning = Within10s(() => Warning(repo));

        Assert.Contains(".git/rebase-merge: a paused rebase", warning);
        Assert.Contains($"couldn't read git-rebase-todo, so pen can't say what it would run: {problem}", warning);
        Assert.DoesNotContain("never read", warning);
        if (how == "unreadable")
            Assert.DoesNotContain("permission denied:", warning); // no path: the file is named already
    }

    [Theory]
    [InlineData("fifo", "it isn't a directory")]
    [InlineData("file", "it isn't a directory")]
    public void AStateDirThatIsntADirectoryIsStillFlagged(string how, string problem)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var state = RebaseDir(repo);
        if (how == "fifo") MkFifo(state);
        else File.WriteAllText(state, "");
        var warning = Warning(repo);
        Assert.Contains(".git/rebase-merge: a paused rebase", warning);
        Assert.Contains($"couldn't read the todo in it: {problem}", warning);
    }

    [Fact]
    public void ASymlinkedSequencerIsFlaggedWithoutBeingMoved()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Directory.CreateSymbolicLink(Path.Combine(repo, ".git", "sequencer"), t.Sub("elsewhere"));
        Assert.Contains(".git/sequencer: a paused cherry-pick or revert", Warning(repo));
    }

    [Theory]
    [InlineData("rebase-merge")]
    [InlineData("rebase-apply")]
    public void ASymlinkedRebaseStateDirIsQuarantined(string name)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var state = Path.Combine(repo, ".git", name);
        Directory.CreateSymbolicLink(state, t.Sub("elsewhere"));
        var moved = Assert.Single(GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved);
        Assert.Equal(state, moved.From);
        Assert.False(PathChecks.ExistsOrIsLink(state));
    }

    [Fact]
    public void ARebaseStateDirThatCantBeEnteredIsQuarantined()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        var state = RebaseDir(repo);
        File.SetUnixFileMode(state, UnixFileMode.None);
        var quarantined = state + GitQuarantine.Suffix;
        try
        {
            var moved = Assert.IsType<Quarantined.Moved>(Assert.Single(GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved));
            Assert.Equal(quarantined, moved.To);
            Assert.Contains("can't be read", moved.Why);
        }
        finally { File.SetUnixFileMode(Directory.Exists(quarantined) ? quarantined : state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void ASequencerThatCantBeEnteredSaysTheTodoCouldntBeRead()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var state = t.Sub("repo", ".git", "sequencer");
        File.SetUnixFileMode(state, UnixFileMode.None);
        try { Assert.Contains("couldn't read todo, so pen can't say what it would run: permission denied", Warning(repo)); }
        finally { File.SetUnixFileMode(state, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    private const string Planted = "$(touch canary)\n";

    /// <summary>A rebase state dir holding what git would write, with <paramref name="file"/> replaced by <paramref name="content"/>.</summary>
    private static string PlantState(string gitDir, string file, string? content = Planted)
    {
        var (dir, name) = (Path.GetDirectoryName(file)!, Path.GetFileName(file));
        var state = Path.Combine(gitDir, dir);
        Directory.CreateDirectory(state);
        foreach (var (f, value) in dir == "rebase-merge"
            ? new[] { ("msgnum", "1\n"), ("end", "2\n"), ("head-name", "refs/heads/main\n") }
            : [("next", "1\n"), ("last", "2\n"), ("head-name", "refs/heads/main\n"), ("rebasing", "")])
            File.WriteAllText(Path.Combine(state, f), value);
        var path = Path.Combine(state, name);
        if (content != null) File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("rebase-merge/msgnum")]
    [InlineData("rebase-merge/end")]
    [InlineData("rebase-merge/head-name")]
    [InlineData("rebase-apply/next")]
    [InlineData("rebase-apply/last")]
    public void PlantedPromptStateIsQuarantinedInEveryGitDir(string file)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var module = ModuleRepo(t, ".git", "modules", "sub");
        // Spaces make this no ref name either, so head-name is held to the same replay.
        var paths = new[]
        {
            PlantState(Path.Combine(repo, ".git"), file),
            PlantState(t.Sub("repo", ".git", "worktrees", "wt"), file),
            PlantState(module, file),
            PlantState(t.Sub("repo", ".git", "modules", "sub", "worktrees", "w"), file),
        };

        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);

        Assert.Equal(paths.Order(StringComparer.Ordinal), check.Moved.Select(q => q.From).Order(StringComparer.Ordinal));
        foreach (var path in paths)
        {
            Assert.False(File.Exists(path));
            Assert.Equal(Planted, File.ReadAllText(path + GitQuarantine.Suffix));
        }
        Assert.Null(check.Failure);
    }

    [Theory]
    [InlineData("symlink")]
    [InlineData("fifo")]
    [InlineData("huge")]
    [InlineData("directory")]
    [InlineData("unreadable")]
    [InlineData("second-line")]
    [InlineData("no-newline")]
    public void AnythingElseAtAPromptFileIsQuarantined(string how)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var path = PlantState(Path.Combine(repo, ".git"), "rebase-merge/msgnum", content: null);
        File.Delete(path);
        switch (how)
        {
            case "symlink":
                var target = Path.Combine(t.Path, "target");
                File.WriteAllText(target, Planted);
                File.CreateSymbolicLink(path, target);
                break;
            case "fifo": MkFifo(path); break;
            case "huge":
                using (var f = File.Create(path)) f.SetLength(1L << 40);
                break;
            case "directory": Directory.CreateDirectory(path); break;
            case "unreadable":
                File.WriteAllText(path, Planted);
                File.SetUnixFileMode(path, UnixFileMode.None);
                break;
            case "second-line": File.WriteAllText(path, "1\n" + Planted); break;
            case "no-newline": File.WriteAllText(path, Planted.TrimEnd()); break;
        }

        var moved = Within10s(() => GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved);

        Assert.Equal(path, Assert.Single(moved).From);
        Assert.False(PathChecks.ExistsOrIsLink(path));
    }

    [Theory]
    [InlineData("1\n", true)]
    [InlineData("12345\n", true)]
    [InlineData("7", true)]
    [InlineData("", true)]
    [InlineData("-1\n", false)]
    [InlineData("1 2\n", false)]
    [InlineData("١\n", false)] // digits in another script aren't what git writes
    [InlineData("$(x)\n", false)]
    [InlineData("`x`\n", false)]
    [InlineData("${x}\n", false)]
    [InlineData("1\0$(x)\n", false)]
    public void CountsAreWhatGitWrites(string content, bool valid) =>
        Assert.Equal(valid, PausedGit.IsCount(System.Text.Encoding.UTF8.GetBytes(content)));

    [Theory]
    [InlineData("detached HEAD\n", true)]
    [InlineData("refs/heads/main\n", true)]
    [InlineData("refs/heads/feature/x\n", true)]
    [InlineData("refs/heads/ünï\n", true)]
    [InlineData("refs/heads/$(touch${IFS}x)\n", true)] // a legal ref name, which the prompt protects
    [InlineData("", true)]
    [InlineData("$(touch canary)\n", false)]
    [InlineData("refs/heads/$(touch canary)\n", false)] // spaces aren't legal in one
    [InlineData("main\n", false)]
    [InlineData("refs/heads/a\nrefs/heads/b\n", false)]
    [InlineData("detached HEAD $(x)\n", false)]
    public void HeadNamesAreWhatGitWrites(string content, bool valid) =>
        Assert.Equal(valid, PausedGit.IsHeadName(System.Text.Encoding.UTF8.GetBytes(content)));

    [Theory]
    [InlineData("refs/heads/main")]
    [InlineData("refs/heads/a.b")]
    [InlineData("refs/heads/a.lock")]
    [InlineData("refs/heads/a.lock/b")]
    [InlineData("refs/heads/.a")]
    [InlineData("refs/heads/a/.b")]
    [InlineData("refs/heads/a..b")]
    [InlineData("refs/heads/a.")]
    [InlineData("refs/heads/a@{b")]
    [InlineData("refs/heads/@")]
    [InlineData("refs//heads")]
    [InlineData("refs/heads/")]
    [InlineData("/refs/heads")]
    [InlineData("refs")]
    [InlineData("@")]
    [InlineData("refs/heads/a b")]
    [InlineData("refs/heads/a\tb")]
    [InlineData("refs/heads/a~b")]
    [InlineData("refs/heads/a^b")]
    [InlineData("refs/heads/a:b")]
    [InlineData("refs/heads/a?b")]
    [InlineData("refs/heads/a*b")]
    [InlineData("refs/heads/a[b")]
    [InlineData("refs/heads/a]b")]
    [InlineData("refs/heads/a\\b")]
    [InlineData("refs/heads/a\u007fb")]
    [InlineData("refs/heads/$(x)")]
    [InlineData("refs/heads/`x`")]
    [InlineData("refs/heads/a;b|c&d")]
    [InlineData("refs/heads/a{b}")]
    [InlineData("refs/heads/ü")]
    public void RefNamesAgreeWithGit(string name)
    {
        var psi = new ProcessStartInfo("git", ["check-ref-format", name]) { RedirectStandardOutput = true, RedirectStandardError = true };
        var byGit = Process.RunAndCaptureText(psi).ExitStatus.ExitCode == 0;
        var latin1 = System.Text.Encoding.Latin1.GetString(System.Text.Encoding.UTF8.GetBytes(name));
        Assert.Equal(byGit, PausedGit.IsRefName(latin1));
    }

    [Theory]
    [InlineData("interactive")]
    [InlineData("detached")]
    [InlineData("oddly-named-branch")]
    [InlineData("apply")]
    [InlineData("am")]
    public void ARealOperationsOwnFilesNeverMove(string how)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        void Commit(string file, string text)
        {
            File.WriteAllText(Path.Combine(repo, file), text);
            Git.Run(repo, "add", file);
            Git.Run(repo, "commit", "-q", "-m", file);
        }
        Commit("f", "base\n");
        Git.Run(repo, "branch", "-m", "main");
        Git.Run(repo, "checkout", "-q", "-b", how == "oddly-named-branch" ? "$(touch${IFS}canary)" : "side");
        Commit("f", "side\n");
        if (how == "am")
        {
            Git.Run(repo, "format-patch", "-q", "-1", "-o", t.Sub("patches"));
            Git.Run(repo, "reset", "-q", "--hard", "HEAD~1");
        }
        var side = Git.Run(repo, "rev-parse", "HEAD").Trim();
        Git.Run(repo, "checkout", "-q", "main");
        Commit("f", "main\n");
        if (how != "am") Git.Run(repo, "checkout", "-q", how == "detached" ? side : "-");
        if (how == "detached") Git.Run(repo, "checkout", "-q", "--detach");

        // Each stops at the conflict in f: a real operation, paused by git itself.
        string[] start = how switch
        {
            "apply" => ["rebase", "--apply", "main"],
            "am" => ["am", Directory.GetFiles(Path.Combine(t.Path, "patches")).Single()],
            _ => ["rebase", "--merge", "main"],
        };
        Assert.Throws<InvalidOperationException>(() => Git.Run(repo, start));
        var state = Path.Combine(repo, ".git", how is "apply" or "am" ? "rebase-apply" : "rebase-merge");
        Assert.True(Directory.Exists(state));
        Assert.True(File.Exists(Path.Combine(state, how is "apply" or "am" ? "next" : "msgnum")));

        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);

        Assert.Empty(check.Moved);
        Assert.Null(check.Failure);
        Assert.Single(check.Paused);
    }

    private static readonly string[] PromptScripts =
    [
        "/opt/homebrew/opt/git/etc/bash_completion.d/git-prompt.sh",
        "/usr/local/opt/git/etc/bash_completion.d/git-prompt.sh",
        "/home/linuxbrew/.linuxbrew/opt/git/etc/bash_completion.d/git-prompt.sh",
        "/usr/lib/git-core/git-sh-prompt",
        "/usr/share/git-core/contrib/completion/git-prompt.sh",
        "/usr/share/git/git-prompt.sh",
        "/etc/bash_completion.d/git-prompt.sh",
    ];

    /// <summary>
    /// Draws one prompt in an interactive shell in <paramref name="repo"/>, the
    /// way a user's would, in the mode where the shell expands PS1. Stops it if it hangs.
    /// </summary>
    private static void DrawPrompt(TempDir t, string shell, string repo, string script)
    {
        var home = t.Sub("prompt-home");
        string[] args;
        if (shell == "bash")
        {
            File.WriteAllText(Path.Combine(home, "bashrc"), $"source '{script}'\nPROMPT_COMMAND='__git_ps1 \"a\" \"b\"'\n");
            args = ["-c", $"exec bash --noprofile --rcfile '{home}/bashrc' -i </dev/null"];
        }
        else
        {
            File.WriteAllText(Path.Combine(home, ".zshrc"), $"source '{script}'\nsetopt PROMPT_SUBST\nprecmd() {{ __git_ps1 \"a\" \"b\"; }}\n");
            args = ["-c", "exec zsh -i </dev/null"];
        }
        var psi = NoTerminal.StartInfo("sh", args);
        psi.WorkingDirectory = repo;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.RedirectStandardInput = true;
        psi.Environment["HOME"] = home;
        psi.Environment["ZDOTDIR"] = home;
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromSeconds(20))) process.Kill(entireProcessTree: true);
    }

    private static bool OnPath(string tool) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => d.Length > 0 && File.Exists(Path.Combine(d, tool)));

    private static string PromptScriptOrSkip(string shell)
    {
        var script = PromptScripts.FirstOrDefault(File.Exists);
        if (script == null) Assert.Skip("git-prompt.sh isn't installed here, so the shell prompt replay can't run");
        if (!OnPath(shell)) Assert.Skip($"{shell} isn't installed here, so its shell prompt replay can't run");
        return script!;
    }

    public static TheoryData<string, string> PromptFiles()
    {
        var data = new TheoryData<string, string>();
        foreach (var shell in new[] { "bash", "zsh" })
            foreach (var file in new[] { "rebase-merge/msgnum", "rebase-merge/end", "rebase-apply/next", "rebase-apply/last" })
                data.Add(shell, file);
        return data;
    }

    [Theory]
    [MemberData(nameof(PromptFiles))]
    public void TheShellPromptReplayRunsBeforeTheCheckAndNotAfter(string shell, string file)
    {
        var script = PromptScriptOrSkip(shell);
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var path = PlantState(Path.Combine(repo, ".git"), file);
        var canary = Path.Combine(repo, "canary");

        DrawPrompt(t, shell, repo, script);
        if (!File.Exists(canary)) Assert.Skip($"{shell} didn't draw a prompt that expands the planted file here, so there's nothing to compare against");
        File.Delete(canary);

        var moved = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved;
        Assert.Equal(path, Assert.Single(moved).From);

        DrawPrompt(t, shell, repo, script);
        Assert.False(File.Exists(canary));
    }

    /// <summary>What git-prompt.sh only tests for or protects: planted, none of it runs, so none of it needs moving.</summary>
    public static TheoryData<string, string, string> ProtectedFiles()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var shell in new[] { "bash", "zsh" })
        {
            data.Add(shell, "HEAD", "ref: refs/heads/$(touch canary)\n");
            data.Add(shell, "HEAD", "$(touch canary)\n");
            data.Add(shell, "MERGE_HEAD", Planted);
            data.Add(shell, "BISECT_LOG", Planted);
            data.Add(shell, "CHERRY_PICK_HEAD", Planted);
            data.Add(shell, "REVERT_HEAD", Planted);
            data.Add(shell, "sequencer/todo", "pick $(touch canary)\n");
            data.Add(shell, "rebase-merge/head-name", "refs/heads/$(touch${IFS}canary)\n");
            data.Add(shell, "rebase-apply/head-name", "refs/heads/$(touch${IFS}canary)\n");
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(ProtectedFiles))]
    public void TheShellPromptRunsNothingFromTheFilesLeftAlone(string shell, string file, string content)
    {
        var script = PromptScriptOrSkip(shell);
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        if (file.StartsWith("rebase-", StringComparison.Ordinal)) PlantState(git, file, content);
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(git, file))!);
            File.WriteAllText(Path.Combine(git, file), content);
        }

        DrawPrompt(t, shell, repo, script);

        Assert.False(File.Exists(Path.Combine(repo, "canary")));
        if (file.StartsWith("rebase-", StringComparison.Ordinal))
            Assert.Empty(GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved); // a legal ref name, so it stays
    }

    private const UnixFileMode ReadOnlyDir = (UnixFileMode)0b101_101_101;
    private const UnixFileMode OpenDir = (UnixFileMode)0b111_101_101;

    /// <summary>Makes each read-only, and on dispose makes all of a tree writable again so it can be deleted.</summary>
    private sealed class ReadOnlyDirs : IDisposable
    {
        private readonly string _root;
        public ReadOnlyDirs(string root, params string[] dirs)
        {
            _root = root;
            foreach (var d in dirs) File.SetUnixFileMode(d, ReadOnlyDir);
        }

        public void Dispose()
        {
            File.SetUnixFileMode(_root, OpenDir);
            foreach (var d in Directory.EnumerateDirectories(_root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                if (!PathChecks.IsLink(d)) File.SetUnixFileMode(d, OpenDir);
        }
    }

    /// <summary>A session that started with this repo, keeping its module repos.</summary>
    private static SessionDir StartedSession(TempDir t, string repo, params string[] modules)
    {
        var session = new SessionDir("pen-test-12345678", t.Sub("runtime", "sessions", "12345678"));
        File.WriteAllText(session.StartedWithGit, Path.Combine(repo, ".git") + "\n");
        File.WriteAllText(session.ModulesFile, string.Concat(modules.Select(m => m + "\0")));
        return session;
    }

    /// <summary>A planted count in a state dir under <paramref name="under"/>, or that state dir a symlink to one.</summary>
    private static string PlantUnder(TempDir t, string under, bool symlinkedState)
    {
        if (!symlinkedState) return PlantState(under, "rebase-merge/msgnum");
        var target = t.Sub("elsewhere-" + Guid.NewGuid());
        var state = Path.Combine(under, "rebase-merge");
        PlantState(target, "rebase-merge/msgnum");
        Directory.CreateSymbolicLink(state, Path.Combine(target, "rebase-merge"));
        return state;
    }

    [Theory]
    [InlineData("the state dir", false)]
    [InlineData("the git dir", true)]
    [InlineData("the git dir and the state dir", false)]
    public void AReadOnlyDirectoryDoesntKeepAPlantInPlaceAtSessionEnd(string level, bool symlinkedState)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        var path = PlantUnder(t, git, symlinkedState);
        string[] readOnly = level switch
        {
            "the state dir" => [Path.GetDirectoryName(path)!],
            "the git dir" => [git],
            _ => [Path.GetDirectoryName(path)!, git],
        };
        using var _ = new ReadOnlyDirs(repo, readOnly);

        var check = GitCheck.RunAtSessionEnd(repo, StartedSession(t, repo));

        Assert.Null(check.Failure);
        Assert.False(PathChecks.ExistsOrIsLink(path));
        Assert.Contains(check.Moved, q => q.From == path && q is Quarantined.Moved);
        Assert.Contains(check.ModeChanges, c => c.Path == Path.GetDirectoryName(path) && c.Now.HasFlag(UnixFileMode.UserWrite));
    }

    public static TheoryData<string, string> ReadOnlyPromptCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var shell in new[] { "bash", "zsh" })
            foreach (var level in new[] { "state dir", "git dir with a symlinked state dir" })
                data.Add(shell, level);
        return data;
    }

    [Theory]
    [MemberData(nameof(ReadOnlyPromptCases))]
    public void TheShellPromptReplayThroughAReadOnlyDirectoryRunsNothingAfterTheCheck(string shell, string level)
    {
        var script = PromptScriptOrSkip(shell);
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        var symlinked = level != "state dir";
        var path = PlantUnder(t, git, symlinked);
        var canary = Path.Combine(repo, "canary");
        using var _ = new ReadOnlyDirs(repo, symlinked ? git : Path.GetDirectoryName(path)!);

        DrawPrompt(t, shell, repo, script);
        if (!File.Exists(canary)) Assert.Skip($"{shell} didn't draw a prompt that expands the planted file here, so there's nothing to compare against");
        File.Delete(canary);

        Assert.Null(GitCheck.RunAtSessionEnd(repo, StartedSession(t, repo)).Failure);

        DrawPrompt(t, shell, repo, script);
        Assert.False(File.Exists(canary));
    }

    [Theory]
    [InlineData("worktree entry", false)]
    [InlineData("worktree entry", true)]
    [InlineData("module repo", false)]
    [InlineData("module repo", true)]
    [InlineData("every directory down to a nested module name", false)]
    [InlineData("every directory down to a nested module name", true)]
    [InlineData("module repo's worktree entry", false)]
    [InlineData("module repo's worktree entry", true)]
    public void EveryLevelThatCanBeMadeReadOnlyIsHandled(string level, bool symlinkedState)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        var modules = new List<string>();
        string under;
        var readOnly = new List<string>();
        switch (level)
        {
            case "worktree entry":
                under = t.Sub("repo", ".git", "worktrees", "wt");
                readOnly.Add(under);
                break;
            case "module repo":
                under = ModuleRepo(t, ".git", "modules", "sub");
                modules.Add(under);
                readOnly.Add(under);
                break;
            case "module repo's worktree entry":
                modules.Add(ModuleRepo(t, ".git", "modules", "sub"));
                under = t.Sub("repo", ".git", "modules", "sub", "worktrees", "w");
                readOnly.Add(under);
                readOnly.Add(Path.GetDirectoryName(under)!);
                break;
            default:
                under = ModuleRepo(t, ".git", "modules", "a", "b");
                modules.Add(under);
                readOnly.AddRange([git, Path.Combine(git, "modules"), Path.Combine(git, "modules", "a"), under]);
                break;
        }
        var path = PlantUnder(t, under, symlinkedState);
        if (!symlinkedState) readOnly.Add(Path.GetDirectoryName(path)!);
        using var _ = new ReadOnlyDirs(repo, [.. readOnly]);

        var check = GitCheck.RunAtSessionEnd(repo, StartedSession(t, repo, [.. modules]));

        Assert.Null(check.Failure);
        Assert.False(PathChecks.ExistsOrIsLink(path));
        Assert.Contains(check.Moved, q => q.From == path && q is Quarantined.Moved);
    }

    /// <summary>Modes that list badly but stay searchable, as the sandbox could set them.</summary>
    public static TheoryData<int> SearchableModes() => [0b011_001_001, 0b001_001_001, 0b011_000_000];

    public static TheoryData<string, int> PromptThroughABadMode()
    {
        var data = new TheoryData<string, int>();
        foreach (var shell in new[] { "bash", "zsh" })
            foreach (var mode in SearchableModes())
                data.Add(shell, (int)mode.Data);
        return data;
    }

    [Theory]
    [MemberData(nameof(PromptThroughABadMode))]
    public void TheShellPromptReplayThroughAnUnlistableGitDirRunsNothingAfterTheCheck(string shell, int mode)
    {
        var script = PromptScriptOrSkip(shell);
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        var path = PlantState(git, "rebase-merge/msgnum");
        var canary = Path.Combine(repo, "canary");
        using var _ = new ReadOnlyDirs(repo);
        File.SetUnixFileMode(git, (UnixFileMode)mode);

        DrawPrompt(t, shell, repo, script);
        if (File.Exists(canary)) File.Delete(canary); // not every mode lets git find the repo, so the replay's "before" can't be required

        var check = GitCheck.RunAtSessionEnd(repo, StartedSession(t, repo));

        Assert.Null(check.Failure);
        Assert.False(File.Exists(path));
        Assert.Contains(check.ModeChanges, c => c.Path == git);
        DrawPrompt(t, shell, repo, script);
        Assert.False(File.Exists(canary));
    }

    /// <summary>A directory at each level that a planted state file sits under, returning it, the plant, and the modules to keep.</summary>
    private static (string Dir, string Plant, string[] Modules) BadModeLevel(TempDir t, string level)
    {
        var repo = Path.Combine(t.Path, "repo");
        var git = Path.Combine(repo, ".git");
        switch (level)
        {
            case ".git": return (git, PlantState(git, "rebase-merge/msgnum"), []);
            case "rebase-merge": { var p = PlantState(git, "rebase-merge/msgnum"); return (Path.GetDirectoryName(p)!, p, []); }
            case "rebase-apply": { var p = PlantState(git, "rebase-apply/next"); return (Path.GetDirectoryName(p)!, p, []); }
            case "worktrees/n":
            {
                var n = t.Sub("repo", ".git", "worktrees", "n");
                return (n, PlantState(n, "rebase-merge/end"), []);
            }
            case "module repo":
            {
                var m = ModuleRepo(t, ".git", "modules", "sub");
                return (m, PlantState(m, "rebase-merge/msgnum"), [m]);
            }
            default: // a level of a module's name with "/" in it: Pen moves the level, plant and all
            {
                var m = ModuleRepo(t, ".git", "modules", "a", "b");
                return (Path.GetDirectoryName(m)!, PlantState(m, "rebase-merge/msgnum"), [m]);
            }
        }
    }

    public static TheoryData<string, int> LevelsAndModes()
    {
        var data = new TheoryData<string, int>();
        foreach (var level in new[] { ".git", "rebase-merge", "rebase-apply", "worktrees/n", "module repo", "between" })
            foreach (var mode in SearchableModes())
                data.Add(level, (int)mode.Data);
        return data;
    }

    [Theory]
    [MemberData(nameof(LevelsAndModes))]
    public void NoLevelInABadModeKeepsAPlantAtSessionEnd(string level, int mode)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var (dir, plant, modules) = BadModeLevel(t, level);
        using var _ = new ReadOnlyDirs(repo);
        File.SetUnixFileMode(dir, (UnixFileMode)mode);

        var check = GitCheck.RunAtSessionEnd(repo, StartedSession(t, repo, modules));

        Assert.Null(check.Failure);
        Assert.False(PathChecks.ExistsOrIsLink(plant));
    }

    [Theory]
    [MemberData(nameof(LevelsAndModes))]
    public void WithoutChangingModesNoLevelKeepsAPlantSilently(string level, int mode)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var (dir, plant, _) = BadModeLevel(t, level);
        using var restore = new ReadOnlyDirs(repo);
        File.SetUnixFileMode(dir, (UnixFileMode)mode);

        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);

        // pen sign changes no modes: it moved the plant by name, or it says so and refuses.
        Assert.True(!PathChecks.ExistsOrIsLink(plant) || check.Failure != null);
        if (Directory.Exists(dir)) Assert.Equal((UnixFileMode)mode, File.GetUnixFileMode(dir));
        Assert.True(check.Failure != null || check.Moved.Count > 0);
    }

    [Fact]
    public void AnUnlistableGitDirIsStillSweptByNameWhenPenWontMakeItListable_AndStaysAFailure()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        var path = PlantState(git, "rebase-merge/msgnum");
        using var _ = new ReadOnlyDirs(repo);
        File.SetUnixFileMode(git, (UnixFileMode)0b011_001_001);

        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);

        Assert.NotNull(check.Failure);
        Assert.Contains("can't read", check.Failure);
        Assert.False(File.Exists(path));
        Assert.Contains(check.Moved, q => q.From == path && q is Quarantined.Moved);
    }

    [Fact]
    public void AWorktreeEntryThatStaysUnlistableIsAFailure()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var entry = t.Sub("repo", ".git", "worktrees", "n");
        using var _ = new ReadOnlyDirs(repo);
        File.SetUnixFileMode(entry, UnixFileMode.None);

        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);

        Assert.NotNull(check.Failure);
        Assert.Contains("worktrees/n", check.Failure);
    }

    [Fact]
    public void AWorktreeEntryThatCanBeMadeListableIsCheckedAtSessionEnd()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var entry = t.Sub("repo", ".git", "worktrees", "n");
        var path = PlantState(entry, "rebase-merge/end");
        using var _ = new ReadOnlyDirs(repo);
        File.SetUnixFileMode(entry, UnixFileMode.None);

        var check = GitCheck.RunAtSessionEnd(repo, StartedSession(t, repo));

        Assert.Null(check.Failure);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WithoutChangingModesTheStateDirGoesAsideInstead()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var path = PlantUnder(t, Path.Combine(repo, ".git"), symlinkedState: false);
        var state = Path.GetDirectoryName(path)!;
        using var _ = new ReadOnlyDirs(repo, state);

        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);

        Assert.Null(check.Failure);
        Assert.Equal(state, Assert.Single(check.Moved).From);
        Assert.False(PathChecks.ExistsOrIsLink(state));
        Assert.Equal(ReadOnlyDir, File.GetUnixFileMode(state + GitQuarantine.Suffix)); // no mode changed
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenNothingCanMoveTheCheckFails_AndPenSignRefuses(bool symlinkedState)
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        var path = PlantUnder(t, git, symlinkedState);
        string[] readOnly = symlinkedState ? [git] : [Path.GetDirectoryName(path)!, git];
        using var _ = new ReadOnlyDirs(repo, readOnly);

        var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);

        Assert.NotNull(check.Failure); // pen sign can't change modes while other sessions run, so it stops here
        Assert.Contains(check.Moved, q => q is Quarantined.Failed);
        var warning = CapturingStderr(() => GitCheckReport.Print(repo, check));
        Assert.Contains("COULD NOT MOVE IT", warning);
        Assert.Contains("git wasn't checked: don't run git here", warning);

        var host = t.Host();
        var project = Project.Resolve(repo, host);
        File.SetUnixFileMode(git, ReadOnlyDir);
        var error = Assert.Throws<PenException>(() => CapturingStderr(() => new SignCommand(host, project, TimeProvider.System).Run(null)));
        Assert.Contains("not signing", error.Message);
    }

    [Fact]
    public void TheWarningClaimsNothingAboutWhatPenLeftAlone()
    {
        var warning = PausedGit.Warning("/p", [new("/p/.git/rebase-merge", "a paused rebase", [])]);
        Assert.DoesNotContain("touched", warning);
        Assert.DoesNotContain("names above", warning);
        Assert.Contains("the todo files, the merge strategy and git apply's options", warning);
    }

    [Fact]
    public void TheNotCheckedWarningNamesThePausedStateToInspect()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var git = Path.Combine(repo, ".git");
        File.SetUnixFileMode(git, UnixFileMode.None);
        try
        {
            var check = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null);
            Assert.NotNull(check.Failure);
            var warning = CapturingStderr(() => GitCheckReport.Print(repo, check));
            Assert.Contains("don't run git", warning);
            foreach (var name in new[] { "rebase-merge", "rebase-apply", "sequencer" })
                Assert.Contains(name, warning);
        }
        finally { File.SetUnixFileMode(git, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void TheWarningMentionsSkipAndWhatItCantVouchFor()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        File.WriteAllText(Path.Combine(RebaseDir(repo), TodoFile), "exec evil\n");
        var warning = Warning(repo);
        Assert.Contains("at git rebase --continue or --skip", warning);
        Assert.Contains("before git rebase --continue or --skip", warning);
        Assert.Contains("what a pick brings in", warning);
        Assert.Contains("reword", warning);
    }

    [Fact]
    public void PenSignRefusesOverPlantedPromptStateAndReportsIt()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var path = PlantState(Path.Combine(repo, ".git"), "rebase-merge/msgnum");
        var host = t.Host();
        var project = Project.Resolve(repo, host);

        PenException? error = null;
        var output = CapturingStderr(() => error = Assert.Throws<PenException>(() => new SignCommand(host, project, TimeProvider.System).Run(null)));

        Assert.Contains("not signing", error!.Message);
        Assert.Contains("msgnum.pen-quarantine", output);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void WorktreeEntriesAreChecked()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "worktree", "add", "-q", Path.Combine(t.Path, "wt"));
        var state = t.Sub("repo", ".git", "worktrees", "wt", "rebase-merge");
        File.WriteAllText(Path.Combine(state, TodoFile), "x evil\n");
        var warning = Warning(repo);
        Assert.Contains(".git/worktrees/wt/rebase-merge: a paused rebase", warning);
        Assert.Contains("x evil", warning);
    }

    [Fact]
    public void AWorktreeEntryThatCantBeEnteredOrIsASymlinkIsFlagged()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var locked = t.Sub("repo", ".git", "worktrees", "locked");
        Directory.CreateSymbolicLink(Path.Combine(repo, ".git", "worktrees", "link"), t.Sub("elsewhere"));
        File.SetUnixFileMode(locked, UnixFileMode.None);
        try
        {
            var warning = PausedGit.Warning(repo, PausedGit.Find(Path.Combine(repo, ".git")));
            Assert.Contains(".git/worktrees/locked: a worktree's git files pen couldn't look in", warning);
            Assert.Contains(".git/worktrees/link: a worktree's git files pen couldn't look in", warning);
        }
        finally { File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    /// <summary>The layout git keeps a submodule's repo in, enough for the scan to call it one.</summary>
    private static string ModuleRepo(TempDir t, params string[] under)
    {
        var repo = t.Sub(["repo", .. under]);
        Directory.CreateDirectory(Path.Combine(repo, "objects"));
        Directory.CreateDirectory(Path.Combine(repo, "refs"));
        File.WriteAllText(Path.Combine(repo, "HEAD"), "ref: refs/heads/main\n");
        return repo;
    }

    [Fact]
    public void ModuleReposAndTheirWorktreeEntriesAreChecked()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var module = ModuleRepo(t, ".git", "modules", "sub");
        var nested = ModuleRepo(t, ".git", "modules", "sub", "modules", "inner");
        File.WriteAllText(Path.Combine(t.Sub("repo", ".git", "modules", "sub", "rebase-merge"), TodoFile), "x in-module\n");
        File.WriteAllText(Path.Combine(t.Sub("repo", ".git", "modules", "sub", "worktrees", "w", "sequencer"), "todo"), "x in-module-worktree\n");
        File.WriteAllText(Path.Combine(t.Sub("repo", ".git", "modules", "sub", "modules", "inner", "rebase-apply"), "apply-opt"), "");

        var warning = Warning(repo);

        Assert.Contains(".git/modules/sub/rebase-merge: a paused rebase", warning);
        Assert.Contains("x in-module", warning);
        Assert.Contains(".git/modules/sub/worktrees/w/sequencer: a paused cherry-pick or revert", warning);
        Assert.Contains("x in-module-worktree", warning);
        Assert.Contains(".git/modules/sub/modules/inner/rebase-apply: a paused rebase or git am", warning);
        Assert.True(Directory.Exists(module) && Directory.Exists(nested)); // nothing moved
    }

    [Fact]
    public void TheListIsCappedWithACountOfTheRest()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        File.WriteAllLines(Path.Combine(RebaseDir(repo), TodoFile), Enumerable.Range(1, 25).Select(i => $"exec evil-{i}-"));

        var warning = Warning(repo);

        Assert.Contains("exec evil-10-", warning);
        Assert.DoesNotContain("exec evil-11-", warning);
        Assert.Contains($"… and {25 - PausedGit.MaxLines} more", warning);
    }

    [Fact]
    public void TheListOfOperationsIsCappedToo()
    {
        var ops = Enumerable.Range(1, 13).Select(i => new PausedOp($"/p/op-{i}-", "a paused rebase", i == 12 ? [new("note", [])] : [])).ToList();
        var warning = PausedGit.Warning("/p", ops);
        Assert.Contains("op-10-", warning);
        Assert.DoesNotContain("op-11-", warning);
        Assert.Contains("… and 3 more, 1 of them with something to look at", warning);
    }

    [Fact]
    public void LinesAreEscapedAndBounded()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        File.WriteAllText(Path.Combine(RebaseDir(repo), TodoFile), $"exec a\u001b[2Kb\tc‮d\u0085e\nx {new string('y', 5000)}; evil\n");

        var warning = Warning(repo);

        Assert.Contains("exec a\\x1b[2Kb\\tc\\u202ed\\x85e", warning);
        Assert.DoesNotContain('\u001b', warning);
        Assert.DoesNotContain("; evil", warning);
        var shown = warning.Split('\n').Single(l => l.Contains("yyy", StringComparison.Ordinal));
        Assert.Contains($"… ({"x ".Length + 5000 + "; evil".Length - PausedGit.MaxLineLength} more characters)", shown);
        Assert.True(shown.Length < PausedGit.MaxLineLength + 50);
    }

    [Fact]
    public void PausedStateIsListedFromTheGitDirThenItsWorktreesThenEachModuleRepoAndItsWorktrees()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var module = ModuleRepo(t, ".git", "modules", "sub");
        var gitDirs = new[]
        {
            Path.Combine(repo, ".git"),
            t.Sub("repo", ".git", "worktrees", "wt"),
            module,
            t.Sub("repo", ".git", "modules", "sub", "worktrees", "w"),
        };
        foreach (var gitDir in gitDirs.Reverse())
            Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge"));

        var paths = Paused(repo).Select(op => op.Path);

        Assert.Equal(gitDirs.Select(gitDir => Path.Combine(gitDir, "rebase-merge")), paths);
    }

    [Fact]
    public void AStrayFileInTheWorktreesDirIsntFlagged()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        File.WriteAllText(Path.Combine(t.Sub("repo", ".git", "worktrees"), "stray"), "");
        Assert.Empty(Paused(repo));
    }

    [Fact]
    public void ReportPrintsItBetweenTheMovesAndTheFailure()
    {
        var moved = new List<Quarantined> { new Quarantined.Moved("/p/.git/commondir", "/p/.git/commondir.pen-quarantine", "why") };
        var paused = new List<PausedOp> { new("/p/.git/rebase-merge", "a paused rebase", []) };
        var output = CapturingStderr(() => GitCheckReport.Print("/p", new CheckResult { Moved = moved, Failure = "reason", Paused = paused }));
        var at = output.IndexOf("a paused rebase", StringComparison.Ordinal);
        Assert.True(output.IndexOf("mv '", StringComparison.Ordinal) < at);
        Assert.True(at < output.IndexOf("wasn't checked", StringComparison.Ordinal));
    }

    [Fact]
    public void ACleanCheckReportsNothing() =>
        Assert.Empty(CapturingStderr(() => GitCheckReport.Print("/p", new CheckResult { Moved = [], Failure = null, Paused = [] })));

    [Fact]
    public void PenSignPrintsItAndStillGoesOn()
    {
        using var t = new TempDir();
        var repo = PausedRebase(t);
        File.AppendAllText(Path.Combine(RebaseDir(repo), TodoFile), "exec evil\n");
        var host = t.Host();
        var project = Project.Resolve(repo, host);

        PenException? error = null;
        var output = CapturingStderr(() => error = Assert.Throws<PenException>(() => new SignCommand(host, project, TimeProvider.System).Run(null)));

        Assert.Contains("exec evil", output);
        Assert.Contains("git rebase --continue", output);
        Assert.DoesNotContain("not signing", error!.Message); // refused for its own reason: a rebase's HEAD is detached
        Assert.Contains("detached", error.Message);
        Assert.True(File.Exists(Path.Combine(RebaseDir(repo), TodoFile)));
    }
}
