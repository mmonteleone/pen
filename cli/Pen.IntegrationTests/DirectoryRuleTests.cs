namespace Pen.IntegrationTests;

/// <summary>Where pen refuses to run, and how a project's path maps to its key.</summary>
public sealed class DirectoryRuleTests(IntegrationFixture fixture) : IClassFixture<IntegrationFixture>
{
    private string Home => fixture.Home.Path;

    [Fact(DisplayName = "refuses $HOME")]
    public void RefusesHome() => AssertRefused(Home, "home directory");

    [Fact(DisplayName = "refuses ~/.config (contains ~/.config/pen)")]
    public void RefusesConfig() => AssertRefused(Path.Combine(Home, ".config"), "~/.config/pen");

    [Fact(DisplayName = "a symlinked path maps to the same project key")]
    public void SymlinkedPathMapsToTheSameKey()
    {
        var link = Path.Combine(fixture.Scratch, "link");
        File.CreateSymbolicLink(link, fixture.Project.Dir);
        var sessions = Path.Combine(fixture.RuntimeDir, "sessions");
        var sessionsBefore = Directory.EnumerateDirectories(sessions).Count();

        var result = fixture.Cli.WithWorkingDirectory(link).Run("run", "true");

        // The HOME is the class's own, so a key pen made for the link's path, whatever its hash, is a second
        // directory here, and the session counts only if it went to the project's key.
        Assert.Multiple(
            () => Assert.True(result.ExitCode == 0, result.Describe()),
            () => Assert.Equal(sessionsBefore + 1, Directory.EnumerateDirectories(sessions).Count()),
            () => Assert.Equal([fixture.RuntimeDir], Directory.EnumerateDirectories(fixture.Home.StateRoot)));
    }

    private void AssertRefused(string dir, string message)
    {
        var result = fixture.Cli.WithWorkingDirectory(dir).Run("run", "true");

        Assert.True(result.IsRefusal(message), $"expected a refusal saying \"{message}\"; {result.Describe()}");
    }
}
