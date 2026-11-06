using System.Text.RegularExpressions;

namespace Pen;

/// <summary>Picks the read-only GitHub token for a project, by the owner of its origin.</summary>
internal static partial class GitHub
{
    /// <summary>
    /// The origin URL from a .git/config, read directly (never by running git).
    /// Handles the subset of git's config syntax that remote sections use.
    /// </summary>
    public static string? OriginUrl(string gitConfigText)
    {
        bool inOrigin = false;
        string? url = null;
        foreach (var raw in gitConfigText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                var m = SectionRegex().Match(line);
                inOrigin = m.Success && m.Groups[1].Value.Equals("remote", StringComparison.OrdinalIgnoreCase) && m.Groups[2].Value == "origin";
                continue;
            }
            if (!inOrigin) continue;
            int eq = line.IndexOf('=');
            if (eq < 0 || !line[..eq].Trim().Equals("url", StringComparison.OrdinalIgnoreCase)) continue;
            url ??= Unquote(line[(eq + 1)..].Trim()); // git uses the first url for fetch
        }
        return url;
    }

    [GeneratedRegex("""^\[\s*([A-Za-z0-9.-]+)\s+"((?:[^"\\]|\\.)*)"\s*\]""")]
    private static partial Regex SectionRegex();

    private static string Unquote(string value)
    {
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (c == '\\' && i + 1 < value.Length) { sb.Append(value[++i]); continue; }
            if (!quoted && (c == '#' || c == ';')) break;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>The owner in a github.com URL: https, scp-style (git@github.com:o/r) or ssh://.</summary>
    public static string? OwnerFromUrl(string url)
    {
        var m = OwnerRegex().Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^(?:(?:https?|ssh|git)://(?:[^@/]+@)?github\.com(?::\d+)?/|(?:[^@/:]+@)?github\.com:/?)([A-Za-z0-9][A-Za-z0-9-]*)/", RegexOptions.IgnoreCase)]
    private static partial Regex OwnerRegex();

    /// <summary>The owner to use: github_owner from .pen/config, else origin's.</summary>
    public static string? ProjectOwner(Project project, Config projectConfig)
    {
        if (projectConfig.GitHubOwner != null) return projectConfig.GitHubOwner;
        var gitConfig = Path.Combine(project.GitDir, "config");
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
            Out.Line($"no GitHub token for '{owner}': add \"github.{owner} = github_pat_…\" to {host.Display(host.UserConfigPath)}");
            File.WriteAllText(marker, "");
        }
        return null;
    }
}
