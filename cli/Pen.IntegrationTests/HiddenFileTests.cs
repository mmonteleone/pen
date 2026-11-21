namespace Pen.IntegrationTests;

/// <summary>Files hide matches read empty in the session, a hidden directory lists empty, a [replace] target shows
/// its source's copy, none of them can be written, and the host's files never change.</summary>
/// <remarks>
/// Where the session ignores case, another spelling of a hidden or replaced file gets past its single-file mount:
/// that limit is checked here too, the way <see cref="DirectoryMountSpellingTests"/> checks the git mounts', and
/// Pen warns about it.
/// </remarks>
public sealed class HiddenFileTests(HiddenFileTests.Fixture fixture) : IClassFixture<HiddenFileTests.Fixture>
{
    private const string Summary =
        "pen: hidden from the agent: apps/web/.env, scripts/env.ps1, secrets/; replaced: .env";

    private const string CaseWarning = "pen: warning: this project's filesystem isn't known to be case-sensitive: " +
        "the agent may read a hidden or replaced file under another spelling (.ENV for .env); hide its directory " +
        "to keep it out of reach";

    [Fact(DisplayName = "pen says what's hidden from the agent and what's replaced")]
    public void SaysWhatsHidden() => AssertSaw(Summary);

    [Fact(DisplayName = "a hidden file reads empty in the session")]
    public void HiddenFileReadsEmpty() => AssertSaw("read: apps/web/.env: []");

    [Fact(DisplayName = "...one a glob matched too")]
    public void GlobbedFileReadsEmpty() => AssertSaw("read: scripts/env.ps1: []");

    [Fact(DisplayName = "a hidden directory lists empty")]
    public void HiddenDirListsEmpty() => AssertSaw("list: secrets: []");

    [Fact(DisplayName = "...and the file in it isn't there")]
    public void FileInHiddenDirIsAbsent() => AssertSaw("absent: secrets/key");

    [Fact(DisplayName = "the replaced file shows its replacement")]
    public void ReplacedFileShowsReplacement() => AssertSaw($"read: .env: [{Fixture.AgentEnv}]");

    [Fact(DisplayName = "a hidden file can't be written in the session")]
    public void HiddenFileIsNotWritable() => AssertRefused("apps/web/.env");

    [Fact(DisplayName = "...nor the replaced file")]
    public void ReplacedFileIsNotWritable() => AssertRefused(".env");

    [Fact(DisplayName = "...nor a new file in the hidden directory")]
    public void HiddenDirIsNotWritable() => AssertRefused("secrets/new");

    // The mask files' modes alone refuse a write; only a read-only mount refuses a chmod that would allow one.
    [Fact(DisplayName = "a hidden file is mounted read-only")]
    public void HiddenFileIsMountedReadOnly() => AssertReadOnlyMount("apps/web/.env");

    [Fact(DisplayName = "...and the replaced file")]
    public void ReplacedFileIsMountedReadOnly() => AssertReadOnlyMount(".env");

    [Fact(DisplayName = "...and the hidden directory")]
    public void HiddenDirIsMountedReadOnly() => AssertReadOnlyMount("secrets");

    // Output cut short must fail here, not look like a session whose writes all failed.
    [Fact(DisplayName = "the session got to the end")]
    public void SessionGotToTheEnd() => AssertSaw("finished");

    [Fact(DisplayName = "the project's files are byte-identical after the session")]
    public void HostFilesAreUnchanged() => Assert.Equal(fixture.TreeBefore, fixture.TreeAfter);

    [Fact(DisplayName = "where the host ignores case, pen warns that another spelling gets past a file's mount")]
    public void WarnsWhereTheHostFoldsCase()
    {
        Assert.SkipUnless(fixture.HostFolds, "skipped: the host is case-sensitive, so .ENV isn't .env");

        AssertSaw(CaseWarning);
    }

    [Fact(DisplayName = "where the host is case-sensitive, pen doesn't warn about other spellings")]
    public void NoWarningWhereTheHostIsCaseSensitive()
    {
        Assert.SkipWhen(fixture.HostFolds, "skipped: the host ignores case");
        Assert.True(fixture.Session.OutputComplete, fixture.Session.Describe());

        Assert.DoesNotContain(CaseWarning, fixture.Session.OutputLines);
    }

    [Fact(DisplayName = "where the session ignores case, .ENV reads the host's .env (the limit warned about)")]
    public void OtherSpellingReadsTheHostsFile()
    {
        Assert.SkipUnless(fixture.SessionFolds, fixture.NoFoldingNote);

        AssertSaw($"read: .ENV: [{Fixture.HostEnv}]");
    }

    [Fact(DisplayName = "...and pen warned about it")]
    public void WarnedWhereTheSessionFoldsCase()
    {
        Assert.SkipUnless(fixture.SessionFolds, fixture.NoFoldingNote);

        AssertSaw(CaseWarning);
    }

    [Fact(DisplayName = "nothing is left running")]
    public void NothingIsLeftRunning() => Assert.Equal(0, fixture.ContainersAtEnd);

    /// <summary>Asserts the session printed this whole line.</summary>
    private void AssertSaw(string line) =>
        Assert.True(fixture.Session.OutputLines.Contains(line), $"no line \"{line}\"; {fixture.Session.Describe()}");

