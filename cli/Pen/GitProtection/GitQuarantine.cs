using System.Text;
using Pen.Platform;

namespace Pen.GitProtection;

/// <summary>
/// Moves aside, or removes, what the .git check flags: renames to a free "….pen-quarantine" name, and removal of a
/// symlink.
/// </summary>
internal static class GitQuarantine
{
    /// <summary>The suffix of the names the check renames things to.</summary>
    public const string Suffix = ".pen-quarantine";

    /// <summary>Renames <paramref name="path"/> to a free "….pen-quarantine" name.</summary>
    /// <param name="path">What to move.</param>
    /// <param name="why">Why it's moved, for the warning.</param>
    /// <param name="breakLayout">
    /// Whether it's a .git directory, whose HEAD goes too: renamed, it would still look like a bare repo to git.
    /// </param>
    /// <returns>
    /// The rename made, or a <see cref="Quarantined.Failed"/> whose Why says it couldn't be moved.
    /// </returns>
    /// <remarks>
    /// Never fatal: the rest still gets checked, and the warning names what couldn't be moved.
    /// </remarks>
    internal static Quarantined Move(string path, string why, bool breakLayout)
    {
        try
        {
            var to = FreeName(path);
            Native.Rename(path, to);
            var inner = breakLayout ? MoveHeadAside(to) : null;
            return new Quarantined.Moved(path, to, why, inner);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Native.Rename's message is strerror's.
            var reason = exception is UnauthorizedAccessException denied
                ? Out.DescribePermissionDenied(denied)
                : exception.Message;
            return new Quarantined.Failed(path, $"{why}. COULD NOT MOVE IT ({reason}): " +
                "don't run git here until you've dealt with it");
        }
    }

    /// <summary>Removes the symlink <paramref name="path"/>, keeping what it held for the warning.</summary>
    /// <param name="root">The project dir, which <paramref name="path"/> is below.</param>
    /// <param name="path">The symlink.</param>
    /// <param name="why">Why it's removed, for the warning.</param>
    /// <returns>
    /// The removal made, or a <see cref="Quarantined.Failed"/> whose Why says it couldn't be removed.
    /// </returns>
    /// <remarks>
    /// Renamed, a symlink would lead to the same place, so it's removed instead: it holds no data. Only ever a
    /// symlink is removed, in the directory reached from <paramref name="root"/> without following a symlink
    /// (<see cref="Native.RemoveSymlink"/>), so another session's agent can't swap a directory on the way for one
    /// that leads elsewhere; anything but a symlink there now is left, and the removal fails. Never fatal, like
    /// <see cref="Move"/>.
    /// </remarks>
    internal static Quarantined RemoveLink(string root, string path, string why)
    {
        try
        {
            var target = Native.RemoveSymlink(root, path);
            return StrictUtf8.DecodeOrNull(target) is { } text
                ? new Quarantined.LinkRemoved(path, why, text)
                : new Quarantined.LinkRemoved(path, why, EscapeTarget(target), LinkTargetEscaped: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            var reason = exception is UnauthorizedAccessException denied
                ? Out.DescribePermissionDenied(denied)
                : exception.Message;
            return new Quarantined.Failed(path, $"{why}. COULD NOT REMOVE IT ({reason}): " +
                "don't run git here until you've dealt with it");
        }
    }

    /// <summary>Shows a target that isn't valid UTF-8: octal escapes for all but printable ASCII.</summary>
    private static string EscapeTarget(byte[] target)
    {
        var text = new StringBuilder();
        foreach (var value in target)
        {
            if (value is >= 0x20 and < 0x7f && value != (byte)'\\')
                text.Append((char)value);
            else
                text.Append('\\').Append(Convert.ToString(value, 8).PadLeft(3, '0'));
        }
        return text.ToString();
    }

    /// <summary>Renames the HEAD inside a .git directory that was just moved to <paramref name="moved"/>.</summary>
    /// <returns>The rename made, or null if it isn't a real directory or has no HEAD.</returns>
    private static InnerRename? MoveHeadAside(string moved)
    {
        if (!Directory.Exists(moved) || PathChecks.IsLink(moved))
            return null;
        var head = Path.Combine(moved, "HEAD");
        if (!PathChecks.ExistsOrIsLink(head))
            return null;
        var headTo = FreeName(head);
        Native.Rename(head, headTo);
        return new(head, headTo);
    }

    /// <summary>Finds a name beside <paramref name="path"/> to quarantine it under.</summary>
    /// <returns>path.pen-quarantine, or the first of path.pen-quarantine-2, -3, … that nothing has.</returns>
    internal static string FreeName(string path)
    {
        var to = path + Suffix;
        for (var i = 2; PathChecks.ExistsOrIsLink(to); i++)
            to = $"{path}{Suffix}-{i}";
        return to;
    }
}
