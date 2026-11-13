using System.Text.RegularExpressions;
using Pen.GitProtection;
using Pen.Platform;
using Pen.Projects;

namespace Pen.Configuration;

/// <summary>Picks the supplied GitHub token for a project, by the owner of its origin.</summary>
internal static partial class GitHub
{
    /// <summary>Finds the origin URL in a .git/config's text, read directly (never by running git).</summary>
    /// <returns>The first url of remote "origin" (git uses it for fetch), or null if there's none.</returns>
    public static string? FindOriginUrl(string gitConfigText) =>
        GitConfigFile.Parse(gitConfigText)
            .Where(e => e.Section == "remote" && e.Subsection == "origin" && e.Key == "url")
            .Select(e => e.Value)
            .FirstOrDefault();

    /// <summary>Parses the owner out of a github.com URL: https, scp-style (git@github.com:o/r) or ssh://.</summary>
    /// <returns>The owner, or null if <paramref name="url"/> isn't a github.com repo URL.</returns>
    public static string? ParseOwner(string url)
    {
        var match = OwnerRegex().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(
        @"^(?:(?:https?|ssh|git)://(?:[^@/]+@)?github\.com(?::\d+)?/"
        + @"|(?:[^@/:]+@)?github\.com:/?)([A-Za-z0-9][A-Za-z0-9-]*)/",
        RegexOptions.IgnoreCase)]
    private static partial Regex OwnerRegex();

    /// <summary>
    /// Finds the owner whose token the project uses: github_owner from .pen/config.toml, else origin's.
    /// </summary>
    /// <returns>The owner, or null if neither names one.</returns>
    public static string? FindProjectOwner(Project project, Config projectConfig) =>
        projectConfig.GitHubOwner ?? ReadOriginOwner(project);

    /// <summary>Reads the owner in origin's URL from the repo's git config.</summary>
    /// <returns>The owner, or null if there's no repo, no GitHub origin or no readable git config.</returns>
    public static string? ReadOriginOwner(Project project)
    {
        if (project.CommonGitDir == null)
            return null;
        var gitConfig = Path.Combine(project.CommonGitDir, "config");
        if (!File.Exists(gitConfig))
            return null;
        var text = Native.ReadRegularFile(gitConfig);
        var url = text == null ? null : FindOriginUrl(text);
        return url == null ? null : ParseOwner(url);
    }

    /// <summary>Selects the user's token for <paramref name="owner"/>, noticing once when there's none.</summary>
    /// <returns>The token, or null if there's no owner or the user has no token for it.</returns>
    /// <remarks>
    /// The notice is printed once per project, recorded by a marker in its runtime dir; having a token clears the
    /// marker, so removing the token later notices again.
    /// </remarks>
    public static string? SelectToken(string? owner, Config userConfig, string runtimeDir, HostEnv host)
    {
        if (owner == null)
            return null;
        var marker = Path.Combine(runtimeDir, "token-notice");
        if (userConfig.GitHubTokens.TryGetValue(owner.ToLowerInvariant(), out var token))
        {
            File.Delete(marker);
            return token;
        }
        if (!File.Exists(marker))
        {
            Out.WriteLine($"no GitHub token for '{Out.Field(owner)}': add {Out.Field(owner)} = " +
                $"\"github_pat_…\" under [github] in {host.Display(host.UserConfigPath)}");
            File.WriteAllText(marker, "");
        }
        return null;
    }
}
