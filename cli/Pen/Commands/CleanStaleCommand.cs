using System.Text;
using System.Text.RegularExpressions;
using Pen.Images;
using Pen.Platform;
using Pen.Projects;
using Pen.Sessions;
using Pen.Trust;

namespace Pen.Commands;

/// <summary>
/// Runs pen clean --stale: removes what moved or deleted projects left behind (volumes, project-layer images,
/// the runtime dir) and tools images on an old base, after asking.
/// </summary>
/// <remarks>
/// A key's project is known from the project-dir file its runtime dir holds
/// (<see cref="ProjectRuntime.EnsureRuntimeDir"/>).
/// </remarks>
internal sealed partial class CleanStaleCommand(HostEnv host, Answers answers)
{
    /// <summary>What became of a key's project.</summary>
    internal enum Status
    {
        /// <summary>Its recorded dir still resolves to the key.</summary>
        Live,

        /// <summary>Its recorded dir resolves to another key.</summary>
        Moved,

        /// <summary>Its recorded dir is gone.</summary>
        Missing,

        /// <summary>It has no record.</summary>
        Unknown,
    }

    /// <summary>What became of a key's project, with what the listing shows for it.</summary>
    internal abstract record Fate
    {
        private Fate(Status status) => Status = status;

        /// <summary>Gets which of the four it is.</summary>
        public Status Status { get; }

        /// <summary>Its recorded dir still resolves to the key.</summary>
        /// <param name="Dir">The recorded dir.</param>
        public sealed record Live(string Dir) : Fate(Status.Live);

        /// <summary>Its recorded dir resolves to another key.</summary>
        /// <param name="Dir">The recorded dir.</param>
        /// <param name="CurrentKey">The key the recorded dir has now.</param>
        public sealed record Moved(string Dir, string CurrentKey) : Fate(Status.Moved);

        /// <summary>Its recorded dir is gone.</summary>
        /// <param name="Dir">The recorded dir.</param>
        public sealed record Missing(string Dir) : Fate(Status.Missing);

        /// <summary>It has no record.</summary>
        public sealed record Unknown : Fate
        {
            /// <summary>Initializes the fate.</summary>
            public Unknown() : base(Status.Unknown)
            {
            }
        }
    }

    /// <summary>One project key, with what it has and what became of its project.</summary>
    internal sealed record Entry
    {
        public required string Key { get; init; }

        /// <summary>Gets what became of its project.</summary>
        public required Fate Fate { get; init; }

        /// <summary>Gets which of the fates its project has.</summary>
        public Status Status => Fate.Status;

        /// <summary>Gets its volumes, in order.</summary>
        public required IReadOnlyList<string> Volumes { get; init; }

        /// <summary>Gets its project-layer images (repository:tag), in order.</summary>
        public required IReadOnlyList<string> Images { get; init; }

        /// <summary>Gets its runtime dir, or null when there's none.</summary>
        public required string? RuntimeDir { get; init; }

        /// <summary>Gets whether pen clean --stale removes it: its project moved or is missing.</summary>
        public bool Removable => Status is Status.Moved or Status.Missing;
    }

    /// <summary>Lists every key and the old tools images, and removes the ones to remove if the user agrees.</summary>
    /// <returns>
    /// 0 once all are removed, or when there's nothing to remove; 1 when the user declines or one fails.
    /// </returns>
    public int Run()
    {
        var images = ListImages();
        var entries = Find(host, ListVolumes(), images);
        var staleTools = FindStaleToolsImages(images, ToolsImage.ReadCurrentBaseId(), ReadBaseId);
        if (entries.Count == 0 && staleTools.Count == 0)
        {
            Out.WriteLine("nothing to clean: no project has volumes or state");
            return 0;
        }
        if (entries.Count > 0)
            Out.WriteRaw(Render(host, entries));

        var removable = entries.Where(entry => entry.Removable).ToList();
        if (removable.Count == 0 && staleTools.Count == 0)
        {
            Out.WriteLine("nothing stale to clean");
            return 0;
        }
        WriteWhatGoes(removable, staleTools);
        if (!Out.Confirm(removable.Count + staleTools.Count == 1 ? "remove it?" : "remove them?", answers))
        {
            Out.WriteLine("nothing removed");
            return 1;
        }

        return RemoveAll(removable, staleTools) ? 0 : 1;
    }

