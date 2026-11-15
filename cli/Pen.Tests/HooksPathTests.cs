using System.Text;

namespace Pen.Tests;

public class HooksPathTests
{
    /// <summary>A repo, a host home outside it, and no config beyond what a test writes.</summary>
    private sealed class Fixture : IDisposable
    {
        public TempDir T { get; } = new();
        public string Home => T.Sub("home");
        public Dictionary<string, string?> Env { get; } = new() { ["GIT_CONFIG_NOSYSTEM"] = "1" };
        public string Dir { get; }
        public string GitDir => Path.Combine(Dir, ".git");
        public string Config => Path.Combine(GitDir, "config");

        public Fixture(bool repo = true)
        {
            Dir = T.Sub("project");
            if (repo) Git.Repo(Dir);
        }

        /// <summary>Appends to the repo's config as it's written on disk.</summary>
        public void Add(string text) => File.AppendAllText(Config, text);

        public void Set(string value) => Add($"[core]\n\thooksPath = {value}\n");

        public string? Warning(string? dir = null)
        {
            dir ??= Dir;
            var (common, repoDir) = Project.FindRepo(dir);
            return HooksPath.FindWarning(new Project(dir, "key", common, repoDir), T.Host(Home), name => Env.GetValueOrDefault(name));
        }

        /// <summary>Real git's hooks dir here, so a test can hold Pen's reading against it.</summary>
        public string GitSays(string? dir = null)
        {
            dir ??= Dir;
            return Git.Run(dir, "rev-parse", "--git-path", "hooks").TrimEnd('\n');
        }

        public void Dispose() => T.Dispose();
    }

    [Theory]
    [InlineData(".husky")]
    [InlineData("./.husky/")]
    [InlineData("sub/dir/hooks")]
    [InlineData("sub/../.husky")]
    [InlineData(".git/hooks/../sneaky")]
    [InlineData(".git/my-hooks")]
    [InlineData("\".husky\" ; comment")]
    public void ARelativePathIntoTheProject_Warns(string value)
    {
        using var f = new Fixture();
        f.Set(value);
        Assert.Contains("core.hooksPath", f.Warning());
    }

    [Fact]
    public void AnAbsolutePathIntoTheProject_Warns()
    {
        using var f = new Fixture();
        f.Set(Path.Combine(f.Dir, "tools", "hooks"));
        Assert.Contains("can be changed from the sandbox", f.Warning());
    }

    [Theory]
    [InlineData(".git/hooks")]
    [InlineData("./.git/hooks/")]
    [InlineData(".git/hooks/sub")]
    public void ThePathGitUsesAnyway_IsReadOnlyAndDoesntWarn(string value)
    {
        using var f = new Fixture();
        f.Set(value);
        Assert.Null(f.Warning());
    }

    [Fact]
    public void NoHooksPath_NoWarning()
    {
        using var f = new Fixture();
        Assert.Null(f.Warning());
    }

    [Theory]
    [InlineData("../../sibling-hooks")]
    [InlineData("/dev/null")]
    [InlineData("/usr/share/hooks")]
    public void AnOutsidePath_DoesntWarn(string value)
    {
        using var f = new Fixture();
        f.Set(value);
        Assert.Null(f.Warning());
    }

