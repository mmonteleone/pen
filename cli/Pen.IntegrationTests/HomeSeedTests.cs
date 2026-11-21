using System.Text.RegularExpressions;

namespace Pen.IntegrationTests;

/// <summary>The home seed: what a new project's first session gets from it, what seed-managed files overwrite,
/// what survives, and how the agents behave with it.</summary>
/// <remarks>With tools = ["codegraph"] and its MCP entry in the seed, codegraph_explore from Claude after
/// codegraph init is a manual check (it needs a login).</remarks>
public sealed class HomeSeedTests(HomeSeedTests.Fixture fixture) : IClassFixture<HomeSeedTests.Fixture>
{
    private const string BlockedDomain =
        @"example\.com.*(block|allow|proxy|403)|(block|allow|proxy|403).*example\.com";

    [Fact(DisplayName = "a new project's first session has the seeded CLAUDE.md and AGENTS.md")]
    public void FirstSessionHasTheNotes() =>
        Assert.Multiple(() => AssertLine(fixture.First, "claude-note"), () => AssertLine(fixture.First, "codex-note"));

    [Fact(DisplayName = "...and no MCP servers for Claude (no .claude.json in the seed)")]
    public void NoMcpServersForClaude() => AssertLine(fixture.First, "mcp=none");

    [Fact(DisplayName = "...or Codex")]
    public void NoMcpServersForCodex() => AssertLine(fixture.First, "codex-no-mcp");

    [Fact(DisplayName = "...Claude's default mode is bypassPermissions")]
    public void ClaudeBypassesPermissions() => AssertLine(fixture.First, "mode=bypassPermissions");

    [Fact(DisplayName = "...with Claude's own attribution defaults (none seeded)")]
    public void ClaudeHasItsOwnAttribution() => AssertLine(fixture.First, "attribution=null");

    [Fact(DisplayName = "...and Codex runs without approvals")]
    public void CodexRunsWithoutApprovals() => AssertLine(fixture.First, "codex-never");

    [Fact(DisplayName = "only this project's .claude/projects/<slug>/ is copied")]
    public void OnlyThisProjectsMemoryIsCopied() => AssertLine(fixture.First, $"projects={fixture.Slug} ");

    [Fact(DisplayName = "...with its memory")]
    public void MemoryIsCopied() => AssertLine(fixture.First, "memory=seeded memory");

    [Fact(DisplayName = "a sandbox commit has the seeded author")]
    public void CommitHasTheSeededAuthor() =>
        Assert.Equal($"{FakeHome.GitName} <{FakeHome.GitEmail}>", fixture.CommitAuthor);

    [Fact(DisplayName = "...and no co-author trailer")]
    public void CommitHasNoCoAuthor() =>
        Assert.DoesNotMatch(new Regex("co-authored-by", RegexOptions.IgnoreCase), fixture.CommitMessage);

    [Fact(DisplayName = "git status in the sandbox doesn't list .codegraph/")]
    public void GitStatusIgnoresCodegraph() => AssertLine(fixture.First, "status=[]");

    [Fact(DisplayName = "seed settings overwrite sandbox edits even with a future mtime")]
    public void SeedOverwritesSandboxEdits() => AssertNoLine(fixture.Second, "local edit");

    [Fact(DisplayName = "...without changing the seed")]
    public void SeedIsUnchanged() =>
        Assert.DoesNotContain("local edit", fixture.SeedClaudeMdAfterEdit, StringComparison.Ordinal);

    [Fact(DisplayName = "...or another project")]
    public void OtherProjectIsUnchanged() =>
        Assert.True(
            !fixture.OtherProject.Stdout.Contains("local edit", StringComparison.Ordinal),
            fixture.OtherProject.Describe());

    [Fact(DisplayName = "with no .claude.json in the seed, Claude's MCP servers are the sandbox's own")]
    public void McpServersAreTheSandboxsOwn() => AssertLine(fixture.Second, "mcp=[\"from-sandbox\"]");

    [Fact(DisplayName = "Codex credentials survive synchronization")]
    public void CodexCredentialsSurvive() => AssertLine(fixture.Second, "project login");

    [Fact(DisplayName = "Claude credentials survive synchronization")]
    public void ClaudeCredentialsSurvive() => AssertLine(fixture.Second, "claude login");

    [Fact(DisplayName = "a file added to the seed later reaches the project's next session")]
    public void AddedFileReachesTheProject() => AssertLine(fixture.Third, "added later");

    [Fact(DisplayName = "editing a seed file replaces the project's copy")]
    public void EditedFileReplacesTheCopy() => AssertLine(fixture.Third, "seed v2");

    [Fact(DisplayName = "a .claude.json added to the seed owns an existing project's whole MCP map")]
    public void SeededClaudeJsonOwnsTheMcpMap() => AssertLine(fixture.Third, "mcp=[\"seeded-later\"]");

    [Fact(DisplayName = "...and the project stays logged in (the rest of .claude.json is kept)")]
    public void ProjectStaysLoggedIn() => AssertLine(fixture.Third, "login={\"integration\":true}");

