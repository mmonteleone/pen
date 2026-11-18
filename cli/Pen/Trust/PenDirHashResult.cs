namespace Pen.Trust;

/// <summary>The hash of .pen/, or why there can't be one.</summary>
internal abstract record PenDirHashResult
{
    private PenDirHashResult()
    {
    }

    /// <summary>The hash of .pen/.</summary>
    internal sealed record Hashed(string Hash) : PenDirHashResult;

    /// <summary>Why .pen/ has no hash; then nothing can match an approval.</summary>
    internal sealed record NoHash(string Problem) : PenDirHashResult;
}
