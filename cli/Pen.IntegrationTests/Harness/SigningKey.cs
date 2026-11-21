using System.Collections.Immutable;

namespace Pen.IntegrationTests.Harness;

/// <summary>An SSH signing key made with ssh-keygen in a test's scratch directory.</summary>
/// <remarks>
/// It has no passphrase and lives only in the scratch directory, so signing with it needs no agent. Commands that
/// sign run without SSH_AUTH_SOCK (<see cref="WithoutAgent"/>), so ssh-keygen never asks the user's agent whether
/// it holds the key.
/// </remarks>
internal sealed class SigningKey
{
    private SigningKey(string path) => Path = path;

    /// <summary>Gets the environment change that keeps a signing command away from the user's SSH agent.</summary>
    public static ImmutableDictionary<string, string?> WithoutAgent { get; } =
        ImmutableDictionary<string, string?>.Empty.Add("SSH_AUTH_SOCK", null);

    /// <summary>Keeps a pen away from the user's SSH agent.</summary>
    /// <param name="pen">The pen.</param>
    /// <returns>A pen that runs without SSH_AUTH_SOCK.</returns>
    public static PenCli WithoutAgentFor(PenCli pen) => pen.WithEnvironment("SSH_AUTH_SOCK", null);

    /// <summary>Gets the private key's path; the public key is beside it, with .pub added.</summary>
    public string Path { get; }

    /// <summary>Creates an ed25519 key without a passphrase (ssh-keygen -q -t ed25519 -N '' -C comment -f
    /// path).</summary>
    /// <param name="path">The private key's path, which mustn't exist yet.</param>
    /// <param name="comment">The key's comment.</param>
    /// <returns>The key.</returns>
    /// <exception cref="InvalidOperationException">ssh-keygen failed.</exception>
    public static SigningKey Create(string path, string comment)
    {
        new HostCommand("ssh-keygen", ["-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", path]).Run()
            .RequireSuccess();
        return new SigningKey(path);
    }

    /// <summary>Writes an allowed signers file that trusts this key for one principal, beside the key with .signers
    /// added, as echo "principal $(cat key.pub)" &gt;key.signers does.</summary>
    /// <param name="principal">The principal, e.g. the committer's email.</param>
    /// <returns>The file's path.</returns>
    public string WriteAllowedSigners(string principal)
    {
        var signers = Path + ".signers";
        var publicKey = File.ReadAllText(Path + ".pub").TrimEnd('\n');
        File.WriteAllText(signers, $"{principal} {publicKey}\n");
        return signers;
    }
}