    /// <summary>Writes which tools images go, and what removing the keys deletes.</summary>
    private static void WriteWhatGoes(List<Entry> removable, IReadOnlyList<string> staleTools)
    {
        if (staleTools.Count > 0)
            Out.WriteRaw($"to remove, tools images on an old base image:\n  {string.Join("\n  ", staleTools)}");
        if (removable.Count > 0)
        {
            Out.WriteRaw("Removing deletes " +
                $"{(removable.Count == 1 ? "that project's" : $"those {removable.Count} projects'")} " +
                "sandbox homes (agent logins"
                    + " and config, shell history), NuGet caches, Docker-in-Docker images, project layers\n"
                    + "and pen's state for them. No project files are touched.");
        }
    }

    /// <summary>Removes the keys, then the tools images.</summary>
    /// <returns>Whether all of them went; each failure is reported.</returns>
    private bool RemoveAll(List<Entry> removable, IReadOnlyList<string> staleTools)
    {
        var failed = 0;
        foreach (var entry in removable)
        {
            if (!Remove(host, entry))
                failed++;
        }

        foreach (var image in staleTools)
        {
            var result = Proc.Capture("docker", ["image", "rm", image]);
            if (result.ExitStatus.ExitCode != 0)
            {
                Out.WriteLine($"can't remove {image}: {result.StandardError.Trim()}");
                failed++;
            }
        }
        return failed == 0;
    }

