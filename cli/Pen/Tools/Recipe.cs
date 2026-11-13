using System.Text.RegularExpressions;
using Pen.Configuration;
using Pen.Images;
using Pen.Platform;
using Pen.Proxy;
using Tomlyn.Model;

namespace Pen.Tools;

/// <summary>
/// A tool's recipe: tools/&lt;name&gt;/tool.toml, its settings, and tools/&lt;name&gt;/install.sh,
/// which runs as root at image build, with the versions as separate arguments.
/// Recipes are embedded and trusted, and can't depend on each other. A hidden one works
/// but isn't offered in listings or messages. <c>kind = "agent"</c> marks a recipe
/// that installs an AI agent, which a command naming it implies (<see cref="ToolSpec.ImpliedAgent"/>).
/// </summary>
internal sealed class Recipe
{
    public required string Name { get; init; }
    public required string Default { get; init; }
    /// <summary>Whether several versions can be installed together.</summary>
    public bool Multiple { get; init; }
    public bool Hidden { get; init; }
    /// <summary>Whether this installs an agent (kind = "agent") rather than a tool.</summary>
    public bool IsAgent { get; init; }
    /// <summary>Domains the tool needs, in file order.</summary>
    public List<string> Allow { get; } = [];
    /// <summary>
    /// Environment for sessions with the tool, in file order: set in its image (<see cref="ToolsImage"/>).
    /// </summary>
    public List<KeyValuePair<string, string>> Env { get; } = [];
    public string InstallPath => $"{RecipeCatalog.Root}{Name}/{RecipeCatalog.InstallFile}";
    /// <summary>The text of tool.toml, as loaded.</summary>
    public required string Settings { get; init; }
    /// <summary>The text of install.sh, as loaded.</summary>
    public required string Script { get; init; }

    private Regex _versionRegex = null!;

    /// <summary>
    /// Whether the regex matches all of the version. Not just IsMatch: an unanchored
    /// pattern could match part of it, and .NET's $ also matches before a final newline.
    /// </summary>
    public bool AcceptsVersion(string version)
    {
        var m = _versionRegex.Match(version);
        return m.Success && m.Index == 0 && m.Length == version.Length;
    }

    /// <summary>Parses a tool.toml, the way <see cref="Config.Parse"/> does a config.toml.</summary>
    public static Recipe Parse(string name, string text, string script)
    {
        var display = $"{RecipeCatalog.Root}{name}/{RecipeCatalog.SettingsFile}";
        PenException Error(string message) => new($"{display}: {Out.Field(message)}");
        var table = Config.ParseToml(text, display);

        foreach (var key in table.Keys)
        {
            if (key is not ("default" or "version" or "multiple" or "hidden" or "kind" or "allow" or "env"))
                throw Error($"unknown key \"{key}\"");
        }
        object Required(string key) => table.TryGetValue(key, out var v) ? v : throw Error($"{key} is missing");
        bool Flag(string key) => !table.TryGetValue(key, out var v) ? false : v as bool? ?? throw Error(
            $"{key} must be true or false (unquoted)");

        var isAgent = false;
        if (table.TryGetValue("kind", out var kind))
        {
            isAgent = Config.RequireString(kind, "kind", Error) == "agent" ? true : throw Error(
                "kind must be \"agent\" or left out");
        }

        Regex regex;
        try
        {
            // Only ever a recipe's own pattern, on a short version: interpreted, so AOT-safe.
            regex = new Regex(Config.RequireString(Required("version"), "version", Error),
                RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            throw Error("version isn't a valid regular expression");
        }

        var recipe = new Recipe
        {
            Name = name,
            Default = Config.RequireString(Required("default"), "default", Error),
            Multiple = Flag("multiple"),
            Hidden = Flag("hidden"),
            IsAgent = isAgent,
            Settings = text,
            Script = script,
            _versionRegex = regex,
        };
        if (!ToolSpec.IsVersionSyntax(recipe.Default) || !recipe.AcceptsVersion(recipe.Default))
            throw Error($"default \"{recipe.Default}\" isn't a valid version");

        if (table.TryGetValue("allow", out var allow))
        {
            if (allow is not TomlArray domains)
                throw Error("allow must be a list of domains");
            foreach (var d in domains)
            {
                if (d is not string domain || !Allowlist.IsValidDomain(domain))
                    throw Error($"allow: {d ?? "null"} isn't a lowercase domain");
                recipe.Allow.Add(domain);
            }
        }
        if (table.TryGetValue("env", out var env))
        {
            foreach (var (variable, value) in Config.RequireTable(env, "env", Error))
            {
                if (!Config.EnvNameRegex().IsMatch(variable))
                    throw Error($"env.{variable} isn't a valid environment variable name");
                if (Config.IsReservedEnv(variable))
                    throw Error($"env.{variable} is set by pen");
                var setting = Config.RequireString(value, $"env.{variable}", Error);
                if (setting.Any(char.IsControl))
                    throw Error($"env.{variable} can't hold a line break or control character");
                recipe.Env.Add(new(variable, setting));
            }
        }
        return recipe;
    }
}
