using Microsoft.Win32.SafeHandles;

namespace Pen.Platform;

/// <summary>An flock(2) lock on a file, held until it's disposed.</summary>
/// <remarks>
/// The kernel drops it when the process dies, so there are no stale locks. Never waits:
/// <see cref="TryTake"/> returns null when someone else holds a conflicting lock.
/// </remarks>
internal sealed class FileLock : IDisposable
{
    private readonly SafeFileHandle _handle;

    private FileLock(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
    }

    /// <summary>Gets the path of the lock file.</summary>
    public string Path { get; }

    /// <summary>Gets the lock's file descriptor, for checking it isn't inherited.</summary>
    internal SafeFileHandle Handle => _handle;

    /// <summary>Takes a lock on <paramref name="path"/>, creating the file if needed, without waiting.</summary>
    /// <param name="path">The lock file.</param>
    /// <param name="exclusive">True for an exclusive lock, false for a shared one.</param>
    /// <returns>The lock, or null if someone else holds a conflicting one.</returns>
    public static FileLock? TryTake(string path, bool exclusive)
    {
        var handle = Native.OpenLockFile(path);
        try
        {
            if (Native.Flock(handle, (exclusive ? Native.LOCK_EX : Native.LOCK_SH) | Native.LOCK_NB))
                return new FileLock(path, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
        handle.Dispose();
        return null;
    }

    /// <summary>Releases the lock.</summary>
    public void Dispose() => _handle.Dispose(); // closing the fd releases the lock
}