    [Fact]
    public void DotDot_WarnsSinceAPushIntoThisRepoRunsHooksFromTheGitDir()
    {
        using var f = new Fixture();
        f.Set("../push-hooks");
        Assert.NotNull(f.Warning());

        // The replay: the sandbox wrote the hook, and a push on the host runs it.
        var hooks = Path.Combine(f.Dir, "push-hooks");
        Directory.CreateDirectory(hooks);
        var marker = Path.Combine(f.T.Path, "planted-hook-ran");
        var hook = Path.Combine(hooks, "pre-receive");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
        var clone = Path.Combine(f.T.Path, "clone");
        Git.Run(f.T.Path, "clone", "-q", f.Dir, clone);
        Git.Run(clone, "push", "-q", "origin", "HEAD:refs/heads/pushed");
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void AnEmptyValue_IsTheRootDirectoryForGit_SoItDoesntWarnAndHidesEarlierOnes()
    {
        using var f = new Fixture();
        f.Add("[core]\n\thooksPath = .husky\n\thooksPath =\n");
        Assert.Equal("./", f.GitSays());
        Assert.Equal("/pre-commit\n", Git.Run(f.Dir, "rev-parse", "--git-path", "hooks/pre-commit"));
        Assert.Null(f.Warning());
    }

    // Last value wins, and names aren't case-sensitive

    [Fact]
    public void TheLastValueWins_InsideLast()
    {
        using var f = new Fixture();
        f.Add("[core]\n\thooksPath = /dev/null\n[CORE]\n\tHOOKSPATH = .husky\n");
        Assert.Equal(".husky", f.GitSays());
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void TheLastValueWins_OutsideLast()
    {
        using var f = new Fixture();
        f.Add("[core]\n\thooksPath = .husky\n\thooksPath = /dev/null\n");
        Assert.Equal("/dev/null", f.GitSays());
        Assert.Null(f.Warning());
    }

    [Theory]
    [InlineData("[core \"x\"]\n\thooksPath = .husky\n")]
    [InlineData("[core.x]\n\thooksPath = .husky\n")]
    public void AnotherKeyInASubsection_IsntCoreHooksPath(string text)
    {
        using var f = new Fixture();
        f.Add(text);
        Assert.Equal(".git/hooks", f.GitSays());
        Assert.Null(f.Warning());
    }

    [Fact]
    public void AComment_EndingInABackslash_CantHideTheKey()
    {
        using var f = new Fixture();
        f.Add("[core]\n\t# looks continued \\\n\thooksPath = .husky\n");
        Assert.Equal(".husky", f.GitSays());
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void AQuotedLeadingSpace_MakesItRelative()
    {
        using var f = new Fixture();
        f.Set("\" /usr/share/hooks\"");
        Assert.Equal(" /usr/share/hooks", f.GitSays());
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void Optional_IsStripped()
    {
        using var f = new Fixture();
        f.Set(":(optional).husky");
        // A missing one counts as unset, until the sandbox creates it.
        Assert.Equal(".git/hooks", f.GitSays());
        Assert.NotNull(f.Warning());
        Directory.CreateDirectory(Path.Combine(f.Dir, ".husky"));
        Assert.Equal(".husky", f.GitSays());
    }

    [Fact]
    public void Optional_AndMissing_StillHidesAnEarlierValue()
    {
        using var f = new Fixture();
        f.Add("[core]\n\thooksPath = .husky\n\thooksPath = \":(optional)/nonexistent-pen-test\"\n");
        Assert.Equal(".git/hooks", f.GitSays());
        Assert.Null(f.Warning());
    }

    // Includes

    [Fact]
    public void AnInclude_Warns()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "inc"), "[core]\n\thooksPath = .husky\n");
        f.Add("[include]\n\tpath = inc\n");
        Assert.Equal(".husky", f.GitSays());
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void AnIncludedRelativePath_IsStillFromTheWorkTree()
    {
        using var f = new Fixture();
        var other = f.T.Sub("elsewhere");
        File.WriteAllText(Path.Combine(other, "inc"), "[core]\n\thooksPath = .husky\n");
        f.Add($"[include]\n\tpath = {other}/inc\n");
        Assert.Equal(".husky", f.GitSays());
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void AValueAfterAnInclude_Overrides_ThenAFileIncludedAgainOverridesThat()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "a"), "[core]\n\thooksPath = .husky\n");
        File.WriteAllText(Path.Combine(f.GitDir, "b"), "[core]\n\thooksPath = /dev/null\n");

        f.Add("[include]\n\tpath = a\n\tpath = b\n");
        Assert.Equal("/dev/null", f.GitSays());
        Assert.Null(f.Warning());

        f.Add("\tpath = a\n");
        Assert.Equal(".husky", f.GitSays());
        Assert.NotNull(f.Warning());
    }

