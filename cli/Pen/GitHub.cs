using System.Text.RegularExpressions;

namespace Pen;

/// <summary>Picks the supplied GitHub token for a project, by the owner of its origin.</summary>
internal static partial class GitHub
{
    /// <summary>The origin URL from a .git/config, read directly (never by running git).</summary>
    public static string? OriginUrl(string gitConfigText) =>
        GitConfigFile.Parse(gitConfigText)
            .Where(e => e.Section == "remote" && e.Subsection == "origin" && e.Key == "url")
            .Select(e => e.Value)
            .FirstOrDefault(); // git uses the first url for fetch

    /// <summary>The owner in a github.com URL: https, scp-style (git@github.com:o/r) or ssh://.</summary>
    public static string? OwnerFromUrl(string url)
    {
        var m = OwnerRegex().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(?:(?:https?|ssh|git)://(?:[^@/]+@)?github\.com(?::\d+)?/|(?:[^@/:]+@)?github\.com:/?)([A-Za-z0-9][A-Za-z0-9-]*)/", RegexOptions.IgnoreCase)]
    private static partial Regex OwnerRegex();

    /// <summary>The owner to use: github_owner from .pen/config.toml, else origin's.</summary>
    public static string? ProjectOwner(Project project, Config projectConfig)
    {
        if (projectConfig.GitHubOwner != null) return projectConfig.GitHubOwner;
        if (project.CommonGitDir == null) return null;
        var gitConfig = Path.Combine(project.CommonGitDir, "config");
        if (!File.Exists(gitConfig)) return null;
        var url = OriginUrl(File.ReadAllText(gitConfig));
        return url == null ? null : OwnerFromUrl(url);
    }

    /// <summary>
    /// The token for the project's owner, or null. When the owner has none,
    /// prints a notice once per project, recorded by a marker in its runtime dir;
    /// having a token clears the marker, so removing it later notices again.
    /// </summary>
    public static string? TokenFor(string? owner, Config userConfig, string runtimeDir, HostEnv host)
    {
        if (owner == null) return null;
        var marker = Path.Combine(runtimeDir, "token-notice");
        if (userConfig.GitHubTokens.TryGetValue(owner.ToLowerInvariant(), out var token))
        {
            File.Delete(marker);
            return token;
        }
        if (!File.Exists(marker))
        {
            Out.Line($"no GitHub token for '{owner}': add {owner} = \"github_pat_…\" under [github] in {host.Display(host.UserConfigPath)}");
            File.WriteAllText(marker, "");
        }
        return null;
    }
}
