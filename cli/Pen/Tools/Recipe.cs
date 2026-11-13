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
/// </summary>
/// <remarks>
/// Recipes are embedded and trusted, and can't depend on each other. A hidden one works
/// but isn't offered in listings or messages. <c>kind = "agent"</c> marks a recipe
/// that installs an AI agent, which a command naming it implies (<see cref="ToolSpec.FindImpliedAgent"/>).
/// </remarks>
internal sealed class Recipe
{
    private static readonly string[] Keys = ["default", "version", "multiple", "hidden", "kind", "allow", "env"];

    /// <summary>Gets the recipe's name, its directory under tools/.</summary>
    public required string Name { get; init; }
    /// <summary>Gets the version a tool named without one gets.</summary>
    public required string Default { get; init; }
    /// <summary>Gets whether several versions can be installed together.</summary>
    public bool Multiple { get; init; }
    /// <summary>Gets whether the recipe is kept out of listings and messages.</summary>
    public bool Hidden { get; init; }
    /// <summary>Gets whether this installs an agent (kind = "agent") rather than a tool.</summary>
    public bool IsAgent { get; init; }
    /// <summary>Gets the domains the tool needs, in file order.</summary>
    public List<string> Allow { get; } = [];
    /// <summary>
    /// Gets the environment for sessions with the tool, in file order: set in its image (<see cref="ToolsImage"/>).
    /// </summary>
    public List<KeyValuePair<string, string>> Env { get; } = [];
    /// <summary>Gets the path of install.sh in the build context.</summary>
    public string InstallPath => $"{RecipeCatalog.Root}{Name}/{RecipeCatalog.InstallFile}";
    /// <summary>Gets the text of tool.toml, as loaded.</summary>
    public required string Settings { get; init; }
    /// <summary>Gets the text of install.sh, as loaded.</summary>
    public required string Script { get; init; }

    private Regex _versionRegex = null!;

    /// <summary>Checks whether the recipe's version regex matches all of <paramref name="version"/>.</summary>
    /// <remarks>
    /// Not just IsMatch: an unanchored pattern could match part of it, and .NET's $ also matches before a
    /// final newline.
    /// </remarks>
    public bool AcceptsVersion(string version)
    {
        var match = _versionRegex.Match(version);
        return match.Success && match.Index == 0 && match.Length == version.Length;
    }

    /// <summary>Parses a tool.toml, the way <see cref="Config.Parse"/> does a config.toml.</summary>
    /// <param name="name">The recipe's name.</param>
    /// <param name="text">The text of its tool.toml.</param>
    /// <param name="script">The text of its install.sh.</param>
    /// <returns>The recipe; anything invalid in tool.toml throws, naming the file.</returns>
    public static Recipe Parse(string name, string text, string script)
    {
        var display = $"{RecipeCatalog.Root}{name}/{RecipeCatalog.SettingsFile}";
        PenException Error(string message) => new($"{display}: {Out.Field(message)}");
        var table = Config.ParseToml(text, display);

        CheckKeys(table, Error);
        var isAgent = ReadIsAgent(table, Error);
        var regex = ReadVersionRegex(table, Error);
        var recipe = new Recipe
        {
            Name = name,
            Default = Config.RequireString(Require(table, "default", Error), "default", Error),
            Multiple = ReadFlag(table, "multiple", Error),
            Hidden = ReadFlag(table, "hidden", Error),
            IsAgent = isAgent,
            Settings = text,
            Script = script,
            _versionRegex = regex,
        };
        if (!ToolSpec.IsVersionSyntax(recipe.Default) || !recipe.AcceptsVersion(recipe.Default))
            throw Error($"default \"{recipe.Default}\" isn't a valid version");

        recipe.ReadAllow(table, Error);
        recipe.ReadEnv(table, Error);
        return recipe;
    }

    /// <summary>Throws for the first key in <paramref name="table"/> that a tool.toml can't have.</summary>
    private static void CheckKeys(TomlTable table, Func<string, PenException> error)
    {
        foreach (var key in table.Keys)
        {
            if (!Keys.Contains(key))
                throw error($"unknown key \"{key}\"");
        }
    }

    /// <summary>Gets the value of <paramref name="key"/>, which has to be there.</summary>
    private static object Require(TomlTable table, string key, Func<string, PenException> error)
    {
        if (table.TryGetValue(key, out var value))
            return value;
        throw error($"{key} is missing");
    }

    /// <summary>Reads the true or false of <paramref name="key"/>, false when it's left out.</summary>
    private static bool ReadFlag(TomlTable table, string key, Func<string, PenException> error)
    {
        if (!table.TryGetValue(key, out var value))
            return false;
        return value as bool? ?? throw error($"{key} must be true or false (unquoted)");
    }

    /// <summary>Reads kind, which can only be "agent" or left out.</summary>
    private static bool ReadIsAgent(TomlTable table, Func<string, PenException> error)
    {
        if (!table.TryGetValue("kind", out var kind))
            return false;
        if (Config.RequireString(kind, "kind", error) != "agent")
            throw error("kind must be \"agent\" or left out");
        return true;
    }

    /// <summary>Reads version, the regex that versions of the tool have to match.</summary>
    private static Regex ReadVersionRegex(TomlTable table, Func<string, PenException> error)
    {
        var pattern = Config.RequireString(Require(table, "version", error), "version", error);
        try
        {
            // Only ever a recipe's own pattern, on a short version: interpreted, so AOT-safe.
            return new Regex(pattern, RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            throw error("version isn't a valid regular expression");
        }
    }

    /// <summary>Reads allow, the domains the tool needs, into <see cref="Allow"/>.</summary>
    private void ReadAllow(TomlTable table, Func<string, PenException> error)
    {
        if (!table.TryGetValue("allow", out var allow))
            return;
        if (allow is not TomlArray domains)
            throw error("allow must be a list of domains");
        foreach (var entry in domains)
        {
            if (entry is not string domain || !Allowlist.IsValidDomain(domain))
                throw error($"allow: {entry ?? "null"} isn't a lowercase domain");
            Allow.Add(domain);
        }
    }

    /// <summary>Reads [env], the sessions' environment, into <see cref="Env"/>.</summary>
    private void ReadEnv(TomlTable table, Func<string, PenException> error)
    {
        if (!table.TryGetValue("env", out var env))
            return;
        foreach (var (variable, value) in Config.RequireTable(env, "env", error))
        {
            if (!Config.EnvNameRegex().IsMatch(variable))
                throw error($"env.{variable} isn't a valid environment variable name");
            if (Config.IsReservedEnv(variable))
                throw error($"env.{variable} is set by pen");
            var setting = Config.RequireString(value, $"env.{variable}", error);
            if (setting.Any(char.IsControl))
                throw error($"env.{variable} can't hold a line break or control character");
            Env.Add(new(variable, setting));
        }
    }
}
