namespace Pen.GitProtection;

/// <summary>The exception thrown when a commit header field isn't valid UTF-8.</summary>
/// <param name="what">The field, such as "author name".</param>
/// <remarks>Such a field can't be passed to git unchanged, so the commit can't be re-created exactly.</remarks>
internal sealed class NotUtf8Exception(string what) : Exception(what)
{
    /// <summary>The field that isn't valid UTF-8, such as "author name".</summary>
    public string What { get; } = what;
}
