using System.Diagnostics.CodeAnalysis;
using System.Text;
using Pen.Platform;
using Pen.Sessions;
using Pen.Tools;

namespace Pen.Proxy;

/// <summary>
/// Regenerates a running session's ACL files from the current config files and has its Squid reload them, once its
/// proxy sees what was written.
/// </summary>
/// <param name="host">The host user's environment.</param>
/// <param name="catalog">The recipes the config files' tools come from.</param>
/// <param name="clock">The clock the wait for another pen allow or pen deny is timed by.</param>
/// <remarks>
/// <para>Of its project's config, only the domains the trust gate lets through count; the tools' domains are the
/// ones it started with (tool-domains), so a tools entry added to .pen/ since adds nothing. The deny lists count in
/// full.</para>
/// <para>A session started by a Pen without deny gets only its allowlist, never with
/// <see cref="Allowlist.AnyDomain"/> (see <see cref="WriteAclFiles"/>).</para>
/// <para>The session's acl.lock is held from reading the config files to the reconfigure, so another pen allow or
/// pen deny can't truncate a file between this one's write and reload, and whichever reloads last read the config
/// last. Only these take it, one session at a time, so it can't deadlock with a session, pen sign or pen clean.</para>
/// </remarks>
internal sealed class SessionAclUpdater(HostEnv host, RecipeCatalog catalog, TimeProvider clock)
{
    /// <summary>How many times the proxy's view of the ACL files is read before giving up.</summary>
    private const int ProxyReadAttempts = 5;

    /// <summary>How long to wait between reads of the proxy's view of the ACL files.</summary>
    private static readonly TimeSpan ProxyReadDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>How long to wait for another pen allow or pen deny applying to the same session.</summary>
    private static readonly TimeSpan AclLockWait = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait between tries to take a session's acl.lock.</summary>
    private static readonly TimeSpan AclLockPoll = TimeSpan.FromMilliseconds(200);

    /// <summary>Where the proxy's ACL files are mounted, in its container.</summary>
    private const string ProxyAclDir = "/etc/squid/";

    /// <summary>The script that prints each ACL file it's given after a line naming it.</summary>
    private const string PrintAclsScript = """for f in "$@"; do printf '=== %s\n' "$f"; cat -- "$f" || exit 1; done""";

    /// <summary>Applies the current config files to <paramref name="session"/>.</summary>
    /// <returns>Whether its Squid took them, and what it couldn't take; or why it wasn't reloaded.</returns>
    /// <remarks>While another pen allow or pen deny holds the session's acl.lock, says it's waiting.</remarks>
    public AclUpdateResult Update(SessionDir session)
    {
        try
        {
            return UpdateFromConfig(session);
        }
        catch (Exception error) when (error is PenException or IOException or UnauthorizedAccessException)
        {
            // A file, a config file or docker failed under it: this session isn't updated, the others still are.
            return new AclUpdateResult.Failed(Out.Describe(error));
        }
    }

    /// <summary>Does <see cref="Update"/>'s work; what fails under it (a file, a config file, docker) throws.</summary>
    private AclUpdateResult UpdateFromConfig(SessionDir session)
    {
        var env = session.ReadEnv();
        if (!env.TryGetValue("PROJECT_DIR", out var projectDir))
            return new AclUpdateResult.Failed("its session.env is missing");

        using var aclLock = TakeAclLock(session);
        if (aclLock is null)
            return new AclUpdateResult.Failed("another pen allow or pen deny is still applying to it; run this again");
        var policy = EgressPolicyResolver.ForRunningSession(host, session, projectDir, catalog);
        var written = WriteAclFiles(session, policy);
        var compose = new ComposeClient(session, env);
        if (AwaitProxyView(compose, written.Files) is { } notSeen)
            return new AclUpdateResult.Failed(notSeen);
        var result = compose.ExecInProxy("squid", "-k", "reconfigure");
        if (result.ExitStatus.ExitCode != 0)
        {
            return new AclUpdateResult.Failed(
                $"squid -k reconfigure failed: {(result.StandardError + result.StandardOutput).Trim()}");
        }
        return new AclUpdateResult.Applied(written.Pending);
    }

    /// <summary>
    /// Takes <paramref name="session"/>'s acl.lock exclusive, waiting while another pen allow or pen deny holds it.
    /// </summary>
    /// <returns>The lock, or null when it's still held after <see cref="AclLockWait"/>.</returns>
    [SuppressMessage("Reliability", "CA2000", Justification = "The lock is returned to the caller, which disposes it")]
    private FileLock? TakeAclLock(SessionDir session)
    {
        var held = FileLock.TryTake(session.AclLockFile, exclusive: true);
        if (held is not null)
            return held;
        Out.WriteLine(
            $"waiting for another pen allow or pen deny applying to the running session {session.ComposeProject}");
        return Retry.Poll(
            () => FileLock.TryTake(session.AclLockFile, exclusive: true), AclLockPoll, AclLockWait, clock);
    }

