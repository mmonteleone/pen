using System.Text.RegularExpressions;
using Pen.GitProtection;
using Pen.Masking;
using Pen.Platform;
using Pen.Proxy;
using Pen.Sessions;
using Pen.Tools;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;

namespace Pen.Configuration;

/// <summary>
/// The settings in one config.toml: ~/.config/pen/'s or a project's .pen/'s, which may be missing.
/// </summary>
/// <remarks>
/// TOML errors name the file and line; Pen's own checks name the file and key. Keys are either
/// user-only or project-only, except allow, deny, tools and hide, whose lists are appended (allow: defaults,
/// user, project; deny: user, project; tools: user, project, --tool; hide: user, project).
/// </remarks>
internal sealed partial class Config
{
    public const string FileName = "config.toml";
    /// <summary>The pre-TOML config file. Its presence is an error, so it's never silently ignored.</summary>
    public const string LegacyName = "config";

    private const string UserFile = "~/.config/pen/" + FileName;
    private const string ProjectFile = ".pen/" + FileName;

    /// <summary>[github] tokens, keyed by lowercased owner (user only).</summary>
    public Dictionary<string, string> GitHubTokens { get; } = new(StringComparer.Ordinal);
    /// <summary>[env] values for the agent service, in file order (user only).</summary>
    public List<KeyValuePair<string, string>> Env { get; } = [];
    /// <summary>Extra allowed domains, or "*" for any domain name, in lowercase (both).</summary>
    public List<string> Allow { get; } = [];
    /// <summary>Denied domains, normalized to lowercase (both). A deny beats every allow.</summary>
    public List<string> Deny { get; } = [];
    /// <summary>Which owner's token to use (project only).</summary>
    public string? GitHubOwner { get; private set; }
    /// <summary>docker = true|false: Docker-in-Docker for every session (project only); null when unset.</summary>
    public bool? Docker { get; private set; }
    /// <summary>
    /// ports = [3000, …]: ports to forward from the host's 127.0.0.1, in file order without duplicates (project only).
    /// </summary>
    public List<int> Ports { get; } = [];
    /// <summary>tools = ["name", "name:version", …], validated against the recipes and canonical (both).</summary>
    public IReadOnlyList<ToolSpec> Tools { get; private set; } = [];
    /// <summary>hide = [patterns]: what the sandbox sees as empty, in file order (both).</summary>
    public List<HidePattern> Hide { get; } = [];
    /// <summary>
    /// [replace] target = source: project-relative paths, in file order (project only). The sandbox sees a copy
    /// of each source over its target.
    /// </summary>
    public List<KeyValuePair<string, string>> Replace { get; } = [];

    public static Config Empty { get; } = new();

