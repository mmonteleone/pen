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

    /// <summary>
    /// A body's first lines, answering the proxy reads of pen allow and pen deny from the session dir's own ACL files
    /// (as an up-to-date bind mount would) by running the real script with its cat redirected there. Other calls go
    /// on to the rest of the body.
    /// </summary>
    public const string ServingAclFiles = """
        case "$*" in *" exec -T proxy sh -c "*" /etc/squid/"*)
          dir=
          while [ "$1" != exec ]; do
            if [ "$1" = -f ] && [ -z "$dir" ]; then dir=$(dirname -- "$2"); fi
            shift
          done
          shift 5
          script=$1
          shift 2
          PROXY_ACL_DIR=$dir exec sh -c 'cat() { command cat -- "$PROXY_ACL_DIR/${2#/etc/squid/}"; }; '"$script" sh "$@"
          ;;
        esac

        """;

    public void Dispose() => Environment.SetEnvironmentVariable("PATH", _path);
}
