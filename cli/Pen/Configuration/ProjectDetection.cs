using System.IO.Enumeration;
using System.Text.Json;
using System.Text.RegularExpressions;
using Pen.Masking;
using Pen.Platform;
using Pen.Tools;
using Tomlyn;
using Tomlyn.Model;

namespace Pen.Configuration;

/// <summary>What a read-only look at a project's files suggests for its config: tools, hide patterns, hints.</summary>
/// <remarks>
/// It's advisory: anything it can't read is skipped, and repo content never makes it throw. The walk lists
/// directories up to three levels below the project dir (so a/b/c/go.mod counts), at most
/// <see cref="MaxEntries"/> entries, doesn't follow the symlinks it lists (a race can still swap one in; see
/// <c>Scan.VisitFile</c>) and never enters <see cref="SkippedDirs"/>.
/// Values are only recipe-validated specs and patterns that pass <see cref="HidePattern.TryParse"/>. The paths
/// kept as sources and hints are only those that can go in a comment or on a terminal (<see cref="IsSafeName"/>);
/// a file under an unsafe name still counts, just without its path.
/// </remarks>
internal sealed partial record ProjectDetection
{
    /// <summary>The deepest directory listed, counted from the project dir at 0.</summary>
    public const int MaxDepth = 3;

    /// <summary>The most entries the walk looks at.</summary>
    public const int MaxEntries = 20_000;

    /// <summary>Directories the walk never enters, at any depth (names compared ignoring case).</summary>
    public static readonly IReadOnlyList<string> SkippedDirs =
        [".git", ".pen", "node_modules", "vendor", "target", "bin", "obj", "dist", "build", ".venv"];

    /// <summary>Nothing detected: the template with everything commented.</summary>
    public static ProjectDetection None { get; } = new();

    /// <summary>The detected tools, in canonical order (<see cref="ToolSpec.TryCanonicalize"/>).</summary>
    public IReadOnlyList<DetectedTool> Tools { get; init; } = [];

    /// <summary>
    /// The detected hide patterns: .env, then .env.* names, then key file patterns anchored at their directories
    /// (*.pem, *.key, *.p12, *.pfx, in that order), then secrets/; within each, ordinally.
    /// </summary>
    public IReadOnlyList<DetectedPattern> Hide { get; init; } = [];

    /// <summary>The Dockerfile, compose.yaml and docker-compose.yml files found, by project-relative path.</summary>
    public IReadOnlyList<string> DockerFiles { get; init; } = [];

    /// <summary>Each .env with a .env.example beside it, for the commented [replace] example.</summary>
    public IReadOnlyList<ReplaceHint> ReplaceHints { get; init; } = [];

    /// <summary>Whether a package.json was found (Node is in the base image, so it brings no tool).</summary>
    public bool HasNode { get; init; }

    /// <summary>Whether a pyproject.toml or requirements.txt was found (Python is in the base image).</summary>
    public bool HasPython { get; init; }

    /// <summary>Gets the detected tools' specs, in canonical order.</summary>
    public IEnumerable<ToolSpec> ToolSpecs => Tools.Select(tool => tool.Tool);

    /// <summary>Looks at the files under <paramref name="projectDir"/>.</summary>
    /// <param name="projectDir">The project dir, by real path.</param>
    /// <param name="catalog">The recipes that tool versions are checked against.</param>
    public static ProjectDetection Detect(string projectDir, RecipeCatalog catalog) =>
        new Scan(projectDir, catalog).Run();

    /// <summary>Checks whether a name or path can be written into a comment or onto a terminal.</summary>
    /// <returns>False if it has a control character (a newline included) or U+FFFD, which stands for bytes that
    /// weren't valid UTF-8.</returns>
    internal static bool IsSafeName(string name) => !name.Any(c => Out.IsControl(c) || c == '\uFFFD');

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z")]
    private static partial Regex EnvSuffixRegex();

