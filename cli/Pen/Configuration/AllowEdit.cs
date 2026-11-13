namespace Pen.Configuration;

/// <summary>A config.toml's text after <see cref="ConfigEdit.AddAllowed"/>, and the domains it added.</summary>
/// <param name="Text">The edited text, or the original text itself if nothing needed adding.</param>
/// <param name="Added">The domains that weren't allowed yet, lowercased, in order.</param>
internal sealed record AllowEdit(string Text, IReadOnlyList<string> Added);
