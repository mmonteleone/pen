namespace Pen.Tests;

public class InteractiveShellTests
{
    [Theory]
    [InlineData("/opt/homebrew/bin/fish", "fish")]
    [InlineData("/usr/bin/fish", "fish")]
    [InlineData("fish", "fish")]
    [InlineData("/bin/zsh", "zsh")]
    [InlineData("/bin/bash", "bash")]
    [InlineData("/bin/sh", "bash")]
    [InlineData("/usr/bin/nu", "bash")]
    [InlineData("/usr/bin/fish5", "bash")]
    [InlineData("/usr/bin/fish/", "bash")]
    [InlineData("/usr/bin/Fish", "bash")]
    [InlineData("/usr/bin/fish -c x", "bash")]
    [InlineData("/fish/sh", "bash")]
    [InlineData("", "bash")]
    [InlineData(null, "bash")]
    public void TheHostShellPicksALoginShell(string? hostShell, string expected) =>
        Assert.Equal([expected, "-l"], InteractiveShell.For(hostShell));
}
