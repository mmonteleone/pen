namespace Pen.GitProtection;

/// <summary>One rename the check made. Inner is a second rename inside the moved item, if any.</summary>
internal sealed record Quarantined(string From, string To, string Why, (string From, string To)? Inner = null);
