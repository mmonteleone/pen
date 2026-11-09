using System.Text;
using System.Text.RegularExpressions;

namespace Pen;

/// <summary>
/// pen prune: removes what moved or deleted projects left behind (volumes,
/// project-layer images, the runtime dir), after asking. A key's project is
/// known from the project-dir file its runtime dir holds
/// (<see cref="Session.EnsureRuntimeDir"/>).
/// </summary>
internal static partial class Prune
{
    internal enum Status { Live, Moved, Missing, Unknown }

    /// <summary>
    /// One project key: its volumes and images, its runtime dir (null when
    /// there's none), the dir it recorded, and for a moved one the key that
    /// dir has now.
    /// </summary>
    internal sealed record Entry(string Key, Status Status, IReadOnlyList<string> Volumes, IReadOnlyList<string> Images,
        string? RuntimeDir, string? RecordedDir, string? CurrentKey = null)
    {
        public bool Removable => Status is Status.Moved or Status.Missing;
    }

    public static int Run(HostEnv host, TextReader input)
    {
        var entries = Find(host, ListVolumes(), ListImages());
        if (entries.Count == 0)
        {
            Out.Line("nothing to prune: no project has volumes or state");
            return 0;
        }
        Out.Raw(Render(host, entries));

        var removable = entries.Where(e => e.Removable).ToList();
        if (removable.Count == 0)
        {
            Out.Line("nothing to prune");
            return 0;
        }
        Out.Raw($"Removing deletes {(removable.Count == 1 ? "that project's" : $"those {removable.Count} projects'")} sandbox homes (agent logins"
                + " and config, shell history), NuGet caches, Docker-in-Docker images, project layers\n"
                + "and pen's state for them. No project files are touched.");
        if (!Out.Confirm(removable.Count == 1 ? "remove it?" : "remove them?", input))
        {
            Out.Line("nothing removed");
            return 1;
        }

        var failed = 0;
        foreach (var e in removable)
            if (!Remove(host, e)) failed++;
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Every key that has volumes or a runtime dir, classified, in key order.
    /// <paramref name="volumes"/> and <paramref name="images"/> are the
    /// names docker lists (images as repository:tag).
    /// </summary>
    internal static List<Entry> Find(HostEnv host, IEnumerable<string> volumes, IEnumerable<string> images)
    {
        var byKey = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var v in volumes)
        {
            if (KeyOfVolume(v) is not { } key) continue;
            if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = [];
            list.Add(v);
        }
        if (Directory.Exists(host.StateRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(host.StateRoot))
            {
                var key = Path.GetFileName(dir);
                if (KeyPattern().IsMatch(key) && !byKey.ContainsKey(key)) byKey[key] = [];
            }
        }

        var imageList = images.ToList();
        return byKey.Select(kv =>
        {
            var runtimeDir = host.RuntimeDir(kv.Key);
            if (!Directory.Exists(runtimeDir)) runtimeDir = null;
            var recorded = runtimeDir == null ? null : ReadProjectDir(runtimeDir);
            var (status, current) = Classify(kv.Key, recorded);
            return new Entry(kv.Key, status, kv.Value.Order(StringComparer.Ordinal).ToList(),
                imageList.Where(i => IsImageOf(kv.Key, i)).Order(StringComparer.Ordinal).ToList(),
                runtimeDir, recorded, current);
        }).ToList();
    }

    /// <summary>
    /// What became of the project <paramref name="key"/> recorded as
    /// <paramref name="recordedDir"/>: live when that dir still resolves to the
    /// key (by its main checkout, as <see cref="Project.Resolve"/> does), moved
    /// when it resolves to another key, missing when it's gone, unknown
    /// without a record.
    /// </summary>
    internal static (Status Status, string? CurrentKey) Classify(string key, string? recordedDir)
    {
        if (recordedDir == null) return (Status.Unknown, null);
        if (!Directory.Exists(recordedDir)) return (Status.Missing, null);
        var real = Native.RealPath(recordedDir);
        if (real == null) return (Status.Missing, null);
        var current = Project.KeyFor(Project.FindRepo(real).RepoDir);
        return current == key ? (Status.Live, null) : (Status.Moved, current);
    }