    /// <summary>Finds the tools images no session can ask for any more, since their name depends on the base.</summary>
    /// <param name="images">The images docker lists, as repository:tag.</param>
    /// <param name="currentBase">The ID of pen:latest now, or null when there's none.</param>
    /// <param name="readBaseId">Reads an image's base-ID label, or null if it can't be read.</param>
    /// <returns>The images in the repository pen-tools itself whose label names another base, in order.</returns>
    /// <remarks>
    /// Going by the repository matters: a project layer stacked on a tools image inherits the label, and a project
    /// named tools has a repository that starts the same way. With no current base to compare, or a label that
    /// can't be read, nothing is called old.
    /// </remarks>
    internal static IReadOnlyList<string> FindStaleToolsImages(
        IEnumerable<string> images,
        string? currentBase,
        Func<string, string?> readBaseId)
    {
        if (currentBase is null)
            return [];
        return [.. images
            .Where(image => ToolsTag().IsMatch(image))
            .Where(image => readBaseId(image) is { } id
                && id.StartsWith("sha256:", StringComparison.Ordinal) && id != currentBase)
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Reads <paramref name="image"/>'s base-ID label.</summary>
    /// <returns>The label, or null if the image can't be inspected.</returns>
    private static string? ReadBaseId(string image)
    {
        var result = Proc.Capture("docker",
            ["image", "inspect", "--format", $"{{{{ index .Config.Labels \"{ToolsImage.BaseIdLabel}\" }}}}", image]);
        return result.ExitStatus.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    /// <summary>
    /// Finds every key that has volumes or a runtime dir, and classifies it, from the volumes and images (as
    /// repository:tag) docker lists.
    /// </summary>
    /// <returns>The keys, in order.</returns>
    internal static IReadOnlyList<Entry> Find(HostEnv host, IEnumerable<string> volumes, IEnumerable<string> images)
    {
        var byKey = GroupVolumesByKey(volumes);
        if (Directory.Exists(host.StateRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(host.StateRoot))
            {
                var key = Path.GetFileName(dir);
                if (KeyPattern().IsMatch(key) && !byKey.ContainsKey(key))
                    byKey[key] = [];
            }
        }

        var imageList = images.ToList();
        return [.. byKey.Select(group => DescribeEntry(host, group.Key, group.Value, imageList))];
    }

    /// <summary>Groups Pen's volumes in <paramref name="volumes"/> by their project key.</summary>
    private static SortedDictionary<string, List<string>> GroupVolumesByKey(IEnumerable<string> volumes)
    {
        var byKey = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var volume in volumes)
        {
            if (FindKeyOfVolume(volume) is not { } key)
                continue;
            if (!byKey.TryGetValue(key, out var list))
                byKey[key] = list = [];
            list.Add(volume);
        }
        return byKey;
    }

    /// <summary>Describes <paramref name="key"/>: what it has, and what became of its project.</summary>
    private static Entry DescribeEntry(HostEnv host, string key, List<string> volumes, List<string> images)
    {
        var runtimeDir = host.GetRuntimeDir(key);
        if (!Directory.Exists(runtimeDir))
            runtimeDir = null;
        var recorded = runtimeDir is null ? null : ReadProjectDir(runtimeDir);
        return new Entry
        {
            Key = key,
            Fate = Classify(key, recorded),
            Volumes = [.. volumes.Order(StringComparer.Ordinal)],
            Images = [.. images.Where(image => IsImageOf(key, image)).Order(StringComparer.Ordinal)],
            RuntimeDir = runtimeDir,
        };
    }

    /// <summary>
    /// Classifies what became of the project <paramref name="key"/> recorded as <paramref name="recordedDir"/>.
    /// </summary>
    /// <returns>
    /// Live when that dir still resolves to the key (by its main checkout, as <see cref="Project.Resolve"/> does),
    /// moved (with the key it has now) when it resolves to another key, missing when it's gone, unknown without a
    /// record.
    /// </returns>
    internal static Fate Classify(string key, string? recordedDir)
    {
        if (recordedDir is null)
            return new Fate.Unknown();
        if (!Directory.Exists(recordedDir))
            return new Fate.Missing(recordedDir);
        var real = Native.RealPath(recordedDir);
        if (real is null)
            return new Fate.Missing(recordedDir);
        var current = Project.ComputeKey(Project.FindRepo(real).RepoDir);
        return current == key ? new Fate.Live(recordedDir) : new Fate.Moved(recordedDir, current);
    }

    /// <summary>Finds the project key a Pen volume (&lt;key&gt;_home, _nuget or _dind) belongs to.</summary>
    /// <returns>The key, or null for any other volume.</returns>
    internal static string? FindKeyOfVolume(string volume)
    {
        foreach (var name in CleanCommand.VolumeSuffixes)
        {
            var suffix = "_" + name;
            if (volume.EndsWith(suffix, StringComparison.Ordinal))
            {
                var key = volume[..^suffix.Length];
                return KeyPattern().IsMatch(key) ? key : null;
            }
        }
        return null;
    }

    /// <summary>
    /// Tells whether <paramref name="image"/> (repository:tag) is a project layer of <paramref name="key"/>.
    /// </summary>
    /// <remarks>
    /// That's &lt;key&gt;, or &lt;key&gt;-&lt;hash&gt; for a linked worktree, tagged latest or with a tools image's
    /// hash (<see cref="Image.GetProjectLayerName"/>).
    /// </remarks>
    internal static bool IsImageOf(string key, string image) =>
        image.StartsWith(key, StringComparison.Ordinal) && LayerSuffix().IsMatch(image[key.Length..]);

    /// <summary>Reads the main checkout <paramref name="runtimeDir"/>'s project-dir records.</summary>
    /// <returns>The dir, or null when there's no record or it isn't an absolute path.</returns>
    internal static string? ReadProjectDir(string runtimeDir)
    {
        try
        {
            var dir = File.ReadAllText(Path.Combine(runtimeDir, ProjectRuntime.ProjectDirFile)).TrimEnd('\n');
            return dir.StartsWith('/') ? dir : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Renders the listing: one line per key, grouped by status, with how to remove an unknown one by hand.
    /// </summary>
    internal static string Render(HostEnv host, IReadOnlyList<Entry> entries)
    {
        var listing = new StringBuilder();
        void Group<T>(string heading, Func<Entry, T, string> describe)
            where T : Fate
        {
            var group = new List<(Entry Entry, T Fate)>();
            foreach (var entry in entries)
            {
                if (entry.Fate is T fate)
                    group.Add((entry, fate));
            }
            if (group.Count == 0)
                return;
            listing.Append(heading).Append('\n');
            foreach (var (entry, fate) in group)
                listing.Append("  ").Append(describe(entry, fate)).Append('\n');
        }

        Group<Fate.Live>("kept, in use:",
            (entry, live) => $"{Out.Field(host.Display(live.Dir))}  ({entry.Key})");
        Group<Fate.Unknown>("kept, project unknown (pen hasn't run there since it began recording projects; run any\n"
                                + "pen command in it to record it, or remove it by hand):",
            (entry, _) => DescribeUnknown(host, entry));
        Group<Fate.Moved>("to remove, moved (the dir now belongs to another project):",
            (entry, moved) => $"{Out.Field(host.Display(moved.Dir))}  ({entry.Key}, now {moved.CurrentKey})"
                + DescribeContents(entry));
        Group<Fate.Missing>("to remove, missing (the dir is gone: if it's on a volume that isn't mounted, mount it\n"
                                + "and run pen clean --stale again instead):",
            (entry, missing) => $"{Out.Field(host.Display(missing.Dir))}  ({entry.Key}){DescribeContents(entry)}");
        return listing.ToString().TrimEnd('\n');
    }

    /// <summary>Describes an unknown key, with the commands that remove what it has by hand.</summary>
    private static string DescribeUnknown(HostEnv host, Entry entry)
    {
        var description = entry.Key;
        if (entry.Volumes.Count > 0)
            description += $"\n    docker volume rm {string.Join(' ', entry.Volumes)}";
        if (entry.Images.Count > 0)
            description += $"\n    docker image rm {string.Join(' ', entry.Images)}";
        if (entry.RuntimeDir is not null)
            description += $"\n    rm -r {Quote(host.Display(entry.RuntimeDir))}";
        return description;
    }

    /// <summary>Describes the volumes and images a key to remove has, on a line of its own.</summary>
    /// <returns>The line, or "" when it has neither.</returns>
    private static string DescribeContents(Entry entry)
    {
        var parts = new List<string>();
        if (entry.Volumes.Count > 0)
            parts.Add(string.Join(", ", entry.Volumes));
        if (entry.Images.Count > 0)
            parts.Add(string.Join(", ", entry.Images));
        return parts.Count == 0 ? "" : "\n    " + string.Join("; ", parts);
    }

    /// <summary>Quotes <paramref name="text"/> for a shell, unless it needs none.</summary>
    private static string Quote(string text) =>
        text.All(c => char.IsAsciiLetterOrDigit(c) || "/~._-".Contains(c))
            ? text
            : "'" + text.Replace("'", "'\\''") + "'";

    /// <summary>Removes one moved or missing key: its volumes, then its images, then its runtime dir.</summary>
    /// <returns>Whether all of it went; a failure is reported.</returns>
    /// <remarks>
    /// It runs under project.lock exclusive, and only with no session running. The runtime dir goes only once the
    /// rest has, so a failed key stays listed.
    /// </remarks>
    internal static bool Remove(HostEnv host, Entry entry)
    {
        var runtimeDir = entry.RuntimeDir ?? host.GetRuntimeDir(entry.Key);
        using var projectLock = FileLock.TryTake(SessionRegistry.GetProjectLockFile(runtimeDir), exclusive: true);
        if (projectLock is null || SessionRegistry.FindRunning(entry.Key, runtimeDir).Count > 0)
        {
            Out.WriteLine($"skipping {entry.Key}: a session (or pen sign or pen clean) is running in it");
            return false;
        }

        if (!TryRemove(entry.Key, "volume", entry.Volumes) || !TryRemove(entry.Key, "image", entry.Images))
            return false;
        try
        {
            LayerContext.RemoveAllIn(runtimeDir);
            Directory.Delete(runtimeDir, recursive: true);
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Out.WriteLine($"can't remove {host.Display(runtimeDir)}: {Out.Describe(error)}");
            return false;
        }
        Out.WriteLine($"removed {entry.Key}");
        return true;
    }

    /// <summary>Removes a key's volumes or images with docker <paramref name="kind"/> rm.</summary>
    /// <returns>Whether they went (or there were none); a failure is reported.</returns>
    private static bool TryRemove(string key, string kind, IReadOnlyList<string> names)
    {
        if (names.Count == 0)
            return true;
        var result = Proc.Capture("docker", [kind, "rm", .. names]);
        if (result.ExitStatus.ExitCode == 0)
            return true;
        Out.WriteLine($"can't remove {key}'s {kind}s: {result.StandardError.Trim()}");
        return false;
    }

    /// <summary>Lists the volumes whose names start with pen-.</summary>
    private static List<string> ListVolumes()
    {
        var result = Proc.Capture("docker", ["volume", "ls", "-q", "--filter", "name=^pen-"]);
        if (result.ExitStatus.ExitCode != 0)
            throw new PenException($"docker volume ls failed: is Docker running? {result.StandardError.Trim()}");
        return SplitLines(result.StandardOutput);
    }

    /// <summary>Lists the images whose repositories start with pen-, as repository:tag.</summary>
    private static List<string> ListImages()
    {
        var result = Proc.Capture("docker",
            ["image", "ls", "--format", "{{.Repository}}:{{.Tag}}", "--filter", "reference=pen-*"]);
        if (result.ExitStatus.ExitCode != 0)
            throw new PenException($"docker image ls failed: {result.StandardError.Trim()}");
        return SplitLines(result.StandardOutput);
    }

    /// <summary>Splits docker's output into its non-empty lines, trimmed.</summary>
    private static List<string> SplitLines(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>A project key: pen-&lt;clean name&gt;-&lt;8 hex&gt; (<see cref="Project.ComputeKey"/>).</summary>
    [GeneratedRegex("^pen-[a-z0-9_-]+-[0-9a-f]{8}$")]
    private static partial Regex KeyPattern();

    /// <summary>What follows a key in a project layer's name: a worktree's hash, if any, and the tag.</summary>
    [GeneratedRegex("^(-[0-9a-f]{8})?:(latest|[0-9a-f]{16})$")]
    private static partial Regex LayerSuffix();

    /// <summary>A tools image: pen-tools:&lt;16 hex&gt;.</summary>
    [GeneratedRegex(@"^pen-tools:[0-9a-f]{16}\z")]
    private static partial Regex ToolsTag();
}
