using Pen.Platform;

namespace Pen.Projects;

/// <summary>Remembers the directory Pen was started in, and moves the process away from it.</summary>
/// <remarks>
/// The project dir is the agent's to chmod or the user's to remove while Pen runs, and Pen's children (git, docker)
/// need a working directory they can use, so the end of a session mustn't depend on it. Nothing uses the process's
/// cwd after <see cref="Leave"/>. Static, since it stands for the process's own working directory.
/// </remarks>
internal static class StartDir
{
    private static bool _left;
    private static string? _dir;

    /// <summary>Gets the directory Pen started in.</summary>
    /// <exception cref="PenException">The directory can't be read; the message says why and how to fix it.</exception>
    public static string Get()
    {
        var dir = _left ? _dir : ReadCurrent();
        return dir ?? throw new PenException("pen can't read the current directory: it " +
            "was removed, or its permissions or a parent's were changed. Fix that (chmod " +
            "u+rwx . if it's only this directory's) or cd to the project, then rerun");
    }

    /// <summary>Gets the directory Pen started in, if it can be read.</summary>
    /// <returns>The directory, or null when it can't be read (a command that needs it reports that).</returns>
    public static string? TryGet()
    {
        try
        {
            return Get();
        }
        catch (PenException)
        {
            return null;
        }
    }

    /// <summary>Changes to Pen's state dir, or / if that can't be used.</summary>
    public static void Leave(HostEnv host)
    {
        _dir = ReadCurrent();
        _left = true;
        try
        {
            Directory.CreateDirectory(host.StateRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.CurrentDirectory = host.StateRoot;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Environment.CurrentDirectory = "/";
        }
    }

    /// <summary>Reads the process's working directory.</summary>
    /// <returns>The directory, or null if it was removed or can't be reached.</returns>
    private static string? ReadCurrent()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
