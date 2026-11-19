using System.Text;
using Pen.Tools;

namespace Pen.Configuration;

/// <summary>Adds domains to a list in a config.toml on disk, for pen allow and pen deny.</summary>
/// <remarks>
/// The text edit is <see cref="ConfigEdit.AddToList"/>'s; this reads the file, chooses its mode and replaces it by
/// rename.
/// </remarks>
internal static class ConfigFileEditor
{
    /// <summary>
    /// Adds the domains not already in <paramref name="key"/> to the config.toml <paramref name="target"/>, creating
    /// it from its stub if it's missing.
    /// </summary>
    /// <returns>The domains added, lowercased, in order; none when the file is left alone.</returns>
    /// <remarks>The file is replaced by rename, keeping its mode.</remarks>
    public static IReadOnlyList<string> AddToList(
        ConfigTarget target,
        ConfigListKey key,
        IReadOnlyList<string> domains,
        RecipeCatalog catalog)
    {
        var existing = ConfigLoader.ReadText(target.Path, target.DisplayPath);
        var exists = existing is not null;
        var edit = ConfigEdit.AddToList(existing ?? target.Stub, target.DisplayPath, target.Kind, key, domains,
            catalog);
        if (edit.Added.Count == 0)
            return edit.Added;

        ReplaceFile(target.Path, edit.Text, ChooseMode(target.Path, exists, target.Kind));
        return edit.Added;
    }

    /// <summary>
    /// Chooses the config.toml's mode: its own when it exists; otherwise 600 for the user's (it may hold secrets) and
    /// 644 for a project's.
    /// </summary>
    private static UnixFileMode ChooseMode(string path, bool exists, ConfigKind kind)
    {
        if (exists)
            return File.GetUnixFileMode(path);
        if (kind == ConfigKind.User)
            return UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    }

    /// <summary>
    /// Replaces the file at <paramref name="path"/> with <paramref name="text"/>, in mode <paramref name="mode"/>,
    /// by writing a temp file and renaming it over.
    /// </summary>
    private static void ReplaceFile(string path, string text, UnixFileMode mode)
    {
        var tmp = $"{path}.tmp-{Environment.ProcessId}";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = mode,
            };
            using (var file = new FileStream(tmp, options))
            {
                file.Write(Encoding.UTF8.GetBytes(text));
            }

            File.SetUnixFileMode(tmp, mode); // in case a stale tmp existed with another mode
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

/// <summary>A config.toml <see cref="ConfigFileEditor.AddToList"/> adds to.</summary>
/// <param name="Path">The file.</param>
/// <param name="DisplayPath">Names the file in messages.</param>
/// <param name="Kind">Which keys the file may hold.</param>
/// <param name="Stub">The text the file starts from when it's missing.</param>
internal sealed record ConfigTarget(string Path, string DisplayPath, ConfigKind Kind, string Stub);
