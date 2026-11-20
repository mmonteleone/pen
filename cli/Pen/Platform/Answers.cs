namespace Pen.Platform;

/// <summary>Where a yes or no comes from, and whether that's a terminal, so a person can be there to give it.</summary>
internal sealed record Answers(TextReader Input, bool IsTerminal);
