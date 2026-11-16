using Pen.Masking;

namespace Pen.Tests;

public class HidePatternTests
{
    private static HidePattern Parse(string text)
    {
        Assert.Null(HidePattern.TryParse(text, out var pattern));
        return pattern!;
    }

    private static string Refuse(string text)
    {
        var problem = HidePattern.TryParse(text, out var pattern);
        Assert.Null(pattern);
        return Assert.IsType<string>(problem);
    }

    private static bool File(string pattern, string path) => Parse(pattern).IsMatch(path, isDirectory: false);
    private static bool Dir(string pattern, string path) => Parse(pattern).IsMatch(path, isDirectory: true);

    [Fact]
    public void ANameWithoutSlashMatchesAtAnyDepth()
    {
        var pattern = Parse(".env");
        Assert.False(pattern.IsAnchored);
        Assert.False(pattern.IsLiteral);
        Assert.Null(pattern.LiteralPath);
        Assert.True(pattern.IsMatch(".env", false));
        Assert.True(pattern.IsMatch("apps/web/.env", false));
        Assert.True(pattern.IsMatch("apps/.env", true));
        Assert.False(pattern.IsMatch(".env.local", false));
        Assert.False(pattern.IsMatch("a.env", false));
        // Only the entry itself is tested, not its parents.
        Assert.False(pattern.IsMatch(".env/inner", false));
    }

    [Fact]
    public void ALeadingOrInnerSlashAnchorsAtTheRoot()
    {
        var leading = Parse("/.env");
        Assert.True(leading.IsAnchored);
        Assert.True(leading.IsMatch(".env", false));
        Assert.False(leading.IsMatch("sub/.env", false));

        var inner = Parse("apps/web/.env");
        Assert.True(inner.IsAnchored);
        Assert.True(inner.IsMatch("apps/web/.env", false));
        Assert.False(inner.IsMatch("x/apps/web/.env", false));
        Assert.False(inner.IsMatch("apps/web", true));
        Assert.False(inner.IsMatch("apps/web/.env/x", false));
    }

    [Fact]
    public void ATrailingSlashMatchesDirectoriesOnly()
    {
        var pattern = Parse("secrets/");
        Assert.True(pattern.IsDirectoryOnly);
        Assert.False(pattern.IsAnchored);
        Assert.True(pattern.IsMatch("secrets", true));
        Assert.True(pattern.IsMatch("a/b/secrets", true));
        Assert.False(pattern.IsMatch("secrets", false));

        var anchored = Parse("config/secrets/");
        Assert.True(anchored.IsAnchored);
        Assert.True(anchored.IsDirectoryOnly);
        Assert.True(anchored.IsMatch("config/secrets", true));
        Assert.False(anchored.IsMatch("config/secrets", false));
        Assert.False(Parse(".env").IsDirectoryOnly);
    }

    [Theory]
    [InlineData("*.ps1", "setup.ps1", true)]
    [InlineData("*.ps1", "scripts/setup.ps1", true)]
    [InlineData("*.ps1", ".ps1", true)]
    [InlineData("*.ps1", "setup.ps1x", false)]
    [InlineData(".env.*", ".env.local", true)]
    [InlineData(".env.*", ".env.", true)]
    [InlineData(".env.*", ".env", false)]
    [InlineData("?.txt", "a.txt", true)]
    [InlineData("?.txt", "ab.txt", false)]
    [InlineData("?.txt", ".txt", false)]
    [InlineData("a*b*c", "aXbYbZc", true)]
    [InlineData("a*b*c", "aXbYbZ", false)]
    [InlineData("?*", "anything", true)]
    // ** inside a segment is a plain *, so it doesn't cross /.
    [InlineData("a**b", "aXXb", true)]
    [InlineData("a**b", "ab", true)]
    [InlineData("/a**b", "aX/b", false)]
    // * and ? don't cross /.
    [InlineData("apps/*.env", "apps/web.env", true)]
    [InlineData("apps/*.env", "apps/web/x.env", false)]
    [InlineData("apps/?/x", "apps/a/x", true)]
    [InlineData("apps/?/x", "apps/ab/x", false)]
    [InlineData("/*/x", "a/x", true)]
    [InlineData("/*/x", "x", false)]
    public void StarAndQuestionMarkStayInOneSegment(string pattern, string path, bool matches) =>
        Assert.Equal(matches, File(pattern, path));

