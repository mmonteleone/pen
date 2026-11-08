using System.Text.Json;

namespace Pen.Tests;

public class SeedTests
{
    private static readonly string[] Expected =
    [
        ".claude.json", ".claude/CLAUDE.md", ".claude/settings.json",
        ".codex/AGENTS.md", ".codex/config.toml", ".gitconfig",
    ];

    [Fact]
    public void TheTemplateEmbedsEveryDotfile()
    {
        var names = Embedded.Under("templates/home/").Select(n => n["templates/home/".Length..]).Order(StringComparer.Ordinal);
        Assert.Equal(Expected.Order(StringComparer.Ordinal), names);
    }

    [Fact]
    public void FirstRunCreatesTheSeed_WithTheHostIdentity()
    {
        using var t = new TempDir();
        var host = t.Host();
        Config.EnsureUserConfig(host, () => ("Ann \"A\" O'Neil", @"ann\x@example.com"));

        foreach (var rel in Expected) Assert.True(File.Exists(Path.Combine(host.SeedDir, rel)), rel);
        var gitconfig = File.ReadAllText(Path.Combine(host.SeedDir, ".gitconfig"));
        Assert.Contains("\tname = \"Ann \\\"A\\\" O'Neil\"\n", gitconfig);
        Assert.Contains("\temail = \"ann\\\\x@example.com\"\n", gitconfig);
        Assert.DoesNotContain("@NAME@", gitconfig);

        // git reads the identity back exactly.
        var r = Git.Run(t.Path, "config", "--file", Path.Combine(host.SeedDir, ".gitconfig"), "user.name");
        Assert.Equal("Ann \"A\" O'Neil\n", r);
    }

    [Fact]
    public void AMissingIdentityIsLeftCommentedOut()
    {
        var text = Seed.GitConfig(Embedded.Text("templates/home/.gitconfig"), null, "");
        Assert.Contains("#\tname = Your Name", text);
        Assert.Contains("#\temail = you@example.com", text);
    }

    [Fact]
    public void AnExistingSeedIsNeverRewritten_AndAMissingOneIsAddedToAnExistingConfigDir()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(host.UserConfigPath, "# mine\n");
        Config.EnsureUserConfig(host, () => ("N", "e@x.io"));
        Assert.True(File.Exists(Path.Combine(host.SeedDir, ".claude", "settings.json")));
        Assert.Equal("# mine\n", File.ReadAllText(host.UserConfigPath));

        File.WriteAllText(Path.Combine(host.SeedDir, ".gitconfig"), "mine");
        File.Delete(Path.Combine(host.SeedDir, ".claude.json"));
        Config.EnsureUserConfig(host, () => throw new InvalidOperationException("not asked again"));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(host.SeedDir, ".gitconfig")));
        Assert.False(File.Exists(Path.Combine(host.SeedDir, ".claude.json")));
    }

    [Fact]
    public void TheJsonTemplatesParse_AndCarryTheDefaultAgentSettings()
    {
        using var settings = JsonDocument.Parse(Embedded.Text("templates/home/.claude/settings.json"));
        var root = settings.RootElement;
        Assert.Equal("", root.GetProperty("attribution").GetProperty("commit").GetString());
        Assert.Equal("", root.GetProperty("attribution").GetProperty("pr").GetString());
        Assert.Equal("bypassPermissions", root.GetProperty("permissions").GetProperty("defaultMode").GetString());

        using var claudeJson = JsonDocument.Parse(Embedded.Text("templates/home/.claude.json"));
        Assert.Equal(["mcpServers"], claudeJson.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("codegraph", claudeJson.RootElement.GetProperty("mcpServers").GetProperty("codegraph").GetProperty("command").GetString());
    }

    [Fact]
    public void BothInstructionFilesCarryTheSandboxNote()
    {
        foreach (var f in new[] { "templates/home/.claude/CLAUDE.md", "templates/home/.codex/AGENTS.md" })
        {
            var text = Embedded.Text(f);
            Assert.Contains("CONNECT tunnel failed", text);
            Assert.Contains("Don't work around it.", text);
            Assert.Contains("`.git/config` is read-only here.", text);
        }
    }
}
