namespace Pen.GitProtection;

/// <summary>One rename the check made, or one it couldn't make.</summary>
/// <param name="From">What was moved.</param>
/// <param name="To">Where it went, or empty if it couldn't be moved (or restored).</param>
/// <param name="Why">Why it was moved, and if it couldn't be, why not.</param>
/// <param name="Inner">A second rename inside the moved item (a .git directory's HEAD), if any.</param>
internal sealed record Quarantined(string From, string To, string Why, (string From, string To)? Inner = null);
