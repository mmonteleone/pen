namespace Pen.IntegrationTests;

/// <summary>Paused git operations the sandbox plants: reported at session end and left in place, while what a
/// shell prompt would run is quarantined, even from a read-only state dir, a read-only .git or one that can't be
/// listed.</summary>
public sealed class PausedGitStateTests(PausedGitStateTests.Fixture fixture)
    : IClassFixture<PausedGitStateTests.Fixture>
{
    private const string MsgnumQuarantined = ".git/rebase-merge/msgnum → .git/rebase-merge/msgnum.pen-quarantine";

    private const string NotChecked = "git wasn't checked";

    [Fact(DisplayName = "a planted paused rebase is reported at session end")]
    public void PausedRebaseIsReported() =>
        IntegrationAssert.Contains(fixture.Rebase, ".git/rebase-merge: a paused rebase");

    [Fact(DisplayName = "...with its exec line")]
    public void ExecLineIsShown() => IntegrationAssert.Contains(fixture.Rebase, "x touch planted-by-a-rebase");

    [Fact(DisplayName = "...and not the commented one")]
    public void CommentedLineIsntShown() => IntegrationAssert.Lacks(fixture.Rebase, "exec commented");

    [Fact(DisplayName = "...saying to inspect it before continuing")]
    public void SaysToInspect() => IntegrationAssert.Contains(fixture.Rebase, "before git rebase --continue");

    [Fact(DisplayName = "...and that a FIFO in it can't be read")]
    public void FifoCantBeRead() => IntegrationAssert.Contains(fixture.Rebase, "couldn't read done");

    [Fact(DisplayName = "...and the sequencer's")]
    public void SequencerIsShown() => IntegrationAssert.Contains(fixture.Rebase, "x from-the-sequencer");

    [Fact(DisplayName = "...and it's left in place")]
    public void RebaseIsLeftInPlace() => Assert.True(fixture.RebaseTodoKept);

    [Fact(DisplayName = "a planted count a shell prompt would run is quarantined at session end")]
    public void CountIsQuarantined() => IntegrationAssert.Contains(fixture.Count, MsgnumQuarantined);

    [Fact(DisplayName = "...while its neighbour git could have written stays")]
    public void NeighbourStays() => Assert.True(fixture.EndKept);

    [Fact(DisplayName = "...and the next prompt runs nothing")]
    public void NextPromptRunsNothing()
    {
        var replay = RequirePromptReplay();

        Assert.False(replay.RanWhileQuarantined);
    }

    [Fact(DisplayName = "...though it does if the file is put back (so that check means something)")]
    public void PromptRunsThePutBackFile()
    {
        var replay = RequirePromptReplay();

        Assert.True(replay.RanWhenPutBack);
    }

    [Fact(DisplayName = "a read-only state dir doesn't keep a planted count in place")]
    public void ReadOnlyStateDirDoesntKeepTheCount() =>
        IntegrationAssert.Contains(fixture.ReadOnlyStateDir, MsgnumQuarantined);

    [Fact(DisplayName = "...and says what mode it changed")]
    public void SaysWhatModeItChanged() =>
        IntegrationAssert.Contains(fixture.ReadOnlyStateDir, "changed .git/rebase-merge from mode 555");

    [Fact(DisplayName = "...nor does a read-only .git keep a symlinked one")]
    public void ReadOnlyGitDirDoesntKeepASymlink() =>
        IntegrationAssert.Contains(fixture.ReadOnlyGitDir, ".git/rebase-merge → .git/rebase-merge.pen-quarantine");

    [Fact(DisplayName = "...and the check finished")]
    public void ReadOnlyGitDirCheckFinished() => IntegrationAssert.Lacks(fixture.ReadOnlyGitDir, NotChecked);

    [Fact(DisplayName = "...nor does a .git that can't be listed")]
    public void UnlistableGitDirDoesntKeepTheCount() =>
        IntegrationAssert.Contains(fixture.UnlistableGitDir, MsgnumQuarantined);

    [Fact(DisplayName = "...and the check finished")]
    public void UnlistableGitDirCheckFinished() => IntegrationAssert.Lacks(fixture.UnlistableGitDir, NotChecked);

    private PromptReplay RequirePromptReplay()
    {
        Assert.SkipWhen(
            fixture.Replay == null, "git-prompt.sh isn't installed here, so the shell prompt replay was skipped");
        return fixture.Replay;
    }

    /// <summary>Whether git-prompt.sh's prompt ran the planted count's command substitution.</summary>
    /// <param name="RanWhileQuarantined">Whether it ran with the count quarantined.</param>
    /// <param name="RanWhenPutBack">Whether it ran once the count was put back.</param>
    internal sealed record PromptReplay(bool RanWhileQuarantined, bool RanWhenPutBack);

    /// <summary>With a linked worktree outside the project, runs sessions that each plant a paused operation in .git,
    /// checking and removing it after each; replays a shell prompt over the quarantined count where git-prompt.sh is
    /// installed.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        private const UnixFileMode UserAll = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

        private static readonly string[] PromptScripts =
        [
            "/opt/homebrew/opt/git/etc/bash_completion.d/git-prompt.sh", "/usr/lib/git-core/git-sh-prompt",
            "/usr/share/git-core/contrib/completion/git-prompt.sh",
        ];

        internal CommandResult Rebase { get; private set; } = null!;

        internal bool RebaseTodoKept { get; private set; }

        internal CommandResult Count { get; private set; } = null!;

        internal bool EndKept { get; private set; }

        /// <summary>Gets the shell prompt replay, or null if git-prompt.sh isn't installed.</summary>
        internal PromptReplay? Replay { get; private set; }

        internal CommandResult ReadOnlyStateDir { get; private set; } = null!;

        internal CommandResult ReadOnlyGitDir { get; private set; } = null!;

        internal CommandResult UnlistableGitDir { get; private set; } = null!;

        private string GitDir => Path.Combine(Project.Dir, ".git");

        private string RebaseMerge => Path.Combine(GitDir, "rebase-merge");

        protected override void Prepare()
        {
            // The worktree's entry is mounted read-only, and the end check visits it.
            Project.RunGit("worktree", "add", "-q", Path.Combine(Scratch, "wt"));
            RunPausedRebase();
            RunCount();
            ReadOnlyStateDir = RunAndRequireSuccess("""
                mkdir .git/rebase-merge && echo "\$(touch prompt-canary)" >.git/rebase-merge/msgnum && echo 2 >.git/rebase-merge/end && chmod 555 .git/rebase-merge
                """);
            RemoveRebaseMerge();
            ReadOnlyGitDir = RunWithGitDirModeAdded(UnixFileMode.UserWrite, """
                mkdir "$HOME/rm" && echo "\$(touch prompt-canary)" >"$HOME/rm/msgnum" && ln -s "$HOME/rm" .git/rebase-merge && chmod 555 .git
                """);
            RemoveRebaseMerge();
            UnlistableGitDir = RunWithGitDirModeAdded(UserAll, """
                mkdir .git/rebase-merge && echo "\$(touch prompt-canary)" >.git/rebase-merge/msgnum && echo 2 >.git/rebase-merge/end && chmod 311 .git
                """);
            RemoveRebaseMerge();
        }

        private CommandResult RunAndRequireSuccess(string script) =>
            Cli.Run("run", "bash", "-c", script).RequireSuccess();

        /// <summary>Runs a session that changes .git's mode, then adds <paramref name="added"/> to it,
        /// as chmod u+w or u+rwx does, even if the session failed.</summary>
        private CommandResult RunWithGitDirModeAdded(UnixFileMode added, string script)
        {
            try
            {
                return RunAndRequireSuccess(script);
            }
            finally
            {
                File.SetUnixFileMode(GitDir, File.GetUnixFileMode(GitDir) | added);
            }
        }

        /// <summary>Removes .git/rebase-merge and what pen quarantined of it, whatever their modes.</summary>
        private void RemoveRebaseMerge()
        {
            Files.DeleteTree(RebaseMerge);
            Files.DeleteTree(RebaseMerge + ".pen-quarantine");
        }

        private void RunPausedRebase()
        {
            Rebase = RunAndRequireSuccess("""
                mkdir .git/rebase-merge .git/sequencer && printf "pick abc one\n  x touch planted-by-a-rebase\n# exec commented\n" >.git/rebase-merge/git-rebase-todo && mkfifo .git/rebase-merge/done && printf "x from-the-sequencer\n" >.git/sequencer/todo
                """);
            var todo = Path.Combine(RebaseMerge, "git-rebase-todo");
            RebaseTodoKept = File.Exists(todo)
                && File.ReadAllText(todo).Contains("planted-by-a-rebase", StringComparison.Ordinal);
            RemoveRebaseMerge();
            Files.DeleteTree(Path.Combine(GitDir, "sequencer"));
        }

        private void RunCount()
        {
            Count = RunAndRequireSuccess("""
                mkdir .git/rebase-merge && echo "\$(touch prompt-canary)" >.git/rebase-merge/msgnum && echo 2 >.git/rebase-merge/end
                """);
            EndKept = Files.IsRegularFile(Path.Combine(RebaseMerge, "end"));
            if (PromptScripts.FirstOrDefault(File.Exists) is { } promptScript)
                Replay = ReplayPrompt(promptScript);
            RemoveRebaseMerge();
        }

        /// <summary>Runs an interactive bash whose prompt is git-prompt.sh's, with the count quarantined and then put
        /// back, and sees whether the canary it would touch appears.</summary>
        private PromptReplay ReplayPrompt(string promptScript)
        {
            var rc = Path.Combine(Scratch, "prompt.rc");
            File.WriteAllText(
                rc, $"source {ShellQuote.Quote(promptScript)}\nPROMPT_COMMAND={ShellQuote.Quote("__git_ps1 a b")}\n");
            var canary = Path.Combine(Project.Dir, "prompt-canary");
            var prompt = new HostCommand("bash", ["--noprofile", "--rcfile", rc, "-i"])
            {
                WorkingDirectory = Project.Dir,
            };
            prompt.Run();
            var ranWhileQuarantined = Files.Exists(canary);
            // If pen left the count in place, the facts on the quarantine have failed already, and it's there to
            // run anyway.
            var msgnum = Path.Combine(RebaseMerge, "msgnum");
            if (File.Exists(msgnum + ".pen-quarantine"))
                File.Move(msgnum + ".pen-quarantine", msgnum);
            prompt.Run();
            var ranWhenPutBack = Files.Exists(canary);
            File.Delete(canary);
            return new PromptReplay(ranWhileQuarantined, ranWhenPutBack);
        }
    }
}
