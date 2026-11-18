using Pen.Platform;
using Pen.Sessions;

namespace Pen.GitProtection;

/// <summary>
/// Chooses the module repos a starting session takes as the submodules' own: those found now that every open session
/// of the repo listed at its start too.
/// </summary>
/// <remarks>
/// <para>
/// An open session's agent can make a module repo in the common git dir, config and hooks included. The planting
/// session's end quarantines it, but a session started meanwhile that took it as its own would keep it at its end,
/// and write its saved config back, after the agent of either has put it there again. So a repo an open session
/// didn't list is left out here, and this session's end quarantines it like one made during the session. Each list
/// was narrowed the same way when its session started, so a plant can't become a baseline through a chain of
/// sessions.
/// </para>
/// <para>
/// A session whose Pen died is open until the next run ends it, and its agent may still be running, so it counts.
/// One that's open with no list to go by (started by an older Pen, or not in this repo) refuses the start.
/// Like the rest of the check, this reads files directly and never runs git.
/// </para>
/// </remarks>
internal static class ModuleBaseline
{
    /// <summary>
    /// Splits the module repos in the starting session's <paramref name="commonGitDir"/> now by the lists
    /// <paramref name="open"/> sessions made at their start.
    /// </summary>
    /// <exception cref="PenException">An open session has no list to go by.</exception>
    /// <remarks>
    /// The open sessions (<see cref="SessionRegistry.FindOpen"/>) are found just before this is called, and their
    /// lists are read before the scan, so a repo it finds that an open session's agent could have made is missing
    /// from that session's list.
    /// </remarks>
    public static ModuleChoice ChooseAtStart(IReadOnlyList<SessionDir> open, string commonGitDir, string dir)
    {
        var lists = ReadOpenLists(open, commonGitDir, dir);
        var found = RepoScan.FindModuleRepos(commonGitDir);
        var listed = Choose(found, lists);
        return new(listed, [.. found.Except(listed, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Saves the starting session's protected files (<see cref="ProtectedFiles.Save"/>) with the sessions open now
    /// as their baselines, having narrowed the chosen module repos to what those sessions listed, then records the
    /// common git dir and the narrowed list for the session-end check and later sessions.
    /// </summary>
    /// <exception cref="PenException">An open session has no list to go by, or a file can't be saved.</exception>
    /// <remarks>
    /// A session that opened after <see cref="ChooseAtStart"/>'s sessions were found but before its scan could have
    /// made a repo the scan found, so the lists are read again, from sessions found again. The same sessions give
    /// the protected files their baselines, and the repos left out here are unlisted for those too.
    /// </remarks>
    public static ModuleChoice NarrowAndSave(BaselineRequest request)
    {
        var (session, mounts, chosen, open, commonGitDir, dir) = request;
        var narrowed = chosen;
        if (commonGitDir is not null)
        {
            var listed = Choose(chosen.Listed, ReadOpenLists(open, commonGitDir, dir));
            narrowed = new(listed, [.. chosen.Unlisted, .. chosen.Listed.Except(listed, StringComparer.Ordinal)]);
        }
        ProtectedFiles.Save(session, mounts, dir, open, narrowed.Unlisted);
        if (commonGitDir is not null)
        {
            File.WriteAllText(session.StartedWithGit, commonGitDir + "\n");
            session.WriteModules(narrowed.Listed);
        }
        return narrowed;
    }

    /// <summary>
    /// Reads the module repos each of <paramref name="open"/> sessions listed at its start, one list per session
    /// that's still open.
    /// </summary>
    /// <exception cref="PenException">
    /// A session that's still open didn't record its module repos, or recorded another common git dir.
    /// </exception>
    /// <remarks>
    /// A session's records are deleted after its compose.yaml, which goes once its check has run, so one that's
    /// missing them and is no longer open has ended while this looked, and what its agent made has been dealt with.
    /// </remarks>
    public static List<IReadOnlyList<string>> ReadOpenLists(
        IReadOnlyList<SessionDir> open,
        string commonGitDir,
        string dir)
    {
        var lists = new List<IReadOnlyList<string>>();
        var unrecorded = new List<SessionDir>();
        foreach (var session in open)
        {
            if (TryReadList(session, commonGitDir) is { } list)
                lists.Add(list);
            else if (session.IsOpen())
                unrecorded.Add(session);
        }
        if (unrecorded.Count > 0)
            throw new PenException(DescribeRefusal(dir, unrecorded));
        return lists;
    }

    /// <summary>Keeps the repos in <paramref name="found"/> that every one of <paramref name="lists"/> has.</summary>
    /// <returns>Those repos, in the order found; compared ordinally.</returns>
    public static List<string> Choose(IReadOnlyList<string> found, IReadOnlyList<IReadOnlyList<string>> lists) =>
        [.. found.Where(repo => lists.All(list => list.Contains(repo, StringComparer.Ordinal)))];

    /// <summary>Reads the module repos <paramref name="session"/> listed at its start in this common git dir.</summary>
    /// <returns>The list, or null if it has none for that dir or it can't be read.</returns>
    private static string[]? TryReadList(SessionDir session, string commonGitDir)
    {
        try
        {
            var (startedWithGit, common) = session.ReadGit();
            if (!startedWithGit || common != commonGitDir)
                return null;
            return session.ReadModules();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Words the refusal to start while <paramref name="unrecorded"/> sessions are open.</summary>
    private static string DescribeRefusal(string dir, List<SessionDir> unrecorded)
    {
        var one = unrecorded.Count == 1;
        return $"refusing to run in {Out.Field(dir)}: pen has no record of which submodule repos " +
            $"{(one ? "this session that's" : "these sessions that are")} open in the project started with " +
            $"(an older pen started {(one ? "it" : "them")}, or not in this repo), so it can't tell which ones " +
            $"{(one ? "its agent" : "their agents")} could have made:\n"
            + string.Join('\n', unrecorded.Select(session => "  " + session.Container))
            + $"\nexit {(one ? "it" : "them")} first (or, for one you can't reach, docker rm -f <name>: " +
            "its pen then ends the session), then rerun";
    }
}

/// <summary>What <see cref="ModuleBaseline.NarrowAndSave"/> narrows and saves for a session that's starting.</summary>
/// <remarks>
/// Session isn't open itself yet. Chosen is what <see cref="ModuleBaseline.ChooseAtStart"/> chose, or
/// <see cref="ModuleChoice.None"/> outside a repo, where CommonGitDir is null. Open is the project's open sessions
/// (<see cref="SessionRegistry.FindOpen"/>), found again just before the save, after the scan
/// <see cref="ModuleBaseline.ChooseAtStart"/> made.
/// </remarks>
internal sealed record BaselineRequest(
    SessionDir Session,
    IReadOnlyList<Mount> Mounts,
    ModuleChoice Chosen,
    IReadOnlyList<SessionDir> Open,
    string? CommonGitDir,
    string Dir);
