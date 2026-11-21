using Pen.TestSupport;

namespace Pen.IntegrationTests.Harness;

/// <summary>A test class's own throwaway project and HOME, with a first session that fixes the project key.</summary>
/// <remarks>
/// Each class that uses it as a class fixture gets its own instance, so nothing depends on another class's
/// leftovers. A fixture that batches an expensive session derives from this and runs it in <see cref="Prepare"/>;
/// the class's facts then assert on the stored result. Anything else it makes there that must not outlive it,
/// such as a background command or an image other than a pen-tools one, it hands to <see cref="Track"/> or
/// <see cref="TrackImage"/>. Cleanup disposes those, then removes the images, the projects' state under both
/// HOMEs and the scratch directory. Every step runs even if an earlier one fails, and an error in cleanup never
/// hides the one that failed the setup. The pen-tools images sessions build are left for later classes:
/// <see cref="ToolsImageSweep"/> removes them at the end of the run.
/// </remarks>
public class IntegrationFixture : IAsyncLifetime
{
    private readonly List<IntegrationProject> _projects = [];
    private readonly List<IDisposable> _tracked = [];
    private readonly List<string> _images = [];
    private IReadOnlySet<string>? _imagesBefore;
    private TempDir? _scratch;

    /// <summary>Gets a scratch directory outside the project as a real path.</summary>
    internal string Scratch => _scratch?.Path ?? throw NotInitialized();

    /// <summary>Gets the throwaway HOME, in <see cref="Scratch"/>/home.</summary>
    internal FakeHome Home { get; private set; } = null!;

    /// <summary>Gets the throwaway repo under the real HOME.</summary>
    internal IntegrationProject Project { get; private set; } = null!;

    /// <summary>Gets pen with the throwaway HOME, in the project.</summary>
    internal PenCli Cli { get; private set; } = null!;

    /// <summary>Gets the project key, fixed by the first session.</summary>
    internal string Key => Project.Key;

    /// <summary>Gets the project's runtime dir under the throwaway HOME.</summary>
    internal string RuntimeDir => Home.GetRuntimeDir(Key);

    /// <summary>Creates the HOME and project, runs the first session, then <see cref="Prepare"/>.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        try
        {
            _imagesBefore = new HashSet<string>(Docker.ReadLines("image", "ls", "-aq", "--no-trunc"));
            _scratch = new TempDir("pen-integration.");
            Home = new FakeHome(Path.Combine(_scratch.Path, "home"));
            Project = CreateProject();
            Cli = PenCli.CreateFor(Home, Project.Dir);
            StartFirstSession();
            Prepare();
        }
        catch
        {
            // The setup's error is the one to report; cleanup's own errors would only hide it.
            CleanUp();
            throw;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes everything the fixture and its tests made.</summary>
    /// <returns>A completed task.</returns>
    /// <exception cref="Exception">The first error a cleanup step threw, once every step has run.</exception>
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        CleanUp().ThrowIfFailed();
        return ValueTask.CompletedTask;
    }

    /// <summary>Creates another throwaway repo under the real HOME, removed with the fixture.</summary>
    /// <returns>The project.</returns>
    internal IntegrationProject CreateProject() => TrackProject(IntegrationProject.CreateRepo());

    /// <summary>Creates a throwaway directory, not a repo, under the real HOME, removed with the fixture.</summary>
    /// <param name="prefix">The start of its name, e.g. .pen-integration-norepo.</param>
    /// <returns>The project.</returns>
    internal IntegrationProject CreateEmptyProject(string prefix) =>
        TrackProject(IntegrationProject.CreateEmpty(prefix));

    /// <summary>Creates a directory, not a repo, in <see cref="Scratch"/>, whose projects' state is removed with the
    /// fixture.</summary>
    /// <param name="name">Its name in <see cref="Scratch"/>.</param>
    /// <returns>The project.</returns>
    internal IntegrationProject CreateScratchProject(string name) =>
        TrackProject(IntegrationProject.CreateAt(Path.Combine(Scratch, name)));

    /// <summary>Has cleanup dispose something, e.g. a background <see cref="RunningCommand"/> or a disposable that
    /// puts back an image a fixture re-tagged, before the projects are removed. Later ones are disposed
    /// first.</summary>
    /// <typeparam name="T">Its type.</typeparam>
    /// <param name="disposable">The thing to dispose.</param>
    /// <returns><paramref name="disposable"/>.</returns>
    internal T Track<T>(T disposable)
        where T : IDisposable
    {
        _tracked.Add(disposable);
        return disposable;
    }

    /// <summary>Has cleanup remove an image (docker image rm -f), before the projects are removed.</summary>
    /// <param name="image">The image's repository:tag or ID.</param>
    internal void TrackImage(string image) => _images.Add(image);

    /// <summary>Runs the class's batched sessions once the first session has fixed the key.</summary>
    protected virtual void Prepare()
    {
    }

    private IntegrationProject TrackProject(IntegrationProject project)
    {
        project.CleanUpUnder(Home.Path);
        _projects.Add(project);
        return project;
    }

    private void StartFirstSession()
    {
        // It builds pen:latest if it's missing or stale, and fixes the project key.
        var first = Cli.Run("run", "true");
        if (Project.FindRuntimeDirs(Home.Path).Count == 0)
            throw new InvalidOperationException($"the first session made no runtime dir: {first.Describe()}");
        Project.ResolveKey(Home.Path);
    }

    private Cleanup CleanUp()
    {
        var cleanup = new Cleanup();
        foreach (var disposable in Enumerable.Reverse(_tracked))
            cleanup.Run(disposable.Dispose);
        _tracked.Clear();
        cleanup.Run(() => Docker.RemoveImages(_images));
        _images.Clear();
        List<string> keys = [];
        foreach (var project in _projects)
            keys.AddRange(project.FindKeys(cleanup));
        foreach (var project in Enumerable.Reverse(_projects))
            cleanup.Run(project.Dispose);
        _projects.Clear();
        cleanup.Run(() => RemoveReplacedLayers(keys.Distinct()));
        if (_scratch is { } scratch)
        {
            cleanup.Run(() => Files.DeleteTree(scratch.Path));
            cleanup.Run(scratch.Dispose);
            _scratch = null;
        }
        return cleanup;
    }

    /// <summary>Removes the untagged project layers of the keys that weren't there when the fixture started.</summary>
    /// <remarks>
    /// A session rebuilds the project layer every time and only moves its tag, so each replaced layer is left
    /// untagged, labeled pen.project=&lt;key&gt;. Without a listing from before, every one would look new.
    /// </remarks>
    /// <param name="keys">The project keys.</param>
    private void RemoveReplacedLayers(IEnumerable<string> keys)
    {
        if (_imagesBefore is not { } before)
            return;
        foreach (var key in keys)
        {
            var layers = Docker.ReadLines(
                "image", "ls", "-q", "--no-trunc", "--filter", "dangling=true", "--filter", $"label=pen.project={key}");
            Docker.RemoveImages(layers.Distinct().Where(id => !before.Contains(id)));
        }
    }

    private static InvalidOperationException NotInitialized() => new("the fixture isn't initialized");
}
