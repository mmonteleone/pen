using System.Diagnostics;
using System.Text;

namespace Pen.Tests;

/// <summary>
/// Holds <see cref="GitConfigFile"/> against real git. A file git accepts must
/// parse to exactly what git lists, or be refused; a file git rejects must be
/// refused. Never a silent difference.
/// </summary>
public class GitConfigDifferentialTests
{
    private const int FuzzSeed = 20261109;
    private const int FuzzCases = 1200;

    /// <summary>What git lists for a file, as name and value; null if it rejects the file.</summary>
    private static List<(string Name, string Value)>? GitLists(string file)
    {
        var psi = new ProcessStartInfo("git", ["config", "--list", "-z", "--file", file])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        var r = Process.RunAndCaptureText(psi);
        if (r.ExitStatus.ExitCode != 0) return null;
        var list = new List<(string, string)>();
        foreach (var item in r.StandardOutput.Split('\0'))
        {
            if (item.Length == 0) continue;
            int nl = item.IndexOf('\n', StringComparison.Ordinal);
            list.Add(nl < 0 ? (item, "true") : (item[..nl], item[(nl + 1)..]));
        }
        return list;
    }

    /// <summary>What Pen reads from a file, in git's terms; null if it refuses the file.</summary>
    private static List<(string Name, string Value)>? PenReads(string file)
    {
        try
        {
            return GitConfigFile.ParseFile(file).Select(e =>
                (e.Section.Length == 0 && e.Subsection == null ? e.Key
                    : e.Subsection == null ? $"{e.Section}.{e.Key}"
                    : $"{e.Section}.{e.Subsection}.{e.Key}", e.Value)).ToList();
        }
        catch (PenException) { return null; }
    }

    private enum Outcome { Parsed, RefusedThoughGitAccepts, BothReject }

    /// <summary>Compares the two on one file. Returns the outcome, or fails the test on a silent difference.</summary>
    private static Outcome Compare(string file, string describe)
    {
        var git = GitLists(file);
        var pen = PenReads(file);
        if (git == null)
        {
            Assert.True(pen == null, $"git rejects this but pen reads it: {describe}");
            return Outcome.BothReject;
        }
        if (pen == null)
        {
            // The one thing Pen refuses that git takes, so real configs can't be refused for anything else.
            Assert.True(File.ReadAllBytes(file).Contains((byte)0), $"pen refuses this though git reads it: {describe}");
            return Outcome.RefusedThoughGitAccepts;
        }
        Assert.True(git.SequenceEqual(pen),
            $"pen reads this differently from git: {describe}\n git: {string.Join(" | ", git)}\n pen: {string.Join(" | ", pen)}");
        return Outcome.Parsed;
    }

    private static byte[] B(string s) => Encoding.Latin1.GetBytes(s);

    private static string Show(byte[] bytes) => Out.Field(Encoding.Latin1.GetString(bytes));

    private static readonly string[] Headers =
    [
        "[core]", "[CORE]", "[include]", "[includeIf \"gitdir:/x/\"]", "[core \"x\"]", "[core.x]", "[x][core]", "[x][include]",
        "[a.b.c \"d\"]", "[ \"x\"]", "[]", "[core", "[a b]", "[a \"b\\\"c\"]", "[a \"b\\\\c\"]", "[a \"b\\xc\"]", "[core ]", "[core \"x\" ]",
        "[a \"x\0y\"]", "[a \"\xff\"]", "[a\t\"b\"]", "[a \n\"b\"]", "[-a]", "[a_b]", "[.core]", "[core.]", "[core \"\"]", "[a\"b\"]", "[a \"b\n\"]", "[\xa0core]", "[core]\xa0",
    ];

    private static readonly string[] Keys =
        ["hooksPath", "HOOKSPATH", "path", "Path", "a-b", "a1", "1a", "-a", "a_b", "a.b", "\xa0path", "path\xa0", "pa\0th", "p"];

    private static readonly string[] Separators = ["=", " = ", "\t=\t", " ", "\\\n=", "", " \\\n ="];

