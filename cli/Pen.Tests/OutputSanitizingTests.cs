using System.Diagnostics;

namespace Pen.Tests;

/// <summary>Runs alone: it captures stderr and stdout, which are process-wide.</summary>
[Collection(nameof(LockAndProcessTests))]
public class OutputSanitizingTests
{
    private const string Esc = "\u001b";

    public static TheoryData<char> ControlChars() =>
        new(['\0', '\a', '\b', '\r', '\v', '\f', '\x1b', '\x7f', '\x80', '\x85', '\x9b', '\x9f',
             '\u061c', '\u200e', '\u200f', '\u2028', '\u2029', '\u202a', '\u202e', '\u2066', '\u2069']);

    [Theory]
    [MemberData(nameof(ControlChars))]
    public void CleanStripsThem_AndFieldEscapesThem(char c)
    {
        Assert.Equal("ab", Out.StripControls($"a{c}b"));
        var field = Out.Field($"a{c}b");
        Assert.DoesNotContain(c, field);
        Assert.StartsWith("a\\", field);
        Assert.EndsWith("b", field);
    }

    [Fact]
    public void CleanKeepsTheMessagesOwnLayout_AndOrdinaryText()
    {
        const string text = "pen: a\n\ttab → é 日本語 🙂 'q' \"d\" $x %d \\ \ufffd";
        Assert.Equal(text, Out.StripControls(text));
    }

    [Fact]
    public void FieldKeepsAValueOnOneLine()
    {
        Assert.Equal("a\\npen: forged\\tb\\r", Out.Field("a\npen: forged\tb\r"));
        Assert.Equal("\\x1b[2J", Out.Field(Esc + "[2J"));
        Assert.Equal("\\u202e", Out.Field("\u202e"));
        Assert.Equal("plain/path with 'quotes' é", Out.Field("plain/path with 'quotes' é"));
    }

    [Fact]
    public void EveryWriterCleans()
    {
        var (err, stdout) = Capture(() =>
        {
            Out.WriteLine("l" + Esc + "1");
            Out.WriteRaw("r" + Esc + "2");
            Out.Prompt("p" + Esc + "3");
            Out.WriteStdout("s" + Esc + "4\n");
        });
        Assert.False(err.Contains(Esc, StringComparison.Ordinal));
        Assert.False(stdout.Contains(Esc, StringComparison.Ordinal));
        Assert.Contains("pen: l1", err);
        Assert.Contains("r2", err);
        Assert.Contains("p3", err);
        Assert.Equal("s4\n", stdout);
    }

    [Fact]
    public void EachWriterGoesToItsOwnStream()
    {
        var (err, stdout) = Capture(() => Out.WriteLine("to stderr"));
        Assert.Equal(("pen: to stderr\n", ""), (err, stdout));
        (err, stdout) = Capture(() => Out.WriteRaw("raw"));
        Assert.Equal(("raw\n", ""), (err, stdout));
        (err, stdout) = Capture(() => Out.Prompt("question? "));
        Assert.Equal(("question? ", ""), (err, stdout));
        (err, stdout) = Capture(() => Out.WriteStdout("to stdout"));
        Assert.Equal(("", "to stdout"), (err, stdout));
    }

