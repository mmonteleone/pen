using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;

namespace Pen.Trust;

/// <summary>A private copy of .pen/ to build the project layer from, or to show and approve.</summary>
/// <remarks>
/// What's built is what was hashed, whatever happens to .pen/ meanwhile. The copy lives under the runtime
/// dir, which <see cref="Project.CheckAllowed"/> keeps out of every sandbox, in a directory only this user
/// can enter. For a build, the path stays the same for a directory, since BuildKit keeps a cache record per
/// context path; so builds in one directory take turns, by a lock. One left by a Pen that died is removed
/// by the next, or by pen clean --stale.
/// </remarks>
internal sealed class LayerContext : IDisposable
{
    private const string Prefix = "layer-";
    private const UnixFileMode PrivateDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        | UnixFileMode.UserExecute;
    private static readonly TimeSpan KeepAbandonedReviews = TimeSpan.FromDays(1);
    private readonly FileLock? _lock;
    private readonly string _parent;

    /// <summary>The copy, to give docker build.</summary>
    public string Dir { get; }

    /// <summary>The hash of the copy, or the problem that means there's none.</summary>
    public PenDirHashResult Result { get; }

    private LayerContext(FileLock? heldLock, string parent, string dir, PenDirHashResult result)
    {
        _lock = heldLock;
        _parent = parent;
        Dir = dir;
        Result = result;
    }

    /// <summary>Takes the copy of <paramref name="project"/>'s .pen/ to build from, after any other build.</summary>
    /// <returns>The copy, which holds the directory's build lock until disposed.</returns>
    public static LayerContext Take(HostEnv host, Project project)
    {
        Session.EnsureRuntimeDir(host, project);
        var runtimeDir = host.GetRuntimeDir(project.Key);
        var parent = Path.Combine(runtimeDir, Prefix + Project.HashPath(project.Dir));
        var heldLock = WaitForLock(parent + ".lock");
        try
        {
            Remove(parent);
            Directory.CreateDirectory(parent, PrivateDirMode);
            var dir = Path.Combine(parent, "context");
            return new(heldLock, parent, dir, PenDirHash.Snapshot(project.PenDir, dir));
        }
        catch
        {
            heldLock.Dispose();
            throw;
        }
    }

    /// <summary>Takes a copy of <paramref name="project"/>'s .pen/ to show the user and approve the hash of.</summary>
    /// <returns>The copy, kept until disposed, once the user has answered.</returns>
    /// <remarks>
    /// It isn't locked, since a prompt can wait for hours, and has a path of its own. One abandoned by a
    /// killed Pen goes when the next review finds it old, or by pen clean --stale.
    /// </remarks>
    public static LayerContext Review(HostEnv host, Project project)
    {
        Session.EnsureRuntimeDir(host, project);
        var runtimeDir = host.GetRuntimeDir(project.Key);
        var prefix = $"{Prefix}{Project.HashPath(project.Dir)}-review-";
        foreach (var old in Directory.GetDirectories(runtimeDir, prefix + "*"))
        {
            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(old) < KeepAbandonedReviews)
                continue;
            try
            {
                Remove(old);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        var parent = Path.Combine(runtimeDir, prefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(parent, PrivateDirMode);
        var dir = Path.Combine(parent, "context");
        return new(null, parent, dir, PenDirHash.Snapshot(project.PenDir, dir));
    }

    /// <summary>Takes the exclusive lock at <paramref name="path"/>, saying so if it has to wait.</summary>
    private static FileLock WaitForLock(string path)
    {
        var heldLock = FileLock.TryTake(path, exclusive: true);
        if (heldLock is not null)
            return heldLock;
        Out.WriteLine("waiting for another pen building this directory's project layer");
        while ((heldLock = FileLock.TryTake(path, exclusive: true)) is null)
            Thread.Sleep(200);
        return heldLock;
    }

    /// <summary>Removes the copy and releases the lock, if it holds one.</summary>
    public void Dispose()
    {
        try
        {
            Remove(_parent);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next build removes it.
        }
        _lock?.Dispose();
    }

    /// <summary>Removes every copy left in <paramref name="runtimeDir"/>.</summary>
    /// <remarks>A plain recursive delete can't remove one that holds a read-only directory.</remarks>
    public static void RemoveAllIn(string runtimeDir)
    {
        if (!Directory.Exists(runtimeDir))
            return;
        foreach (var parent in Directory.GetDirectories(runtimeDir, Prefix + "*"))
            Remove(parent);
    }

    private static void Remove(string path)
    {
        if (!Directory.Exists(path))
            return;
        MakeRemovable(new DirectoryInfo(path));
        Directory.Delete(path, recursive: true);
    }

    /// <summary>Gives this user full access to <paramref name="dir"/> and every real directory in it.</summary>
    private static void MakeRemovable(DirectoryInfo dir)
    {
        dir.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var child in dir.EnumerateDirectories("*", options))
        {
            if (child.LinkTarget is null)
                MakeRemovable(child);
        }
    }
}
