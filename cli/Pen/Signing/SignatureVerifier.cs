using Pen.Platform;

namespace Pen.Signing;

/// <summary>Finds which commits carry a signature that verifies under the user's global git settings.</summary>
/// <param name="git">The repo's git, without verification settings: they're added from the global config.</param>
/// <remarks>
/// A gpgsig header proves nothing, since the agent can write any header, and neither does a verification the
/// repo's config had a say in. Only %G? = G counts: U is a good signature from a key the user doesn't trust.
/// </remarks>
internal sealed class SignatureVerifier(HostGit git)
{
    private static readonly string[] TrustLevels = ["undefined", "never", "marginal", "fully", "ultimate"];

    /// <summary>Finds the commits whose signature verifies under the user's global settings.</summary>
    /// <returns>The verified commits' hashes; empty when none verifies or none was given.</returns>
    /// <exception cref="PenException">A commit can't be read at all.</exception>
    /// <remarks>
    /// A forged signature can make git fail, so a failed read of a commit that can be read otherwise just leaves it
    /// unverified. One that can't be read at all is an error.
    /// </remarks>
    public HashSet<string> FindVerified(IReadOnlyList<string> commits)
    {
        var verified = new HashSet<string>(StringComparer.Ordinal);
        if (commits.Count == 0)
            return verified;
        var verifying = git.WithConfig(SignatureConfig.BuildForVerifying(git));
        var lines = ReadSignatureLines(verifying, commits);

        // %G? leaves gpg.minTrustLevel to verify-commit, so it's applied here.
        var minTrust = RankTrust(SignatureConfig.ReadMinTrust(git));
        var wanted = commits.ToHashSet(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var fields = line.Split(' ');
            if (fields is [var sha, "G", var trust] && wanted.Contains(sha)
                && minTrust >= 0 && RankTrust(trust) >= minTrust)
            {
                verified.Add(sha);
            }
        }
        return verified;
    }

    /// <summary>Counts the commits whose signature doesn't verify under the user's global settings.</summary>
    /// <exception cref="PenException">A commit can't be read at all.</exception>
    public int CountUnsigned(IReadOnlyList<string> commits) => commits.Count - FindVerified(commits).Count;

    /// <summary>Formats advice on fixing the user's setup when commits Pen just signed don't verify.</summary>
    /// <param name="created">The commits just signed.</param>
    /// <returns>The warning and how to fix it, or null when they all verify or checking them fails.</returns>
    /// <remarks>
    /// Until it's fixed, each pen sign re-signs them. It never throws: the signing is done by now, and this is
    /// advice.
    /// </remarks>
    public string? FormatHint(IReadOnlyList<string> created)
    {
        try
        {
            if (FindVerified(created).Count == created.Count)
                return null;
            return FormatAdvice();
        }
        catch (PenException)
        {
            return null;
        }
    }

    /// <summary>Reads each commit's %H %G? %GT line with the verification settings.</summary>
    /// <param name="verifying">The repo's git with the verification settings added.</param>
    /// <param name="commits">The commits to read.</param>
    /// <returns>The lines git gave, for the commits it could read.</returns>
    /// <exception cref="PenException">A commit can't be read even without its signature.</exception>
    private List<string> ReadSignatureLines(HostGit verifying, IReadOnlyList<string> commits)
    {
        var whole = ReadSignatures(verifying, commits);
        if (whole.Ok)
            return whole.Lines.ToList();

        // Some commit killed that read: ssh-keygen giving up on a forged
        // signature before reading the message (git takes the SIGPIPE), or
        // git refusing a signature header. Its output could be cut short.
        var lines = new List<string>();
        foreach (var commit in commits)
        {
            var one = ReadSignatures(verifying, [commit]);
            if (one.Ok)
            {
                lines.AddRange(one.Lines);
                continue;
            }
            var read = git.Run("cat-file", "commit", commit);
            if (!read.Ok)
                throw new PenException($"can't read commit {commit[..12]}: {read.Stderr.Trim()}");
        }
        return lines;
    }

    /// <summary>Runs one <c>git log</c> that prints %H %G? %GT for each of <paramref name="commits"/>.</summary>
    private static GitResult ReadSignatures(HostGit verifying, IReadOnlyList<string> commits) => verifying.Run(
        [.. HostGit.LogEachArgs, "--format=%H %G? %GT"],
        HostGit.EncodeLines(commits));

    /// <summary>Ranks a gpg trust level, case-insensitively.</summary>
    /// <returns>Its rank from 0 (undefined) to 4 (ultimate), or -1 for a level that isn't one of them.</returns>
    private static int RankTrust(string level) => Array.IndexOf(TrustLevels, level.ToLowerInvariant());

    /// <summary>Formats the warning that the new signatures don't verify, with the fix for the user's setup.</summary>
    private string FormatAdvice()
    {
        var warning = "warning: git can't verify the signatures just made, so " +
            "each pen sign will re-sign these commits and change their hashes.\n";
        if (git.ReadGlobalValue("gpg.format") != "ssh")
            return warning + "  Check that your key is in your keyring, and that you trust it.";

        var signers = git.ReadGlobalValue("gpg.ssh.allowedSignersFile");
        var addKey = $"echo \"{FormatEmailForHint()} {FormatPublicKeyForHint()}\" >> ";
        if (SignatureConfig.KeepIfOutsideTheRepo(signers) != null)
        {
            return warning + $"  Your key may not be listed in \"{Out.Field(signers!)}\". To add " +
                $"it:\n    {addKey}\"$(git config --global --type=path gpg.ssh.allowedSignersFile)\"";
        }

        var reason = signers == null
            ? "gpg.ssh.allowedSignersFile isn't set in your global git config"
            : $"gpg.ssh.allowedSignersFile (\"{Out.Field(signers)}\") is a relative " +
                "path, which pen ignores since the project could write to it";
        return warning
            + $"  {reason}. To set it up:\n"
            + "    mkdir -p ~/.config/git\n"
            + $"    {addKey}~/.config/git/allowed_signers\n"
            + "    git config --global gpg.ssh.allowedSignersFile ~/.config/git/allowed_signers";
    }

    /// <summary>Formats shell text that gives the user's email in an allowed-signers line.</summary>
    private string FormatEmailForHint() =>
        git.ReadGlobalValue("user.email") != null ? "$(git config --global user.email)" : "you@example.com";

    /// <summary>
    /// Formats shell text that gives the public key of the configured signing key, which can be given in several
    /// forms.
    /// </summary>
    private string FormatPublicKeyForHint()
    {
        const string keyPath = "\"$(git config --global --type=path user.signingkey)\"";
        return git.ReadGlobalValue("user.signingkey") switch
        {
            null => "<your public key>", // it comes from gpg.ssh.defaultKeyCommand
            var key when key.StartsWith("key::", StringComparison.Ordinal) =>
                "$(git config --global user.signingkey | cut -c6-)",
            var key when key.StartsWith("ssh-", StringComparison.Ordinal) => "$(git config --global user.signingkey)",
            var key when key.EndsWith(".pub", StringComparison.Ordinal) => $"$(cat {keyPath})",
            _ => $"$(ssh-keygen -y -f {keyPath})",
        };
    }
}
