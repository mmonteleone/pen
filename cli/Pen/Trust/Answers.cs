using Pen.Platform;

namespace Pen.Trust;

/// <summary>Where a yes or no comes from, and whether a person can be there to give it.</summary>
/// <param name="Input">Where the answer is read from.</param>
/// <param name="IsTerminal">Whether that's a terminal, so a person can answer.</param>
internal sealed record Answers(TextReader Input, bool IsTerminal)
{
    /// <summary>Gets this process's stdin, and whether it's a terminal.</summary>
    public static Answers FromProcess() => new(Console.In, Native.IsATty(0));
}
