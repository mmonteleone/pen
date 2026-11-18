using Pen.Platform;
using Pen.Tools;

namespace Pen.Configuration;

/// <summary>Reads a config.toml from disk, ~/.config/pen/'s or a project's .pen/'s, which may be missing.</summary>
internal static class ConfigLoader
{
    public const string FileName = "config.toml";
    /// <summary>The pre-TOML config file. Its presence is an error, so it's never silently ignored.</summary>
    public const string LegacyName = "config";

    /// <summary>Loads the user config.toml in <paramref name="dir"/>.</summary>
    /// <param name="dir">The config directory.</param>
    /// <param name="displayDir">How messages name the directory (e.g. "~/.config/pen").</param>
    /// <param name="catalog">The recipes the file's tools come from.</param>
    /// <returns>The settings, or empty ones if there's no config.toml.</returns>
    public static UserConfiguration LoadUser(string dir, string displayDir, RecipeCatalog catalog) =>
        ReadConfigText(dir, displayDir, out var display) is { } text
            ? ConfigParser.ParseUser(text, display, catalog)
            : UserConfiguration.Empty;

    /// <summary>Loads the project config.toml in <paramref name="dir"/>, a project's .pen/.</summary>
    /// <param name="dir">The config directory.</param>
    /// <param name="displayDir">How messages name the directory (e.g. ".pen").</param>
    /// <param name="catalog">The recipes the file's tools come from.</param>
    /// <returns>The settings, or empty ones if there's no config.toml.</returns>
    public static ProjectConfiguration LoadProject(string dir, string displayDir, RecipeCatalog catalog) =>
        ReadConfigText(dir, displayDir, out var display) is { } text
            ? ConfigParser.ParseProject(text, display, catalog)
            : ProjectConfiguration.Empty;

    /// <summary>Refuses a legacy config in <paramref name="dir"/>, then reads its config.toml's text.</summary>
    /// <param name="dir">The config directory.</param>
    /// <param name="displayDir">How messages name the directory.</param>
    /// <param name="display">How messages name the config.toml.</param>
    /// <returns>The text, or null if there's no config.toml.</returns>
    private static string? ReadConfigText(string dir, string displayDir, out string display)
    {
        if (File.Exists(Path.Combine(dir, LegacyName)))
        {
            throw new PenException(
                $"{displayDir}/{LegacyName} is pen's old config format, " +
                    "which is no longer read: move its settings to " +
                $"{displayDir}/{FileName} (see the README), then delete it");
        }

        display = $"{displayDir}/{FileName}";
        return ReadText(Path.Combine(dir, FileName), display);
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
}
