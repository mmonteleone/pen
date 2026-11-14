namespace Pen.SmokeTests.Harness;

/// <summary>Keeps an image under a backup tag of its own while a test replaces it, and puts it back exactly: the
/// same ID under its own tag, with the backup tag and the images that stood in for it removed.</summary>
/// <remarks>
/// The backup tag is new for each backup, so an image already under a tag like it is never moved or removed. The
/// images that stood in for the original are only those a test names with <see cref="RemoveWithRestore"/>: whatever
/// else is under the original's tag when it's restored, which the test can't tell from an image another process put
/// there, is left untagged, as smoke.sh does. Only the named ones that are untagged once the original is back are
/// removed, and never one that was there before the backup was taken: a build gives the same ID as an identical
/// earlier one. Restoring is idempotent, so a test restores before its checks, as smoke.sh does, and disposal makes
/// sure of it if the test fails first.
/// </remarks>
internal sealed class ImageBackup : IDisposable
{
    private readonly List<string> _standIns = [];
    private readonly HashSet<string> _imagesBefore;
    private bool _restored;

    private ImageBackup(string image, string backupRepository)
    {
        Image = image;
        Backup = $"{backupRepository}:smoke-backup-{Guid.NewGuid():N}";
        if (FindId(Backup) != null)
            throw new InvalidOperationException($"{Backup} already exists");
        _imagesBefore = [.. Docker.ReadLines("image", "ls", "-aq", "--no-trunc")];
        OriginalId = FindId(image) ?? throw new InvalidOperationException($"{image} doesn't exist");
        Tag(image, Backup);
    }

    /// <summary>Gets the image that's backed up, e.g. pen:latest.</summary>
    public string Image { get; }

    /// <summary>Gets the backup tag, e.g. pen:smoke-backup-0123….</summary>
    public string Backup { get; }

    /// <summary>Gets the image's ID when it was backed up.</summary>
    public string OriginalId { get; }

    /// <summary>Tags <paramref name="image"/> under a new backup tag too (docker image tag).</summary>
    /// <param name="image">The image to keep.</param>
    /// <param name="backupRepository">The repository of the backup tag, e.g. pen.</param>
    /// <returns>The backup, which puts the image back when it's restored or disposed.</returns>
    /// <exception cref="InvalidOperationException">The image doesn't exist, or Docker couldn't tag it.</exception>
    public static ImageBackup Create(string image, string backupRepository) => new(image, backupRepository);

    /// <summary>Has restoring also remove an image that a test built in the original's place.</summary>
    /// <param name="id">The image's full ID (sha256:…).</param>
    public void RemoveWithRestore(string id) => _standIns.Add(id);

    /// <summary>Puts the original back under its tag, then removes the backup tag and the stand-ins.</summary>
    /// <exception cref="InvalidOperationException">Docker couldn't tag the backup back.</exception>
    public void Restore()
    {
        if (_restored)
            return;
        Tag(Backup, Image);
        _restored = true;
        Docker.Run("image", "rm", Backup);
        // IDs are full (sha256:…), as docker build -q prints them.
        var untagged = Docker.ReadLines("image", "ls", "-q", "--no-trunc", "--filter", "dangling=true");
        var standIns = _standIns.Distinct()
            .Where(id => id != OriginalId && !_imagesBefore.Contains(id) && untagged.Contains(id));
        Docker.RemoveImages(standIns);
    }

    /// <summary>Restores the original, if that hasn't happened yet.</summary>
    public void Dispose() => Restore();

    /// <summary>Gets an image's full ID.</summary>
    /// <param name="image">The image's name or ID.</param>
    /// <returns>The ID, or null if there's no such image.</returns>
    private static string? FindId(string image)
    {
        var inspected = Docker.Run("image", "inspect", "--format", "{{.Id}}", image);
        return inspected.ExitCode == 0 ? inspected.Stdout.Trim() : null;
    }

    private static void Tag(string source, string target)
    {
        var tagged = Docker.Run("image", "tag", source, target);
        if (tagged.ExitCode != 0)
            throw new InvalidOperationException($"docker image tag {source} {target} failed: {tagged.Output}");
    }
}
