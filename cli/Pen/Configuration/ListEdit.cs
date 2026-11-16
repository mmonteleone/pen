namespace Pen.Configuration;

/// <summary>A config.toml's text after <see cref="ConfigEdit.AddToList"/>, and the domains it added.</summary>
/// <param name="Text">The edited text, or the original text itself if nothing needed adding.</param>
/// <param name="Added">The domains that weren't in the list yet, lowercased, in order.</param>
internal sealed record ListEdit(string Text, IReadOnlyList<string> Added);
