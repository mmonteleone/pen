namespace Pen.Tests;

[Collection(nameof(LockAndProcessTests))]
public class DenyCommandTests
{
    private static readonly RecipeCatalog Catalog = RecipeCatalog.FromFiles(new Dictionary<string, string>
    {
        ["tools/agent/tool.toml"] = "default = \"1\"\nversion = '^\\d+$'\nkind = \"agent\"\nallow = [\".anthropic.com\", \"api.github.com\"]\n",
        ["tools/agent/install.sh"] = "#!/bin/sh\n",
        ["tools/secret/tool.toml"] = "default = \"1\"\nversion = '^\\d+$'\nhidden = true\nallow = [\"api.anthropic.com\"]\n",
        ["tools/secret/install.sh"] = "#!/bin/sh\n",
    });

    private static string CapturingStderr(Action action)
    {
        var stderr = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try { action(); }
        finally { Console.SetError(stderr); }
        return captured.ToString();
    }

    [Fact]
    public void ARunningSessionGetsTheDenyLive_AndRunningItAgainAddsNothing()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(host.UserConfigPath, "allow = [\"typed.example\"]\n");
        var project = Project.Resolve(t.Sub("repo"), host);
        Session.EnsureRuntimeDir(host, project);
        var s = new SessionDir(project.Key, t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678"));
        File.WriteAllText(s.ComposeFile, "");
        File.WriteAllText(s.EnvFile, $"PROJECT_DIR={project.Dir}\n");
        File.WriteAllText(s.AllowlistFile, "");
        File.WriteAllText(s.DenylistFile, "");
        File.WriteAllText(s.OpenEgressFile, "");
        using var sessionLock = FileLock.TryTake(s.LockFile, exclusive: true);
        using var fake = new FakeDocker(t, FakeDocker.ServingAclFiles + "exit 0\n");

        var where = host.Display(host.UserConfigPath);
        var err = CapturingStderr(() => Assert.Equal(0, new DenyCommand(host, Catalog).Run(["gist.github.com"], global: true)));
        Assert.Contains($"pen: added to {where}: gist.github.com\n", err);
        Assert.Contains("pen: applied to 1 running session\n", err);
        Assert.Equal("gist.github.com\n", File.ReadAllText(s.DenylistFile));
        Assert.Contains("typed.example\n", File.ReadAllText(s.AllowlistFile));
        var user = Config.Load(host.ConfigDir, "x", ConfigKind.User);
        Assert.Equal(["typed.example"], user.Allow);
        Assert.Equal(["gist.github.com"], user.Deny);

        var again = CapturingStderr(() => Assert.Equal(0, new DenyCommand(host, Catalog).Run(["gist.github.com"], global: true)));
        Assert.Contains($"pen: already in {where}: gist.github.com\n", again);
        Assert.DoesNotContain("added to", again);
    }

    [Fact]
    public void AProjectDenyIsWrittenFromTheStub_WithNoApproval()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        var project = Project.Resolve(t.Sub("repo"), host);
        Session.EnsureRuntimeDir(host, project);
        var addition = AllowCommand.AddToConfig(host, project, global: false, "deny", ["pastebin.com"]);
        Assert.Equal(".pen/config.toml", addition.Where);
        Assert.Equal(["pastebin.com"], addition.Added);
        var text = File.ReadAllText(project.ProjectConfigPath);
        Assert.StartsWith(AllowCommand.ProjectConfigStub, text);
        Assert.Equal(["pastebin.com"], Config.Parse(text, "x", ConfigKind.Project).Deny);
        Assert.Empty(Config.Parse(text, "x", ConfigKind.Project).Allow);
        Assert.False(File.Exists(TrustStore.GetPath(host.GetRuntimeDir(project.Key), project.Dir)));
    }

    [Fact]
    public void ListingNamesEachEntrysFile_AndWhatItTakes()
    {
        var user = Config.Parse("deny = [\"pastebin.com\", \"api.github.com\"]\n", "u", ConfigKind.User);
        var project = Config.Parse("deny = [\"api.anthropic.com\", \".anthropic.com\"]\n", "p", ConfigKind.Project);
        Assert.Equal("""
            denied (deny wins over every allow):
              pastebin.com       ~/.config/pen/config.toml
              api.github.com     ~/.config/pen/config.toml  takes api.github.com (default, agent)
              api.anthropic.com  .pen/config.toml           takes part of .anthropic.com (agent)
              .anthropic.com     .pen/config.toml           takes .anthropic.com (agent)

            """, DenyCommand.FormatListing(user, "~/.config/pen/config.toml", project, Catalog));
    }

    [Fact]
    public void ListingWithNothingDenied() =>
        Assert.Equal("nothing denied; pen deny <domain…> [--global] adds to deny\n",
            DenyCommand.FormatListing(new Config(), "~/.config/pen/config.toml", new Config(), Catalog));
}
