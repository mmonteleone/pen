using Pen.Masking;
using Pen.Platform;
using Pen.Proxy;
using Pen.Sessions;
using Pen.Tools;
using Tomlyn.Model;

namespace Pen.Configuration;

/// <summary>Turns config.toml text into its settings, checking every key.</summary>
/// <remarks>
/// The TOML syntax is validated first (<see cref="TomlReader.ParseToml"/>), and TOML errors name the file and
/// line; Pen's own checks name the file and key. Each key belongs in the user or the project file, except allow,
/// deny, tools and hide, which both may hold.
/// </remarks>
internal static class ConfigParser
{
    private const string UserFile = "~/.config/pen/" + ConfigLoader.FileName;
    private const string ProjectFile = ".pen/" + ConfigLoader.FileName;

    /// <summary>Parses the text of the user config.toml, which errors call <paramref name="displayPath"/>.</summary>
    public static UserConfiguration ParseUser(string text, string displayPath, RecipeCatalog catalog)
    {
        var settings = Parse(text, displayPath, ConfigKind.User, catalog);
        return new(settings.Allow, settings.Deny, settings.Tools, settings.Hide, settings.GitHubTokens, settings.Env);
    }

    /// <summary>
    /// Parses the text of a project's .pen/config.toml, which errors call <paramref name="displayPath"/>.
    /// </summary>
    public static ProjectConfiguration ParseProject(string text, string displayPath, RecipeCatalog catalog)
    {
        var settings = Parse(text, displayPath, ConfigKind.Project, catalog);
        return new(settings.Allow, settings.Deny, settings.Tools, settings.Hide, settings.Replacements,
            settings.GitHubOwner, settings.Docker, settings.Ports);
    }

