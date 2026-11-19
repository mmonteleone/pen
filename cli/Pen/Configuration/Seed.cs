using System.Text;
using Pen.Platform;

namespace Pen.Configuration;

/// <summary>Creates the home seed, ~/.config/pen/home/: sandbox-wide agent config laid out like $HOME.</summary>
/// <remarks>
/// It's mounted read-only at /etc/pen/seed and copied into each project's home by sync-seed.sh. See docs/usage.md's
/// agent config section for ownership.
/// </remarks>
internal static class Seed
{
    private const string Prefix = "templates/home/";

    /// <summary>
    /// Creates the seed from templates/home/ when it's missing, with <paramref name="identity"/> filled in to its
    /// .gitconfig.
    /// </summary>
    /// <returns>Whether it created the seed; an existing one is never rewritten.</returns>
    public static bool EnsureExists(HostEnv host, GitIdentity identity)
    {
        var seed = host.SeedDir;
        if (Path.Exists(seed) || PathChecks.IsLink(seed))
            return false;

        // Built next to it, then renamed, so a failure never leaves half a seed.
        var staging = seed + ".tmp-" + Environment.ProcessId;
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        foreach (var resource in Embedded.ListUnder(Prefix))
        {
            var relative = resource[Prefix.Length..];
            var path = Path.Combine(staging, relative);
            Directory.CreateDirectory(Paths.GetParent(path));
            if (relative == ".gitconfig")
                File.WriteAllText(path, BuildGitConfig(Embedded.ReadText(resource), identity.Name, identity.Email));
            else
                File.WriteAllBytes(path, Embedded.ReadBytes(resource));
        }
        Directory.Move(staging, seed);
        return true;
    }

    /// <summary>Builds the seed's .gitconfig from its template.</summary>
    /// <returns>
    /// The template with its @NAME@ and @EMAIL@ lines replaced by the identity, or by commented-out examples where
    /// it's unknown.
    /// </returns>
    internal static string BuildGitConfig(string template, string? name, string? email)
    {
        var builder = new StringBuilder();
        foreach (var line in template.Split('\n'))
        {
            if (line.Contains("@NAME@"))
                builder.Append(IdentityLine("name", name, "Your Name"));
            else if (line.Contains("@EMAIL@"))
                builder.Append(IdentityLine("email", email, "you@example.com"));
            else
                builder.Append(line);
            builder.Append('\n');
        }
        return builder.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>Formats one [user] line: the value quoted, or the example commented out if there's no value.</summary>
    private static string IdentityLine(string key, string? value, string example) =>
        value is { Length: > 0 } ? $"\t{key} = {Quote(value)}" : $"#\t{key} = {example}";

    /// <summary>Quotes a git config value, with \ and " escaped.</summary>
    internal static string Quote(string value) =>
        "\""
            + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            + "\"";
}