    [Theory]
    [InlineData(false, "pen: remove them? [y/N] y\n")]
    [InlineData(true, "pen: remove them? [y/N] ")]
    public void ConfirmEchoesTheAnswer_OnlyWhenTheAnswersArentFromATerminal(bool isTerminal, string expected)
    {
        var answered = false;
        var (err, _) = Capture(() => answered = Out.Confirm("remove them?", new Answers(new StringReader("y\n"), isTerminal)));
        Assert.True(answered);
        Assert.Equal(expected, err);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void WritesDontThrow_AfterTheTerminalIsGone(Type failure)
    {
        var (stderr, stdout) = (Console.Error, Console.Out);
        using var gone = new GoneTerminal((Exception)Activator.CreateInstance(failure)!);
        Console.SetError(gone);
        Console.SetOut(gone);
        try
        {
            Out.WriteLine("line");
            Out.WriteRaw("raw");
            Out.Prompt("question? ");
            Out.WriteStdout("text");
        }
        finally { Console.SetError(stderr); Console.SetOut(stdout); }
        Assert.True(gone.Attempts >= 4, $"{gone.Attempts} writes reached the writer");
    }

    /// <summary>A writer whose terminal hung up: every write and flush throws.</summary>
    private sealed class GoneTerminal(Exception failure) : TextWriter
    {
        public int Attempts { get; private set; }

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override void Write(char value) => Fail();

        public override void Write(string? value) => Fail();

        public override void WriteLine(string? value) => Fail();

        public override void Flush() => Fail();

        private void Fail()
        {
            Attempts++;
            throw failure;
        }
    }

    [Fact]
    public void APermissionErrorsPathIsAField()
    {
        var m = Out.DescribePermissionDenied(new UnauthorizedAccessException("UnauthorizedAccess_IODenied_Path, /a" + Esc + "\nb"));
        Assert.Equal("permission denied: /a\\x1b\\nb", m);
        Assert.Equal(m, Out.Describe(new UnauthorizedAccessException("x, /a" + Esc + "\nb")));
        Assert.Equal("IOException", Out.Describe(new IOException("IO_ResourceKey")));
    }

    [Fact]
    public void WarningsShowHostileNamesAsFields()
    {
        const string evil = "/p/x\x1b[2J\npen: all clear";
        var w = GitCheckWording.Warning("/p", [new Quarantined.Moved(evil + "/.git", evil + "/.git.pen-quarantine", "why")]);
        Assert.Contains("  x\\x1b[2J\\npen: all clear/.git → x\\x1b[2J\\npen: all clear/.git.pen-quarantine", w);
        Assert.DoesNotContain("\npen: all clear", w, StringComparison.Ordinal);
        Assert.Equal(w, Out.StripControls(w));

        var n = GitCheckWording.NotCheckedWarning(evil, "why");
        Assert.False(n.Contains(Esc, StringComparison.Ordinal));
        Assert.DoesNotContain("\npen: all clear", n, StringComparison.Ordinal);

        var (err, _) = Capture(() => GitCheckReport.Print("/p", new() { Moved = [], Failure = null, ModeChanges = [new(evil, UnixFileMode.None, UnixFileMode.UserRead)] }));
        Assert.False(err.Contains(Esc, StringComparison.Ordinal));
        Assert.DoesNotContain("\npen: all clear", err, StringComparison.Ordinal);
        Assert.Contains("changed x\\x1b", err);
    }

    [Fact]
    public void AnUnlistableDirectorysNameIsAField()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        var evil = Path.Combine(repo, "d" + Esc + "\npen: fine");
        Directory.CreateDirectory(evil);
        File.SetUnixFileMode(evil, UnixFileMode.UserExecute);
        try
        {
            var m = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message;
            Assert.Contains("d\\x1b\\npen: fine", m);
            Assert.False(m.Contains(Esc, StringComparison.Ordinal));
            Assert.DoesNotContain("\npen: fine", m, StringComparison.Ordinal);
        }
        finally { File.SetUnixFileMode(evil, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void ANestedReposNameIsAField_InARefusal()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Repo(t.Sub("repo", "n" + Esc + "\npen: fine"));
        var m = Assert.Throws<PenException>(() => RepoGuard.CheckAtStart(repo)).Message;
        Assert.Contains("n\\x1b\\npen: fine", m);
        Assert.False(m.Contains(Esc, StringComparison.Ordinal));
    }

    [Fact]
    public void AnIncludesNameIsAField_InARefusal()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "config", "include.path", "../inc" + Esc + "\tname");
        var m = Assert.Throws<PenException>(() => new MountPlanner(repo, "/nonexistent-home").EnsureTargetsAndPlan()).Message;
        Assert.Contains("inc\\x1b\\tname", m);
        Assert.False(m.Contains(Esc, StringComparison.Ordinal));
    }

    [Fact]
    public void ACommitSubjectIsAField()
    {
        var c = CommitObject.Parse(System.Text.Encoding.UTF8.GetBytes($"tree {new string('1', 40)}\nauthor a <a@b> 1 +0000\ncommitter a <a@b> 1 +0000\n\nsub" + Esc + "[2Jject\n"));
        var line = BranchSigner.FormatListing(new string('a', 40), c, verified: false);
        Assert.Equal("  aaaaaaaaaaaa sub\\x1b[2Jject", line);
    }

    [Fact]
    public void ProxyHostsAreFields_AndOnlyValidOnesAreSuggested()
    {
        var line = Allowlist.FormatBlockedLine(["evil" + Esc + "[2J.com", "ok.example.com"])!;
        Assert.False(line.Contains(Esc, StringComparison.Ordinal));
        Assert.Contains("evil\\x1b[2J.com", line);
        Assert.Contains("→ pen allow ok.example.com (", line);
    }

