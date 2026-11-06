using System.Text.RegularExpressions;

namespace Pen;

internal enum ConfigKind { User, Project }

/// <summary>
/// ~/.config/pen/config and a project's .pen/config: one "key = value" per
/// line, split at the first '=', whitespace trimmed, '#' comments on their own
/// lines. An unknown key is an error naming the file and line.
/// </summary>
internal sealed partial class Config
{
    /// <summary>github.&lt;owner&gt; tokens, keyed by lowercased owner (user only).</summary>
    public Dictionary<string, string> GitHubTokens { get; } = new(StringComparer.Ordinal);
    /// <summary>env.&lt;NAME&gt; values for the agent service, in file order (user only).</summary>
    public List<KeyValuePair<string, string>> Env { get; } = [];
    /// <summary>Extra allowed domains, normalized to lowercase (both).</summary>
    public List<string> Allow { get; } = [];
    /// <summary>Which owner's token to use (project only).</summary>
    public string? GitHubOwner { get; private set; }

    public static Config Empty { get; } = new();

    // Names pen sets itself, or that would steer the sandbox around its proxy.
    private static readonly HashSet<string> ReservedEnv = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROJECT_DIR", "NUGET_PACKAGES", "GH_TOKEN", "GITHUB_TOKEN", "HOME", "USER", "LOGNAME",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY", "DOCKER_HOST",
    };

    public static Config Load(string path, string displayPath, ConfigKind kind) =>
        File.Exists(path) ? Parse(File.ReadAllText(path), displayPath, kind) : new Config();

    public static Config Parse(string text, string displayPath, ConfigKind kind)
    {
        var config = new Config();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            string Where() => $"{displayPath}:{i + 1}";
            int eq = line.IndexOf('=');
            if (eq < 0) throw new PenException($"{Where()}: expected \"key = value\"");
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();
            if (key.Length == 0) throw new PenException($"{Where()}: missing key before '='");
            if (value.Length == 0) throw new PenException($"{Where()}: \"{key}\" has no value");

            void UserOnly()
            {
                if (kind == ConfigKind.Project)
                    throw new PenException($"{Where()}: \"{key}\" belongs in ~/.config/pen/config, not a project's .pen/config (which is usually committed)");
            }

            if (key.StartsWith("github.", StringComparison.Ordinal))
            {
                UserOnly();
                var owner = key["github.".Length..];
                if (!OwnerRegex().IsMatch(owner)) throw new PenException($"{Where()}: \"{owner}\" isn't a GitHub owner name");
                if (!config.GitHubTokens.TryAdd(owner.ToLowerInvariant(), value))
                    throw new PenException($"{Where()}: a token for \"{owner}\" is already set above");
            }
            else if (key.StartsWith("env.", StringComparison.Ordinal))
            {
                UserOnly();
                var name = key["env.".Length..];
                if (!EnvNameRegex().IsMatch(name)) throw new PenException($"{Where()}: \"{name}\" isn't a valid environment variable name");
                if (ReservedEnv.Contains(name) || name.StartsWith("PEN_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase))
                    throw new PenException($"{Where()}: {name} is set by pen and can't be overridden");
                if (config.Env.Any(e => e.Key == name)) throw new PenException($"{Where()}: {name} is already set above");
                config.Env.Add(new(name, value));
            }
            else if (key == "allow")
            {
                var domain = value.ToLowerInvariant();
                if (!Allowlist.IsValidDomain(domain))
                    throw new PenException($"{Where()}: \"{value}\" isn't a domain (use example.com, or .example.com to include its subdomains)");
                config.Allow.Add(domain);
            }
            else if (key == "github_owner")
            {
                if (kind == ConfigKind.User)
                    throw new PenException($"{Where()}: \"github_owner\" belongs in a project's .pen/config");
                if (!OwnerRegex().IsMatch(value)) throw new PenException($"{Where()}: \"{value}\" isn't a GitHub owner name");
                if (config.GitHubOwner != null) throw new PenException($"{Where()}: github_owner is already set above");
                config.GitHubOwner = value;
            }
            else
            {
                throw new PenException($"{Where()}: unknown key \"{key}\"");
            }
        }
        return config;
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    internal static partial Regex OwnerRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvNameRegex();

    /// <summary>
    /// First run: creates ~/.config/pen/ with its config file (600) from the
    /// template, and the home seed (see <see cref="Seed"/>), each only when
    /// missing, and says so in one line. Never rewrites an existing file.
    /// <paramref name="identity"/> gives the host's git user.name/user.email
    /// for the seed's .gitconfig (default: the host's global git config).
    /// </summary>
    public static void EnsureUserConfig(HostEnv host, Func<(string? Name, string? Email)>? identity = null)
    {
        bool createdConfig = false;
        if (!Directory.Exists(host.ConfigDir))
        {
            Directory.CreateDirectory(host.ConfigDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using (var f = new FileStream(host.UserConfigPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            }))
            {
                Embedded.Open("templates/config").CopyTo(f);
            }
            createdConfig = true;
        }

        bool createdSeed = false;
        if (!Path.Exists(host.SeedDir) && !RepoGuard.IsLink(host.SeedDir))
        {
            var (name, email) = (identity ?? HostIdentity)();
            createdSeed = Seed.EnsureExists(host, name, email);
        }

        if (createdConfig)
            Out.Line($"created {host.Display(host.ConfigDir)}/: add GitHub tokens to {host.Display(host.UserConfigPath)}");
        else if (createdSeed)
            Out.Line($"created {host.Display(host.SeedDir)}/, the home seed: sandbox-wide agent config, copied into every project's sandbox home");
    }

    private static (string?, string?) HostIdentity() =>
        (HostGit.GlobalConfig("user.name"), HostGit.GlobalConfig("user.email"));
}
