using System.Text;

namespace Pen.Commands;

/// <summary>A commit header field that isn't valid UTF-8, so it can't be passed to git unchanged.</summary>
internal sealed class NotUtf8Exception(string what) : Exception(what)
{
    public string What { get; } = what;
}