    [Fact]
    public void PenAllowsListIsSanitized()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        File.WriteAllText(Path.Combine(dir, "proxy.log"), $"2026-01-01T00:00:00Z 1.1.1.1 TCP_DENIED/403 CONNECT evil{Esc}[2J.com:443 -\n");
        var (_, stdout) = Capture(() => new AllowCommand(host, new StartDir(null), RecipeCatalog.Embedded, TimeProvider.System).ListBlocked(new ProjectContext(host, project, UserConfiguration.Empty, ProjectConfiguration.Empty)));
        Assert.False(stdout.Contains(Esc, StringComparison.Ordinal));
        Assert.Contains("evil\\x1b[2j.com", stdout); // HostOf lowercases
        Assert.DoesNotContain("pen allow evil", stdout);
    }

    [Fact]
    public void PenAllowsListShowsTheNewestFew_AndCountsTheRest()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var dir = t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678");
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllLines(Path.Combine(dir, "proxy.log"), Enumerable.Range(0, 5000).Select(i =>
            $"{start.AddSeconds(i):yyyy-MM-ddTHH:mm:ssZ} 1.1.1.1 TCP_DENIED/403 CONNECT h{i}.example:443 -"));
        var (_, stdout) = Capture(() => new AllowCommand(host, new StartDir(null), RecipeCatalog.Embedded, TimeProvider.System).ListBlocked(new ProjectContext(host, project, UserConfiguration.Empty, ProjectConfiguration.Empty)));
        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1 + Allowlist.MaxListed + 2, lines.Length);
        Assert.StartsWith("  h4999.example ", lines[1]);
        Assert.StartsWith("  h4980.example ", lines[Allowlist.MaxListed]);
        Assert.Equal("  and 4980 more, blocked earlier", lines[^2]);
        Assert.Equal("pen allow " + string.Join(' ', Enumerable.Range(4980, 20).Reverse().Select(i => $"h{i}.example")) + "   (add --global for all projects)", lines[^1]);
        Assert.True(stdout.Length < 4000);
    }

    [Fact]
    public void PenPsShowsFields()
    {
        var d = PsCommand.Describe(["run" + Esc + "[2J", "a\nb"]);
        Assert.False(d.Contains(Esc, StringComparison.Ordinal));
        Assert.Contains("run\\x1b[2J", d);
        Assert.DoesNotContain("\n", d, StringComparison.Ordinal);
    }

    private static (string Err, string Out) Capture(Action action)
    {
        var (stderr, stdout) = (Console.Error, Console.Out);
        using var e = new StringWriter();
        using var o = new StringWriter();
        Console.SetError(e);
        Console.SetOut(o);
        try { action(); }
        finally { Console.SetError(stderr); Console.SetOut(stdout); }
        return (e.ToString(), o.ToString());
    }
}

/// <summary>
/// The restore command is pasted into a shell, on a path the agent chose: these
/// run the real printed command, in each shell, on hostile names.
/// </summary>
public class RestoreCommandTests
{
    public static TheoryData<string> Names() => new(
        "a'b",
        "a\"b",
        "$(touch PWNED)",
        "`touch PWNED`",
        "${IFS}x",
        "a\\b",
        "tail\\",
        "a\\'; touch PWNED; '",
        "'; touch PWNED; '",
        "100%d%n%s%",
        "-rf",
        "--",
        "!!",
        "a!b\x01",
        "a\nb",
        "a\n; touch PWNED\n.",
        "\x1b[2Jx",
        "0\u001b1",
        "\u202egit",
        "é\x01日本",
        "\ufffdreplacement\x01",
        "c\x01\"$(touch PWNED)\"",
        "c\x01`touch PWNED`",
        "c\x01'; touch PWNED; '",
        "c\x01\\'; touch PWNED; '",
        "c\x01%d\\047",
        "; touch PWNED #\x01",
        "tab\there");

    private static readonly (string File, string[] Args)[] Shells =
    [
        ("sh", []),
        ("bash", []),
        ("bash", ["--norc", "--noprofile", "-i"]), // history expansion
        ("zsh", ["-f"]),
        ("zsh", ["-f", "-i"]), // history expansion
        ("fish", ["--no-config"]),
    ];