    /// <summary>Asserts the session's write to <paramref name="path"/> failed.</summary>
    private void AssertRefused(string path)
    {
        var refused = fixture.Session.HasLineStartingWith($"refused: {path}: ")
            && !fixture.Session.OutputLines.Contains($"wrote: {path}");

        Assert.True(refused, $"no failed write of {path}; {fixture.Session.Describe()}");
    }

    /// <summary>Asserts the session's chmod u+w of <paramref name="path"/> failed on a read-only mount.</summary>
    private void AssertReadOnlyMount(string path)
    {
        var prefix = $"chmod refused: {path}: ";
        var refused = fixture.Session.OutputLines.Any(line =>
            line.StartsWith(prefix, StringComparison.Ordinal)
            && line.Contains("Read-only file system", StringComparison.Ordinal));

        Assert.True(refused, $"no chmod of {path} refused by a read-only mount; {fixture.Session.Describe()}");
    }

    /// <summary>Makes a project with secrets in files, hides and replaces them, and runs a session that reads,
    /// lists and writes them.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        /// <summary>The host's .env.</summary>
        internal const string HostEnv = "SECRET=host-only";

        /// <summary>The replacement for .env.</summary>
        internal const string AgentEnv = "SECRET=";

        private const string CaseMarker = "integration-case-marker";

        internal string Token { get; } = $"integration-case-{Random.Shared.Next(32768)}{Random.Shared.Next(32768)}";

        internal CommandResult Session { get; private set; } = null!;

        /// <summary>Gets whether the host's project dir ignores case.</summary>
        internal bool HostFolds { get; private set; }

        /// <summary>Gets whether the session read the case marker under its upper-case name.</summary>
        internal bool SessionFolds { get; private set; }

        /// <summary>Gets the skip note for a session that doesn't fold case.</summary>
        internal string NoFoldingNote { get; private set; } = null!;

        /// <summary>Gets every project file outside .git, by path, with its hash, before the session.</summary>
        internal IReadOnlyList<string> TreeBefore { get; private set; } = [];

        /// <summary>Gets the same after the session.</summary>
        internal IReadOnlyList<string> TreeAfter { get; private set; } = [];

        internal int ContainersAtEnd { get; private set; }

        protected override void Prepare()
        {
            WriteProjectFile(".env", HostEnv);
            WriteProjectFile("apps/web/.env", "WEB_SECRET=host-only");
            WriteProjectFile("scripts/env.ps1", "$env:SECRET = 'host-only'");
            WriteProjectFile("secrets/key", "host-only-key");
            WriteProjectFile(".pen/agent.env", AgentEnv);
            WriteProjectFile(".pen/config.toml", """
                hide = [".env", "*.ps1", "secrets/"]

                [replace]
                ".env" = ".pen/agent.env"
                """);
            WriteProjectFile(CaseMarker, Token);
            HostFolds = Files.Exists(Path.Combine(Project.Dir, CaseMarker.ToUpperInvariant()));
            TreeBefore = HashTree();

            Session = Cli.Run("run", "bash", "-c", """
                export LC_ALL=C
                seen() { echo "read: $1: [$(cat "$1" 2>&1)]"; }
                attempt() {
                  local err
                  if err=$({ echo integration >>"$1"; } 2>&1); then echo "wrote: $1"
                  else echo "refused: $1: $err"; fi
                }
                seen .env; seen apps/web/.env; seen scripts/env.ps1
                echo "list: secrets: [$(ls -A secrets 2>&1)]"
                if [ -e secrets/key ]; then echo "present: secrets/key"; else echo "absent: secrets/key"; fi
                attempt apps/web/.env
                attempt .env
                attempt secrets/new
                change() {
                  local err
                  if err=$(chmod u+w "$1" 2>&1); then echo "chmodded: $1"
                  else echo "chmod refused: $1: $err"; fi
                }
                change apps/web/.env
                change .env
                change secrets
                seen INTEGRATION-CASE-MARKER
                seen .ENV
                echo finished
                """).RequireSuccess();

            SessionFolds = Session.OutputLines.Contains($"read: INTEGRATION-CASE-MARKER: [{Token}]");
            NoFoldingNote = HostFolds
                ? "the host ignores case but the session doesn't: only the host's files could be checked"
                : "skipped: this filesystem is case-sensitive, so .ENV isn't .env";
            TreeAfter = HashTree();
            ContainersAtEnd = Docker.CountContainers(Key);
        }

        private void WriteProjectFile(string relative, string text)
        {
            var path = Path.Combine(Project.Dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text + "\n");
        }

        /// <summary>Lists every file and directory in the project outside .git, with each file's hash.</summary>
        private IReadOnlyList<string> HashTree() =>
        [
            .. Directory.EnumerateFileSystemEntries(Project.Dir, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(Project.Dir, path))
                .Where(relative => relative != ".git" && !relative.StartsWith(".git/", StringComparison.Ordinal))
                .Select(relative => Directory.Exists(Path.Combine(Project.Dir, relative))
                    ? relative + "/"
                    : $"{relative} {Files.HashFile(Path.Combine(Project.Dir, relative))}")
                .Order(StringComparer.Ordinal),
        ];
    }
}