    /// <summary>The walk over the project and what it collects.</summary>
    private sealed class Scan(string projectDir, RecipeCatalog catalog)
    {
        private static readonly EnumerationOptions Options = new()
        {
            AttributesToSkip = 0, // dot files are "hidden" on Unix, and .env is one
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        private static readonly JsonDocumentOptions JsonOptions = new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        };

        private static readonly string[] EnvExampleNames = ["example", "sample", "template", "dist", "defaults"];
        private static readonly string[] KeyExtensions = [".pem", ".key", ".p12", ".pfx"];
        private static readonly string[] DotnetProjectExtensions = [".csproj", ".fsproj", ".sln", ".slnx"];
        private static readonly string[] JavaMarkers =
        [
            "pom.xml", "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "mvnw", "gradlew",
        ];
        private static readonly string[] DockerNames = ["Dockerfile", "compose.yaml", "docker-compose.yml"];

        private readonly List<FoundFile> _globalJsons = [];
        private readonly List<FoundFile> _dotnetProjects = [];
        private readonly List<FoundFile> _goMods = [];
        private readonly List<FoundFile> _cargoTomls = [];
        private readonly List<FoundFile> _javaMarkers = [];

        /// <summary>Version files, by the project-relative directory they're in.</summary>
        private readonly Dictionary<string, FoundFile> _rustToolchainTomls = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FoundFile> _rustToolchains = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FoundFile> _javaVersions = new(StringComparer.Ordinal);

        /// <summary>The hide patterns found, by pattern ignoring case as matching does.</summary>
        private readonly Dictionary<string, HideFinding> _hide = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tools found, with what they were found from.</summary>
        private readonly Dictionary<ToolSpec, ToolFinding> _tools = [];

        private readonly SortedSet<string> _dockerFiles = new(StringComparer.Ordinal);
        private readonly List<ReplaceHint> _replaceHints = [];
        private bool _hasNode;
        private bool _hasPython;
        private int _entries;

        /// <summary>Walks the project and turns what it found into the detection.</summary>
        public ProjectDetection Run()
        {
            Walk();
            AddDotnet();
            foreach (var goMod in _goMods)
                AddTool("go", null, null, [goMod]);
            AddWithVersionFiles("rust", _cargoTomls, ReadRustChannel, _rustToolchains, _rustToolchainTomls);
            AddWithVersionFiles("java", _javaMarkers, ReadJavaVersion, _javaVersions);
            return new ProjectDetection
            {
                Tools = BuildTools(),
                Hide = BuildHide(),
                DockerFiles = [.. _dockerFiles],
                ReplaceHints = [.. _replaceHints.OrderBy(hint => hint.Target, StringComparer.Ordinal)],
                HasNode = _hasNode,
                HasPython = _hasPython,
            };
        }

        /// <summary>Lists directories breadth first, to <see cref="MaxDepth"/> and <see cref="MaxEntries"/>.</summary>
        private void Walk()
        {
            var queue = new Queue<Place>();
            queue.Enqueue(new Place(projectDir, "", 0, IsSafe: true));
            while (queue.Count > 0 && _entries < MaxEntries)
            {
                var dir = queue.Dequeue();
                var entries = List(dir);
                foreach (var entry in entries)
                {
                    var relative = dir.Relative.Length == 0 ? entry.Name : $"{dir.Relative}/{entry.Name}";
                    var isSafe = dir.IsSafe && IsSafeName(entry.Name);
                    var file = new FoundFile(Path.Combine(dir.Path, entry.Name), relative, dir.Relative, isSafe);
                    if (entry.IsDirectory && !entry.IsLink)
                    {
                        VisitDirectory(entry.Name, file);
                        if (dir.Depth < MaxDepth && !IsSkipped(entry.Name))
                            queue.Enqueue(new Place(file.Path, relative, dir.Depth + 1, file.IsSafe));
                    }
                    else
                    {
                        VisitFile(entry, file);
                    }
                }
                if (entries.Count > 0)
                    AddReplaceHint(dir, entries);
            }
        }

