namespace Pen.SmokeTests;

/// <summary>The read-only mounts of .git/config, .git/hooks and .pen hold under other spellings of the name (.GIT,
/// .git/HOOKS, .PEN) where the session's filesystem ignores case.</summary>
/// <remarks>
/// Only the session can say whether it folds case: the host doing so doesn't prove the sandbox's view does. A
/// session that can't see the host's files, or can't write anywhere, proves nothing, so that's checked first.
/// </remarks>
public sealed class DirectoryMountSpellingTests(DirectoryMountSpellingTests.Fixture fixture)
    : IClassFixture<DirectoryMountSpellingTests.Fixture>
{
    private const string Marker = Fixture.Marker;

    private string Token => fixture.Token;

    [Fact(DisplayName = "the session reads what the host put in .git")]
    public void ReadsGit() => AssertSaw($"read: .git/{Marker}: {Token}");

    [Fact(DisplayName = "...in .git/hooks")]
    public void ReadsHooks() => AssertSaw($"read: .git/hooks/{Marker}: {Token}");

    [Fact(DisplayName = "...and in .pen")]
    public void ReadsPen() => AssertSaw($"read: .pen/{Marker}: {Token}");

    [Fact(DisplayName = "a normal .git/ path is writable in the session")]
    public void GitPathIsWritable() => AssertSaw("wrote: .git/.smoke27-plain");

    [Fact(DisplayName = "...and reaches the host")]
    public void GitPathReachesTheHost() => AssertWroteOnHost(".git/.smoke27-plain");

    [Fact(DisplayName = "a project file is writable in the session")]
    public void ProjectFileIsWritable() => AssertSaw("wrote: smoke27-project");

    [Fact(DisplayName = "...and reaches the host")]
    public void ProjectFileReachesTheHost() => AssertWroteOnHost("smoke27-project");

    // Output cut short must fail here, not look like a session that doesn't fold case.
    [Fact(DisplayName = "the session tried the other spellings and got to the end")]
    public void SessionGotToTheEnd() => AssertSaw("finished");

    [Fact(DisplayName = "the session wrote through none of the other spellings")]
    public void WroteThroughNoOtherSpelling()
    {
        Assert.SkipWhen(fixture.Folds > 0, "the session ignores case: the other spellings' mounts are checked instead");

        Assert.False(
            fixture.Session.OutputLines.Any(line => line.StartsWith("wrote: .GIT/", StringComparison.Ordinal)
                || line.StartsWith("wrote: .git/HOOKS/", StringComparison.Ordinal)
                || line.StartsWith("wrote: .PEN/", StringComparison.Ordinal)),
            fixture.Session.Describe());
    }

    [Fact(DisplayName = "the session reads the same files as .GIT, .git/HOOKS and .PEN")]
    public void ReadsTheSameFiles()
    {
        RequireFolding();

        Assert.True(fixture.Folds == 3, $"{fixture.Folds} of 3; {fixture.Session.Describe()}");
    }

    [Fact(DisplayName = "...and .GIT/config is the host's .git/config")]
    public void GitConfigIsTheHosts()
    {
        RequireFolding();

        AssertSaw($"read: .GIT/config: # {Marker} {Token}");
    }

    [Fact(DisplayName = "a new file in .GIT is writable in the session")]
    public void NewFileInGitIsWritable()
    {
        RequireFolding();

        AssertSaw("wrote: .GIT/.smoke27-alias");
    }

    [Fact(DisplayName = "...and reaches the host's .git")]
    public void NewFileReachesTheHostsGit()
    {
        RequireFolding();

        AssertWroteOnHost(".git/.smoke27-alias");
    }

    [Fact(DisplayName = "writing .GIT/config hits the read-only mount")]
    public void GitConfigIsReadOnly()
    {
        RequireFolding();

        AssertSaw("read-only: .GIT/config");
    }

    [Fact(DisplayName = "writing .git/HOOKS/x hits the read-only mount")]
    public void HooksIsReadOnly()
    {
        RequireFolding();

        AssertSaw("read-only: .git/HOOKS/x");
    }

    [Fact(DisplayName = "writing .PEN/x hits the read-only mount")]
    public void PenIsReadOnly()
    {
        RequireFolding();

        AssertSaw("read-only: .PEN/x");
    }

    [Fact(DisplayName = ".git/config is unchanged on the host")]
    public void ConfigIsUnchanged() => Assert.Equal(fixture.ConfigHashBefore, fixture.ConfigHashAfter);

    [Fact(DisplayName = ".git/hooks is unchanged on the host")]
    public void HooksAreUnchanged() => Assert.Equal(fixture.HooksBefore, fixture.HooksAfter);

    [Fact(DisplayName = ".pen is unchanged on the host")]
    public void PenIsUnchanged() => Assert.Equal(fixture.PenBefore, fixture.PenAfter);

    [Fact(DisplayName = "nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    /// <summary>Asserts the session printed this whole line (saw).</summary>
    private void AssertSaw(string line) =>
        Assert.True(fixture.Session.OutputLines.Contains(line), $"no line \"{line}\"; {fixture.Session.Describe()}");

    /// <summary>Asserts a file on the host holds what the session wrote, as $(cat file) = smoke27.</summary>
    private void AssertWroteOnHost(string path)
    {
        var text = fixture.ReadHostFile(path);

        Assert.True(text == "smoke27", $"{path}: {text ?? "(can't be read)"}");
    }

    /// <summary>Skips a check that only means something where the session folds case, with smoke.sh's note.</summary>
    private void RequireFolding() => Assert.SkipWhen(fixture.Folds == 0, fixture.NoFoldingNote);

    /// <summary>Puts a marker with a random token in .git, .git/hooks, .pen and .git/config, then runs a session
    /// that reads and writes them by every spelling.</summary>
    public sealed class Fixture : SmokeFixture
    {
        /// <summary>The marker file's name.</summary>
        internal const string Marker = ".smoke27-marker";

        internal string Token { get; } = $"smoke27-{Random.Shared.Next(32768)}{Random.Shared.Next(32768)}";

        internal CommandResult Session { get; private set; } = null!;

        /// <summary>Gets how many of the other spellings the session read the marker through.</summary>
        internal int Folds { get; private set; }

        /// <summary>Gets smoke.sh's note for a session that doesn't fold case.</summary>
        internal string NoFoldingNote { get; private set; } = null!;

        internal string ConfigHashBefore { get; private set; } = null!;

        internal string ConfigHashAfter { get; private set; } = null!;

        internal IReadOnlyList<string> HooksBefore { get; private set; } = [];

        internal IReadOnlyList<string> HooksAfter { get; private set; } = [];

        internal IReadOnlyList<string> PenBefore { get; private set; } = [];

        internal IReadOnlyList<string> PenAfter { get; private set; } = [];

        internal int ContainersAtEnd { get; private set; }

        private string GitDir => Path.Combine(Project.Dir, ".git");

        private string HooksDir => Path.Combine(GitDir, "hooks");

        private string PenDir => Path.Combine(Project.Dir, ".pen");

        /// <summary>Reads a project file as $(cat file) gives it.</summary>
        /// <param name="path">The file, relative to the project.</param>
        /// <returns>Its text without trailing newlines, or null if it can't be read.</returns>
        internal string? ReadHostFile(string path)
        {
            try
            {
                return File.ReadAllText(Path.Combine(Project.Dir, path)).TrimEnd('\n');
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        protected override void Prepare()
        {
            Directory.CreateDirectory(HooksDir);
            Directory.CreateDirectory(PenDir);
            var config = Path.Combine(GitDir, "config");
            File.AppendAllText(config, $"# {Marker} {Token}\n");
            foreach (var dir in new[] { GitDir, HooksDir, PenDir })
                File.WriteAllText(Path.Combine(dir, Marker), Token + "\n");
            ConfigHashBefore = Files.HashFile(config);
            HooksBefore = Files.ListNames(HooksDir);
            PenBefore = Files.ListNames(PenDir);

            Session = Cli.Run("run", "bash", "-c", """
                export LC_ALL=C
                M=.smoke27-marker
                seen() { echo "read: $1: $(cat "$1" 2>/dev/null)"; }
                attempt() {
                  local err
                  if err=$({ echo smoke27 >>"$1"; } 2>&1); then echo "wrote: $1"
                  elif [[ $err == *"Read-only file system"* ]]; then echo "read-only: $1"
                  else echo "refused: $1: $err"; fi
                }
                seen ".git/$M"; seen ".git/hooks/$M"; seen ".pen/$M"
                attempt .git/.smoke27-plain
                attempt smoke27-project
                seen ".GIT/$M"; seen ".git/HOOKS/$M"; seen ".PEN/$M"
                echo "read: .GIT/config: $(grep -F "$M" .GIT/config 2>/dev/null)"
                attempt .GIT/.smoke27-alias
                attempt .GIT/config
                attempt .git/HOOKS/x
                attempt .PEN/x
                echo finished
                """).RequireSuccess();

            string[] spellings = [$".GIT/{Marker}", $".git/HOOKS/{Marker}", $".PEN/{Marker}"];
            Folds = spellings.Count(path => Session.OutputLines.Contains($"read: {path}: {Token}"));
            NoFoldingNote = Files.Exists(Path.Combine(Project.Dir, ".GIT", Marker))
                ? "the host ignores case but the session doesn't: only the host's files could be checked"
                : "skipped: this filesystem is case-sensitive, so .GIT isn't .git";

            ConfigHashAfter = Files.HashFile(config);
            HooksAfter = Files.ListNames(HooksDir);
            PenAfter = Files.ListNames(PenDir);
            ContainersAtEnd = Docker.CountContainers(Key);
        }
    }
}