    [Fact]
    public void QuestionMarkTakesASurrogatePairAsOneCharacter()
    {
        Assert.True(File("?.txt", "\U0001F600.txt"));
        Assert.True(File("a*\U0001F600", "aXX\U0001F600"));
        Assert.False(File("??.txt", "\U0001F600.txt"));
    }

    [Theory]
    // A leading ** matches at any depth, the root included.
    [InlineData("**/.env", ".env", true)]
    [InlineData("**/.env", "a/b/.env", true)]
    [InlineData("**/web/.env", "web/.env", true)]
    [InlineData("**/web/.env", "apps/web/.env", true)]
    [InlineData("**/web/.env", "apps/web/x/.env", false)]
    // An inner ** matches zero or more directories.
    [InlineData("apps/**/.env", "apps/.env", true)]
    [InlineData("apps/**/.env", "apps/web/.env", true)]
    [InlineData("apps/**/.env", "apps/a/b/c/.env", true)]
    [InlineData("apps/**/.env", "lib/apps/.env", false)]
    [InlineData("a/**/b/**/c", "a/b/c", true)]
    [InlineData("a/**/b/**/c", "a/x/b/y/z/c", true)]
    [InlineData("a/**/b/**/c", "a/x/y/c", false)]
    [InlineData("a/**/**/b", "a/b", true)]
    [InlineData("a/**/*.key", "a/x/y/z.key", true)]
    // A trailing ** matches everything inside, but not the directory itself.
    [InlineData("secrets/**", "secrets/a", true)]
    [InlineData("secrets/**", "secrets/a/b/c", true)]
    [InlineData("secrets/**", "secrets", false)]
    [InlineData("secrets/**", "other/a", false)]
    [InlineData("**/secrets/**", "x/secrets/a", true)]
    public void DoubleStarMatchesAnyRunOfDirectories(string pattern, string path, bool matches) =>
        Assert.Equal(matches, File(pattern, path));

    [Fact]
    public void ATrailingDoubleStarWithSlashMatchesDirectoriesInside()
    {
        Assert.True(Dir("secrets/**/", "secrets/a"));
        Assert.False(File("secrets/**/", "secrets/a"));
        Assert.False(Dir("secrets/**/", "secrets"));
    }

    [Theory]
    [InlineData(".env", ".ENV")]
    [InlineData(".ENV", "apps/.env")]
    [InlineData("*.PS1", "Setup.ps1")]
    [InlineData("/Apps/Web/.Env", "apps/web/.env")]
    [InlineData("apps/**/SECRET?", "APPS/x/secret1")]
    [InlineData("cafÉ", "café")]
    public void MatchingIgnoresCase(string pattern, string path) => Assert.True(File(pattern, path));

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    public void EmptyPatternsAreRefused(string text) => Assert.Equal("a pattern can't be empty", Refuse(text));

    [Theory]
    [InlineData("a//b")]
    [InlineData("//a")]
    [InlineData("a//")]
    public void EmptySegmentsAreRefused(string text) =>
        Assert.Equal("a pattern can't have an empty segment (//)", Refuse(text));

    [Theory]
    [InlineData("[ab].txt")]
    [InlineData("a]")]
    [InlineData("a\\*")]
    public void BracketsAndBackslashesAreRefused(string text) =>
        Assert.Equal("[, ] and \\ aren't supported in patterns (use * or ?)", Refuse(text));

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/../b")]
    [InlineData(".")]
    [InlineData("./a")]
    [InlineData("a/./b")]
    public void DotSegmentsAreRefused(string text) =>
        Assert.Equal("a pattern can't have . or .. segments", Refuse(text));

    [Theory]
    [InlineData(".git")]
    [InlineData(".GIT")]
    [InlineData("/.git/config")]
    [InlineData(".git/")]
    [InlineData("sub/.git/config")]
    [InlineData("**/.git")]
    [InlineData("a/.Git/")]
    public void AGitSegmentAnywhereIsRefused(string text) =>
        Assert.Equal("a pattern can't reach into .git", Refuse(text));

    [Theory]
    [InlineData(".pen")]
    [InlineData(".PEN/")]
    [InlineData("/.pen/agent.env")]
    [InlineData(".pen/*")]
    public void AFirstSegmentOfPenIsRefused(string text) =>
        Assert.Equal("a pattern can't start with .pen, Pen's own directory", Refuse(text));