    /// <remarks>Keys are checked in file order, each fully before the next, so the first problem is reported.</remarks>
    private static Settings Parse(string text, string displayPath, ConfigKind kind, RecipeCatalog catalog)
    {
        var table = TomlReader.ParseToml(text, displayPath);
        var config = new Settings();
        PenException Error(string message) => new($"{displayPath}: {Out.Field(message)}");
        foreach (var (key, value) in table)
        {
            ThrowIfMisplaced(key, kind, Error);
            ReadKey(config, key, value, catalog, Error);
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
        if (belongsIn is not null)
            throw error($"\"{key}\" belongs in {belongsIn}");
    }

    /// <summary>Validates one top-level key's value and stores it.</summary>
    private static void ReadKey(Settings config, string key, object value, RecipeCatalog catalog,
        Func<string, PenException> error)
    {
        switch (key)
        {
            case "allow":
                ReadDomains("allow", value, config.Allow, allowStar: true, error);
                break;
            case "deny":
                ReadDomains("deny", value, config.Deny, allowStar: false, error);
                break;
            case "github":
                ReadGitHubTokens(config, value, error);
                break;
            case "env":
                ReadEnv(config, value, error);
                break;
            case "github_owner":
                ReadGitHubOwner(config, value, error);
                break;
            case "docker":
                config.Docker = value as bool? ?? throw error("docker must be true or false (unquoted)");
                break;
            case "ports":
                ReadPorts(config, value, error);
                break;
            case "tools":
                ReadTools(config, value, catalog, error);
                break;
            case "hide":
                ReadHide(config, value, error);
                break;
            case "replace":
                ReadReplace(config, value, error);
                break;
            default:
                throw error($"unknown key \"{key}\"");
        }
    }

    /// <summary>
    /// Validates a list of domains, allow's or deny's, and adds them to <paramref name="list"/>, lowercased.
    /// </summary>
    /// <remarks>
    /// <paramref name="allowStar"/> says whether "*", any domain name, is valid as a whole entry: it is in allow, never
    /// in deny.
    /// </remarks>
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
            if (domain is null || !IsValidEntry(domain, allowStar))
            {
                throw error($"{key}: {DescribeItem(item)} " +
                    "isn't a domain (use example.com, or .example.com to include its subdomains)");
            }

            list.Add(domain);
        }
    }

    /// <summary>Whether <paramref name="domain"/> is valid in allow (<paramref name="allowStar"/>) or deny.</summary>
    private static bool IsValidEntry(string domain, bool allowStar) =>
        allowStar ? Allowlist.IsValidAllowEntry(domain) : Allowlist.IsValidDomain(domain);

    private static void ReadGitHubTokens(Settings config, object value, Func<string, PenException> error)
    {
        foreach (var (owner, token) in TomlReader.RequireTable(value, "github", error))
        {
            if (!TomlReader.OwnerRegex().IsMatch(owner))
                throw error($"github.{owner}: \"{owner}\" isn't a GitHub owner name");
            var tokenText = TomlReader.RequireString(token, $"github.{owner}", error);
            if (!config.GitHubTokens.TryAdd(owner.ToLowerInvariant(), tokenText))
            {
                throw error($"github.{owner}: a token for \"{owner}\" " +
                    "is already set (owner names ignore case)");
            }
        }
    }

    /// <summary>Reads [env], the user's environment variables for sessions.</summary>
    /// <remarks>
    /// Values may hold line breaks and other control characters, because they reach only the environment of the
    /// session's <c>docker compose run</c>, which names them with <c>-e NAME</c>, and never a line-based file such as
    /// session.env or a Dockerfile.
    /// </remarks>
    private static void ReadEnv(Settings config, object value, Func<string, PenException> error)
    {
        foreach (var (name, setting) in TomlReader.RequireTable(value, "env", error))
        {
            if (!TomlReader.EnvNameRegex().IsMatch(name))
                throw error($"env.{name}: \"{name}\" isn't a valid environment variable name");
            if (TomlReader.IsReservedEnv(name))
                throw error($"env.{name}: {name} is set by pen and can't be overridden");
            config.Env.Add(new(name, TomlReader.RequireString(setting, $"env.{name}", error)));
        }
    }

    private static void ReadGitHubOwner(Settings config, object value, Func<string, PenException> error)
    {
        var owner = TomlReader.RequireString(value, "github_owner", error);
        if (!TomlReader.OwnerRegex().IsMatch(owner))
            throw error($"github_owner: \"{owner}\" isn't a GitHub owner name");
        config.GitHubOwner = owner;
    }

    private static void ReadPorts(Settings config, object value, Func<string, PenException> error)
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

            if (!config.Ports.Contains((int)number))
                config.Ports.Add((int)number);
        }
    }

    private static void ReadTools(Settings config, object value, RecipeCatalog catalog,
        Func<string, PenException> error)
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

            if (!ToolSpec.TryParse(spec, catalog, out var tool, out var invalid))
                throw error($"tools: {invalid}");
            parsed.Add(tool);
        }
        if (!ToolSpec.TryCanonicalize(parsed, catalog, out var canonical, out var conflict))
            throw error($"tools: {conflict}");
        config.Tools = canonical;
    }

    private static void ReadHide(Settings config, object value, Func<string, PenException> error)
    {
        if (value is not TomlArray patterns)
            throw error("hide must be a list of patterns, e.g. hide = [\".env\", \"secrets/\"]");
        foreach (var item in patterns)
        {
            if (item is not string text)
                throw error($"hide: {DescribeItem(item)} isn't a pattern (put it in quotes)");
            if (!HidePattern.TryParse(text, out var pattern, out var problem))
                throw error($"hide: \"{text}\": {problem}");
            config.Hide.Add(pattern);
        }
    }

    private static void ReadReplace(Settings config, object value, Func<string, PenException> error)
    {
        var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (target, item) in TomlReader.RequireTable(value, "replace", error))
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

            config.Replacements.Add(new(target, source));
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

    /// <summary>The settings read so far from one file, which the entry points copy into its configuration.</summary>
    /// <remarks>
    /// One shape for both files: <see cref="ThrowIfMisplaced"/> refuses a key before it's read, so what belongs only in
    /// the other file stays empty.
    /// </remarks>
    private sealed class Settings
    {
        public List<string> Allow { get; } = [];
        public List<string> Deny { get; } = [];
        public IReadOnlyList<ToolSpec> Tools { get; set; } = [];
        public List<HidePattern> Hide { get; } = [];
        public Dictionary<string, string> GitHubTokens { get; } = new(StringComparer.Ordinal);
        public List<KeyValuePair<string, string>> Env { get; } = [];
        public List<FileReplacement> Replacements { get; } = [];
        public string? GitHubOwner { get; set; }
        public bool? Docker { get; set; }
        public List<int> Ports { get; } = [];
    }
}
