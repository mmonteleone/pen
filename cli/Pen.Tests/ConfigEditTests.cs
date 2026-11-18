using System.Reflection;

namespace Pen.Tests;

/// <summary>The check that an edit for pen allow or pen deny changed nothing but the one list.</summary>
public class ConfigEditTests
{
    private const string UserBase = """
        allow = ["a.io"]
        deny = ["d.io"]
        tools = ["go"]
        hide = [".env", "secrets/"]

        [github]
        acme = "t"

        [env]
        X = "1"
        Y = "2"
        """;

    private const string ProjectBase = """
        allow = ["a.io"]
        deny = ["d.io"]
        tools = ["go"]
        hide = [".env", "secrets/"]
        github_owner = "acme"
        docker = true
        ports = [3000]

        [replace]
        "a.txt" = "fixtures/b.txt"
        "c.txt" = "fixtures/d.txt"
        """;

    /// <summary>
    /// For each property of <see cref="UserConfiguration"/> and <see cref="ProjectConfiguration"/>, files of that kind
    /// that differ from its base only in it.
    /// </summary>
    private static readonly (string Property, ConfigKind Kind, string Text)[] Changes =
    [
        ("Allow", ConfigKind.User, UserBase.Replace("allow = [\"a.io\"]", "allow = [\"b.io\"]")),
        ("Deny", ConfigKind.User, UserBase.Replace("deny = [\"d.io\"]", "deny = [\"e.io\"]")),
        ("GitHubTokens", ConfigKind.User, UserBase.Replace("acme = \"t\"", "acme = \"u\"")),
        ("GitHubTokens", ConfigKind.User, UserBase.Replace("acme = \"t\"", "other = \"t\"")),
        ("Env", ConfigKind.User, UserBase.Replace("X = \"1\"", "X = \"9\"")),
        ("Env", ConfigKind.User, UserBase.Replace("X = \"1\"\nY = \"2\"", "Y = \"2\"\nX = \"1\"")),
        ("Allow", ConfigKind.Project, ProjectBase.Replace("allow = [\"a.io\"]", "allow = [\"b.io\"]")),
        ("Deny", ConfigKind.Project, ProjectBase.Replace("deny = [\"d.io\"]", "deny = [\"e.io\"]")),
        ("GitHubOwner", ConfigKind.Project, ProjectBase.Replace("github_owner = \"acme\"", "github_owner = \"other\"")),
        ("Docker", ConfigKind.Project, ProjectBase.Replace("docker = true", "docker = false")),
        ("Ports", ConfigKind.Project, ProjectBase.Replace("ports = [3000]", "ports = [3001]")),
        ("Tools", ConfigKind.User, UserBase.Replace("tools = [\"go\"]", "tools = [\"rust\"]")),
        ("Tools", ConfigKind.Project, ProjectBase.Replace("tools = [\"go\"]", "tools = [\"rust\"]")),
        ("Hide", ConfigKind.User, UserBase.Replace("\"secrets/\"]", "\"keys/\"]")),
        ("Hide", ConfigKind.Project, ProjectBase.Replace("[\".env\", \"secrets/\"]", "[\"secrets/\", \".env\"]")),
        ("Replacements", ConfigKind.Project, ProjectBase.Replace("\"fixtures/d.txt\"", "\"fixtures/e.txt\"")),
        ("Replacements", ConfigKind.Project, ProjectBase.Replace("\"c.txt\"", "\"e.txt\"")),
    ];

    // By index, since ConfigKind is internal and a public test method can't take one.
    public static TheoryData<string, int> ChangeCases()
    {
        var data = new TheoryData<string, int>();
        for (var i = 0; i < Changes.Length; i++)
            data.Add(Changes[i].Property, i);
        return data;
    }

    private static UserConfiguration ParseUser(string text) =>
        ConfigParser.ParseUser(text, "x.toml", RecipeCatalog.Embedded);

    private static ProjectConfiguration ParseProject(string text) =>
        ConfigParser.ParseProject(text, "x.toml", RecipeCatalog.Embedded);

    /// <summary>Parses both texts as <paramref name="kind"/> and compares them.</summary>
    private static bool IsOnlyAdded(ConfigKind kind, string before, string after, ConfigListKey key, List<string> added) =>
        kind == ConfigKind.User
            ? ConfigEdit.IsOnlyAdded(ParseUser(before), ParseUser(after), key, added)
            : ConfigEdit.IsOnlyAdded(ParseProject(before), ParseProject(after), key, added);

    private static string Base(ConfigKind kind) => kind == ConfigKind.User ? UserBase : ProjectBase;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheSameSettingsParsedTwicePass(bool user)
    {
        var kind = user ? ConfigKind.User : ConfigKind.Project;
        Assert.True(IsOnlyAdded(kind, Base(kind), Base(kind), ConfigListKey.Allow, []));
    }

    [Theory]
    [MemberData(nameof(ChangeCases))]
    public void AChangeToAnyOtherSettingFails(string property, int index)
    {
        var (_, kind, changed) = Changes[index];
        Assert.NotEqual(Base(kind), changed);
        Assert.False(IsOnlyAdded(kind, Base(kind), changed, ConfigListKey.Allow, []), property);
        Assert.False(IsOnlyAdded(kind, Base(kind), changed, ConfigListKey.Deny, []), property);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryConfigurationPropertyHasAChangeCase(bool user)
    {
        var (type, kind) = user ? (typeof(UserConfiguration), ConfigKind.User) : (typeof(ProjectConfiguration), ConfigKind.Project);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name);
        Assert.Empty(properties.Except(Changes.Where(c => c.Kind == kind).Select(c => c.Property)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void OnlyTheAddedDomainsAtTheEndOfTheirListPass(bool allow, bool user)
    {
        var key = allow ? ConfigListKey.Allow : ConfigListKey.Deny;
        var kind = user ? ConfigKind.User : ConfigKind.Project;
        var (from, to) = key == ConfigListKey.Allow
            ? ("allow = [\"a.io\"]", "allow = [\"a.io\", \"n.io\"]")
            : ("deny = [\"d.io\"]", "deny = [\"d.io\", \"n.io\"]");
        var after = Base(kind).Replace(from, to);
        Assert.True(IsOnlyAdded(kind, Base(kind), after, key, ["n.io"]));
        Assert.False(IsOnlyAdded(kind, Base(kind), after, key, []));
        var other = key == ConfigListKey.Allow ? ConfigListKey.Deny : ConfigListKey.Allow;
        Assert.False(IsOnlyAdded(kind, Base(kind), after, other, ["n.io"]));
    }
}
