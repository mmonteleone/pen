using Microsoft.Win32.SafeHandles;

namespace Pen.Platform;

/// <summary>
/// An flock(2) lock on a file. The kernel drops it when the process dies, so
/// there are no stale locks. Never waits: <see cref="TryTake"/> returns null
/// when someone else holds a conflicting lock.
/// </summary>
internal sealed class FileLock : IDisposable
{
    private readonly SafeFileHandle _handle;
    public string Path { get; }

    private FileLock(string path, SafeFileHandle handle)
    {
        Path = path;
        _handle = handle;
    }

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

    /// <summary>The lock's file descriptor, for checking it isn't inherited.</summary>
    internal SafeFileHandle Handle => _handle;

    public void Dispose() => _handle.Dispose(); // closing the fd releases the lock
}
