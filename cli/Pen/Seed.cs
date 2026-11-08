using System.Text;

namespace Pen;

/// <summary>
/// The home seed, ~/.config/pen/home/: sandbox-wide agent config laid out like
/// $HOME, mounted read-only at /etc/pen/seed and copied into each project's
/// home by sync-seed.sh. See README.md's agent config section for ownership.
/// </summary>
internal static class Seed
{
    private const string Prefix = "templates/home/";

    /// <summary>
    /// Creates the seed from templates/home/ when it's missing, with the
    /// .gitconfig identity filled in from the host's global git config.
    /// Returns whether it did. Never rewrites an existing seed.
    /// </summary>
    public static bool EnsureExists(HostEnv host, string? name, string? email)
    {
        var seed = host.SeedDir;
        if (Path.Exists(seed) || RepoGuard.IsLink(seed)) return false;

        // Built next to it, then renamed, so a failure never leaves half a seed.
        var tmp = seed + ".tmp-" + Environment.ProcessId;
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        Directory.CreateDirectory(tmp);
        foreach (var res in Embedded.Under(Prefix))
        {
            var rel = res[Prefix.Length..];
            var path = Path.Combine(tmp, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (rel == ".gitconfig")
                File.WriteAllText(path, GitConfig(Embedded.Text(res), name, email));
            else
                File.WriteAllBytes(path, Embedded.Bytes(res));
        }
        Directory.Move(tmp, seed);
        return true;
    }

    /// <summary>The seed's .gitconfig, with the identity filled in, or commented out where unknown.</summary>
    internal static string GitConfig(string template, string? name, string? email)
    {
        string Line(string key, string placeholder, string? value, string example) =>
            value is { Length: > 0 } ? $"\t{key} = {Quote(value)}" : $"#\t{key} = {example}";
        var sb = new StringBuilder();
        foreach (var line in template.Split('\n'))
        {
            if (line.Contains("@NAME@")) sb.Append(Line("name", "@NAME@", name, "Your Name"));
            else if (line.Contains("@EMAIL@")) sb.Append(Line("email", "@EMAIL@", email, "you@example.com"));
            else sb.Append(line);
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>A git config value, quoted, with \ and " escaped.</summary>
    internal static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
