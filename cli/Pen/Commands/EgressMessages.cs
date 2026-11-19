using Pen.CommandLine;
using Pen.Platform;
using Pen.Projects;
using Pen.Proxy;
using Pen.Sessions;

namespace Pen.Commands;

/// <summary>What pen allow and pen deny both say about the domains they add.</summary>
internal static class EgressMessages
{
    /// <summary>Says which of <paramref name="domains"/> were added, and which were there already.</summary>
    public static void WriteAdded(ConfigAddition addition, IReadOnlyList<string> domains)
    {
        var skipped = domains.Except(addition.Added).ToList();
        if (addition.Added.Count > 0)
            Out.WriteLine($"added to {addition.Where}: {string.Join(' ', addition.Added)}");
        if (skipped.Count > 0)
            Out.WriteLine($"already in {addition.Where}: {string.Join(' ', skipped)}");
    }

    /// <summary>
    /// Says how applying the change to the running <paramref name="session"/> went: why it failed, or what a session
    /// started by an older Pen can't take; nothing when it took everything.
    /// </summary>
    public static void WriteUpdate(SessionDir session, AclUpdateResult result)
    {
        switch (result)
        {
            case AclUpdateResult.Failed { Reason: var reason }:
                Out.WriteLine($"couldn't apply it to the running session {session.ComposeProject}: {reason}");
                break;
            case AclUpdateResult.Applied { Pending: { } pending }:
                Out.WriteLine($"the running session {session.ComposeProject} was started by an older Pen: " +
                    $"{pending} only to sessions started from now on");
                break;
        }
    }

    /// <summary>
    /// Says how many running sessions took the change, and whether they were only this directory's; nothing when none
    /// did.
    /// </summary>
    public static void WriteApplied(int applied, EgressTarget target)
    {
        if (applied > 0)
        {
            Out.WriteLine($"applied to {applied} running session{(applied == 1 ? "" : "s")}"
                + $"{(target is EgressTarget.Global ? "" : " in this directory")}");
        }
    }

    /// <summary>
    /// Says, when an added argument of pen <paramref name="command"/> is also the name of a file or directory in
    /// the start directory, that the shell may have expanded an unquoted *, and where the names went.
    /// </summary>
    /// <param name="command">The subcommand.</param>
    /// <param name="startDir">The start directory; no hint when it couldn't be read.</param>
    /// <param name="addition">What was added, and to which file.</param>
    /// <remarks>The domains are still added: a file and a domain can share a name.</remarks>
    public static void WriteGlobHint(string command, StartDir startDir, ConfigAddition addition)
    {
        if (startDir.Dir is { } dir && FormatGlobHint(command, dir, addition.Added, addition.Where) is { } hint)
            Out.WriteLine(hint);
    }

    /// <summary>
    /// Formats <see cref="WriteGlobHint"/>'s hint for <paramref name="domains"/>, added to <paramref name="where"/>
    /// (the config file as messages name it), in the start directory <paramref name="dir"/>.
    /// </summary>
    /// <returns>The hint, or null when no argument is named like one, or the directory can't be listed.</returns>
    /// <remarks>Names are compared ignoring case, since the parser lowercased the arguments.</remarks>
    internal static string? FormatGlobHint(
        string command,
        string dir,
        IReadOnlyList<string> domains,
        string where)
    {
        var matches = domains.Where(domain => ShellGlob.NamesEntry(dir, domain)).ToList();
        if (matches.Count == 0)
            return null;
        var what = matches.Count == 1 ? "names a file here" : "name files here";
        var were = matches.Count == 1 ? "it was" : "they were";
        return $"{string.Join(' ', matches.Select(Out.Field))} {what}: {ShellGlob.FormatAdvice(command)}; " +
            $"{were} added to {where}: remove {(matches.Count == 1 ? "it" : "them")} there if " +
            $"{(matches.Count == 1 ? "it isn't a domain" : "they aren't domains")}";
    }
}