    /// <summary>
    /// Reads <paramref name="files"/> as the session's proxy sees them until they hold what was written, retrying a
    /// few times.
    /// </summary>
    /// <param name="compose">The session's compose calls.</param>
    /// <param name="files">Each ACL file written, by name, with its text.</param>
    /// <returns>
    /// Null once the proxy sees every file as written; otherwise why Squid mustn't be reloaded: reading them failed,
    /// or they still differ after <see cref="ProxyReadAttempts"/> reads.
    /// </returns>
    /// <remarks>
    /// A VM's file sharing (seen with OrbStack) can show the proxy a host-written file as it was a moment ago, empty
    /// right after the truncate; reloading then would drop the deny list without a word. All the files are printed by
    /// one exec, each after a line naming it, and compared as one, so any one of them differing holds the reload back.
    /// After a failed check the session dir's files hold the new lists while Squid still enforces the old ones, so
    /// pen ps and the session-end lines report from the new files until an apply succeeds.
    /// </remarks>
    private static string? AwaitProxyView(ComposeClient compose, IReadOnlyList<AclFile> files)
    {
        var paths = files.Select(file => ProxyAclDir + file.Name).ToList();
        var expected = FormatProxyView(files);
        for (var attempt = 1; ; attempt++)
        {
            var result = compose.ExecInProxy(["sh", "-c", PrintAclsScript, "sh", .. paths]);
            // A failed read isn't retried.
            if (result.ExitStatus.ExitCode != 0)
            {
                return "couldn't read its proxy's ACL files, so Squid wasn't reloaded; run this again: " +
                    (result.StandardError + result.StandardOutput).Trim();
            }
            if (result.StandardOutput == expected)
                return null;
            if (attempt >= ProxyReadAttempts)
            {
                return "its proxy still sees other ACL files than were written, so Squid wasn't reloaded; " +
                    "run this again";
            }
            Retry.Wait(ProxyReadDelay);
        }
    }

    /// <summary>Formats what the proxy prints for <paramref name="files"/> when it sees what was written.</summary>
    private static string FormatProxyView(IReadOnlyList<AclFile> files) =>
        string.Concat(files.Select(file => $"=== {ProxyAclDir}{file.Name}\n{file.Text}"));

    /// <summary>Writes <paramref name="egress"/> into <paramref name="session"/>'s ACL files.</summary>
    /// <returns>The files written, and what its proxy can't take.</returns>
    /// <remarks>
    /// <para>Each file is truncated and written, never renamed: the proxy's single-file bind mounts pin the inodes.
    /// A file that's gone fails the write rather than being made, since the proxy wouldn't see a new one.</para>
    /// <para>A session without a denylist.txt was started by a Pen whose squid.conf has no deny or open ACL, so only
    /// its allowlist.txt is written, from the allow list without <see cref="Allowlist.AnyDomain"/>: a literal "*"
    /// there would be a domain to squid, and leaving the file as it was would keep domains taken out of the config
    /// since. The named domains are what the config grants by name now, so it never gets more than that.</para>
    /// </remarks>
    internal static AclWrite WriteAclFiles(SessionDir session, EgressPolicy egress)
    {
        if (!session.HasDenyAndOpen())
        {
            var allowlist = Overwrite(session.AllowlistFile, Allowlist.Render(egress.NamedAllow));
            var open = egress.Allow.Contains(Allowlist.AnyDomain);
            var pending = (egress.Deny.Count > 0, open) switch
            {
                (true, true) => "deny and allow = [\"*\"] apply",
                (true, false) => "deny applies",
                (false, true) => "allow = [\"*\"] applies",
                _ => null,
            };
            return new([allowlist], pending);
        }

        var acls = Allowlist.RenderFiles(egress.NamedAllow, egress.Allow.Contains(Allowlist.AnyDomain), egress.Deny);
        var denylist = Overwrite(session.DenylistFile, acls.DenylistText);
        var named = Overwrite(session.AllowlistFile, acls.AllowlistText);
        var openEgress = Overwrite(session.OpenEgressFile, acls.OpenEgressText);
        return new([denylist, named, openEgress], null);
    }

    /// <summary>Truncates the existing file at <paramref name="path"/> and writes <paramref name="text"/>.</summary>
    /// <returns>The file, by name, with what was written.</returns>
    private static AclFile Overwrite(string path, string text)
    {
        using (var file = new FileStream(path, FileMode.Truncate, FileAccess.Write))
        {
            file.Write(Encoding.UTF8.GetBytes(text));
        }
        return new(Path.GetFileName(path), text);
    }

    /// <summary>One ACL file <see cref="WriteAclFiles"/> wrote.</summary>
    /// <param name="Name">Its name, the same in the session dir and in the proxy's /etc/squid.</param>
    /// <param name="Text">What was written.</param>
    internal sealed record AclFile(string Name, string Text);

    /// <summary>What <see cref="WriteAclFiles"/> did.</summary>
    /// <param name="Files">The files written, in the order they were.</param>
    /// <param name="Pending">
    /// What the session's proxy can't take (sessions started from now on get it), worded for the notice; null when it
    /// took everything.
    /// </param>
    internal sealed record AclWrite(IReadOnlyList<AclFile> Files, string? Pending);
}

/// <summary>How <see cref="SessionAclUpdater.Update"/> went for one session.</summary>
internal abstract record AclUpdateResult
{
    private AclUpdateResult()
    {
    }

    /// <summary>The session's Squid reloaded the files written.</summary>
    /// <param name="Pending">
    /// What a session started by an older Pen can't take (sessions started from now on get it), worded for the
    /// notice; null when it took everything.
    /// </param>
    public sealed record Applied(string? Pending) : AclUpdateResult;

    /// <summary>The session wasn't updated, or its Squid wasn't reloaded.</summary>
    /// <param name="Reason">Why, worded for the line naming the session.</param>
    public sealed record Failed(string Reason) : AclUpdateResult;
}
