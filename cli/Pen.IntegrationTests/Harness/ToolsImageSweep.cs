namespace Pen.IntegrationTests.Harness;

/// <summary>Removes, once the whole run is over, every pen-tools image that wasn't there when it started.</summary>
/// <remarks>
/// It's the assembly fixture (AssemblyInfo.cs) rather than part of <see cref="IntegrationFixture"/>, so the tools
/// images that sessions build (pen-test, the agents, dotnet) are built once per run and every later class
/// reuses them, as they would be on a user's machine.
/// </remarks>
public sealed class ToolsImageSweep : IAsyncLifetime
{
    private IReadOnlyList<string>? _before;

    /// <summary>Lists the pen-tools images there before any test runs.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask InitializeAsync()
    {
        _before = Docker.ListImages("pen-tools");
        return ValueTask.CompletedTask;
    }

    /// <summary>Removes the pen-tools images the run made.</summary>
    /// <returns>A completed task.</returns>
    public ValueTask DisposeAsync()
    {
        // Only with a listing from before: without one, every pen-tools image would look new.
        if (_before is { } before)
            Docker.RemoveImages(Docker.ListImages("pen-tools").Except(before));
        _before = null;
        return ValueTask.CompletedTask;
    }
}