        /// <summary>Lists a directory, up to what's left of <see cref="MaxEntries"/>.</summary>
        /// <returns>The entries, or as many as could be listed before an error.</returns>
        private List<Entry> List(Place dir)
        {
            var entries = new List<Entry>();
            try
            {
                var listing = new FileSystemEnumerable<Entry>(dir.Path,
                    (ref FileSystemEntry e) => new Entry(
                        e.FileName.ToString(), e.IsDirectory, (e.Attributes & FileAttributes.ReparsePoint) != 0),
                    Options);
                foreach (var entry in listing)
                {
                    if (_entries >= MaxEntries)
                        break;
                    _entries++;
                    entries.Add(entry);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
            return entries;
        }

        private static bool IsSkipped(string name) =>
            SkippedDirs.Contains(name, StringComparer.OrdinalIgnoreCase);

        private void VisitDirectory(string name, FoundFile dir)
        {
            if (name.Equals("secrets", StringComparison.OrdinalIgnoreCase))
                AddHide("secrets/", dir);
        }

        /// <summary>Records what a file's name says.</summary>
        /// <remarks>
        /// A symlink counts only for hide patterns, which match its name (the sandbox hides what it leads to);
        /// it's never a toolchain marker, version file or hint, so the walk doesn't read through a symlink it
        /// listed. That can be raced: a file listed as regular and replaced by a symlink before it's read is read
        /// through it (<see cref="Native.ReadRegularFile"/> follows symlinks), as a directory replaced before
        /// it's listed is listed through it. The most that does is suggest a tool version from a file outside the
        /// project, and only one that passes the recipe's check, or report names found there.
        /// </remarks>
        private void VisitFile(Entry entry, FoundFile file)
        {
            var name = entry.Name;
            VisitHideName(name, file);
            if (entry.IsLink)
                return;
            switch (name)
            {
                case "global.json":
                    _globalJsons.Add(file);
                    break;
                case "go.mod":
                    _goMods.Add(file);
                    break;
                case "Cargo.toml":
                    _cargoTomls.Add(file);
                    break;
                case "rust-toolchain.toml":
                    _rustToolchainTomls[file.Directory] = file;
                    break;
                case "rust-toolchain":
                    _rustToolchains[file.Directory] = file;
                    break;
                case ".java-version":
                    _javaVersions[file.Directory] = file;
                    break;
                case "package.json":
                    _hasNode = true;
                    break;
                case "pyproject.toml" or "requirements.txt":
                    _hasPython = true;
                    break;
                default:
                    VisitOtherFile(name, file);
                    break;
            }
        }

        private void VisitOtherFile(string name, FoundFile file)
        {
            if (JavaMarkers.Contains(name))
                _javaMarkers.Add(file);
            else if (DotnetProjectExtensions.Any(extension => name.EndsWith(extension, StringComparison.Ordinal)))
                _dotnetProjects.Add(file);
            else if (DockerNames.Contains(name) && file.IsSafe)
                _dockerFiles.Add(file.Relative);
        }

        /// <summary>Adds the hide pattern a file's name calls for, if any.</summary>
        private void VisitHideName(string name, FoundFile file)
        {
            if (name.Equals(".env", StringComparison.OrdinalIgnoreCase))
            {
                AddHide(".env", file);
            }
            else if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
            {
                var suffix = name[".env.".Length..];
                var lastPart = suffix[(suffix.LastIndexOf('.') + 1)..];
                if (EnvSuffixRegex().IsMatch(suffix)
                    && !EnvExampleNames.Contains(suffix, StringComparer.OrdinalIgnoreCase)
                    && !EnvExampleNames.Contains(lastPart, StringComparer.OrdinalIgnoreCase))
                {
                    AddHide(name, file);
                }
            }
            else if (KeyExtensions.FirstOrDefault(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase)) is { } ext
                && name.Length > ext.Length
                && AnchorKeyPattern(file, ext) is { } pattern)
            {
                AddHide(pattern, file);
            }
        }

        /// <summary>Makes the pattern hiding <paramref name="extension"/> files in a key file's directory.</summary>
        /// <returns>
        /// /dir/*.ext for a file in dir, or /*.ext at the root; a directory name that can't be written literally
        /// becomes *, so the pattern keeps the file's depth (/a/*/d/*.ext); null if no pattern would match the
        /// file (never expected).
        /// </returns>
        /// <remarks>
        /// Anchored, since an any-depth *.pem would also hide what masking finds in <see cref="SkippedDirs"/>,
        /// which this walk never looks at (a venv's certifi/cacert.pem, which Python needs for HTTPS; test
        /// certificates under node_modules, enough of them to pass the mount cap). A directory name a pattern
        /// would read as a wildcard or refuse, or one that can't go in a comment, is written as * and never
        /// as **, so the pattern matches at one depth only.
        /// </remarks>
        private static string? AnchorKeyPattern(FoundFile file, string extension)
        {
            var segments = file.Directory.Length == 0 ? [] : file.Directory.Split('/');
            var directories = string.Concat(
                segments.Select(segment => "/" + (IsLiteralSegment(segment) ? segment : "*")));
            var pattern = $"{directories}/*{extension}";
            return HidePattern.TryParse(pattern, out var parsed) is null
                && parsed!.IsMatch(file.Relative, isDirectory: false)
                    ? pattern
                    : null;
        }

        /// <summary>Checks whether a directory name can be a literal segment of a hide pattern.</summary>
        /// <remarks>
        /// *, ? and ** would be wildcards, and [, ] and \ are refused. Spaces, ! and # are taken as written
        /// inside an anchored pattern, which starts with / and ends with the extension; anything else
        /// <see cref="HidePattern.TryParse"/> refuses is caught by trying the pattern.
        /// </remarks>
        private static bool IsLiteralSegment(string name) =>
            IsSafeName(name) && name.AsSpan().IndexOfAny("*?[]\\") < 0;

        /// <summary>Records a hide pattern and the file or directory it matched.</summary>
        /// <remarks>
        /// Spellings that differ only in case are one pattern, written in the ordinally least spelling, so the
        /// result doesn't depend on the order directories are listed in.
        /// </remarks>
        private void AddHide(string pattern, FoundFile file)
        {
            if (!_hide.TryGetValue(pattern, out var finding))
            {
                finding = new HideFinding(pattern);
                _hide[pattern] = finding;
            }
            else if (string.CompareOrdinal(pattern, finding.Pattern) < 0)
            {
                finding.Pattern = pattern;
            }
            if (file.IsSafe)
                finding.Sources.Add(file.Relative);
        }

        /// <summary>Notes a .env with a .env.example beside it in a directory with a safe path.</summary>
        private void AddReplaceHint(Place dir, List<Entry> entries)
        {
            if (!dir.IsSafe)
                return;
            bool IsPlainFile(string name) => entries.Any(e => e.Name == name && !e.IsDirectory && !e.IsLink);
            if (IsPlainFile(".env") && IsPlainFile(".env.example"))
            {
                var prefix = dir.Relative.Length == 0 ? "" : dir.Relative + "/";
                _replaceHints.Add(new ReplaceHint($"{prefix}.env", $"{prefix}.env.example"));
            }
        }

        /// <summary>Adds dotnet: the major of each global.json's sdk.version, or the default.</summary>
        /// <remarks>
        /// The default comes from a global.json without a readable version, or from project and solution files,
        /// but only when no global.json gave a version: a global.json that does is the more specific source.
        /// </remarks>
        private void AddDotnet()
        {
            var versionless = new List<FoundFile>();
            var versioned = false;
            foreach (var globalJson in _globalJsons)
            {
                if (ReadSdkVersion(globalJson) is { } version)
                {
                    AddTool("dotnet", FindMajor(version), version, [globalJson]);
                    versioned = true;
                }
                else
                {
                    versionless.Add(globalJson);
                }
            }
            if (versioned)
                return;
            if (versionless.Count > 0)
                AddTool("dotnet", null, null, versionless);
            else if (_dotnetProjects.Count > 0)
                AddTool("dotnet", null, null, _dotnetProjects);
        }

        /// <summary>
        /// Adds <paramref name="name"/> for each marker, at the version in the nearest version file at or above
        /// the marker's directory, or at the default when there is none.
        /// </summary>
        /// <param name="name">The tool.</param>
        /// <param name="markers">The files that show the project uses it.</param>
        /// <param name="read">Reads a version file's version: the version to try and the text found, or null.</param>
        /// <param name="versionFiles">Version files by directory, the first taking precedence in a directory.</param>
        private void AddWithVersionFiles(
            string name,
            List<FoundFile> markers,
            Func<FoundFile, FoundVersion?> read,
            params Dictionary<string, FoundFile>[] versionFiles)
        {
            foreach (var marker in markers)
            {
                if (FindVersionFile(marker.Directory, versionFiles) is { } versionFile
                    && read(versionFile) is { } found)
                {
                    AddTool(name, found.Version, found.Text, [marker, versionFile]);
                }
                else
                {
                    AddTool(name, null, null, [marker]);
                }
            }
        }

        private static FoundFile? FindVersionFile(string directory, Dictionary<string, FoundFile>[] versionFiles)
        {
            for (var dir = directory; dir is not null; dir = GetParent(dir))
            {
                foreach (var files in versionFiles)
                {
                    if (files.TryGetValue(dir, out var file))
                        return file;
                }
            }
            return null;
        }

        /// <summary>Gets a project-relative directory's parent: "" for one at the top, null for "".</summary>
        private static string? GetParent(string dir)
        {
            if (dir.Length == 0)
                return null;
            var slash = dir.LastIndexOf('/');
            return slash < 0 ? "" : dir[..slash];
        }

        /// <summary>Records a tool at <paramref name="version"/>, or its default if the recipe lacks it.</summary>
        /// <param name="name">The recipe's name.</param>
        /// <param name="version">The version to try, or null for the default.</param>
        /// <param name="found">What the file said, for the note when the version isn't known.</param>
        /// <param name="sources">The files it was found from.</param>
        private void AddTool(string name, string? version, string? found, IEnumerable<FoundFile> sources)
        {
            var tool = default(ToolSpec);
            var isPinned = version is not null && ToolSpec.TryParse($"{name}:{version}", catalog, out tool) is null;
            if (!isPinned && ToolSpec.TryParse(name, catalog, out tool) is not null)
                return;
            var unknown = !isPinned && version is not null;
            if (!_tools.TryGetValue(tool, out var finding))
            {
                finding = new ToolFinding();
                _tools[tool] = finding;
            }
            foreach (var source in sources.Where(source => source.IsSafe))
                finding.Sources.Add(source.Relative);
            finding.IsPinned |= isPinned;
            if (unknown)
            {
                finding.HasUnknownVersion = true;
                if (found is not null && ToolSpec.IsVersionSyntax(found))
                    finding.UnknownVersions.Add(found);
            }
        }

        /// <summary>Makes the canonical tool list, one version of a tool whose recipe allows only one.</summary>
        private List<DetectedTool> BuildTools()
        {
            var merged = new Dictionary<ToolSpec, ToolFinding>();
            foreach (var (tool, finding) in _tools)
            {
                var key = tool;
                if (catalog.TryGet(tool.Name, out var recipe) && !recipe.Multiple
                    && _tools.Keys.Count(other => other.Name == tool.Name) > 1)
                {
                    key = new ToolSpec(tool.Name, recipe.Default);
                }
                // A version given up for the default isn't a pin of the default.
                if (key != tool)
                    finding.IsPinned = false;
                if (merged.TryGetValue(key, out var existing))
                    existing.MergeFrom(finding);
                else
                    merged[key] = finding;
            }
            if (ToolSpec.TryCanonicalize(merged.Keys, catalog, out var canonical) is { } conflict)
                throw new InvalidOperationException($"detected tools don't go together: {conflict}");
            return [.. canonical.Select(tool => merged[tool].ToDetected(tool))];
        }

        private List<DetectedPattern> BuildHide()
        {
            var patterns = new List<DetectedPattern>();
            var ordered = _hide.Values
                .OrderBy(finding => RankHide(finding.Pattern))
                .ThenBy(finding => finding.Pattern, StringComparer.Ordinal);
            foreach (var finding in ordered)
            {
                if (HidePattern.TryParse(finding.Pattern, out _) is null)
                    patterns.Add(new DetectedPattern(finding.Pattern, [.. finding.Sources]));
            }
            return patterns;
        }

        /// <summary>Ranks a pattern: .env, .env.* names, key files by extension, then secrets/.</summary>
        private static int RankHide(string pattern)
        {
            if (pattern == ".env")
                return 0;
            if (pattern == "secrets/")
                return 2 + KeyExtensions.Length;
            if (!pattern.Contains('*'))
                return 1;
            var extension = Array.FindIndex(KeyExtensions, e => pattern.EndsWith(e, StringComparison.Ordinal));
            return 2 + extension;
        }

        private static string? ReadText(FoundFile file)
        {
            try
            {
                return Native.ReadRegularFile(file.Path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>Reads global.json's sdk.version.</summary>
        /// <returns>The version, trimmed, or null if it can't be read or isn't a non-empty string.</returns>
        private static string? ReadSdkVersion(FoundFile file)
        {
            if (ReadText(file) is not { } text)
                return null;
            try
            {
                using var document = JsonDocument.Parse(text, JsonOptions);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("sdk", out var sdk)
                    && sdk.ValueKind == JsonValueKind.Object
                    && sdk.TryGetProperty("version", out var version)
                    && version.ValueKind == JsonValueKind.String)
                {
                    return TrimToNull(version.GetString());
                }
            }
            catch (JsonException)
            {
            }
            return null;
        }

        /// <summary>Reads the channel from rust-toolchain.toml, or a rust-toolchain file in either format.</summary>
        private static FoundVersion? ReadRustChannel(FoundFile file)
        {
            if (ReadText(file) is not { } text)
                return null;
            string? channel;
            if (file.Relative.EndsWith(".toml", StringComparison.Ordinal) || text.Contains("[toolchain]"))
                channel = ReadTomlChannel(text, file.Relative);
            else
                channel = TrimToNull(ReadFirstLine(text));
            return channel is null ? null : new FoundVersion(channel, channel);
        }

        private static string? ReadTomlChannel(string text, string displayPath)
        {
            try
            {
                var table = Config.ParseToml(text, displayPath);
                if (table.TryGetValue("toolchain", out var toolchain) && toolchain is TomlTable toolchainTable
                    && toolchainTable.TryGetValue("channel", out var channel) && channel is string channelText)
                {
                    return TrimToNull(channelText);
                }
            }
            catch (Exception exception) when (exception is PenException or TomlException)
            {
            }
            return null;
        }

        /// <summary>Reads .java-version's first line, taking its major (8 for 1.8.x).</summary>
        private static FoundVersion? ReadJavaVersion(FoundFile file)
        {
            if (ReadText(file) is not { } text || TrimToNull(ReadFirstLine(text)) is not { } line)
                return null;
            var major = line.StartsWith("1.", StringComparison.Ordinal) ? FindMajor(line[2..]) : FindMajor(line);
            return new FoundVersion(major, line);
        }

        private static string FindMajor(string version)
        {
            var dot = version.IndexOf('.');
            return dot < 0 ? version : version[..dot];
        }

        private static string ReadFirstLine(string text)
        {
            var end = text.AsSpan().IndexOfAny('\r', '\n');
            return end < 0 ? text : text[..end];
        }

        private static string? TrimToNull(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>A directory to list.</summary>
    /// <param name="Path">Its full path.</param>
    /// <param name="Relative">Its project-relative path, "" for the project dir.</param>
    /// <param name="Depth">How far below the project dir it is.</param>
    /// <param name="IsSafe">Whether its relative path is safe for comments (<see cref="IsSafeName"/>).</param>
    private sealed record Place(string Path, string Relative, int Depth, bool IsSafe);

    /// <summary>One entry of a listing.</summary>
    private readonly record struct Entry(string Name, bool IsDirectory, bool IsLink);

    /// <summary>A file or directory the walk found.</summary>
    /// <param name="Path">Its full path.</param>
    /// <param name="Relative">Its project-relative path.</param>
    /// <param name="Directory">The project-relative directory it's in.</param>
    /// <param name="IsSafe">Whether its relative path is safe for comments (<see cref="IsSafeName"/>).</param>
    private sealed record FoundFile(string Path, string Relative, string Directory, bool IsSafe);

    /// <summary>A version read from a file: the version to try, and the text it came from.</summary>
    private sealed record FoundVersion(string Version, string Text);

    /// <summary>A hide pattern found, while the walk's results are put together.</summary>
    /// <param name="pattern">The pattern, in its first spelling.</param>
    private sealed class HideFinding(string pattern)
    {
        /// <summary>The pattern, in the ordinally least spelling found.</summary>
        public string Pattern { get; set; } = pattern;

        /// <summary>The paths it matched that are safe for comments.</summary>
        public SortedSet<string> Sources { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>What one tool was found from, while the walk's results are put together.</summary>
    private sealed class ToolFinding
    {
        public SortedSet<string> Sources { get; } = new(StringComparer.Ordinal);

        public bool IsPinned { get; set; }

        public bool HasUnknownVersion { get; set; }

        public SortedSet<string> UnknownVersions { get; } = new(StringComparer.Ordinal);

        public void MergeFrom(ToolFinding other)
        {
            Sources.UnionWith(other.Sources);
            UnknownVersions.UnionWith(other.UnknownVersions);
            IsPinned |= other.IsPinned;
            HasUnknownVersion |= other.HasUnknownVersion;
        }

        public DetectedTool ToDetected(ToolSpec tool) => new(tool, [.. Sources])
        {
            IsPinned = IsPinned,
            HasUnknownVersion = HasUnknownVersion,
            UnknownVersions = [.. UnknownVersions],
        };
    }
}

/// <summary>A tool detection turned on, with what it was found from.</summary>
/// <param name="Tool">The tool, valid for its recipe.</param>
/// <param name="Sources">The project-relative paths it was found from, sorted; only those safe for comments.</param>
internal sealed record DetectedTool(ToolSpec Tool, IReadOnlyList<string> Sources)
{
    /// <summary>Whether <see cref="Tool"/>'s version was read from a file and accepted by the recipe.</summary>
    /// <remarks>
    /// False when it's the recipe's default because nothing gave a version (or the one given was refused), so
    /// it can be written as the bare name and follow the recipe's default; true even when the version read
    /// equals today's default, so that it stays pinned when the default moves.
    /// </remarks>
    public bool IsPinned { get; init; }

    /// <summary>Whether a version found isn't one the recipe knows, so <see cref="Tool"/> is at its default.</summary>
    public bool HasUnknownVersion { get; init; }

    /// <summary>The unknown versions found, sorted; only those that have the form of a version.</summary>
    /// <remarks>Empty while <see cref="HasUnknownVersion"/> is true when none had that form.</remarks>
    public IReadOnlyList<string> UnknownVersions { get; init; } = [];
}

/// <summary>A hide pattern detection turned on, with the paths it matched.</summary>
/// <param name="Pattern">The pattern, which passes <see cref="HidePattern.TryParse"/>.</param>
/// <param name="Sources">The project-relative paths it matched, sorted; only those safe for comments.</param>
internal sealed record DetectedPattern(string Pattern, IReadOnlyList<string> Sources);

/// <summary>A .env with a .env.example beside it, for the commented [replace] example.</summary>
/// <param name="Target">The .env's project-relative path.</param>
/// <param name="Source">The .env.example's project-relative path.</param>
internal sealed record ReplaceHint(string Target, string Source);