    // Names Pen sets itself, or that would steer the sandbox around its proxy.
    private static readonly HashSet<string> ReservedEnv = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROJECT_DIR", "NUGET_PACKAGES", "GH_TOKEN", "GITHUB_TOKEN", "HOME", "USER", "LOGNAME",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY", "DOCKER_HOST",
    };

    /// <summary>Loads <paramref name="dir"/>'s config.toml.</summary>
    /// <param name="dir">The config directory.</param>
    /// <param name="displayDir">How messages name the directory (e.g. "~/.config/pen").</param>
    /// <param name="kind">Which keys the file may hold.</param>
    /// <returns>The settings, or empty ones if there's no config.toml.</returns>
    public static Config Load(string dir, string displayDir, ConfigKind kind)
    {
        if (File.Exists(Path.Combine(dir, LegacyName)))
        {
            throw new PenException(
                $"{displayDir}/{LegacyName} is pen's old config format, " +
                    "which is no longer read: move its settings to " +
                $"{displayDir}/{FileName} (see the README), then delete it");
        }

        var path = Path.Combine(dir, FileName);
        var display = $"{displayDir}/{FileName}";
        return ReadText(path, display) is { } text ? Parse(text, display, kind) : new Config();
    }

    /// <summary>Reads a config.toml's text.</summary>
    /// <returns>The text, or null if there's no file.</returns>
    /// <remarks>
    /// An in-tree worktree's .pen/ is writable from a session, so the file may be a FIFO or huge:
    /// anything but a regular file of a reasonable size is refused.
    /// </remarks>
    internal static string? ReadText(string path, string displayPath)
    {
        try
        {
            return Native.ReadRegularFile(path) ?? throw new PenException(
                $"{displayPath} isn't a regular file of a reasonable size: remove it, then rerun");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Parses TOML text into its untyped table.</summary>
    /// <param name="text">The TOML text.</param>
    /// <param name="displayPath">Names the file in errors.</param>
    internal static TomlTable ParseToml(string text, string displayPath)
    {
        // Validating the syntax tree first enforces the whole TOML spec (e.g.
        // a key or table defined twice), with line numbers; the model built
        // afterwards would quietly let the last definition win.
        var document = SyntaxParser.Parse(text, displayPath, validate: true);
        foreach (var diagnostic in document.Diagnostics)
        {
            if (diagnostic.Kind == Tomlyn.Syntax.DiagnosticMessageKind.Error)
            {
                throw new PenException(
                    $"{displayPath}:{diagnostic.Span.Start.Line + 1}: {Out.Field(diagnostic.Message)}");
            }
        }
        try
        {
            return TomlSerializer.Deserialize(text, TomlContext.Default.TomlTable) ?? new TomlTable();
        }
        catch (TomlException exception)
        {
            throw new PenException($"{displayPath}: {Out.Field(exception.Message)}");
        }
    }

    /// <summary>Parses config.toml text.</summary>
    /// <param name="text">The TOML text.</param>
    /// <param name="displayPath">Names the file in errors.</param>
    /// <param name="kind">Which keys the file may hold.</param>
    /// <remarks>Keys are checked in file order, each fully before the next, so the first problem is reported.</remarks>
    public static Config Parse(string text, string displayPath, ConfigKind kind)
    {
        var table = ParseToml(text, displayPath);
        var config = new Config();
        PenException Error(string message) => new($"{displayPath}: {Out.Field(message)}");
        foreach (var (key, value) in table)
        {
            ThrowIfMisplaced(key, kind, Error);
            config.ReadKey(key, value, Error);
        }
        return config;
    }

    /// <summary>Refuses a key that belongs in the other kind of config file.</summary>
    private static void ThrowIfMisplaced(string key, ConfigKind kind, Func<string, PenException> error)
    {
        var belongsIn = (key, kind) switch
        {
            ("github" or "env", ConfigKind.Project) => $"{UserFile}, not {ProjectFile} (which is usually committed)",
            ("github_owner", ConfigKind.User) => ProjectFile,
            ("docker", ConfigKind.User) => $"{ProjectFile} (or use pen --docker)",
            ("ports", ConfigKind.User) => $"{ProjectFile} (or use pen --port)",
            ("replace", ConfigKind.User) => ProjectFile,
            _ => null,
        };
        if (belongsIn != null)
            throw error($"\"{key}\" belongs in {belongsIn}");
    }

    /// <summary>Validates one top-level key's value and stores it.</summary>
    private void ReadKey(string key, object value, Func<string, PenException> error)
    {
        switch (key)
        {
            case "allow":
                ReadDomains("allow", value, Allow, allowStar: true, error);
                break;
            case "deny":
                ReadDomains("deny", value, Deny, allowStar: false, error);
                break;
            case "github":
                ReadGitHubTokens(value, error);
                break;
            case "env":
                ReadEnv(value, error);
                break;
            case "github_owner":
                ReadGitHubOwner(value, error);
                break;
            case "docker":
                Docker = value as bool? ?? throw error("docker must be true or false (unquoted)");
                break;
            case "ports":
                ReadPorts(value, error);
                break;
            case "tools":
                ReadTools(value, error);
                break;
            case "hide":
                ReadHide(value, error);
                break;
            case "replace":
                ReadReplace(value, error);
                break;
            default:
                throw error($"unknown key \"{key}\"");
        }
    }

    /// <summary>Validates a list of domains, allow's or deny's, and adds them to <paramref name="list"/>.</summary>
    /// <param name="key">The key, which errors name.</param>
    /// <param name="value">The key's value.</param>
    /// <param name="list">Gets the domains, lowercased.</param>
    /// <param name="allowStar">Whether "*", any domain name, is valid as a whole entry (allow's, not deny's).</param>
    /// <param name="error">Makes the exception for a problem.</param>
    private static void ReadDomains(string key, object value, List<string> list, bool allowStar,
        Func<string, PenException> error)
    {
        if (value is not TomlArray domains)
            throw error($"{key} must be a list of domains, e.g. {key} = [\"example.com\"]");
        foreach (var item in domains)
        {
            var domain = item is string text ? text.ToLowerInvariant() : null;
            if (!allowStar && domain == Allowlist.AnyDomain)
                throw error($"{key} = [\"*\"] would block everything; remove the allow entries instead");
            if (domain == null
                || !(allowStar ? Allowlist.IsValidAllowEntry(domain) : Allowlist.IsValidDomain(domain)))
            {
                throw error($"{key}: {DescribeItem(item)} " +
                    "isn't a domain (use example.com, or .example.com to include its subdomains)");
            }

            list.Add(domain);
        }
    }

    private void ReadGitHubTokens(object value, Func<string, PenException> error)
    {
        foreach (var (owner, token) in RequireTable(value, "github", error))
        {
            if (!OwnerRegex().IsMatch(owner))
                throw error($"github.{owner}: \"{owner}\" isn't a GitHub owner name");
            var tokenText = RequireString(token, $"github.{owner}", error);
            if (!GitHubTokens.TryAdd(owner.ToLowerInvariant(), tokenText))
            {
                throw error($"github.{owner}: a token for \"{owner}\" " +
                    "is already set (owner names ignore case)");
            }
        }
    }

    private void ReadEnv(object value, Func<string, PenException> error)
    {
        foreach (var (name, setting) in RequireTable(value, "env", error))
        {
            if (!EnvNameRegex().IsMatch(name))
                throw error($"env.{name}: \"{name}\" isn't a valid environment variable name");
            if (IsReservedEnv(name))
                throw error($"env.{name}: {name} is set by pen and can't be overridden");
            Env.Add(new(name, RequireString(setting, $"env.{name}", error)));
        }
    }

    private void ReadGitHubOwner(object value, Func<string, PenException> error)
    {
        var owner = RequireString(value, "github_owner", error);
        if (!OwnerRegex().IsMatch(owner))
            throw error($"github_owner: \"{owner}\" isn't a GitHub owner name");
        GitHubOwner = owner;
    }

    private void ReadPorts(object value, Func<string, PenException> error)
    {
        if (value is not TomlArray ports)
            throw error("ports must be a list of port numbers, e.g. ports = [3000, 5173]");
        foreach (var item in ports)
        {
            if (item is not long number || !Port.IsValid(number))
            {
                throw error($"ports: {DescribeItem(item)} " +
                    "isn't a port number (1 to 65535, unquoted)");
            }

            if (!Ports.Contains((int)number))
                Ports.Add((int)number);
        }
    }

    private void ReadTools(object value, Func<string, PenException> error)
    {
        if (value is not TomlArray toolSpecs)
            throw error("tools must be a list of tools, e.g. tools = [\"name\", \"name:version\"]");
        var parsed = new List<ToolSpec>();
        foreach (var item in toolSpecs)
        {
            if (item is not string spec)
            {
                throw error($"tools: {item?.ToString() ?? "null"} isn't " +
                    "a tool (use name or name:version, in quotes)");
            }

            if (ToolSpec.TryParse(spec, RecipeCatalog.Embedded, out var tool) is { } invalid)
                throw error($"tools: {invalid}");
            parsed.Add(tool);
        }
        if (ToolSpec.TryCanonicalize(parsed, RecipeCatalog.Embedded, out var canonical) is { } conflict)
            throw error($"tools: {conflict}");
        Tools = canonical;
    }

    private void ReadHide(object value, Func<string, PenException> error)
    {
        if (value is not TomlArray patterns)
            throw error("hide must be a list of patterns, e.g. hide = [\".env\", \"secrets/\"]");
        foreach (var item in patterns)
        {
            if (item is not string text)
                throw error($"hide: {DescribeItem(item)} isn't a pattern (put it in quotes)");
            if (HidePattern.TryParse(text, out var pattern) is { } problem)
                throw error($"hide: \"{text}\": {problem}");
            Hide.Add(pattern!);
        }
    }

    private void ReadReplace(object value, Func<string, PenException> error)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, item) in RequireTable(value, "replace", error))
        {
            if (item is not string source)
            {
                throw error($"replace: \"{target}\" must be set to a path in quotes, and a target path " +
                    "goes in quotes as one key, e.g. \".env\" = \".pen/agent.env\"");
            }

            if (FindReplacePathProblem(target, isTarget: true) is { } targetProblem)
                throw error($"replace: target \"{target}\" {targetProblem}");
            if (FindReplacePathProblem(source, isTarget: false) is { } sourceProblem)
                throw error($"replace: source \"{source}\" (for \"{target}\") {sourceProblem}");
            if (!targets.TryAdd(target, target))
            {
                throw error($"replace: \"{targets[target]}\" and \"{target}\" " +
                    "are the same file where the filesystem ignores case");
            }

            Replace.Add(new(target, source));
        }
    }

    /// <summary>Checks a [replace] target or source: a relative path to a file, with no wildcards.</summary>
    /// <returns>What's wrong, as the end of a sentence about the path, or null.</returns>
    /// <remarks>
    /// Neither may reach into .git (any case). A target can't be in .pen, Pen's own directory; a source may,
    /// since .pen is read-only in the sandbox.
    /// </remarks>
    private static string? FindReplacePathProblem(string path, bool isTarget)
    {
        if (path.Length == 0)
            return "is empty";
        if (path.Length > HidePattern.MaxLength)
            return $"can't be longer than {HidePattern.MaxLength} characters";
        if (path.StartsWith('/'))
            return "must be relative to the project, not absolute";
        if (path.EndsWith('/'))
            return "must name a file, so it can't end in /";
        if (path.Contains('\0'))
            return "contains a NUL character";
        if (path.AsSpan().IndexOfAny("*?[]\\") >= 0)
            return "can't have *, ?, [, ] or \\ in it";
        var segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0))
            return "can't have an empty segment (//)";
        if (segments.Any(segment => segment is "." or ".."))
            return "can't have . or .. segments";
        if (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            return "can't be in .git";
        if (isTarget && segments[0].Equals(".pen", StringComparison.OrdinalIgnoreCase))
            return "can't be in .pen, Pen's own directory";
        return null;
    }

    /// <summary>Describes a list item for an error: a string in quotes, anything else as TOML shows it.</summary>
    private static string DescribeItem(object? item)
    {
        if (item is string)
            return $"\"{item}\"";
        return item?.ToString() ?? "null";
    }

    /// <summary>Tells whether an environment variable is Pen's own, so no config may set it.</summary>
    internal static bool IsReservedEnv(string name) =>
        ReservedEnv.Contains(name)
            || name.StartsWith("PEN_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns <paramref name="value"/> as a table, or throws <paramref name="error"/>'s exception.</summary>
    internal static TomlTable RequireTable(object value, string key, Func<string, PenException> error) =>
        value as TomlTable ?? throw error($"{key} must be a table ([{key}])");

    /// <summary>
    /// Returns <paramref name="value"/> as a non-empty string, or throws <paramref name="error"/>'s exception.
    /// </summary>
    internal static string RequireString(object value, string key, Func<string, PenException> error) =>
        value is string text && text.Length > 0
            ? text
            : throw error($"{key} must be a non-empty string (in quotes)");

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})\z")]
    internal static partial Regex OwnerRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    internal static partial Regex EnvNameRegex();

    /// <summary>
    /// Creates ~/.config/pen/ and, each only when missing, its config.toml (600) from the template
    /// and the home seed (see <see cref="Seed"/>), and says so in one line.
    /// </summary>
    /// <param name="host">The host user's environment.</param>
    /// <param name="identity">
    /// Gives the git user.name and user.email for the seed's .gitconfig; called only when the seed is created.
    /// Pen passes <see cref="GitIdentity.ReadFromHost"/>.
    /// </param>
    /// <remarks>
    /// Never rewrites an existing file, and adds no config.toml next to a legacy config (Load reports that).
    /// </remarks>
    public static void EnsureUserConfig(HostEnv host, Func<GitIdentity> identity)
    {
        var createdDir = EnsureConfigDir(host);
        var createdConfig = EnsureUserConfigFile(host);
        var createdSeed = EnsureSeed(host, identity);
        ReportCreated(host, createdDir, createdConfig, createdSeed);
    }

    private static bool EnsureConfigDir(HostEnv host)
    {
        if (Directory.Exists(host.ConfigDir))
            return false;
        Directory.CreateDirectory(host.ConfigDir,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return true;
    }

    private static bool EnsureUserConfigFile(HostEnv host)
    {
        if (File.Exists(host.UserConfigPath) || File.Exists(Path.Combine(host.ConfigDir, LegacyName)))
            return false;
        CreateFromTemplate(host.UserConfigPath, "templates/" + FileName,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return true;
    }

    private static bool EnsureSeed(HostEnv host, Func<GitIdentity> identity)
    {
        if (Path.Exists(host.SeedDir) || RepoGuard.IsLink(host.SeedDir))
            return false;
        return Seed.EnsureExists(host, identity());
    }

    /// <summary>Says what was created: only the outermost of the dir, config.toml and seed.</summary>
    private static void ReportCreated(HostEnv host, bool createdDir, bool createdConfig, bool createdSeed)
    {
        if (createdDir)
        {
            Out.WriteLine($"created {host.Display(host.ConfigDir)}/: add " +
                $"GitHub tokens to {host.Display(host.UserConfigPath)}");
        }
        else if (createdConfig)
        {
            Out.WriteLine($"created {host.Display(host.UserConfigPath)}: add GitHub tokens there");
        }
        else if (createdSeed)
        {
            Out.WriteLine($"created {host.Display(host.SeedDir)}/, the home seed: " +
                "sandbox-wide agent config, copied into every project's sandbox home");
        }
    }

    private static void CreateFromTemplate(string path, string template, UnixFileMode mode)
    {
        try
        {
            using var file = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = mode,
            });
            Embedded.Open(template).CopyTo(file);
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another Pen created it first.
        }
    }
}
