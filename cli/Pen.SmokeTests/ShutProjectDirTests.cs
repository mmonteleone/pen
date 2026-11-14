using System.Text.RegularExpressions;

namespace Pen.SmokeTests;

/// <summary>A session that plants an fsmonitor and then shuts pen's own cwd (chmod 000 .): the end still runs,
/// says git wasn't checked and prints the one command that fixes it, which restores the config.</summary>
public sealed class ShutProjectDirTests(ShutProjectDirTests.Fixture fixture)
    : IClassFixture<ShutProjectDirTests.Fixture>
{
    [Fact(DisplayName = "chmod 000 . in a session: it's really shut")]
    public void ProjectIsShut() => Assert.Equal("0", fixture.ModeAfter);

    [Fact(DisplayName = "...and pen warns that git wasn't checked")]
    public void WarnsGitWasntChecked() => SmokeAssert.Contains(fixture.Shut, "git wasn't checked");

    [Fact(DisplayName = "...and takes the stack down")]
    public void TakesTheStackDown() => Assert.Equal(0, fixture.ContainersAfterShut);

    [Fact(DisplayName = "...and a command started there says why it can't run")]
    public void CommandThereSaysWhy() =>
        Assert.True(
            Regex.IsMatch(fixture.StartedThere.Output, "current directory|can.t read"),
            fixture.StartedThere.Describe());

    [Fact(DisplayName = "...and prints a command to fix it")]
    public void PrintsAFix() =>
        Assert.True(fixture.FixLine.Contains("chmod u+rwx", StringComparison.Ordinal), fixture.Shut.Describe());

    [Fact(DisplayName = "...which restores the config")]
    public void FixRestoresTheConfig() => Assert.True(fixture.FixRan && !fixture.ConfigHasFsmonitor, NoFix);

    [Fact(DisplayName = "...so the planted fsmonitor doesn't run")]
    public void FsmonitorDoesntRun() => Assert.True(fixture.FixRan && !fixture.CanaryRan, NoFix);

    [Fact(DisplayName = "...and ends that session")]
    public void FixEndsTheSession() => Assert.True(fixture.FixRan && fixture.ContainersAfterFix == 0, NoFix);

    [Fact(DisplayName = "...and the next session starts normally")]
    public void NextSessionStarts() => SmokeAssert.Succeeded(fixture.Next);

    private const string NoFix = "no fix line was printed, so the fix was not run";

    /// <summary>Runs the session that shuts the project, then pen from a shell already in it, then the fix line from
    /// outside, host git in the project and the next session.</summary>
    public sealed class Fixture : SmokeFixture
    {
        internal CommandResult Shut { get; private set; } = null!;

        /// <summary>Gets the project's mode after the session, as stat -c %a prints it.</summary>
        internal string ModeAfter { get; private set; } = null!;

        internal int ContainersAfterShut { get; private set; }

        internal CommandResult StartedThere { get; private set; } = null!;

        /// <summary>Gets the text after "as one line: " in the session's output, one line per match.</summary>
        internal string FixLine { get; private set; } = null!;

        /// <summary>Gets whether a fix line was printed and run.</summary>
        internal bool FixRan { get; private set; }

        internal bool ConfigHasFsmonitor { get; private set; }

        internal bool CanaryRan { get; private set; }

        internal int ContainersAfterFix { get; private set; }

        internal CommandResult Next { get; private set; } = null!;

        protected override void Prepare()
        {
            var canary = Path.Combine(Scratch, "shut-canary");
            var hook = Path.Combine(Scratch, "shut-hook");
            File.WriteAllText(hook, $"#!/bin/sh\ntouch {ShellQuote.Quote(canary)}\n");
            File.SetUnixFileMode(hook, Files.Mode755);
            // smoke.sh's next pen ran from its shell, which was in the project before it was shut: a shell started
            // there now, waiting for a line, stands in for it.
            const string WaitThenRun = "read -r _ && exec \"$0\" run true </dev/null";
            var waiting = Cli.Wrap("/bin/sh", "-c", WaitThenRun, SmokeEnvironment.PenPath);
            var shell = Track((waiting with { KeepStdinOpen = true }).Start());
            try
            {
                var plant = $"printf '[core]\\n fsmonitor = {hook}\\n' >> .git/CONFIG; chmod 000 .";
                Shut = Cli.Run("run", "bash", "-c", plant);
                ModeAfter = Files.GetMode(Project.Dir);
                ContainersAfterShut = Docker.CountContainers(Key);
                shell.WriteStdin("\n");
                shell.CloseStdin();
                StartedThere = shell.WaitForExit();
                RunFix();
                CanaryRan = FixRan && RunHostGitStatus(canary);
                ContainersAfterFix = Docker.CountContainers(Key);
            }
            finally
            {
                File.SetUnixFileMode(Project.Dir, Files.Mode755);
            }
            Next = Cli.Run("run", "true");
            File.Delete(Path.Combine(Project.Dir, ".git", "config.pen-quarantine"));
        }

        /// <summary>Runs the fix line from the scratch directory, with pen as $PEN in place of its pen.</summary>
        private void RunFix()
        {
            FixLine = string.Join(
                '\n',
                Shut.OutputLines.Select(line => Regex.Match(line, ".*as one line: (.*)$"))
                    .Where(match => match.Success).Select(match => match.Groups[1].Value));
            if (FixLine.Length == 0)
            {
                return;
            }

            const string Retry = "pen true";
            var command = FixLine.EndsWith(Retry, StringComparison.Ordinal) ? FixLine[..^Retry.Length] : FixLine;
            Cli.WithWorkingDirectory(Scratch).WithEnvironment("PEN", SmokeEnvironment.PenPath)
                .Wrap("bash", "-c", command + "\"$PEN\" run true </dev/null").Run().RequireSuccess();
            FixRan = true;
            ConfigHasFsmonitor = ReadsFsmonitor(Path.Combine(Project.Dir, ".git", "config"));
        }

        /// <summary>Runs host git status in the project, which would run a planted fsmonitor.</summary>
        /// <returns>True if the hook's canary appeared.</returns>
        private bool RunHostGitStatus(string canary)
        {
            SmokeGit.CreateCommand(Project.Dir, "status").Run().RequireSuccess();
            return Files.Exists(canary);
        }

        /// <summary>Checks whether a file has fsmonitor in it, as grep -q does: a file that can't be read
        /// doesn't.</summary>
        private static bool ReadsFsmonitor(string path)
        {
            try
            {
                return File.ReadAllText(path).Contains("fsmonitor", StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }
}