    [Fact(DisplayName = "claude starts in skip-permissions mode (wrote and committed without asking)")]
    public void ClaudeSkipsPermissions()
    {
        var claude = RequireClaude();

        Assert.True(claude.WroteFile, claude.Prompt.Describe());
    }

    [Fact(DisplayName = "...and its commit has the seeded author")]
    public void ClaudesCommitHasTheSeededAuthor() => Assert.Equal(FakeHome.GitName, RequireClaude().CommitAuthor);

    [Fact(DisplayName = "claude reports the blocked domain")]
    public void ClaudeReportsTheBlockedDomain() =>
        Assert.Matches(new Regex(BlockedDomain, RegexOptions.IgnoreCase), RequireClaude().BlockedFetch.Output);

    [Fact(DisplayName = "codex reports the blocked domain")]
    public void CodexReportsTheBlockedDomain()
    {
        IntegrationEnvironment.RequireCredential("OPENAI_API_KEY");
        Assert.NotNull(fixture.CodexBlockedFetch);

        Assert.Matches(new Regex(BlockedDomain, RegexOptions.IgnoreCase), fixture.CodexBlockedFetch.Output);
    }

    private static void AssertLine(CommandResult result, string line) =>
        Assert.True(result.OutputLines.Contains(line), $"no line \"{line}\"; {result.Describe()}");

    private static void AssertNoLine(CommandResult result, string line) =>
        Assert.True(
            result.OutputComplete && !result.OutputLines.Contains(line), $"a line \"{line}\"; {result.Describe()}");

    private ClaudeResult RequireClaude()
    {
        IntegrationEnvironment.RequireCredential("CLAUDE_CODE_OAUTH_TOKEN");
        Assert.NotNull(fixture.Claude);
        return fixture.Claude;
    }

    /// <summary>What Claude did with the seed's settings.</summary>
    /// <param name="Prompt">The session asked to write and commit a file.</param>
    /// <param name="WroteFile">Whether perm-check.txt was there afterwards.</param>
    /// <param name="CommitAuthor">The author name of the project's last commit afterwards.</param>
    /// <param name="BlockedFetch">The session asked to fetch example.com.</param>
    internal sealed record ClaudeResult(
        CommandResult Prompt, bool WroteFile, string CommitAuthor, CommandResult BlockedFetch);

    /// <summary>Seeds a project's Claude memory, runs a new project's first session that edits its copies, then
    /// sessions after that and after the seed changes, and, with credentials, Claude and Codex.</summary>
    public sealed class Fixture : IntegrationFixture
    {
        internal IntegrationProject Seeded { get; private set; } = null!;

        /// <summary>Gets the seeded project's path with each character other than a letter or digit as a dash, as
        /// Claude names its project dirs.</summary>
        internal string Slug { get; private set; } = null!;

        internal CommandResult First { get; private set; } = null!;

        internal string CommitAuthor { get; private set; } = null!;

        internal string CommitMessage { get; private set; } = null!;

        internal CommandResult Second { get; private set; } = null!;

        internal string SeedClaudeMdAfterEdit { get; private set; } = null!;

        internal CommandResult OtherProject { get; private set; } = null!;

        internal CommandResult Third { get; private set; } = null!;

        /// <summary>Gets Claude's sessions, or null without CLAUDE_CODE_OAUTH_TOKEN.</summary>
        internal ClaudeResult? Claude { get; private set; }

        /// <summary>Gets Codex's blocked fetch, or null without OPENAI_API_KEY.</summary>
        internal CommandResult? CodexBlockedFetch { get; private set; }

        private string SeedClaudeMd => Path.Combine(Home.SeedDir, ".claude", "CLAUDE.md");

        protected override void Prepare()
        {
            Seeded = CreateEmptyProject(".pen-integration-seed.");
            Seeded.RunGit("init", "-q");
            Seeded.RunGit(
                "-c", "user.name=x", "-c", "user.email=x@example.invalid", "-c", "commit.gpgsign=false",
                "commit", "-q", "--allow-empty", "-m", "init");
            Home.MakeToolFree();
            Slug = Regex.Replace(Seeded.Dir, "[^A-Za-z0-9]", "-");
            WriteSeedFile($".claude/projects/{Slug}/memory/MEMORY.md", "seeded memory\n");
            WriteSeedFile(".claude/projects/-elsewhere/memory/MEMORY.md", "not yours\n");

            RunFirstSession();
            Second = RunSeeded("""
                tail -1 ~/.claude/CLAUDE.md; echo "mcp=$(jq -c ".mcpServers | keys" ~/.claude.json)"; cat ~/.codex/auth.json; echo; cat ~/.claude/.credentials.json; echo
                """).RequireSuccess();
            SeedClaudeMdAfterEdit = File.ReadAllText(SeedClaudeMd);
            OtherProject = Cli.Run("run", "tail", "-1", "/home/agent/.claude/CLAUDE.md");
            RunAfterSeedChanges();
            RunSeeded("rm ~/.codex/auth.json ~/.claude/.credentials.json").RequireSuccess();

            Claude = RunClaude();
            CodexBlockedFetch = RunCodex();
        }

