using Pen.GitProtection;
using Pen.Masking;
using Pen.Proxy;
using Pen.Tools;

namespace Pen.Sessions;

/// <summary>What a session's preparation found and built for it, which its files are written from.</summary>
internal sealed class Preparation
{
    /// <summary>Gets the session's tools, merged and canonical.</summary>
    public required IReadOnlyList<ToolSpec> Tools { get; init; }

    /// <summary>Gets the git-protection mounts over the project mount.</summary>
    public required IReadOnlyList<Mount> Mounts { get; init; }

    /// <summary>Gets what the session hides and replaces, mounted after <see cref="Mounts"/>.</summary>
    public required MaskPlan Masks { get; init; }

    /// <summary>
    /// Gets the submodule repos in the common git dir now, split by what the open sessions listed
    /// (<see cref="ModuleBaseline"/>).
    /// </summary>
    public required ModuleChoice Modules { get; init; }

    /// <summary>Gets the image the agent runs.</summary>
    public required string Image { get; init; }

    /// <summary>Gets whether Docker is rootless.</summary>
    public required bool Rootless { get; init; }

    /// <summary>Gets the GitHub token for the project's owner, if there's one.</summary>
    public required string? Token { get; init; }

    /// <summary>Gets what the session's proxy allows and denies, and the domains its tools bring.</summary>
    public required EgressPolicy Egress { get; init; }
}
