namespace Pen.Tests;

/// <summary>Each entry point takes its tools' domains from its own source; pen-test brings example.org.</summary>
public class EgressPolicyResolverTests
{
    private static readonly ToolSpec PenTest = new("pen-test", "1");

    private static Config User(string toml) => ConfigParser.ParseUser(toml, "u", RecipeCatalog.Embedded);

    private static Config ProjectConfig(string toml) => ConfigParser.ParseProject(toml, "p", RecipeCatalog.Embedded);

    [Fact]
    public void SessionStart_TakesTheToolsSelectedForTheSession_NotTheConfiguredOnes()
    {
        using var t = new TempDir();
        var host = t.Host();
        var project = Project.Resolve(t.Sub("repo"), host);
        var trust = TrustStore.For(host, project);

        var selected = EgressPolicyResolver.ForSessionStart(new ProjectContext(host, project, User(""), ProjectConfig("")),
            trust, [PenTest], RecipeCatalog.Embedded);
        Assert.Equal(["example.org"], selected.ToolDomains);
        Assert.Contains("example.org", selected.Allow);
        Assert.Contains("example.org", selected.NamedAllow);

        var configured = EgressPolicyResolver.ForSessionStart(
            new ProjectContext(host, project, User("tools = [\"pen-test\"]\n"), ProjectConfig("tools = [\"pen-test\"]\n")),
            trust, [], RecipeCatalog.Embedded);
        Assert.Empty(configured.ToolDomains);
        Assert.DoesNotContain("example.org", configured.Allow);
    }

    [Fact]
    public void SessionStart_LeavesOutAProjectAllowDomainThatIsntApproved()
    {
        using var t = new TempDir();
        var host = t.Host();
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var trust = TrustStore.For(host, project).ApproveDomains(["approved.example"]);
        var config = ProjectConfig("allow = [\"approved.example\", \"unapproved.example\"]\n");

        var policy = EgressPolicyResolver.ForSessionStart(new ProjectContext(host, project, User(""), config), trust, [],
            RecipeCatalog.Embedded);
        Assert.Contains("approved.example", policy.Allow);
        Assert.DoesNotContain("unapproved.example", policy.Allow);
        Assert.DoesNotContain("unapproved.example", policy.NamedAllow);
    }

    [Fact]
    public void Listing_TakesTheUsersToolsAndOnlyApprovedProjectOnes()
    {
        using var t = new TempDir();
        var host = t.Host();
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);

        var users = EgressPolicyResolver.ForListing(new ProjectContext(host, project, User("tools = [\"pen-test\"]\n"), ProjectConfig("")),
            RecipeCatalog.Embedded);
        Assert.Equal(["example.org"], users.ToolDomains);
        Assert.Contains("example.org", users.NamedAllow);

        var unapproved = EgressPolicyResolver.ForListing(new ProjectContext(host, project, User(""), ProjectConfig("tools = [\"pen-test\"]\n")),
            RecipeCatalog.Embedded);
        Assert.Empty(unapproved.ToolDomains);
        Assert.DoesNotContain("example.org", unapproved.Allow);

        TrustStore.For(host, project).Approve(new TrustRequest([], false, null, null, null) { Tools = [new(PenTest, true, ["example.org"])] });
        var approved = EgressPolicyResolver.ForListing(new ProjectContext(host, project, User(""), ProjectConfig("tools = [\"pen-test\"]\n")),
            RecipeCatalog.Embedded);
        Assert.Equal(["example.org"], approved.ToolDomains);
        Assert.Contains("example.org", approved.NamedAllow);
    }

    [Fact]
    public void RunningSession_TakesTheToolDomainsItRecorded_NotTheConfiguredTools()
    {
        using var t = new TempDir();
        var host = t.Host();
        Directory.CreateDirectory(host.ConfigDir);
        File.WriteAllText(host.UserConfigPath, "tools = [\"pen-test\"]\ndeny = [\"pastebin.com\"]\n");
        var project = Project.Resolve(t.Sub("repo"), host);
        ProjectRuntime.EnsureRuntimeDir(host, project);
        var s = new SessionDir(project.Key, t.Sub("home", ".local", "state", "pen", project.Key, "sessions", "12345678"));
        s.WriteTools([PenTest], ["recorded.example"]);

        var policy = EgressPolicyResolver.ForRunningSession(host, s, project.Dir, RecipeCatalog.Embedded);
        Assert.Equal(["recorded.example"], policy.ToolDomains);
        Assert.Contains("recorded.example", policy.NamedAllow);
        Assert.DoesNotContain("example.org", policy.Allow);
        Assert.Equal(["pastebin.com"], policy.Deny);
    }

    [Fact]
    public void AStarInAllowMakesTheMergedListJustTheStar_AndTheNamedListKeepsTheRest()
    {
        using var t = new TempDir();
        var host = t.Host();
        var project = Project.Resolve(t.Sub("repo"), host);
        var policy = EgressPolicyResolver.ForSessionStart(
            new ProjectContext(host, project, User("allow = [\"*\", \"typed.example\"]\n"), ProjectConfig("")),
            TrustStore.For(host, project), [], RecipeCatalog.Embedded);
        Assert.Equal(["*"], policy.Allow);
        Assert.Contains("typed.example", policy.NamedAllow);
        Assert.Contains("github.com", policy.NamedAllow);
        Assert.DoesNotContain("*", policy.NamedAllow);
    }
}
