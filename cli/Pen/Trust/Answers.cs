namespace Pen.Trust;

/// <summary>Where a yes or no comes from, and whether a person can be there to give it.</summary>
/// <param name="Input">Where the answer is read from.</param>
/// <param name="IsTerminal">Whether that's a terminal, so a person can answer.</param>
internal sealed record Answers(TextReader Input, bool IsTerminal);