        private void RunFirstSession()
        {
            First = RunSeeded("""
                grep -q "running in a pen sandbox" ~/.claude/CLAUDE.md && echo claude-note
                grep -q "running in a pen sandbox" ~/.codex/AGENTS.md && echo codex-note
                echo "mcp=$(jq -c ".mcpServers | keys" ~/.claude.json 2>/dev/null || echo none)"
                echo "mode=$(jq -r .permissions.defaultMode ~/.claude/settings.json)"
                echo "attribution=$(jq -c .attribution ~/.claude/settings.json)"
                grep -q "^approval_policy = \"never\"" ~/.codex/config.toml && echo codex-never
                grep -q "mcp_servers" ~/.codex/config.toml || echo codex-no-mcp
                echo "projects=$(ls ~/.claude/projects | tr "\n" " ")"
                echo "memory=$(cat ~/.claude/projects/*/memory/MEMORY.md)"
                git commit -q --allow-empty -m "sandbox commit" && echo committed
                mkdir -p .codegraph && touch .codegraph/db && echo "status=[$(git status --porcelain)]"
                echo "local edit" >>~/.claude/CLAUDE.md
                touch -d "next year" ~/.claude/CLAUDE.md
                printf "%s" "project login" >~/.codex/auth.json
                printf "%s" "claude login" >~/.claude/.credentials.json
                echo "{\"oauthAccount\": {\"integration\": true}, \"mcpServers\": {\"from-sandbox\": {\"command\": \"true\"}}}" >~/.claude.json
                """);
            if (First.ExitCode != 0)
                throw new InvalidOperationException($"the first seeded session failed: {First.Describe()}");
            CommitAuthor = Seeded.RunGit("log", "-1", "--format=%an <%ae>").TrimEnd('\n');
            CommitMessage = Seeded.RunGit("log", "-1", "--format=%B");
        }

        /// <summary>Adds a file to the seed, edits one and adds a .claude.json, each replaced by rename as editors
        /// save, then runs a session that reads them.</summary>
        /// <remarks>On OrbStack, a host file rewritten in place can look unchanged to a new container for several
        /// seconds.</remarks>
        private void RunAfterSeedChanges()
        {
            File.WriteAllText(Path.Combine(Home.SeedDir, ".added-later"), "added later\n");
            ReplaceSeedFile(".claude/CLAUDE.md", File.ReadAllText(SeedClaudeMd) + "seed v2\n");
            ReplaceSeedFile(
                ".claude.json", """{"mcpServers": {"seeded-later": {"type": "stdio", "command": "true"}}}""");
            Third = RunSeeded("""
                cat ~/.added-later; tail -1 ~/.claude/CLAUDE.md; echo "mcp=$(jq -c ".mcpServers | keys" ~/.claude.json)"; echo "login=$(jq -c .oauthAccount ~/.claude.json)"
                """).RequireSuccess();
        }

        private ClaudeResult? RunClaude()
        {
            if (IntegrationEnvironment.FindCredential("CLAUDE_CODE_OAUTH_TOKEN") is not { } token)
                return null;
            Home.WriteConfig("[env]", $"CLAUDE_CODE_OAUTH_TOKEN = \"{token}\"");
            var prompt = RunSeededWithTool("claude", "timeout", "300", "claude", "-p", """
                Create a file named perm-check.txt containing "ok", then commit it with git (message "perm check"). Reply "done".
                """);
            var wrote = File.Exists(Path.Combine(Seeded.Dir, "perm-check.txt"));
            var author = Seeded.RunGit("log", "-1", "--format=%an").TrimEnd('\n');
            var fetch = RunSeededWithTool(
                "claude", "timeout", "300", "claude", "-p", "Fetch https://example.com and show me its title.");
            return new ClaudeResult(prompt, wrote, author, fetch);
        }

        private CommandResult? RunCodex()
        {
            if (IntegrationEnvironment.FindCredential("OPENAI_API_KEY") is not { } key)
                return null;
            Home.WriteConfig("[env]", $"OPENAI_API_KEY = \"{key}\"");
            return RunSeededWithTool(
                "codex", "timeout", "300", "codex", "exec", "Fetch https://example.com and show me its title.");
        }

        private CommandResult RunSeeded(string script) =>
            Cli.WithWorkingDirectory(Seeded.Dir).Run("run", "bash", "-c", script);

        private CommandResult RunSeededWithTool(string tool, params string[] command) =>
            Cli.WithWorkingDirectory(Seeded.Dir).Run(["--tool", tool, "run", .. command]);

        private void WriteSeedFile(string relative, string text)
        {
            var path = Path.Combine(Home.SeedDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        private void ReplaceSeedFile(string relative, string text)
        {
            var path = Path.Combine(Home.SeedDir, relative);
            File.WriteAllText(path + ".new", text);
            File.Move(path + ".new", path, overwrite: true);
        }
    }
}
