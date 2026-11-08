namespace Pen.Tests;

/// <summary>Use only in the nonparallel process-test collection: PATH is process-wide.</summary>
internal sealed class FakeDocker : IDisposable
{
    private readonly string? _path = Environment.GetEnvironmentVariable("PATH");

    public FakeDocker(TempDir temp, string body)
    {
        var bin = temp.Sub("bin");
        var script = Path.Combine(bin, "docker");
        File.WriteAllText(script, "#!/bin/sh\nset -eu\ncd -- \"$(dirname -- \"$0\")/..\"\nprintf '%s\\n' \"$*\" >> calls\n" + body);
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Environment.SetEnvironmentVariable("PATH", bin + ":" + _path);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("PATH", _path);
}
