namespace Pen.Tests;

public class GitConfigFileTests
{
    [Fact]
    public void ParsesSectionsSubsectionsAndValues()
    {
        const string text = """
            # comment
            [core]
            	bare = false
            	FSMonitor = "a \"quoted\" value" ; trailing comment
            [remote "Origin"]
            	url = x
            [includeIf "gitdir:~/work/"]
            	path = work.inc
            [Section.Sub]
            	key
            [extensions] worktreeConfig = true
            [alias]
            	long = one \
            two
            """;
        var e = GitConfigFile.Parse(text);
        Assert.Contains(new GitConfigFile.Entry("core", null, "bare", "false"), e);
        Assert.Contains(new GitConfigFile.Entry("core", null, "fsmonitor", "a \"quoted\" value"), e);
        Assert.Contains(new GitConfigFile.Entry("remote", "Origin", "url", "x"), e);
        Assert.Contains(new GitConfigFile.Entry("includeif", "gitdir:~/work/", "path", "work.inc"), e);
        Assert.Contains(new GitConfigFile.Entry("section", "sub", "key", "true"), e);
        Assert.Contains(new GitConfigFile.Entry("extensions", null, "worktreeconfig", "true"), e);
        Assert.Contains(new GitConfigFile.Entry("alias", null, "long", "one two"), e);
    }

    [Theory]
    [InlineData("# note \\\nhooksPath = x")]
    [InlineData("; note \\\nhooksPath = x")]
    [InlineData("bare = false # note \\\nhooksPath = x")]
    public void AComment_EndsAtItsNewline_EvenAfterABackslash(string body)
    {
        // git reads the next line as a key, so taking it as continuing the comment would miss it.
        var e = GitConfigFile.Parse("[core]\n" + body + "\n");
        Assert.Contains(new GitConfigFile.Entry("core", null, "hookspath", "x"), e);
    }

    [Fact]
    public void AValue_ContinuesAfterABackslash_ButNotAfterAnEscapedOne()
    {
        Assert.Contains(new GitConfigFile.Entry("a", null, "k", "xy"), GitConfigFile.Parse("[a]\nk = x\\\ny # c\n"));
        Assert.Contains(new GitConfigFile.Entry("a", null, "k", "x\\"), GitConfigFile.Parse("[a]\nk = x\\\\\nother = 1\n"));
    }

    [Theory]
    [InlineData("\" /abs\"", " /abs")]
    [InlineData("\"x \"", "x ")]
    [InlineData("x   ", "x")]
    [InlineData("  x y  # c", "x y")]
    [InlineData("\" \" x  ", "  x")]
    [InlineData("x\\t  ", "x\t")]
    public void Whitespace_IsTrimmedOnlyWhereItIsNotQuotedOrEscaped(string written, string value) =>
        Assert.Contains(new GitConfigFile.Entry("a", null, "k", value), GitConfigFile.Parse($"[a]\nk = {written}\n"));