    [Theory]
    [InlineData("[include \"x\"]\n\tpath = inc\n")]
    [InlineData("[includeIf]\n\tpath = inc\n")]
    public void WhatGitDoesntTakeAsAnInclude_DoesntCount(string text)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "inc"), "[core]\n\thooksPath = .husky\n");
        f.Add(text);
        Assert.Equal(".git/hooks", f.GitSays());
        Assert.Null(f.Warning());
    }

    [Fact]
    public void AConditionalInclude_WarnsForTheBranchItCouldApplyTo()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "inc"), "[core]\n\thooksPath = .husky\n");
        f.Add("[includeIf \"onbranch:some-other-branch\"]\n\tpath = inc\n");
        Assert.Equal(".git/hooks", f.GitSays());
        Assert.NotNull(f.Warning()); // the sandbox can switch to that branch
        Git.Run(f.Dir, "checkout", "-q", "-b", "some-other-branch");
        Assert.Equal(".husky", f.GitSays());
    }

    [Fact]
    public void AConditionalValue_DoesntHideADefiniteOneBeforeIt()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "inc"), "[core]\n\thooksPath = /dev/null\n");
        f.Add("[core]\n\thooksPath = .husky\n[includeIf \"onbranch:other\"]\n\tpath = inc\n");
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void ADefiniteValue_HidesAConditionalOneBeforeIt()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "inc"), "[core]\n\thooksPath = .husky\n");
        f.Add("[includeIf \"onbranch:other\"]\n\tpath = inc\n[core]\n\thooksPath = /dev/null\n");
        Assert.Null(f.Warning());
    }

    // Global and system config

    [Fact]
    public void GlobalConfig_ThatResolvesIntoTheProject_Warns()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), "[core]\n\thooksPath = .husky\n");
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void GlobalConfig_WithAnAbsolutePathIntoTheProject_Warns()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), $"[core]\n\thooksPath = {f.Dir}/hooks\n");
        Assert.Contains("~/.gitconfig", f.Warning());
    }

    [Fact]
    public void GlobalConfig_ThatLeadsElsewhere_DoesntWarn()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), "[core]\n\thooksPath = ~/hooks\n");
        Assert.Null(f.Warning());
    }

    [Fact]
    public void TheRepoConfig_OverridesGlobal_BothWays()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), "[core]\n\thooksPath = .husky\n");
        f.Set("/dev/null");
        Assert.Null(f.Warning());

        using var g = new Fixture();
        File.WriteAllText(Path.Combine(g.Home, ".gitconfig"), "[core]\n\thooksPath = /dev/null\n");
        g.Set(".husky");
        Assert.NotNull(g.Warning());
    }

    [Fact]
    public void HomeGitconfig_OverridesXdgConfig()
    {
        using var f = new Fixture();
        var xdg = Path.Combine(f.Home, ".config", "git");
        Directory.CreateDirectory(xdg);
        File.WriteAllText(Path.Combine(xdg, "config"), "[core]\n\thooksPath = .husky\n");
        Assert.NotNull(f.Warning());
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), "[core]\n\thooksPath = /dev/null\n");
        Assert.Null(f.Warning());
    }

    [Fact]
    public void XdgConfigHome_IsWhereItSays()
    {
        using var f = new Fixture();
        var xdg = f.T.Sub("xdg", "git");
        File.WriteAllText(Path.Combine(xdg, "config"), "[core]\n\thooksPath = .husky\n");
        Assert.Null(f.Warning());
        f.Env["XDG_CONFIG_HOME"] = Path.GetDirectoryName(xdg);
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void GitConfigGlobal_ReplacesBothGlobalFiles()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), "[core]\n\thooksPath = .husky\n");
        foreach (var none in new[] { "/dev/null", "" })
        {
            f.Env["GIT_CONFIG_GLOBAL"] = none;
            Assert.Null(f.Warning());
        }
        var file = Path.Combine(f.T.Path, "global");
        File.WriteAllText(file, "[core]\n\thooksPath = .other\n");
        f.Env["GIT_CONFIG_GLOBAL"] = file;
        Assert.Contains(".other", f.Warning());
    }

    [Fact]
    public void SystemConfig_CountsUnlessTurnedOff()
    {
        using var f = new Fixture();
        var file = Path.Combine(f.T.Path, "system");
        File.WriteAllText(file, "[core]\n\thooksPath = .husky\n");
        f.Env["GIT_CONFIG_SYSTEM"] = file;
        Assert.Null(f.Warning());
        f.Env.Remove("GIT_CONFIG_NOSYSTEM");
        Assert.NotNull(f.Warning());
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), "[core]\n\thooksPath = /dev/null\n");
        Assert.Null(f.Warning());
    }

    // ~

    [Fact]
    public void Tilde_IsTheHostHome_AndALinkThereCanLeadIn()
    {
        using var f = new Fixture();
        File.CreateSymbolicLink(Path.Combine(f.Home, "proj"), f.Dir);
        f.Set("~/proj/hooks");
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void Tilde_OnItsOwn_IsOutsideTheProject()
    {
        using var f = new Fixture();
        f.Set("~");
        Assert.Null(f.Warning());
    }

    // Symlinks

    [Fact]
    public void AnOutsideLookingPathThatLeadsIn_Warns()
    {
        using var f = new Fixture();
        var link = Path.Combine(f.T.Path, "outside-link");
        File.CreateSymbolicLink(link, Path.Combine(f.Dir, "hooks"));
        f.Set(link);
        Assert.NotNull(f.Warning()); // dangling: the sandbox can make what it names
        Directory.CreateDirectory(Path.Combine(f.Dir, "hooks"));
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void ALinkSwappedForARealDirectory_RunsThePlantedHook_WhichIsWhyItWarnedBefore()
    {
        using var f = new Fixture();
        var outside = f.T.Sub("outside-hooks");
        var link = Path.Combine(f.Dir, "hooks");
        File.CreateSymbolicLink(link, outside);
        f.Set("hooks");
        Assert.NotNull(f.Warning());

        // What the agent does in the sandbox.
        File.Delete(link);
        Directory.CreateDirectory(link);
        var marker = Path.Combine(f.T.Path, "planted-hook-ran");
        var hook = Path.Combine(link, "pre-commit");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
        File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);

        Git.Run(f.Dir, "commit", "-q", "--allow-empty", "-m", "on the host");
        Assert.True(File.Exists(marker));
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void ALinkInTheProject_ReachedOnlyThroughALinkOutside_Warns()
    {
        // Outside as written and outside where it leads: only the walk sees the name the sandbox can replace.
        using var f = new Fixture();
        var inner = Path.Combine(f.Dir, "inner");
        File.CreateSymbolicLink(inner, f.T.Sub("real-hooks"));
        var outer = Path.Combine(f.T.Path, "outer");
        File.CreateSymbolicLink(outer, inner);
        f.Set(outer);
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void ARealDirectoryInTheProject_ThatThePathOnlyPassesThrough_Warns()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Dir, "sub"));
        f.Set("sub/../../sibling-hooks");
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void APathThatOnlyReadsAsInside_Warns()
    {
        using var f = new Fixture();
        var deep = f.T.Sub("a", "b", "c");
        var outside = f.T.Sub("outside");
        File.CreateSymbolicLink(Path.Combine(outside, "link"), deep);
        var rel = Path.GetRelativePath(outside, f.Dir);
        f.Set($"{outside}/link/../{rel}/hooks"); // reads as project/hooks; really under a/b
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void APathOutsideOnBothViews_DoesntWarn()
    {
        using var f = new Fixture();
        var target = f.T.Sub("real-hooks");
        var link = Path.Combine(f.T.Path, "outside-link");
        File.CreateSymbolicLink(link, target);
        f.Set(link);
        Assert.Null(f.Warning());
    }

    [Fact]
    public void ALinkInGitHooks_LeadingOut_CantBeReplaced_SoDoesntWarn()
    {
        using var f = new Fixture();
        var hooks = Path.Combine(f.GitDir, "hooks");
        Directory.CreateDirectory(hooks);
        File.CreateSymbolicLink(Path.Combine(hooks, "out"), f.T.Sub("elsewhere"));
        f.Set(hooks + "/out");
        Assert.Null(f.Warning());
    }

    [Fact]
    public void DotDot_AfterALink_IsWhereTheLinkLed()
    {
        using var f = new Fixture();
        var inner = Path.Combine(f.Dir, "inner");
        Directory.CreateDirectory(inner);
        var outside = f.T.Sub("outside");
        File.CreateSymbolicLink(Path.Combine(outside, "link"), inner);
        f.Set($"{outside}/link/../hk"); // reads as outside/hk, but it's project/hk
        Assert.NotNull(f.Warning());

        using var g = new Fixture();
        var away = g.T.Sub("away", "deep");
        File.CreateSymbolicLink(Path.Combine(g.Dir, "link"), away);
        g.Set("link/../hk"); // reads as project/hk, leads to away/hk, and the sandbox can replace link
        Assert.NotNull(g.Warning());
    }

    [Fact]
    public void ALinkToGitHooks_IsGitHooks()
    {
        using var f = new Fixture();
        var link = Path.Combine(f.T.Path, "to-hooks");
        Directory.CreateDirectory(Path.Combine(f.GitDir, "hooks"));
        File.CreateSymbolicLink(link, Path.Combine(f.GitDir, "hooks"));
        f.Set(link);
        Assert.Null(f.Warning());
    }

    [Fact]
    public void ACaseVariant_OfAPathInside_Warns_WhereTheFileSystemFolds()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.Dir, ".husky"));
        var variant = f.Dir.ToUpperInvariant() + "/.HUSKY";
        f.Set(variant);
        bool folds = Directory.Exists(variant);
        Assert.Equal(folds, f.Warning() != null);
    }

    [Fact]
    public void ACaseVariant_OfGitHooks_IsGitHooks_WhereTheFileSystemFolds()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(Path.Combine(f.GitDir, "hooks"));
        f.Set(".GIT/HOOKS");
        bool folds = Directory.Exists(Path.Combine(f.Dir, ".GIT", "HOOKS"));
        Assert.Equal(!folds, f.Warning() != null); // where it doesn't fold, .GIT/HOOKS is just another project path
    }

    [Fact]
    public void ALinkLoop_WarnsAsUnresolvable()
    {
        using var f = new Fixture();
        var a = Path.Combine(f.T.Path, "a");
        var b = Path.Combine(f.T.Path, "b");
        File.CreateSymbolicLink(a, b);
        File.CreateSymbolicLink(b, a);
        f.Set(a + "/hooks");
        Assert.Contains("can't tell", f.Warning());
    }

    // config.worktree

    [Fact]
    public void ConfigWorktree_CountsOnlyWhenEnabled_AndOverridesConfig()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "config.worktree"), "[core]\n\thooksPath = .husky\n");
        Assert.Equal(".git/hooks", f.GitSays());
        Assert.Null(f.Warning());

        f.Add("[extensions]\n\tworktreeConfig = true\n[core]\n\thooksPath = /dev/null\n");
        Assert.Equal(".husky", f.GitSays());
        Assert.NotNull(f.Warning());
    }

    [Theory]
    [InlineData("[extensions]\n\tworktreeConfig = 2\n", true)]
    [InlineData("[extensions]\n\tworktreeConfig\n", true)]
    [InlineData("[extensions]\n\tworktreeConfig = true\n\tworktreeConfig = false\n", false)]
    [InlineData("[extensions]\n\tworktreeConfig = no\n", false)]
    public void TheExtension_ReadsAsGitReadsIt(string text, bool enabled)
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "config.worktree"), "[core]\n\thooksPath = .husky\n");
        f.Add(text);
        Assert.Equal(enabled ? ".husky" : ".git/hooks", f.GitSays());
        Assert.Equal(enabled, f.Warning() != null);
    }

    [Fact]
    public void TheExtension_FromAnInclude_DoesntEnableIt()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "config.worktree"), "[core]\n\thooksPath = .husky\n");
        File.WriteAllText(Path.Combine(f.GitDir, "ext"), "[extensions]\n\tworktreeConfig = true\n");
        f.Add("[include]\n\tpath = ext\n");
        Assert.Equal(".git/hooks", f.GitSays());
        Assert.Null(f.Warning());
    }

    [Fact]
    public void ConfigWorktree_FollowsItsIncludes()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "config.worktree"), "[include]\n\tpath = wi\n");
        File.WriteAllText(Path.Combine(f.GitDir, "wi"), "[core]\n\thooksPath = .husky\n");
        f.Add("[extensions]\n\tworktreeConfig = true\n");
        Assert.Equal(".husky", f.GitSays());
        Assert.NotNull(f.Warning());
    }

    // core.worktree moves where a relative path starts

    [Fact]
    public void CoreWorktree_ElsewhereTakesARelativePathOut()
    {
        using var f = new Fixture();
        var other = f.T.Sub("other-tree");
        f.Add($"[core]\n\tworktree = {other}\n\thooksPath = ../../hk\n");
        Assert.Null(f.Warning());
    }

    [Fact]
    public void CoreWorktree_InsideBringsARelativePathIn()
    {
        using var f = new Fixture();
        Assert.Null(Warned("../../hk"));
        var deep = Path.Combine(f.Dir, "a", "b");
        Directory.CreateDirectory(deep);
        f.Add($"[core]\n\tworktree = {deep}\n\thooksPath = ../../hk\n");
        Assert.NotNull(f.Warning());

        static string? Warned(string value)
        {
            using var g = new Fixture();
            g.Set(value);
            return g.Warning();
        }
    }

    [Fact]
    public void CoreWorktree_IsRelativeToTheGitDir()
    {
        using var f = new Fixture();
        f.T.Sub("up");
        f.Add("[core]\n\tworktree = ../../up\n\thooksPath = ../../hk\n");
        Assert.Null(f.Warning());

        using var g = new Fixture();
        Directory.CreateDirectory(Path.Combine(g.Dir, "a", "b"));
        g.Add("[core]\n\tworktree = ../a/b\n\thooksPath = ../../hk\n");
        Assert.NotNull(g.Warning());
    }

    [Fact]
    public void CoreWorktree_FromAnInclude_IsIgnoredAsGitIgnoresIt()
    {
        using var f = new Fixture();
        var other = f.T.Sub("other-tree");
        File.WriteAllText(Path.Combine(f.GitDir, "inc"), $"[core]\n\tworktree = {other}\n");
        f.Add("[include]\n\tpath = inc\n[core]\n\thooksPath = .husky\n");
        Assert.NotNull(f.Warning());
    }

    // The environment

    [Fact]
    public void GitConfigCount_OutranksEveryFile()
    {
        using var f = new Fixture();
        f.Env["GIT_CONFIG_COUNT"] = "1";
        f.Env["GIT_CONFIG_KEY_0"] = "CORE.hooksPATH";
        f.Env["GIT_CONFIG_VALUE_0"] = ".env-hooks";
        f.Set("/dev/null");
        Assert.Contains(".env-hooks", f.Warning());
        f.Env["GIT_CONFIG_VALUE_0"] = "/dev/null";
        f.Add("[core]\n\thooksPath = .husky\n");
        Assert.Null(f.Warning());
    }

    // Linked worktrees

    private static string Linked(Fixture f)
    {
        var wt = Path.Combine(f.T.Path, "linked");
        Git.Run(f.Dir, "worktree", "add", "-q", "-b", "linked", wt);
        return wt;
    }

    [Fact]
    public void Linked_ARelativePath_IsInThatWorktree()
    {
        using var f = new Fixture();
        var wt = Linked(f);
        f.Set(".husky");
        Assert.Contains("worktree", f.Warning(wt));
    }

    [Fact]
    public void Linked_AnAbsolutePathIntoItsOwnTree_Warns()
    {
        using var f = new Fixture();
        var wt = Linked(f);
        f.Set(wt + "/hooks");
        Assert.NotNull(f.Warning(wt));
    }

    [Fact]
    public void Linked_APathIntoTheCommonGitDir_Warns()
    {
        using var f = new Fixture();
        var wt = Linked(f);
        f.Set(f.GitDir + "/my-hooks");
        Assert.NotNull(f.Warning(wt));
    }

    [Fact]
    public void Linked_TheCommonHooks_DontWarn()
    {
        using var f = new Fixture();
        var wt = Linked(f);
        f.Set(f.GitDir + "/hooks");
        Assert.Null(f.Warning(wt));
    }

    [Fact]
    public void Linked_TheMainCheckoutsTree_IsntInTheSandbox()
    {
        using var f = new Fixture();
        var wt = Linked(f);
        f.Set(f.Dir + "/.husky");
        Assert.Null(f.Warning(wt));
        Assert.NotNull(f.Warning());
    }

    [Fact]
    public void Linked_ReadsItsOwnConfigWorktree_NotTheMainCheckouts()
    {
        using var f = new Fixture();
        var wt = Linked(f);
        f.Add("[extensions]\n\tworktreeConfig = true\n");
        File.WriteAllText(Path.Combine(f.GitDir, "config.worktree"), "[core]\n\thooksPath = .main\n");
        Assert.Equal(".git/hooks", Git.Run(wt, "rev-parse", "--git-path", "hooks").TrimEnd('\n').Replace(f.GitDir, ".git", StringComparison.Ordinal));
        Assert.Null(f.Warning(wt));
        Assert.Contains(".main", f.Warning());

        var entry = Path.Combine(f.GitDir, "worktrees", "linked");
        File.WriteAllText(Path.Combine(entry, "config.worktree"), "[core]\n\thooksPath = .own\n");
        Assert.Equal(".own", f.GitSays(wt));
        Assert.Contains(".own", f.Warning(wt));
    }

    [Fact]
    public void Linked_IgnoresTheCommonCoreWorktree_ButNotItsOwn()
    {
        using var f = new Fixture();
        var wt = Linked(f);
        var other = f.T.Sub("other-tree");
        var deep = Path.Combine(wt, "a", "b");
        Directory.CreateDirectory(deep);
        f.Add($"[core]\n\thooksPath = ../../../hk\n\tworktree = {deep}\n");
        Assert.Null(f.Warning(wt)); // the common one would put it in the worktree

        f.Add("[extensions]\n\tworktreeConfig = true\n");
        File.WriteAllText(Path.Combine(f.GitDir, "worktrees", "linked", "config.worktree"), $"[core]\n\tworktree = {deep}\n");
        Assert.NotNull(f.Warning(wt));
    }

    // A plain directory

    [Fact]
    public void APlainDirectory_WarnsOnlyWhereGlobalConfigLeadsIn()
    {
        using var f = new Fixture(repo: false);
        Assert.Null(f.Warning());
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), "[core]\n\thooksPath = .husky\n");
        Assert.Null(f.Warning());
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), $"[core]\n\thooksPath = {f.Dir}/hooks\n");
        Assert.NotNull(f.Warning());
    }

    // Hostile text, and what can't be resolved

    [Fact]
    public void AHostileValue_IsShownEscapedOnOneLine()
    {
        using var f = new Fixture();
        f.Set("\"x\\n\\tpen: all clear\u001b[2K‮\"");
        var w = f.Warning();
        Assert.NotNull(w);
        Assert.DoesNotContain('\n', w);
        Assert.DoesNotContain('\u001b', w);
        Assert.DoesNotContain('‮', w);
        Assert.Contains("x\\n\\tpen: all clear\\x1b[2K\\u202e", w);
    }

    [Fact]
    public void AHugeValue_IsCutShort()
    {
        using var f = new Fixture();
        f.Set(new string('a', 50_000));
        var w = f.Warning();
        Assert.NotNull(w);
        Assert.True(w.Length < 1000, $"{w.Length} characters");
        Assert.EndsWith("Check them before running git here", w);
    }

    [Theory]
    [InlineData("~someone/hooks")]
    [InlineData("%(prefix)/hooks")]
    public void APathPenCantExpand_WarnsThatItCantTell(string value)
    {
        using var f = new Fixture();
        f.Set(value);
        var w = f.Warning();
        Assert.Contains("can't tell", w);
        Assert.Contains(value, w);
    }

    [Fact]
    public void AnUnparsableConfig_WarnsThatItCantTell()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.Home, ".gitconfig"), "[core\n");
        var w = f.Warning();
        Assert.Contains("can't tell", w);
        Assert.Contains(".gitconfig", w);
    }

    [Fact]
    public void AValueThatIsntValidUtf8_WarnsThatItCantTell()
    {
        using var f = new Fixture();
        using (var s = new FileStream(f.Config, FileMode.Append))
            s.Write([.. Encoding.ASCII.GetBytes("[core]\n\thooksPath = /outside/"), 0xff, (byte)'\n']);
        Assert.Contains("can't tell", f.Warning());
    }

    [Fact]
    public void AnUnreadableInclude_WarnsThatItCantTell()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads any file");
        using var f = new Fixture();
        var inc = Path.Combine(f.GitDir, "inc");
        File.WriteAllText(inc, "");
        File.SetUnixFileMode(inc, UnixFileMode.None);
        f.Add("[include]\n\tpath = inc\n");
        Assert.Contains("can't tell", f.Warning());
    }

    [Fact]
    public void AnUnreadableInclude_CanBeOverriddenByALaterValue()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root reads any file");
        using var f = new Fixture();
        var inc = Path.Combine(f.GitDir, "inc");
        File.WriteAllText(inc, "");
        File.SetUnixFileMode(inc, UnixFileMode.None);
        f.Add("[include]\n\tpath = inc\n[core]\n\thooksPath = /dev/null\n");
        Assert.Null(f.Warning());
    }

    [Theory]
    [InlineData("~someone/inc")]
    [InlineData("%(prefix)/inc")]
    public void AnIncludePenCantExpand_WarnsThatItCantTell(string value)
    {
        using var f = new Fixture();
        f.Add($"[include]\n\tpath = {value}\n");
        Assert.Contains("can't tell", f.Warning());
    }

    [Fact]
    public void AnIncludeCycle_Ends_AndWarnsThatItCantTell()
    {
        using var f = new Fixture();
        f.Add("[include]\n\tpath = config\n");
        Assert.Contains("can't tell", f.Warning());
    }

    [Fact]
    public void IncludesThatFanOut_End_AndWarnThatItCantTell()
    {
        using var f = new Fixture();
        // Twenty levels of two includes each: 2^10 paths at git's depth limit.
        for (int i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(f.GitDir, $"f{i}"), $"[include]\n\tpath = f{i + 1}\n\tpath = f{i + 1}\n");
        f.Add("[include]\n\tpath = f0\n");
        Assert.Contains("can't tell", f.Warning());
    }

    [Fact]
    public void ADirectoryThatCantBeEntered_WarnsThatItCantTell()
    {
        Assert.SkipWhen(Native.GetUid() == 0, "root enters any directory");
        using var f = new Fixture();
        var shut = f.T.Sub("shut");
        File.SetUnixFileMode(shut, UnixFileMode.None);
        try
        {
            f.Set(shut + "/link/hooks"); // link could lead anywhere
            Assert.Contains("can't tell", f.Warning());
        }
        finally { File.SetUnixFileMode(shut, (UnixFileMode)0b111_000_000); }
    }

    [Fact]
    public void TheFirstInReach_IsPreferredToOneThatCantBeResolved_AndThereIsOneWarning()
    {
        using var f = new Fixture();
        File.WriteAllText(Path.Combine(f.GitDir, "a"), "[core]\n\thooksPath = ~someone/x\n");
        File.WriteAllText(Path.Combine(f.GitDir, "b"), "[core]\n\thooksPath = .husky\n");
        f.Add("[includeIf \"onbranch:a\"]\n\tpath = a\n[includeIf \"onbranch:b\"]\n\tpath = b\n");
        var warnings = new List<string>();
        var (common, repoDir) = Project.FindRepo(f.Dir);
        HooksPath.WarnIfWritable(new Project(f.Dir, "key", common, repoDir), f.T.Host(f.Home), warnings.Add, name => f.Env.GetValueOrDefault(name));
        var only = Assert.Single(warnings);
        Assert.Contains(".husky", only);
        Assert.DoesNotContain("can't tell", only);
    }
}
