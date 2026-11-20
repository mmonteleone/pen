namespace Pen.Projects;

/// <summary>The directory Pen was started in, as read before Pen left it.</summary>
/// <param name="Dir">The directory, or null when it couldn't be read (removed, or its permissions changed).</param>
/// <remarks>
/// Pen leaves it at startup (<see cref="Platform.HostEnv.LeaveWorkingDirectory"/>), so this, not the process's
/// working directory, is where commands find the project.
/// </remarks>
internal sealed record StartDir(string? Dir)
{
    /// <summary>Gets the directory Pen started in.</summary>
    /// <exception cref="PenException">
    /// The directory couldn't be read; the message says why and how to fix it.
    /// </exception>
    public string Get() => Dir ?? throw new PenException("pen can't read the current directory: it " +
        "was removed, or its permissions or a parent's were changed. Fix that (chmod " +
        "u+rwx . if it's only this directory's) or cd to the project, then rerun");
}
