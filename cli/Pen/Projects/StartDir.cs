using Pen.Platform;

namespace Pen.Projects;

/// <summary>
/// Where Pen was started, and moving away from there. The project dir is the
/// agent's to chmod or the user's to remove while Pen runs, and Pen's children
/// (git, docker) need a working directory they can use, so the end of a session
/// mustn't depend on it. Nothing uses the process's cwd after <see cref="Leave"/>.
/// </summary>
internal static class StartDir
{
    private static bool _left;
    private static string? _dir;

    /// <summary>The directory Pen started in, or a refusal saying why it can't be used.</summary>
    public static string Get()
    {
        var dir = _left ? _dir : Read();
        return dir ?? throw new PenException("pen can't read the current directory: it " +
            "was removed, or its permissions or a parent's were changed. Fix that (chmod " +
            "u+rwx . if it's only this directory's) or cd to the project, then rerun");
    }

    /// <summary>Changes to Pen's state dir, or / if that can't be used.</summary>
    public static void Leave(HostEnv host)
    {
        _dir = Read();
        _left = true;
        try
        {
            Directory.CreateDirectory(host.StateRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Environment.CurrentDirectory = host.StateRoot;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Environment.CurrentDirectory = "/";
        }
    }

    private static string? Read()
    {
        try
        {
            return Environment.CurrentDirectory;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