    [Fact]
    public void PenFurtherDownIsAllowed() => Assert.True(File("apps/.pen/x", "apps/.pen/x"));

    [Theory]
    [InlineData("**")]
    [InlineData("/**")]
    [InlineData("**/")]
    [InlineData("**/**")]
    [InlineData("*")]
    [InlineData("/*")]
    [InlineData("*/")]
    [InlineData("**/*")]
    [InlineData("*/**")]
    public void PatternsOfOnlyStarSegmentsAreRefused(string text) =>
        Assert.Equal("this pattern would hide the whole project", Refuse(text));

    [Theory]
    [InlineData("*/*")]
    [InlineData("*.env")]
    [InlineData("**/*.env")]
    public void StarsWithMoreThanOneEntryLevelAreAllowed(string text) => Parse(text);

    [Theory]
    [InlineData(".env ")]
    [InlineData(" .env")]
    [InlineData("a/b\t")]
    public void SurroundingWhitespaceIsRefused(string text) =>
        Assert.Equal("a pattern can't start or end with a space", Refuse(text.Replace("\\t", "\t")));

    [Fact]
    public void NegationAndCommentPrefixesAreRefused()
    {
        Assert.Equal("! (negation) isn't supported", Refuse("!keep.env"));
        Assert.StartsWith("# isn't a comment here", Refuse("#env"));
    }

    [Fact]
    public void ANulIsRefusedOnItsOwn() =>
        Assert.Equal("a pattern can't contain a NUL character", Refuse("a\0b"));

    [Fact]
    public void ANfcPatternMatchesAnNfdName()
    {
        var pattern = Parse("caf\u00e9.env");
        Assert.True(pattern.IsMatch("caf\u0065\u0301.env", false));
        Assert.True(pattern.IsMatch("dir/CAFE\u0301.env", false));
        Assert.Equal("caf\u00e9.env", pattern.Text);
    }

    [Fact]
    public void AnNfdPatternMatchesAnNfcName()
    {
        var pattern = Parse("a/caf\u0065\u0301");
        Assert.True(pattern.IsMatch("a/caf\u00e9", true));
        Assert.Equal("a/caf\u00e9", pattern.LiteralPath);
        Assert.True(Parse("caf\u0065\u0301*").IsMatch("caf\u00e9s", false));
    }

    [Fact]
    public void WildcardsNeverMatchInsideGitOrTopLevelPen()
    {
        Assert.False(File("?*", ".git"));
        Assert.False(Dir("?*", ".GIT"));
        Assert.False(File("**/config", ".git/config"));
        Assert.False(File("**/config", "sub/.git/config"));
        Assert.False(File("?*", ".pen"));
        Assert.False(File("**/agent.env", ".pen/agent.env"));
        Assert.True(File("**/agent.env", "a/.pen/agent.env"));
        Assert.True(File("?*", ".github"));
    }

    [Theory]
    [InlineData("/.env", ".env", false)]
    [InlineData("apps/web/.env", "apps/web/.env", false)]
    [InlineData("/Apps/Secrets/", "Apps/Secrets", true)]
    public void AnchoredPatternsWithoutWildcardsAreLiteral(string text, string path, bool isDirectoryOnly)
    {
        var pattern = Parse(text);
        Assert.True(pattern.IsLiteral);
        Assert.Equal(path, pattern.LiteralPath);
        Assert.Equal(isDirectoryOnly, pattern.IsDirectoryOnly);
        Assert.Equal(text, pattern.Text);
        Assert.Equal(text, pattern.ToString());
    }

    [Theory]
    [InlineData(".env")]
    [InlineData("secrets/")]
    [InlineData("/*.env")]
    [InlineData("apps/?/x")]
    [InlineData("apps/**/x")]
    [InlineData("**/x")]
    [InlineData("apps/**")]
    public void PatternsWithWildcardsOrUnanchoredAreNotLiteral(string text)
    {
        var pattern = Parse(text);
        Assert.False(pattern.IsLiteral);
        Assert.Null(pattern.LiteralPath);
    }

    [Fact]
    public void AnEmptyPathNeverMatches() => Assert.False(File("?*", ""));
}
