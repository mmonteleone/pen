namespace Pen.IntegrationTests.Harness;

/// <summary>Removes the images a test's builds left untagged when a later build took their tag, such as an earlier
/// project layer once &lt;key&gt;:latest is rebuilt.</summary>
/// <remarks>
/// Only images noted with <see cref="Note"/> are removed, and only those that are untagged when it's disposed and
/// weren't there when it was created: a build can give the same ID as an identical earlier one.
/// </remarks>
internal sealed class ReplacedImages : IDisposable
{
    private readonly HashSet<string> _imagesBefore = [.. Docker.ReadLines("image", "ls", "-aq", "--no-trunc")];
    private readonly List<string> _noted = [];

    /// <summary>Notes the image a tag points at now, to be removed if a later build leaves it untagged.</summary>
    /// <param name="image">The image's repository:tag.</param>
    public void Note(string image)
    {
        var inspected = Docker.Run("image", "inspect", "--format", "{{.Id}}", image);
        if (inspected.ExitCode == 0)
            _noted.Add(inspected.Stdout.Trim());
    }

    /// <summary>Removes the noted images that are untagged now and are new.</summary>
    public void Dispose()
    {
        var untagged = Docker.ReadLines("image", "ls", "-q", "--no-trunc", "--filter", "dangling=true");
        Docker.RemoveImages(_noted.Distinct().Where(id => !_imagesBefore.Contains(id) && untagged.Contains(id)));
    }
}
