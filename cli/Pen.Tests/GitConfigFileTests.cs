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
    [InlineData("true", true)]
    [InlineData("Yes", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    public void Booleans(string v, bool expected) => Assert.Equal(expected, GitConfigFile.IsTrue(v));
}
