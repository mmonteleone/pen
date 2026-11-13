using Pen.Platform;

namespace Pen.Trust;

/// <summary>Where a yes or no comes from, and whether a person can be there to give it.</summary>
internal sealed record Answers(TextReader Input, bool IsTerminal)
{
    public static Answers FromProcess() => new(Console.In, Native.IsATty(0));
}
