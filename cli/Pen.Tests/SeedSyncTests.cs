namespace Pen.Tests;

public class SeedSyncTests
{
    private static void Write(string root, string rel, string text)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Sync(TempDir t)
    {
        Assert.SkipWhen(Proc.Capture("sh", ["-c", "command -v jq"]).ExitStatus.ExitCode != 0, "seed JSON checks need jq");
        var script = Path.Combine(t.Path, "sync-seed.sh");
        File.WriteAllText(script, Embedded.Text("image/sync-seed.sh"));
        var result = Proc.Capture("bash", [script, t.Sub("seed"), t.Sub("sandbox"), "/project"]);
        Assert.True(result.ExitStatus.ExitCode == 0, result.StandardError);
    }

    [Fact]
    public void SeedWinsRegardlessOfTimeAndMissingSeedFilesStayLocal()
    {
        using var t = new TempDir();
        var seed = t.Sub("seed");
        var home = t.Sub("sandbox");
        Write(seed, ".codex/config.toml", "seed");
        Write(seed, ".claude/CLAUDE.md", "instructions");
        Write(home, ".codex/config.toml", "agent edit");
        File.SetLastWriteTimeUtc(Path.Combine(home, ".codex/config.toml"), DateTime.UtcNow.AddYears(1));
        Write(home, ".codex/history.jsonl", "history");
        Sync(t);
        Assert.Equal("seed", File.ReadAllText(Path.Combine(home, ".codex/config.toml")));
        Assert.Equal("instructions", File.ReadAllText(Path.Combine(home, ".claude/CLAUDE.md")));
        File.Delete(Path.Combine(seed, ".codex/config.toml"));
        Write(home, ".codex/config.toml", "now local");
        Sync(t);
        Assert.Equal("now local", File.ReadAllText(Path.Combine(home, ".codex/config.toml")));
        Assert.Equal("history", File.ReadAllText(Path.Combine(home, ".codex/history.jsonl")));
    }

    [Fact]
    public void CredentialsAndDockerStateStayLocalEvenIfTheyAppearInTheSeed()
    {
        using var t = new TempDir();
        var seed = t.Sub("seed");
        var home = t.Sub("sandbox");
        string[] credentials = [".claude/.credentials.json", ".codex/auth.json", ".local/share/opencode/auth.json", ".pi/agent/auth.json", ".docker/config.json"];
        foreach (var rel in credentials)
        {
            Write(seed, rel, "host credentials");
            Write(home, rel, "project credentials");
        }
        Sync(t);
        foreach (var rel in credentials) Assert.Equal("project credentials", File.ReadAllText(Path.Combine(home, rel)));
    }

    [Fact]
    public void ClaudeMcpMapIsAuthoritativeWhileLoginAndOtherFieldsSurvive()
    {
        using var t = new TempDir();
        var seed = t.Sub("seed");
        var home = t.Sub("sandbox");
        Write(seed, ".claude.json", """{"mcpServers":{"host":{"command":"true"}},"oauthAccount":"host-secret"}""");
        Write(home, ".claude.json", """{"mcpServers":{"sandbox":{}},"oauthAccount":"login","other":42}""");
        Sync(t);
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(home, ".claude.json"))))
        {
            Assert.Equal(["host"], doc.RootElement.GetProperty("mcpServers").EnumerateObject().Select(p => p.Name));
            Assert.Equal("login", doc.RootElement.GetProperty("oauthAccount").GetString());
            Assert.Equal(42, doc.RootElement.GetProperty("other").GetInt32());
        }
        Write(seed, ".claude.json", "{\"mcpServers\":{}}");
        Sync(t);
        using var empty = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(home, ".claude.json")));
        Assert.Empty(empty.RootElement.GetProperty("mcpServers").EnumerateObject());
        Assert.Equal("login", empty.RootElement.GetProperty("oauthAccount").GetString());

        Write(home, ".claude.json", "malformed login state");
        Sync(t);
        Assert.Equal("malformed login state", File.ReadAllText(Path.Combine(home, ".claude.json")));
    }

    [Fact]
    public void OnlyThisProjectsMemoryIsCopied()
    {
        using var t = new TempDir();
        Write(t.Sub("seed"), ".claude/projects/-project/memory/MEMORY.md", "ours");
        Write(t.Sub("seed"), ".claude/projects/-elsewhere/memory/MEMORY.md", "theirs");
        Sync(t);
        Assert.Equal("ours", File.ReadAllText(Path.Combine(t.Path, "sandbox/.claude/projects/-project/memory/MEMORY.md")));
        Assert.False(Directory.Exists(Path.Combine(t.Path, "sandbox/.claude/projects/-elsewhere")));
    }
}
