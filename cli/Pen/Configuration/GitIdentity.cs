using Pen.GitProtection;

namespace Pen.Configuration;

/// <summary>A git user.name and user.email, either of which may be unknown.</summary>
/// <param name="Name">The user.name, or null if it isn't set.</param>
/// <param name="Email">The user.email, or null if it isn't set.</param>
internal sealed record GitIdentity(string? Name, string? Email)
{
    /// <summary>Reads the identity from the host's global git config.</summary>
    public static GitIdentity ReadFromHost() =>
        new(HostGit.Global.ReadGlobalValue("user.name"), HostGit.Global.ReadGlobalValue("user.email"));
}
