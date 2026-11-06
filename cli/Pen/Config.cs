using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Serialization;

namespace Pen;

internal enum ConfigKind { User, Project }

/// <summary>
/// The settings in a config directory (~/.config/pen/ or a project's .pen/):
/// config.toml (TOML) and allowlist.txt (one domain per line, kept separate so
/// `pen allow` can append to it without rewriting a hand-edited TOML file).
/// Either file may be missing. TOML errors name the file and line; pen's own
/// checks name the file and key.
/// </summary>
internal sealed partial class Config
{
    public const string FileName = "config.toml";
    public const string AllowlistName = "allowlist.txt";
    /// <summary>The pre-TOML config file. Its presence is an error, so it's never silently ignored.</summary>
    public const string LegacyName = "config";

    /// <summary>[github] tokens, keyed by lowercased owner (user only).</summary>
    public Dictionary<string, string> GitHubTokens { get; } = new(StringComparer.Ordinal);
    /// <summary>[env] values for the agent service, in file order (user only).</summary>
    public List<KeyValuePair<string, string>> Env { get; } = [];
    /// <summary>Extra allowed domains from allowlist.txt, normalized to lowercase (both).</summary>
    public List<string> Allow { get; } = [];
    /// <summary>Which owner's token to use (project only).</summary>
    public string? GitHubOwner { get; private set; }
    /// <summary>docker = true|false: Docker-in-Docker for every session (project only); null when unset.</summary>
    public bool? Docker { get; private set; }

    public static Config Empty { get; } = new();

