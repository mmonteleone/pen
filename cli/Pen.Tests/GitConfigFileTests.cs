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
}