    private static bool Exists(string shell) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(d => File.Exists(Path.Combine(d, shell)));

    private static void RunInShell(string file, string[] args, string cwd, string script)
    {
        var psi = NoTerminal.StartInfo(file, args);
        psi.WorkingDirectory = cwd;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        p.ErrorDataReceived += (_, _) => { };
        p.OutputDataReceived += (_, _) => { };
        p.BeginErrorReadLine();
        p.BeginOutputReadLine();
        p.StandardInput.Write("echo first\n" + script + "\nexit\n");
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(TimeSpan.FromSeconds(30)), $"{file} didn't finish");
    }

    private static void AssertNothingInjected(string root) =>
        Assert.Empty(Directory.EnumerateFiles(root, "PWNED", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }));

    [Theory]
    [MemberData(nameof(Names))]
    public void ThePrintedMvRestoresANestedRepo_InEveryShell_AndInjectsNothing(string name)
    {
        foreach (var (file, args) in Shells)
        {
            if (!Exists(file))
            {
                Assert.Equal("fish", file); // the others have to be there
                continue;
            }
            using var t = new TempDir();
            var repo = Git.Repo(t.Sub("repo"));
            var nested = t.Sub("repo", name);
            Git.Run(nested, "init", "-q");
            var moved = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: null).Moved;
            var q = Assert.IsType<Quarantined.Moved>(Assert.Single(moved));
            Assert.Equal(Path.Combine(nested, ".git"), q.From);
            Assert.False(Directory.Exists(q.From));

            var warning = GitCheckWording.Warning(repo, moved);
            Assert.Equal(warning, Out.StripControls(warning)); // nothing left for a terminal to act on
            var command = warning.Split('\n').Single(l => l.StartsWith("  mv ", StringComparison.Ordinal)).Trim();

            RunInShell(file, args, t.Path, command);

            var shell = $"{file} {string.Join(' ', args)}";
            Assert.True(File.Exists(Path.Combine(q.From, "HEAD")), $"not restored by {shell}: {command}");
            Assert.False(Directory.Exists(q.To), $"{shell}: {command}");
            AssertNothingInjected(t.Path);
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void AQuotedWordIsExactlyTheName_WithALeadingDashMadeHarmless(string name)
    {
        foreach (var (file, args) in Shells)
        {
            if (!Exists(file)) continue;
            using var t = new TempDir();
            var work = t.Sub("work");
            File.WriteAllText(Path.Combine(work, "from"), "");
            RunInShell(file, args, work, $"mv from {Shell.Quote(name)}");
            Assert.True(File.Exists(Path.Combine(work, name)), $"{file} {string.Join(' ', args)}: {Shell.Quote(name)}");
            AssertNothingInjected(t.Path);
        }
    }

    [Theory]
    [InlineData("/p/plain/.git", "'/p/plain/.git'")]
    [InlineData("/p/with space/$x`y`%d!/.git", "'/p/with space/$x`y`%d!/.git'")]
    [InlineData("/p/it's", "'/p/it'\\''s'")]
    [InlineData("/p/é", "'/p/é'")]
    public void WithoutControlCharactersItKeepsTheReadableForm(string path, string quoted) =>
        Assert.Equal(quoted, Shell.Quote(path));

    [Fact]
    public void WithThemItUsesPrintf_WithEveryAwkwardCharacterInOctal()
    {
        Assert.Equal("\"$(printf '/p/\\033\\047\\044\\140\\045\\134\\041\\012')\"", Shell.Quote("/p/\x1b'$`%\\!\n"));
        Assert.Equal("\"$(printf './-rf\\001')\"", Shell.Quote("-rf\x01"));
        Assert.Equal("\"$(printf '\\342\\200\\256')\"", Shell.Quote("\u202e"));
    }

    [Fact]
    public void ANameEndingInANewlineGetsNoCommand_RatherThanAWrongOne()
    {
        using var t = new TempDir();
        var repo = Git.Repo(t.Sub("repo"));
        Git.Run(repo, "init", "-q", "--bare", ".git/modules/x\n");
        var moved = GitCheck.RunWithoutSession(repo, startedWithGit: true, commonGitDir: null, modulesAtStart: []).Moved;
        var w = GitCheckWording.Warning(repo, moved);
        Assert.Contains("pen can't write a command for", w);
        Assert.Contains("x\\n, since the name ends in a newline", w);
        Assert.DoesNotContain("  mv ", w);
    }
}