    // Names pen sets itself, or that would steer the sandbox around its proxy.
    private static readonly HashSet<string> ReservedEnv = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROJECT_DIR", "NUGET_PACKAGES", "GH_TOKEN", "GITHUB_TOKEN", "HOME", "USER", "LOGNAME",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY", "DOCKER_HOST",
    };

    /// <summary>
    /// Loads <paramref name="dir"/>'s config.toml and allowlist.txt.
    /// <paramref name="displayDir"/> is how messages name the directory (e.g. "~/.config/pen").
    /// </summary>
    public static Config Load(string dir, string displayDir, ConfigKind kind)
    {
        if (File.Exists(Path.Combine(dir, LegacyName)))
            throw new PenException(
                $"{displayDir}/{LegacyName} is pen's old config format, which is no longer read: move its settings to " +
                $"{displayDir}/{FileName} (domains to {displayDir}/{AllowlistName}, one per line), then delete it");

        var path = Path.Combine(dir, FileName);
        var config = File.Exists(path) ? Parse(File.ReadAllText(path), $"{displayDir}/{FileName}", kind) : new Config();
        var allowlist = Path.Combine(dir, AllowlistName);
        if (File.Exists(allowlist))
            config.Allow.AddRange(ParseAllowlist(File.ReadAllText(allowlist), $"{displayDir}/{AllowlistName}"));
        return config;
    }

    /// <summary>Parses config.toml text. <paramref name="displayPath"/> names the file in errors.</summary>
    public static Config Parse(string text, string displayPath, ConfigKind kind)
    {
        // Validating the syntax tree first enforces the whole TOML spec (e.g.
        // a key or table defined twice), with line numbers; the model built
        // afterwards would quietly let the last definition win.
        var doc = SyntaxParser.Parse(text, displayPath, validate: true);
        foreach (var d in doc.Diagnostics)
        {
            if (d.Kind == Tomlyn.Syntax.DiagnosticMessageKind.Error)
                throw new PenException($"{displayPath}:{d.Span.Start.Line + 1}: {d.Message}");
        }
        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? new TomlTable();
        }
        catch (TomlException e)
        {
            throw new PenException($"{displayPath}: {e.Message}");
        }

        var config = new Config();
        var userFile = "~/.config/pen/" + FileName;
        var projectFile = ".pen/" + FileName;
        foreach (var (key, value) in table)
        {
            PenException Error(string message) => new($"{displayPath}: {message}");
            PenException BelongsIn(string file, string? why = null) =>
                Error($"\"{key}\" belongs in {file}{why}");

            switch (key)
            {
                case "github" when kind == ConfigKind.Project:
                case "env" when kind == ConfigKind.Project:
                    throw BelongsIn(userFile, $", not {projectFile} (which is usually committed)");
                case "github_owner" when kind == ConfigKind.User:
                    throw BelongsIn(projectFile);
                case "docker" when kind == ConfigKind.User:
                    throw BelongsIn(projectFile, " (or use pen --docker)");
                case "allow":
                    throw Error($"domains go in {AllowlistName} next to it, one per line (or use pen allow)");

                case "github":
                    foreach (var (owner, token) in Table(value, key, Error))
                    {
                        if (!OwnerRegex().IsMatch(owner)) throw Error($"github.{owner}: \"{owner}\" isn't a GitHub owner name");
                        if (!config.GitHubTokens.TryAdd(owner.ToLowerInvariant(), String(token, $"github.{owner}", Error)))
                            throw Error($"github.{owner}: a token for \"{owner}\" is already set (owner names ignore case)");
                    }
                    break;

                case "env":
                    foreach (var (name, v) in Table(value, key, Error))
                    {
                        if (!EnvNameRegex().IsMatch(name)) throw Error($"env.{name}: \"{name}\" isn't a valid environment variable name");
                        if (ReservedEnv.Contains(name) || name.StartsWith("PEN_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase))
                            throw Error($"env.{name}: {name} is set by pen and can't be overridden");
                        config.Env.Add(new(name, String(v, $"env.{name}", Error)));
                    }
                    break;

                case "github_owner":
                    var o = String(value, key, Error);
                    if (!OwnerRegex().IsMatch(o)) throw Error($"github_owner: \"{o}\" isn't a GitHub owner name");
                    config.GitHubOwner = o;
                    break;

                case "docker":
                    config.Docker = value as bool? ?? throw Error("docker must be true or false (unquoted)");
                    break;

                default:
                    throw Error($"unknown key \"{key}\"");
            }
        }
        return config;
    }

    private static TomlTable Table(object value, string key, Func<string, PenException> error) =>
        value as TomlTable ?? throw error($"{key} must be a table ([{key}])");

    private static string String(object value, string key, Func<string, PenException> error) =>
        value is string s && s.Length > 0 ? s : throw error($"{key} must be a non-empty string (in quotes)");

    /// <summary>
    /// allowlist.txt: one domain per line (.example.com also allows its
    /// subdomains), '#' comments, blank lines ignored. A bad line names the file and line.
    /// </summary>
    public static List<string> ParseAllowlist(string text, string displayPath)
    {
        var domains = new List<string>();
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            int hash = line.IndexOf('#');
            var entry = (hash >= 0 ? line[..hash] : line).Trim();
            if (entry.Length == 0) continue;
            var domain = entry.ToLowerInvariant();
            if (!Allowlist.IsValidDomain(domain))
                throw new PenException($"{displayPath}:{i + 1}: \"{entry}\" isn't a domain (use example.com, or .example.com to include its subdomains)");
            domains.Add(domain);
        }
        return domains;
    }

    [GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
    internal static partial Regex OwnerRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvNameRegex();

    /// <summary>
    /// Creates ~/.config/pen/ and, each only when missing, its config.toml
    /// (600) and allowlist.txt from the templates and the home seed (see
    /// <see cref="Seed"/>), and says so in one line. Never rewrites an existing
    /// file, and adds no config.toml next to a legacy config (Load reports that).
    /// <paramref name="identity"/> gives the host's git user.name/user.email
    /// for the seed's .gitconfig (default: the host's global git config).
    /// </summary>
    public static void EnsureUserConfig(HostEnv host, Func<(string? Name, string? Email)>? identity = null)
    {
        bool createdDir = false;
        if (!Directory.Exists(host.ConfigDir))
        {
            Directory.CreateDirectory(host.ConfigDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            createdDir = true;
        }

        bool createdConfig = false;
        if (!File.Exists(host.UserConfigPath) && !File.Exists(Path.Combine(host.ConfigDir, LegacyName)))
        {
            CreateFromTemplate(host.UserConfigPath, "templates/" + FileName, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            createdConfig = true;
        }
        if (!File.Exists(host.UserAllowlistPath))
            CreateFromTemplate(host.UserAllowlistPath, "templates/" + AllowlistName,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        bool createdSeed = false;
        if (!Path.Exists(host.SeedDir) && !RepoGuard.IsLink(host.SeedDir))
        {
            var (name, email) = (identity ?? HostIdentity)();
            createdSeed = Seed.EnsureExists(host, name, email);
        }

        if (createdDir)
            Out.Line($"created {host.Display(host.ConfigDir)}/: add GitHub tokens to {host.Display(host.UserConfigPath)}");
        else if (createdConfig)
            Out.Line($"created {host.Display(host.UserConfigPath)}: add GitHub tokens there");
        else if (createdSeed)
            Out.Line($"created {host.Display(host.SeedDir)}/, the home seed: sandbox-wide agent config, copied into every project's sandbox home");
    }

    private static void CreateFromTemplate(string path, string template, UnixFileMode mode)
    {
        try
        {
            using var f = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = mode,
            });
            Embedded.Open(template).CopyTo(f);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another pen created it first.
        }
    }

    private static (string?, string?) HostIdentity() =>
        (HostGit.GlobalConfig("user.name"), HostGit.GlobalConfig("user.email"));
}

/// <summary>Source-generated TOML metadata: the untyped model only, so no reflection (Native AOT).</summary>
[TomlSerializable(typeof(TomlTable))]
internal sealed partial class TomlContext : TomlSerializerContext;