    /// <summary>The project key a pen volume (&lt;key&gt;_home, _nuget or _dind) belongs to, or null for any other volume.</summary>
    internal static string? KeyOfVolume(string volume)
    {
        foreach (var s in Reset.VolumeSuffixes)
        {
            var suffix = "_" + s;
            if (volume.EndsWith(suffix, StringComparison.Ordinal))
            {
                var key = volume[..^suffix.Length];
                return KeyPattern().IsMatch(key) ? key : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="image"/> (repository:tag) is a project layer of
    /// <paramref name="key"/>: &lt;key&gt;:latest, or &lt;key&gt;-&lt;hash&gt;:latest
    /// for a linked worktree (<see cref="Image.ProjectLayerName"/>).
    /// </summary>
    internal static bool IsImageOf(string key, string image) =>
        image == key + ":latest"
        || (image.StartsWith(key + "-", StringComparison.Ordinal) && WorktreeSuffix().IsMatch(image[(key.Length + 1)..]));

    internal static string? ReadProjectDir(string runtimeDir)
    {
        try
        {
            var dir = File.ReadAllText(Path.Combine(runtimeDir, Session.ProjectDirFile)).TrimEnd('\n');
            return dir.StartsWith('/') ? dir : null;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>The listing: one line per key, grouped by status, with how to remove an unknown one by hand.</summary>
    internal static string Render(HostEnv host, IReadOnlyList<Entry> entries)
    {
        var sb = new StringBuilder();
        void Group(Status status, string heading, Func<Entry, string> describe)
        {
            var group = entries.Where(e => e.Status == status).ToList();
            if (group.Count == 0) return;
            sb.Append(heading).Append('\n');
            foreach (var e in group) sb.Append("  ").Append(describe(e)).Append('\n');
        }

        Group(Status.Live, "kept, in use:", e => $"{Out.Field(host.Display(e.RecordedDir!))}  ({e.Key})");
        Group(Status.Unknown, "kept, project unknown (pen hasn't run there since it began recording projects; run any\n"
                                + "pen command in it to record it, or remove it by hand):",
            e => e.Key + (e.Volumes.Count > 0 ? $"\n    docker volume rm {string.Join(' ', e.Volumes)}" : "")
                       + (e.Images.Count > 0 ? $"\n    docker image rm {string.Join(' ', e.Images)}" : "")
                       + (e.RuntimeDir != null ? $"\n    rm -r {Quote(host.Display(e.RuntimeDir))}" : ""));
        Group(Status.Moved, "to remove, moved (the dir now belongs to another project):",
            e => $"{Out.Field(host.Display(e.RecordedDir!))}  ({e.Key}, now {e.CurrentKey}){Contents(e)}");
        Group(Status.Missing, "to remove, missing (the dir is gone: if it's on a volume that isn't mounted, mount it\n"
                                + "and run pen prune again instead):",
            e => $"{Out.Field(host.Display(e.RecordedDir!))}  ({e.Key}){Contents(e)}");
        return sb.ToString().TrimEnd('\n');
    }

    private static string Contents(Entry e)
    {
        var parts = new List<string>();
        if (e.Volumes.Count > 0) parts.Add(string.Join(", ", e.Volumes));
        if (e.Images.Count > 0) parts.Add(string.Join(", ", e.Images));
        return parts.Count == 0 ? "" : "\n    " + string.Join("; ", parts);
    }

    private static string Quote(string s) => s.All(c => char.IsAsciiLetterOrDigit(c) || "/~._-".Contains(c)) ? s : "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Removes one moved or missing key: under project.lock exclusive, and only
    /// with no session running. Volumes, then images, then the runtime dir,
    /// which goes only once the rest has, so a failed key stays listed.
    /// </summary>
    internal static bool Remove(HostEnv host, Entry e)
    {
        var runtimeDir = e.RuntimeDir ?? host.RuntimeDir(e.Key);
        using var l = FileLock.TryTake(Sessions.ProjectLockFile(runtimeDir), exclusive: true);
        if (l == null || Sessions.Running(e.Key, runtimeDir).Count > 0)
        {
            Out.Line($"skipping {e.Key}: a session (or pen sign or reset) is running in it");
            return false;
        }

        if (e.Volumes.Count > 0)
        {
            var r = Proc.Capture("docker", ["volume", "rm", .. e.Volumes]);
            if (r.ExitStatus.ExitCode != 0)
            {
                Out.Line($"can't remove {e.Key}'s volumes: {r.StandardError.Trim()}");
                return false;
            }
        }
        if (e.Images.Count > 0)
        {
            var r = Proc.Capture("docker", ["image", "rm", .. e.Images]);
            if (r.ExitStatus.ExitCode != 0)
            {
                Out.Line($"can't remove {e.Key}'s images: {r.StandardError.Trim()}");
                return false;
            }
        }
        try { Directory.Delete(runtimeDir, recursive: true); }
        catch (DirectoryNotFoundException) { }
        catch (Exception x) when (x is IOException or UnauthorizedAccessException)
        {
            Out.Line($"can't remove {host.Display(runtimeDir)}: {Out.Describe(x)}");
            return false;
        }
        Out.Line($"removed {e.Key}");
        return true;
    }

    private static List<string> ListVolumes()
    {
        var r = Proc.Capture("docker", ["volume", "ls", "-q", "--filter", "name=^pen-"]);
        if (r.ExitStatus.ExitCode != 0)
            throw new PenException($"docker volume ls failed: is Docker running? {r.StandardError.Trim()}");
        return Lines(r.StandardOutput);
    }

    private static List<string> ListImages()
    {
        var r = Proc.Capture("docker", ["image", "ls", "--format", "{{.Repository}}:{{.Tag}}", "--filter", "reference=pen-*"]);
        if (r.ExitStatus.ExitCode != 0)
            throw new PenException($"docker image ls failed: {r.StandardError.Trim()}");
        return Lines(r.StandardOutput);
    }

    private static List<string> Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>A project key: pen-&lt;clean name&gt;-&lt;8 hex&gt; (<see cref="Project.KeyFor"/>).</summary>
    [GeneratedRegex("^pen-[a-z0-9_-]+-[0-9a-f]{8}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[0-9a-f]{8}:latest$")]
    private static partial Regex WorktreeSuffix();
}