    private static readonly string[] Values =
    [
        "../inc", ".h", "\"../inc\"", "\"\" ../inc", "a\"b\"c", "../inc\xa0", "\xa0../inc", "\f../inc", "../inc\f", "\v", "../inc \\\n", "../inc\\",
        "a\\\nb", "a\\\n  b", "\"a\\\nb\"", "\"a\nb\"", "a\\nb", "a\\tb", "a\\bb", "a\\qb", "a\\\"b", "a\\\\b", "a#b", "a;b", "\"a#b\"", "a #b \\", "a ;b",
        "a\0b", "\0", "\"a\0\"", "\xe2\x80\x83x", "x\xe2\x80\x83", "\xff", "a\rb", "a\r", "\"", "a\"", " x  y  ", "\"x \" y ", "x \"\" ", "x\t", "\"x\"\t#c", "", "\"\"", "  ", " \" \" ", "x\\ ", "\\", "a \\\n b \\\n", "~me/../x",
    ];

    private static readonly string[] Ends = ["\n", "\n", "\n", "\r\n", "\r", "", " ", " # c\n", " ; c \\\n", "\\\n", "\0\n", "\t\n"];

    private static readonly string[] Comments = ["# c\n", "; c \\\n", "#\0\n", "# \"\n", "\n", " \n", "\t\r\n", "\r", "\f\n", "\xa0\n", "\0\n"];

    /// <summary>One file: mostly plausible lines from the awkward pieces above, sometimes with a stray byte.</summary>
    private static byte[] Generate(Random r)
    {
        var sb = new StringBuilder();
        switch (r.Next(40))
        {
            case 0: sb.Append("\xef\xbb\xbf"); break;
            case 1: sb.Append("\xef\xbb"); break;
        }
        int pieces = r.Next(1, 5);
        for (int i = 0; i < pieces; i++)
        {
            string P(string[] from) => from[r.Next(from.Length)];
            // Easy pieces most of the time, or nearly every file is one git rejects.
            string Easy(string[] from, string easy) => r.Next(3) == 0 ? P(from) : easy;
            switch (r.Next(6))
            {
                case 0: sb.Append(P(Comments)); break;
                case 1: sb.Append(P(Headers)).Append(r.Next(2) == 0 ? "\n" : ""); break;
                default:
                    if (r.Next(2) == 0) sb.Append(Easy(Headers, "[core]")).Append(r.Next(4) == 0 ? "" : "\n");
                    sb.Append(Easy(["\t", " ", "\r", "\f", "\xa0"], "\t"));
                    sb.Append(Easy(Keys, r.Next(2) == 0 ? "path" : "hooksPath"));
                    sb.Append(Easy(Separators, " = ")).Append(P(Values)).Append(Easy(Ends, "\n"));
                    break;
            }
        }
        var bytes = B(sb.ToString());
        if (r.Next(10) == 0 && bytes.Length > 0) bytes[r.Next(bytes.Length)] = (byte)r.Next(256);
        return bytes;
    }

