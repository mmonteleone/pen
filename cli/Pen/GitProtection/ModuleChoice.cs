namespace Pen.GitProtection;

/// <summary>
/// The module repos in the common git dir that a starting session found, split by <see cref="ModuleBaseline"/>.
/// </summary>
/// <param name="Listed">
/// The ones every open session listed too, which the session-end check takes as the submodules' own.
/// </param>
/// <param name="Unlisted">
/// The others, which an open session's agent could have made: nothing in them is vouched for, and the session-end
/// check quarantines them.
/// </param>
internal sealed record ModuleChoice(IReadOnlyList<string> Listed, IReadOnlyList<string> Unlisted)
{
    /// <summary>Gets the choice outside a repo, where there are none.</summary>
    public static ModuleChoice None { get; } = new([], []);
}
