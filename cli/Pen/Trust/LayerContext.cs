using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;

namespace Pen.Trust;

/// <summary>
/// A private copy of .pen/ to run docker build on, so that what's built is what
/// was hashed, whatever happens to .pen/ meanwhile. It lives under the runtime
/// dir, which <see cref="Project.CheckAllowed"/> keeps out of every sandbox, in a
/// directory only this user can enter. The path stays the same for a directory,
/// since BuildKit keeps a cache record per context path; so builds in one directory
/// take turns, by a lock. One left by a Pen that died is removed by the next, or
/// by pen prune.
/// </summary>
internal sealed class LayerContext : IDisposable
{
    private const string Prefix = "layer-";
    private static readonly TimeSpan KeepAbandonedReviews = TimeSpan.FromDays(1);
    private readonly FileLock? _lock;
    private readonly string _parent;

    /// <summary>The copy, to give docker build.</summary>
    public string Dir { get; }
    public PenDirHashResult Result { get; }

    private LayerContext(FileLock? held, string parent, string dir, PenDirHashResult result)
    {
        _lock = held;
        _parent = parent;
        Dir = dir;
        Result = result;
    }

    public static LayerContext Take(HostEnv host, Project project)
    {
        Session.EnsureRuntimeDir(host, project);
        var runtimeDir = host.GetRuntimeDir(project.Key);
        var parent = Path.Combine(runtimeDir, Prefix + Project.Hash(project.Dir));
        var held = WaitForLock(parent + ".lock");
        try
        {
            Remove(parent);
            System.IO.Directory.CreateDirectory(parent,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var dir = Path.Combine(parent, "context");
            return new(held, parent, dir, PenDirHash.Snapshot(project.PenDir, dir));
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A copy to show the user and approve the hash of, kept until they've answered.
    /// Not locked, since a prompt can wait for hours, and with a path of its own.
    /// One abandoned by a killed Pen goes when the next review finds it old, or by pen prune.
    /// </summary>
    public static LayerContext Review(HostEnv host, Project project)
    {
        Session.EnsureRuntimeDir(host, project);
        var runtimeDir = host.GetRuntimeDir(project.Key);
        var prefix = $"{Prefix}{Project.Hash(project.Dir)}-review-";
        foreach (var old in System.IO.Directory.GetDirectories(runtimeDir, prefix + "*"))
        {
            if (DateTime.UtcNow - System.IO.Directory.GetLastWriteTimeUtc(old) < KeepAbandonedReviews)
                continue;
            try
            {
                Remove(old);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        var parent = Path.Combine(runtimeDir, prefix + Guid.NewGuid().ToString("N")[..8]);
        System.IO.Directory.CreateDirectory(parent,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var dir = Path.Combine(parent, "context");
        return new(null, parent, dir, PenDirHash.Snapshot(project.PenDir, dir));
    }

    private static FileLock WaitForLock(string path)
    {
        var held = FileLock.TryTake(path, exclusive: true);
        if (held != null)
            return held;
        Out.WriteLine("waiting for another pen building this directory's project layer");
        while ((held = FileLock.TryTake(path, exclusive: true)) == null)
            Thread.Sleep(200);
        return held;
    }

    public void Dispose()
    {
        try
        {
            Remove(_parent);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // the next build removes it
        _lock?.Dispose();
    }

    /// <summary>
    /// Everything left in a runtime dir, which a plain recursive delete can't remove if it holds a read-only directory.
    /// </summary>
    public static void RemoveAllIn(string runtimeDir)
    {
        if (!System.IO.Directory.Exists(runtimeDir))
            return;
        foreach (var parent in System.IO.Directory.GetDirectories(runtimeDir, Prefix + "*"))
            Remove(parent);
    }

    private static void Remove(string path)
    {
        if (!System.IO.Directory.Exists(path))
            return;
        MakeRemovable(new DirectoryInfo(path));
        System.IO.Directory.Delete(path, recursive: true);
    }

    private static void MakeRemovable(DirectoryInfo dir)
    {
        dir.UnixFileMode |= UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var all = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        foreach (var sub in dir.EnumerateDirectories("*", all))
        {
            if (sub.LinkTarget == null)
                MakeRemovable(sub);
        }
    }
}