    [Fact]
    public void AFuzzedFile_ParsesAsGitParsesIt_OrIsRefused()
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "config");
        int cases = int.TryParse(Environment.GetEnvironmentVariable("PEN_FUZZ_CASES"), out var n) ? n : FuzzCases;
        var r = new Random(FuzzSeed);
        var counts = new Dictionary<Outcome, int>();
        for (int i = 0; i < cases; i++)
        {
            var bytes = Generate(r);
            File.WriteAllBytes(file, bytes);
            var outcome = Compare(file, $"case {i}: {Show(bytes)}");
            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
        }
        TestContext.Current.SendDiagnosticMessage($"fuzz: {string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}"))}");
        if (Environment.GetEnvironmentVariable("PEN_FUZZ_REPORT") is { } report)
            File.WriteAllText(report, string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}")) + "\n");
        // A fuzz that only ever hit one outcome wouldn't be testing the grammar.
        Assert.True(counts.GetValueOrDefault(Outcome.Parsed) > cases / 10, "too few files parsed");
        Assert.True(counts.GetValueOrDefault(Outcome.BothReject) > cases / 10, "too few files rejected");
    }

    /// <summary>A sample from outside the suite, for one-off runs: PEN_GITCONFIG_SAMPLE names a file listing paths, one per line.</summary>
    [Fact]
    public void AnOutsideSample_ParsesAsGitParsesIt_OrIsRefused()
    {
        var list = Environment.GetEnvironmentVariable("PEN_GITCONFIG_SAMPLE");
        Assert.SkipWhen(list == null, "no PEN_GITCONFIG_SAMPLE");
        var counts = new Dictionary<Outcome, int>();
        var refused = new List<string>();
        foreach (var path in File.ReadAllLines(list!))
        {
            var outcome = Compare(path, path);
            counts[outcome] = counts.GetValueOrDefault(outcome) + 1;
            if (outcome != Outcome.Parsed) refused.Add($"{outcome} {path}");
        }
        File.WriteAllLines(list + ".report", [string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}")), .. refused]);
    }

    // What the fuzz found once, as named replays through the real protections.

    public static TheoryData<string, string> HiddenIncludes => new()
    {
        { "stacked sections", "[x][include]path=../inc\n" },
        { "empty quotes first", "[include]\npath=\"\" ../inc\n" },
        { "NBSP is part of the value", "[include]\npath=../inc\xc2\xa0\n" },
        { "form feed is part of the value", "[include]\npath=../inc\f\n" },
        { "whitespace then continuation", "[include]\npath=../inc \\\n\n" },
        { "backslash at the end of the file", "[include]\npath=../inc\\" },
        { "comment ending in a backslash", "[include]\n# c \\\npath=../inc\n" },
        { "CRLF", "[include]\r\npath=../inc\r\n" },
        { "a key right after its header", "; c\n[include]path=../inc\n" },
    };

    [Theory]
    [MemberData(nameof(HiddenIncludes))]
    public void AnIncludeInAnyForm_IsProtectedOrRefused(string name, string text)
    {
        Assert.NotEmpty(name);
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("project"));
        var config = Path.Combine(dir, ".git", "config");
        using (var s = new FileStream(config, FileMode.Append)) s.Write(B(text));

        // Whatever file git reads, under whatever exact name, gets mounted read-only.
        var read = GitLists(config);
        Assert.NotNull(read);
        var included = read!.Single(e => e.Name == "include.path").Value;
        var target = Path.GetFullPath(Path.Combine(dir, ".git", included));
        File.WriteAllText(target, "");
        var mounts = new MountPlanner(dir, t.Sub("home")).EnsureTargetsAndPlan();
        Assert.Contains(mounts, m => m.Path == target && m.ReadOnly);
    }

    [Fact]
    public void AnIncludeThroughAnotherUsersHome_IsRefused()
    {
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("project"));
        File.AppendAllText(Path.Combine(dir, ".git", "config"), "[include]\n\tpath = ~someone/inc\n");
        var e = Assert.Throws<PenException>(() => new MountPlanner(dir, t.Sub("home")).EnsureTargetsAndPlan());
        Assert.Contains("can't resolve", e.Message);
    }

    [Fact]
    public void AnIncludeThroughHome_ThatLeadsIntoTheProject_IsProtected()
    {
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("project"));
        var home = t.Sub("home");
        File.WriteAllText(Path.Combine(dir, "inc"), "");
        File.AppendAllText(Path.Combine(dir, ".git", "config"), "[include]\n\tpath = ~/../project/inc\n");
        var mounts = new MountPlanner(dir, home).EnsureTargetsAndPlan();
        Assert.Contains(mounts, m => m.Path == Path.Combine(dir, "inc") && m.ReadOnly);
    }

    [Fact]
    public void AnIncludeNamingOwnUserHome_IsRefused()
    {
        // git would expand this into the project; Pen doesn't look users up.
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("project"));
        File.WriteAllText(Path.Combine(dir, "inc"), "");
        File.AppendAllText(Path.Combine(dir, ".git", "config"), $"[include]\n\tpath = ~{Environment.UserName}/../../../../../../../../..{dir}/inc\n");
        Assert.Throws<PenException>(() => new MountPlanner(dir, t.Sub("home")).EnsureTargetsAndPlan());
    }

    // The OS takes the ".." from where link leads, project/sub, which the agent could replace with a symlink.
    [Fact]
    public void AnIncludeThroughAnOutsideLink_DotDotIntoTheProject_IsRefused()
    {
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("project"));
        var outside = t.Sub("outside");
        File.CreateSymbolicLink(Path.Combine(outside, "link"), Path.Combine(dir, "sub"));
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "inc"), "");
        File.AppendAllText(Path.Combine(dir, ".git", "config"), $"[include]\n\tpath = {outside}/link/../inc\n");
        var e = Assert.Throws<PenException>(() => new MountPlanner(dir, t.Sub("home")).EnsureTargetsAndPlan());
        Assert.Contains($"includes {outside}/link/../inc, whose \"..\" passes through a directory", e.Message);
    }

    // What git reads through a ".." that leaves only the including file's own directory or the ones above it is
    // the file Pen protects.
    [Theory]
    [InlineData("../.gitconfig")]
    [InlineData("../../project/.gitconfig")]
    [InlineData("./../.git/../.gitconfig")]
    public void AnIncludesDotDot_ThatLeavesOnlyTheIncludingFilesDirectories_IsWhatGitReads(string value)
    {
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("project"));
        var mounted = Path.Combine(dir, ".gitconfig");
        File.WriteAllText(mounted, "[pen]\n\tprobe = here\n");
        File.AppendAllText(Path.Combine(dir, ".git", "config"), $"[include]\n\tpath = {value}\n");
        var mounts = new MountPlanner(dir, t.Sub("home")).EnsureTargetsAndPlan();
        Assert.Contains(mounts, m => m.Path == mounted && m.ReadOnly && m.Verify);
        var psi = new ProcessStartInfo("git", ["config", "--includes", "--show-origin", "--get", "pen.probe"])
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        var r = Process.RunAndCaptureText(psi);
        Assert.Equal(0, r.ExitStatus.ExitCode);
        var origin = r.StandardOutput.Split('\t')[0]["file:".Length..];
        Assert.Equal(mounted, Native.RealPath(Path.Combine(dir, origin)));
    }

    [Fact]
    public void AnIncludeThatIsntValidUtf8_IsRefused()
    {
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("project"));
        using (var c = new FileStream(Path.Combine(dir, ".git", "config"), FileMode.Append)) c.Write(B("[include]\npath=../inc\xa0\n"));
        Assert.Contains("isn't valid UTF-8", Assert.Throws<PenException>(() => new MountPlanner(dir, t.Sub("home")).EnsureTargetsAndPlan()).Message);
    }

    [Theory]
    [InlineData("[include]\npath=inc\0junk\n")]
    [InlineData("[core]\nk=a\0b\n")]
    [InlineData("[a \"b\0c\"]\nk=v\n")]
    public void ANul_InAValueOrSubsection_IsARefusalNamingFileAndLine_NotACrash(string text)
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "config");
        File.WriteAllBytes(file, B("# first\n" + text));
        var e = Assert.Throws<PenException>(() => GitConfigFile.ParseFile(file));
        Assert.Contains(file, e.Message);
        Assert.Contains("line ", e.Message);
    }

    [Fact]
    public void AGarbledRepoConfig_RefusesTheSession_NamingFileAndLine()
    {
        using var t = new TempDir();
        var dir = Git.Repo(t.Sub("project"));
        File.AppendAllText(Path.Combine(dir, ".git", "config"), "[core\n");
        var e = Assert.Throws<PenException>(() => new MountPlanner(dir, t.Sub("home")).EnsureTargetsAndPlan());
        Assert.Contains(".git/config", e.Message);
        Assert.Contains("line ", e.Message);
    }

    [Fact]
    public void AModuleRepoWithAGarbledConfig_IsntVouchedForAtTheEnd()
    {
        using var t = new TempDir();
        var repo = t.Sub("repo");
        File.WriteAllText(Path.Combine(repo, "config"), "[core\n");
        var checkout = t.Sub("checkout");
        var gitFile = Path.Combine(checkout, ".git");
        File.WriteAllText(gitFile, $"gitdir: {repo}\n");
        Assert.Null(RepoScan.ResolveModuleCheckout(gitFile, new HashSet<string>(StringComparer.Ordinal) { repo }));
    }

    [Fact]
    public void AnotherEncoding_IsNotDecoded()
    {
        // A .NET reader would take this for UTF-16 and read an include; git rejects the first byte.
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "config");
        File.WriteAllBytes(file, [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("[include]\npath=inc\n")]);
        Assert.Equal(Outcome.BothReject, Compare(file, "UTF-16"));
    }

    // Real configs must keep parsing.

    public static TheoryData<string> RealWorldFiles => new()
    {
        // The example in git-config(1).
        """
        #
        # This is the config file, and
        # a '#' or ';' character indicates
        # a comment
        #

        ; core variables
        [core]
        	; Don't trust file modes
        	filemode = false

        ; Our diff algorithm
        [diff]
        	external = /usr/local/bin/diff-wrapper
        	renames = true

        ; Proxy settings
        [core]
        	gitproxy=proxy-command for kernel.org
        	gitproxy=default-proxy ; for all the rest

        ; HTTP
        [http]
        	sslVerify
        [http "https://weak.example.com"]
        	sslVerify = false
        	cookieFile = /tmp/cookie.txt

        ; include if $GIT_DIR is /path/to/foo/.git
        [includeIf "gitdir:/path/to/foo/.git"]
        	path = /path/to/foo.inc

        ; include only if we are in a worktree where foo-branch is
        ; currently checked out
        [includeIf "onbranch:foo-branch"]
        	path = foo.inc

        ; include only if a remote with the given URL exists
        [includeIf "hasconfig:remote.*.url:https://example.com/**"]
        	path = foo.inc
        [remote "origin"]
        	url = https://example.com/git
        """,
        // A repo as git init and clone leave it, with LFS and submodules.
        "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n\tlogallrefupdates = true\n\tignorecase = true\n\tprecomposeunicode = true\n"
            + "[remote \"origin\"]\n\turl = git@github.com:someone/some-repo.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n"
            + "[branch \"main\"]\n\tremote = origin\n\tmerge = refs/heads/main\n[branch \"feat/a#b;c\"]\n\tremote = origin\n"
            + "[lfs \"https://github.com/someone/some-repo.git/info/lfs\"]\n\taccess = basic\n[lfs]\n\trepositoryformatversion = 0\n"
            + "[submodule \"libs/a b\"]\n\tactive = true\n\turl = ../a.git\n",
        // A hand-kept ~/.gitconfig.
        "[user]\n  name = Some \"Nick\" One\n  email = a@example.com\n  signingkey = ssh-ed25519 AAAA\n[alias]\n  lg = log --graph --pretty=format:'%h %s' --abbrev-commit\n"
            + "  st = \"!f() { git status \\\"$@\\\"; }; f\"\n  co=checkout\n  x = !sh -c 'echo a; echo b' -\n"
            + "[url \"git@github.com:\"]\n\tinsteadOf = https://github.com/\n[filter \"lfs\"]\n\tclean = git-lfs clean -- %f\n\trequired = true\n"
            + "[credential \"https://github.com\"]\n\thelper =\n\thelper = !/opt/homebrew/bin/gh auth git-credential\n"
            + "[init]\n\tdefaultBranch = main\n[include]\n\tpath = ~/.gitconfig.local\n[core]\n\texcludesfile = ~/.gitignore_global\n\tpager = less -FRX\n\twhitespace = trailing-space,space-before-tab\n"
            + "[gpg \"ssh\"]\n\tprogram = \"/Applications/1Password.app/Contents/MacOS/op-ssh-sign\"\n[Section.Old]\n\tkey = v\n",
        // Windows line ends, and a byte-order mark from Notepad.
        "\xef\xbb\xbf[core]\r\n\tautocrlf = true\r\n\t# comment\r\n\tname = a\\\r\nb\r\n",
    };

    [Theory]
    [MemberData(nameof(RealWorldFiles))]
    public void ARealWorldFile_Parses(string text)
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "config");
        File.WriteAllBytes(file, B(text));
        Assert.Equal(Outcome.Parsed, Compare(file, text));
    }

    public static TheoryData<string, string> Written => new()
    {
        { "core.hooksPath", ".husky" },
        { "a.value", " leading and trailing " },
        { "a.value", "#hash and ;semi" },
        { "a.value", "say \"hi\" \\ there" },
        { "a.value", "two\nlines\tand tab" },
        { "a.value", "ünïcode ✓" },
        { "a.value", "\r" },
        { "a.value", "" },
        { "a.value", "ends in backslash\\" },
        { "sect.sub with \"quote\" and \\ and ] and #.key", "v" },
        { "sect.sub.with.dots.key", "v" },
        { "sect.Mixed Case.Key", "v" },
        { "include.path", "../some where/inc" },
        { "includeif.gitdir:~/work/.path", "~/.work" },
    };

    [Theory]
    [MemberData(nameof(Written))]
    public void WhatGitConfigWrites_Parses(string key, string value)
    {
        using var t = new TempDir();
        var file = Path.Combine(t.Path, "config");
        Git.Run(t.Path, "config", "--file", file, "--add", key, value);
        Git.Run(t.Path, "config", "--file", file, "--add", key, value + " again");
        Assert.Equal(Outcome.Parsed, Compare(file, $"{key} = {value}"));
    }
}
