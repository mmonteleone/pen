using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// One scan of a git dir for the files and state directories to quarantine because a shell prompt would run them,
/// which keeps what it finds as it goes.
/// </summary>
/// <param name="gitDir">The git dir; its worktree entries and its module repos and theirs are scanned too.</param>
/// <param name="tryOpen">Opens a worktrees dir or entry that can't be looked in, or null to change no modes.</param>
/// <param name="atStart">
/// What the session saved at its start, at a session's end; null to leave every config.worktree alone.
/// </param>
/// <remarks>
/// <para>
/// It looks in the same git dirs as <see cref="PausedGit.Find"/>. A real rebase's own files always pass: see
/// <see cref="PausedGit.IsCount"/> and <see cref="PausedGit.IsHeadName"/>. A planted symlink counts whatever it
/// points to, and a state directory Pen can't enter counts too, since the prompt reads it once the user fixes its
/// mode. Every known name is tried directly, so a directory that can be searched but not listed (git and the prompt
/// can read through one too) hides nothing. A worktree entry or worktrees dir still closed after
/// <paramref name="tryOpen"/> goes in <see cref="UnreachableDirs"/>: what's in it can't be vouched for.
/// </para>
/// <para>
/// With <paramref name="atStart"/> (a session's end), so does each config.worktree it doesn't vouch for, wherever git
/// would read one. git takes settings from it that run programs, as soon as anything runs git in that worktree, the
/// prompt included. Whether extensions.worktreeConfig is on now doesn't matter: one left there takes effect whenever
/// it's turned on. So does a symlink in place of worktrees/ or of an entry in it, which git follows to a
/// config.worktree nothing checks, and which was refused at the start so can't have been there then.
/// </para>
/// </remarks>
internal sealed class PromptHazardScan(string gitDir, Func<string, bool>? tryOpen, ProtectedManifest? atStart)
{
    private const string Prompt = "git's shell prompt (git-prompt.sh) can run what's in it, with no git command typed";

    private readonly List<PromptHazard> _hazards = [];
    private readonly List<string> _unreachableDirs = [];

    /// <summary>Gets what to quarantine, each with why, in the order found.</summary>
    public IReadOnlyList<PromptHazard> Hazards => _hazards;

    /// <summary>Gets the worktrees dirs and entries it couldn't look in, in the order found.</summary>
    public IReadOnlyList<string> UnreachableDirs => _unreachableDirs;

    /// <summary>Scans the git dir. Call it once per instance.</summary>
    /// <remarks>
    /// If it throws, what it found before stays in <see cref="Hazards"/> and <see cref="UnreachableDirs"/>.
    /// </remarks>
    public void Run() => GitDirWalk.Visit(gitDir, Visit, AddUnreachable, tryOpen);

    /// <summary>Looks for hazards in one git dir or worktree entry.</summary>
    private void Visit(string dir)
    {
        LookInState(Path.Combine(dir, "rebase-merge"), ["msgnum", "end"]);
        LookInState(Path.Combine(dir, "rebase-apply"), ["next", "last"]);
        if (atStart is null)
            return;
        var config = Path.Combine(dir, "config.worktree");
        if (RepoGuard.Exists(config) && !atStart.Vouches(config))
        {
            _hazards.Add(new(config, null, "a config.worktree that wasn't there at the session's start, or that " +
                "another session could have made: git reads it as config, so a setting in it can run code on " +
                "this machine. git worktree add copies one into a new worktree, so it may be harmless"));
        }
    }

    /// <summary>Records a worktrees dir or entry it couldn't look in.</summary>
    private void AddUnreachable(string path)
    {
        if (!RepoGuard.IsLink(path))
        {
            _unreachableDirs.Add(path); // git won't follow a symlink here to a project's worktree: a warning covers it
        }
        else if (atStart is not null)
        {
            _hazards.Add(new(path, null, "a symlink where git keeps a worktree's " +
                "files: it points git at a config.worktree in another directory"));
        }
    }

    /// <summary>Looks in a paused rebase's state directory for what the prompt would expand.</summary>
    /// <param name="stateDir">The state directory.</param>
    /// <param name="counts">The names in it that git only writes a number in.</param>
    private void LookInState(string stateDir, string[] counts)
    {
        if (RepoGuard.IsLink(stateDir))
        {
            _hazards.Add(new(stateDir, null, $"a symlink where git keeps a paused rebase: {Prompt}"));
            return;
        }
        if (!Directory.Exists(stateDir))
            return;
        if (!Native.CanEnter(stateDir))
        {
            _hazards.Add(new(stateDir, null, "a paused rebase's directory that can't be " +
                $"read, so pen can't check it: {Prompt}, once its permissions are fixed"));
            return;
        }
        foreach (var name in counts)
        {
            var path = Path.Combine(stateDir, name);
            if (!HoldsWhatGitWrites(path, PausedGit.IsCount))
                _hazards.Add(new(path, stateDir, $"git only writes a number here: {Prompt}"));
        }
        var head = Path.Combine(stateDir, "head-name");
        if (!HoldsWhatGitWrites(head, PausedGit.IsHeadName))
            _hazards.Add(new(head, stateDir, $"git only writes a ref name here: {Prompt}"));
    }

    /// <summary>Tells whether a state file is missing or holds what git writes.</summary>
    /// <returns>
    /// True if it's missing or <paramref name="isValid"/> holds for it; false for one that can't be vouched for.
    /// </returns>
    private static bool HoldsWhatGitWrites(string path, Func<byte[], bool> isValid)
    {
        try
        {
            if (RepoGuard.IsLink(path))
                return false;
            var bytes = Native.ReadProtected(path)?.Bytes;
            return bytes is not null && isValid(bytes);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

/// <summary>A file or state directory a shell prompt would run.</summary>
/// <param name="Path">The file or directory.</param>
/// <param name="StateDir">
/// The state directory holding it, if it's a file: the directory can go aside in its place.
/// </param>
/// <param name="Why">Why it's quarantined.</param>
internal sealed record PromptHazard(string Path, string? StateDir, string Why);