    [Theory]
    [InlineData("true", true)]
    [InlineData("Yes", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData("0", false)]
    [InlineData("00", false)]
    [InlineData("0k", false)]
    [InlineData("no", false)]
    [InlineData("off", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void Booleans(string v, bool expected) => Assert.Equal(expected, GitConfigFile.IsTrue(v));

    [Fact]
    public void TryParseFile_GivesTheEntries_NoneForAMissingFile_AndNullWhereParseFileThrows()
    {
        using var t = new TempDir();
        var good = Path.Combine(t.Path, "good");
        File.WriteAllText(good, "[core]\n\tbare = false\n");
        var bad = Path.Combine(t.Path, "bad");
        File.WriteAllText(bad, "[core\n");
        var dir = t.Sub("dir");

        Assert.Equal([new GitConfigFile.Entry("core", null, "bare", "false")], GitConfigFile.TryParseFile(good));
        Assert.Empty(GitConfigFile.TryParseFile(Path.Combine(t.Path, "missing"))!);
        Assert.Empty(GitConfigFile.TryParseFile(Path.Combine(t.Path, "no-dir", "config"))!);
        Assert.Throws<PenException>(() => GitConfigFile.ParseFile(bad));
        Assert.Null(GitConfigFile.TryParseFile(bad));
        Assert.Throws<PenException>(() => GitConfigFile.ParseFile(dir));
        Assert.Null(GitConfigFile.TryParseFile(dir));
    }

    [Fact]
    public void FindLast_TakesTheLastValue_InExactlyTheSubsectionAsked()
    {
        var entries = GitConfigFile.Parse("""
            [core]
            	worktree = first
            [core "x"]
            	worktree = in-x
            [core]
            	worktree = last
            	bare = true
            [core "X"]
            	worktree = in-capital-x
            [core "x"]
            	worktree = in-x-again
            """);
        Assert.Equal("last", GitConfigFile.FindLast(entries, "core", "worktree"));
        Assert.Equal("in-x-again", GitConfigFile.FindLast(entries, "core", "worktree", "x"));
        Assert.Equal("in-capital-x", GitConfigFile.FindLast(entries, "core", "worktree", "X"));
        Assert.Null(GitConfigFile.FindLast(entries, "core", "worktree", "y"));
        Assert.Null(GitConfigFile.FindLast(entries, "core", "bare", "x"));
        Assert.Null(GitConfigFile.FindLast(entries, "core", "hookspath"));
        Assert.Null(GitConfigFile.FindLast([], "core", "worktree"));
    }

    [Fact]
    public void FindLast_IgnoresEntriesOnlyInASubsection_WhenNoneIsAsked()
    {
        var entries = GitConfigFile.Parse("[extensions \"x\"]\n\tworktreeConfig = true\n");
        Assert.Null(GitConfigFile.FindLast(entries, "extensions", "worktreeconfig"));
        Assert.Equal("true", GitConfigFile.FindLast(entries, "extensions", "worktreeconfig", "x"));
    }

    [Fact]
    public void FindLast_MatchesTheLowercasedSectionAndKey_AsTheEntriesHoldThem()
    {
        var entries = GitConfigFile.Parse("[Extensions]\n\tWorktreeConfig = true\n");
        Assert.Equal("true", GitConfigFile.FindLast(entries, "extensions", "worktreeconfig"));
        Assert.Null(GitConfigFile.FindLast(entries, "Extensions", "WorktreeConfig"));
    }

    [Theory]
    [InlineData("~", "/home/u")]
    [InlineData("~/", "/home/u/")]
    [InlineData("~/inc/x", "/home/u/inc/x")]
    [InlineData("/abs/x", "/abs/x")]
    [InlineData("rel/x", "rel/x")]
    [InlineData("../x", "../x")]
    [InlineData("", "")]
    [InlineData("a~b", "a~b")]
    [InlineData("x/%(prefix)/y", "x/%(prefix)/y")]
    [InlineData("%(prefix)x", "%(prefix)x")]
    public void ExpandPath_ExpandsTheHome_AndLeavesOtherValuesAsTheyAre(string value, string expected) =>
        Assert.Equal(
            new GitConfigFile.ExpandedPath.Expanded(expected),
            GitConfigFile.ExpandPath(value, "/home/u"));

    [Theory]
    [InlineData("~user")]
    [InlineData("~user/x")]
    [InlineData("~~")]
    [InlineData("%(prefix)/etc/gitconfig")]
    [InlineData("%(prefix)/")]
    public void ExpandPath_RefusesAnotherUsersHomeAndThePrefix(string value) =>
        Assert.Equal(
            new GitConfigFile.ExpandedPath.Refused(GitConfigFile.PathRefusal.UnknownForm),
            GitConfigFile.ExpandPath(value, "/home/u"));

    [Theory]
    [InlineData("/abs/\uFFFD")]
    [InlineData("rel\uFFFD")]
    [InlineData("~/\uFFFD")]
    [InlineData("~user\uFFFD")]
    [InlineData("%(prefix)/\uFFFD")]
    [InlineData("\uFFFD")]
    public void ExpandPath_RefusesInvalidUtf8_BeforeAnythingElse(string value) =>
        Assert.Equal(
            new GitConfigFile.ExpandedPath.Refused(GitConfigFile.PathRefusal.NotUtf8),
            GitConfigFile.ExpandPath(value, "/home/u"));
}
