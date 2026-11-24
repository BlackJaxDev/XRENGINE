using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Process-wide, invalidatable GUID index for one Unity project snapshot.
/// </summary>
public sealed class SourceGuidIndex : IDisposable
{
    private const string DiskCacheHeaderPrefix = "#xrengine-unity-guid-index-v1";

    private static readonly ConcurrentDictionary<string, SourceGuidIndex> CachedIndexes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();
    private readonly Dictionary<string, SourceGuidResolution> _byGuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _guidByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _metaWriteTicksByMetaPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SourceImportDiagnostic> _diagnostics = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _initialized;
    private bool _invalidated;
    private bool _loadedFromDiskCache;
    private bool _bypassDiskCacheOnce;
    private bool _disposed;

    private SourceGuidIndex(string projectRoot)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
    }

    public string ProjectRoot { get; }
    public int ScanCount { get; private set; }
    public int Generation { get; private set; }

    public IReadOnlyList<SourceImportDiagnostic> Diagnostics
    {
        get
        {
            EnsureInitialized();
            lock (_sync)
                return [.. _diagnostics];
        }
    }

    public static SourceGuidIndex GetOrCreate(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string normalized = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return CachedIndexes.GetOrAdd(normalized, static root => new SourceGuidIndex(root));
    }

    public static void Refresh(string projectRoot)
        => GetOrCreate(projectRoot).Invalidate();

    public SourceGuidResolution Resolve(string guid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guid);
        EnsureInitialized();
        lock (_sync)
        {
            if (_byGuid.TryGetValue(guid, out SourceGuidResolution? resolution))
            {
                if (!_loadedFromDiskCache || IsCachedResolutionCurrent(guid, resolution))
                    return resolution;

                RebuildIndexFromDiskLocked();
                return _byGuid.TryGetValue(guid, out resolution)
                    ? resolution
                    : new SourceGuidResolution { Guid = guid };
            }

            if (_loadedFromDiskCache)
            {
                // A missing GUID in a disk-cached snapshot can mean an asset was added
                // while the editor was closed. Rescan once so new assets always resolve.
                RebuildIndexFromDiskLocked();
                if (_byGuid.TryGetValue(guid, out resolution))
                    return resolution;
            }

            return new SourceGuidResolution { Guid = guid };
        }
    }

    public string? ResolvePath(string? guid)
        => string.IsNullOrWhiteSpace(guid) ? null : Resolve(guid).Selected?.AssetPath;

    public bool TryGetGuid(string assetPath, out string? guid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        EnsureInitialized();
        string fullPath = Path.GetFullPath(assetPath);
        lock (_sync)
        {
            if (_guidByPath.TryGetValue(fullPath, out guid))
            {
                if (!_loadedFromDiskCache || IsCachedMetaPathCurrent(fullPath + ".meta", guid))
                    return true;

                RebuildIndexFromDiskLocked();
                return _guidByPath.TryGetValue(fullPath, out guid);
            }

            if (_loadedFromDiskCache)
            {
                RebuildIndexFromDiskLocked();
                return _guidByPath.TryGetValue(fullPath, out guid);
            }

            return false;
        }
    }

    /// <summary>
    /// Returns a snapshot of all indexed asset paths under this Unity project without re-walking disk.
    /// </summary>
    internal string[] SnapshotIndexedAssetPaths()
    {
        EnsureInitialized();
        lock (_sync)
            return [.. _guidByPath.Keys];
    }

    /// <summary>
    /// Resolves a sidecar disk cache file path for project-scoped Unity index metadata,
    /// or returns null for ephemeral temp directories or when no asset cache root is configured.
    /// </summary>
    internal string? TryResolveProjectSidecarCachePath(string suffix)
    {
        string? cacheRoot = Engine.Assets?.GameCachePath;
        if (string.IsNullOrWhiteSpace(cacheRoot))
            return null;

        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (IsPathBelow(ProjectRoot, tempRoot))
            return null;

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(ProjectRoot.ToUpperInvariant()));
        string hashHex = Convert.ToHexString(hashBytes)[..24];
        return Path.Combine(cacheRoot, "UnityGuidIndex", $"{hashHex}.{suffix}");
    }

    public string NormalizePortablePath(string assetPath)
    {
        string fullPath = Path.GetFullPath(assetPath);
        foreach (SourceSearchRoot root in EnumerateSearchRoots())
        {
            if (!IsPathBelow(fullPath, root.PhysicalPath))
                continue;

            string relative = Path.GetRelativePath(root.PhysicalPath, fullPath).Replace('\\', '/');
            return string.IsNullOrEmpty(relative)
                ? root.PortablePrefix
                : $"{root.PortablePrefix.TrimEnd('/')}/{relative}";
        }

        return fullPath.Replace('\\', '/');
    }

    public void Invalidate()
    {
        lock (_sync)
        {
            _invalidated = true;
            _bypassDiskCacheOnce = true;
        }
    }

    private void EnsureInitialized()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialized && !_invalidated)
                return;

            BuildIndex();
        }
    }

    private void RebuildIndexFromDiskLocked()
    {
        _bypassDiskCacheOnce = true;
        BuildIndex();
    }

    private void BuildIndex()
    {
        _byGuid.Clear();
        _guidByPath.Clear();
        _metaWriteTicksByMetaPath.Clear();
        _diagnostics.Clear();
        _loadedFromDiskCache = false;

        List<SourceSearchRoot> roots = [.. EnumerateSearchRoots()];
        bool allowDiskCache = !_bypassDiskCacheOnce;
        _bypassDiskCacheOnce = false;

        if (allowDiskCache && TryLoadFromDiskCacheLocked(roots))
        {
            ResetWatchers(roots);
            _initialized = true;
            _invalidated = false;
            _loadedFromDiskCache = true;
            ScanCount++;
            Generation++;
            return;
        }

        var candidatesByGuid = new Dictionary<string, List<SourceGuidCandidate>>(StringComparer.OrdinalIgnoreCase);
        var scanDiagnostics = new ConcurrentBag<SourceImportDiagnostic>();
        var scannedEntries = new ConcurrentBag<(SourceGuidCandidate Candidate, long MetaWriteTicks)>();

        foreach (SourceSearchRoot root in roots)
        {
            List<string> metaFiles = [];
            HashSet<string> existingEntries = new(StringComparer.OrdinalIgnoreCase);

            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    ReturnSpecialDirectories = false,
                };

                // One recursive pass collects both .meta files and sibling assets/directories
                // so we avoid separate per-file File.Exists and Directory.Exists syscalls.
                foreach (string entry in Directory.EnumerateFileSystemEntries(root.PhysicalPath, "*", options))
                {
                    if (entry.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        metaFiles.Add(entry);
                    else
                        existingEntries.Add(entry);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                scanDiagnostics.Add(new SourceImportDiagnostic
                {
                    Code = "UNITYGUID0001",
                    Severity = SourceImportDiagnosticSeverity.Warning,
                    Category = SourceImportDiagnosticCategory.GuidResolution,
                    SourcePath = root.PhysicalPath,
                    Message = $"Could not scan Unity GUID root '{root.PhysicalPath}': {ex.Message}",
                });
                continue;
            }

            Parallel.ForEach(
                metaFiles,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 16) },
                metaPath =>
                {
                    string assetPath = metaPath[..^5];
                    if (!existingEntries.Contains(assetPath))
                        return;

                    string? guid;
                    long metaWriteTicks;
                    try
                    {
                        guid = TryReadGuid(metaPath);
                        if (string.IsNullOrWhiteSpace(guid))
                            return;

                        metaWriteTicks = File.GetLastWriteTimeUtc(metaPath).Ticks;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return;
                    }

                    string relative = Path.GetRelativePath(root.PhysicalPath, assetPath).Replace('\\', '/');
                    var candidate = new SourceGuidCandidate
                    {
                        Guid = guid,
                        AssetPath = Path.GetFullPath(assetPath),
                        MetaPath = Path.GetFullPath(metaPath),
                        PortablePath = string.IsNullOrEmpty(relative)
                            ? root.PortablePrefix
                            : $"{root.PortablePrefix.TrimEnd('/')}/{relative}",
                        Precedence = root.Precedence,
                    };
                    scannedEntries.Add((candidate, metaWriteTicks));
                });
        }

        foreach (SourceImportDiagnostic diagnostic in scanDiagnostics)
            _diagnostics.Add(diagnostic);

        foreach ((SourceGuidCandidate candidate, long metaWriteTicks) in scannedEntries)
        {
            _metaWriteTicksByMetaPath[candidate.MetaPath] = metaWriteTicks;
            if (!candidatesByGuid.TryGetValue(candidate.Guid, out List<SourceGuidCandidate>? candidates))
            {
                candidates = [];
                candidatesByGuid.Add(candidate.Guid, candidates);
            }

            if (!candidates.Any(existing => string.Equals(existing.AssetPath, candidate.AssetPath, StringComparison.OrdinalIgnoreCase)))
                candidates.Add(candidate);
        }

        PopulateIndexFromCandidatesLocked(candidatesByGuid);
        TrySaveToDiskCacheLocked(roots);

        ResetWatchers(roots);
        _initialized = true;
        _invalidated = false;
        ScanCount++;
        Generation++;
    }

    private void PopulateIndexFromCandidatesLocked(Dictionary<string, List<SourceGuidCandidate>> candidatesByGuid)
    {
        foreach ((string guid, List<SourceGuidCandidate> candidates) in candidatesByGuid)
        {
            SourceGuidCandidate[] ordered =
            [
                .. candidates
                    .OrderBy(static candidate => candidate.Precedence)
                    .ThenBy(static candidate => candidate.PortablePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static candidate => candidate.AssetPath, StringComparer.OrdinalIgnoreCase),
            ];
            var resolution = new SourceGuidResolution
            {
                Guid = guid,
                Selected = ordered[0],
                Candidates = ordered,
            };
            _byGuid.Add(guid, resolution);
            _guidByPath.TryAdd(ordered[0].AssetPath, guid);

            if (ordered.Length > 1)
            {
                string paths = string.Join(", ", ordered.Select(static candidate => candidate.AssetPath));
                _diagnostics.Add(new SourceImportDiagnostic
                {
                    Code = "UNITYGUID0002",
                    Severity = SourceImportDiagnosticSeverity.Warning,
                    Category = SourceImportDiagnosticCategory.GuidResolution,
                    SourcePath = ordered[0].AssetPath,
                    Message = $"Duplicate Unity GUID '{guid}' resolved deterministically to '{ordered[0].AssetPath}'. Candidates: {paths}",
                });
            }
        }
    }

    private bool IsCachedResolutionCurrent(string guid, SourceGuidResolution resolution)
    {
        if (resolution.Selected is not { } selected)
            return false;

        if (!File.Exists(selected.AssetPath) && !Directory.Exists(selected.AssetPath))
            return false;

        return IsCachedMetaPathCurrent(selected.MetaPath, guid);
    }

    private bool IsCachedMetaPathCurrent(string metaPath, string expectedGuid)
    {
        if (!File.Exists(metaPath))
            return false;

        long currentTicks = File.GetLastWriteTimeUtc(metaPath).Ticks;
        if (_metaWriteTicksByMetaPath.TryGetValue(metaPath, out long cachedTicks) && cachedTicks == currentTicks)
            return true;

        try
        {
            string? diskGuid = TryReadGuid(metaPath);
            if (!string.Equals(diskGuid, expectedGuid, StringComparison.OrdinalIgnoreCase))
                return false;

            _metaWriteTicksByMetaPath[metaPath] = currentTicks;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string? TryResolveDiskCachePath()
    {
        string? cacheRoot = Engine.Assets?.GameCachePath;
        if (string.IsNullOrWhiteSpace(cacheRoot))
            return null;

        // Do not persist caches for ephemeral unit-test project directories under %TEMP%.
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (IsPathBelow(ProjectRoot, tempRoot))
            return null;

        byte[] hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(ProjectRoot.ToUpperInvariant()));
        string hashHex = Convert.ToHexString(hashBytes)[..24];
        return Path.Combine(cacheRoot, "UnityGuidIndex", $"{hashHex}.v1.tsv");
    }

    private string BuildCacheHeader(IReadOnlyList<SourceSearchRoot> roots)
    {
        string packagesRoot = Path.Combine(ProjectRoot, "Packages");
        long manifestTicks = GetFileWriteTicksOrZero(Path.Combine(packagesRoot, "manifest.json"));
        long lockTicks = GetFileWriteTicksOrZero(Path.Combine(packagesRoot, "packages-lock.json"));
        string rootSignature = string.Join(
            "|",
            roots.Select(static r => $"{r.Precedence}:{r.PortablePrefix}:{r.PhysicalPath}"));
        return $"{DiskCacheHeaderPrefix}\t{ProjectRoot}\t{manifestTicks}\t{lockTicks}\t{rootSignature}";
    }

    private bool TryLoadFromDiskCacheLocked(IReadOnlyList<SourceSearchRoot> roots)
    {
        string? cachePath = TryResolveDiskCachePath();
        if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
            return false;

        try
        {
            string expectedHeader = BuildCacheHeader(roots);
            var candidatesByGuid = new Dictionary<string, List<SourceGuidCandidate>>(StringComparer.OrdinalIgnoreCase);

            using var reader = new StreamReader(cachePath, Encoding.UTF8);
            string? header = reader.ReadLine();
            if (!string.Equals(header, expectedHeader, StringComparison.Ordinal))
                return false;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Length == 0)
                    continue;

                string[] parts = line.Split('\t');
                if (parts.Length != 6 ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int precedence) ||
                    !long.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out long metaWriteTicks))
                {
                    return false;
                }

                var candidate = new SourceGuidCandidate
                {
                    Guid = parts[0],
                    Precedence = precedence,
                    PortablePath = parts[2],
                    AssetPath = parts[3],
                    MetaPath = parts[4],
                };
                _metaWriteTicksByMetaPath[candidate.MetaPath] = metaWriteTicks;
                if (!candidatesByGuid.TryGetValue(candidate.Guid, out List<SourceGuidCandidate>? candidates))
                {
                    candidates = [];
                    candidatesByGuid.Add(candidate.Guid, candidates);
                }

                candidates.Add(candidate);
            }

            if (candidatesByGuid.Count == 0)
                return false;

            PopulateIndexFromCandidatesLocked(candidatesByGuid);
            Debug.Meshes($"[UnityGuidIndex] Loaded {candidatesByGuid.Count} cached Unity GUIDs for '{ProjectRoot}'.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            _byGuid.Clear();
            _guidByPath.Clear();
            _metaWriteTicksByMetaPath.Clear();
            _diagnostics.Clear();
            return false;
        }
    }

    private void TrySaveToDiskCacheLocked(IReadOnlyList<SourceSearchRoot> roots)
    {
        string? cachePath = TryResolveDiskCachePath();
        if (string.IsNullOrWhiteSpace(cachePath) || _byGuid.Count == 0)
            return;

        try
        {
            string? directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            using (var writer = new StreamWriter(tempPath, append: false, Encoding.UTF8))
            {
                writer.WriteLine(BuildCacheHeader(roots));
                foreach (SourceGuidResolution resolution in _byGuid.Values)
                {
                    foreach (SourceGuidCandidate candidate in resolution.Candidates)
                    {
                        long metaTicks = _metaWriteTicksByMetaPath.TryGetValue(candidate.MetaPath, out long ticks)
                            ? ticks
                            : 0L;
                        writer.Write(candidate.Guid);
                        writer.Write('\t');
                        writer.Write(candidate.Precedence.ToString(CultureInfo.InvariantCulture));
                        writer.Write('\t');
                        writer.Write(candidate.PortablePath);
                        writer.Write('\t');
                        writer.Write(candidate.AssetPath);
                        writer.Write('\t');
                        writer.Write(candidate.MetaPath);
                        writer.Write('\t');
                        writer.WriteLine(metaTicks.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            File.Move(tempPath, cachePath, overwrite: true);
            Debug.Meshes($"[UnityGuidIndex] Saved {_byGuid.Count} Unity GUIDs to disk cache for '{ProjectRoot}'.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Disk cache writes are opportunistic and never fail an import.
        }
    }

    private static long GetFileWriteTicksOrZero(string path)
        => File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0L;

    private IEnumerable<SourceSearchRoot> EnumerateSearchRoots()
    {
        string assetsRoot = Path.Combine(ProjectRoot, "Assets");
        if (Directory.Exists(assetsRoot))
        {
            yield return new SourceSearchRoot
            {
                PhysicalPath = Path.GetFullPath(assetsRoot),
                PortablePrefix = "Assets",
                Precedence = 0,
            };
        }

        string packagesRoot = Path.Combine(ProjectRoot, "Packages");
        if (Directory.Exists(packagesRoot))
        {
            yield return new SourceSearchRoot
            {
                PhysicalPath = Path.GetFullPath(packagesRoot),
                PortablePrefix = "Packages",
                Precedence = 10,
            };
        }

        foreach (SourceSearchRoot root in EnumerateManifestPackageRoots(packagesRoot))
            yield return root;
    }

    private IEnumerable<SourceSearchRoot> EnumerateManifestPackageRoots(string packagesRoot)
    {
        Dictionary<string, string> dependencies = ReadPackageDependencies(Path.Combine(packagesRoot, "manifest.json"));
        Dictionary<string, string> lockedVersions = ReadLockedPackageVersions(Path.Combine(packagesRoot, "packages-lock.json"));
        string packageCacheRoot = Path.Combine(ProjectRoot, "Library", "PackageCache");

        foreach ((string packageName, string declaredVersion) in dependencies.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            string version = lockedVersions.TryGetValue(packageName, out string? lockedVersion)
                ? lockedVersion
                : declaredVersion;
            string? localPath = ResolveLocalPackagePath(packagesRoot, declaredVersion);
            if (localPath is not null && Directory.Exists(localPath))
            {
                yield return new SourceSearchRoot
                {
                    PhysicalPath = Path.GetFullPath(localPath),
                    PortablePrefix = $"Packages/{packageName}",
                    Precedence = 20,
                };
                continue;
            }

            if (!Directory.Exists(packageCacheRoot))
                continue;

            string exactPath = Path.Combine(packageCacheRoot, $"{packageName}@{version}");
            string? cachePath = Directory.Exists(exactPath)
                ? exactPath
                : Directory.EnumerateDirectories(packageCacheRoot, $"{packageName}@*")
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
            if (cachePath is null)
                continue;

            yield return new SourceSearchRoot
            {
                PhysicalPath = Path.GetFullPath(cachePath),
                PortablePrefix = $"Packages/{packageName}",
                Precedence = 30,
            };
        }
    }

    private static Dictionary<string, string> ReadPackageDependencies(string path)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return dependencies;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("dependencies", out JsonElement values))
                return dependencies;

            foreach (JsonProperty property in values.EnumerateObject())
                dependencies[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            // Dependency parsing reports the malformed file when it becomes reachable.
        }

        return dependencies;
    }

    private static Dictionary<string, string> ReadLockedPackageVersions(string path)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return versions;

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("dependencies", out JsonElement values))
                return versions;

            foreach (JsonProperty property in values.EnumerateObject())
            {
                if (property.Value.TryGetProperty("version", out JsonElement version))
                    versions[property.Name] = version.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Dependency parsing reports the malformed file when it becomes reachable.
        }

        return versions;
    }

    private static string? ResolveLocalPackagePath(string packagesRoot, string declaredVersion)
    {
        const string filePrefix = "file:";
        if (!declaredVersion.StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string localPath = declaredVersion[filePrefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(localPath)
            ? Path.GetFullPath(localPath)
            : Path.GetFullPath(Path.Combine(packagesRoot, localPath));
    }

    private void ResetWatchers(IEnumerable<SourceSearchRoot> roots)
    {
        foreach (FileSystemWatcher watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();

        foreach (string path in roots
            .Select(static root => root.PhysicalPath)
            .Append(Path.Combine(ProjectRoot, "Packages"))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(path))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(path)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnSourceChanged;
                watcher.Created += OnSourceChanged;
                watcher.Deleted += OnSourceChanged;
                watcher.Renamed += OnSourceRenamed;
                _watchers.Add(watcher);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Add(new SourceImportDiagnostic
                {
                    Code = "UNITYGUID0003",
                    Severity = SourceImportDiagnosticSeverity.Warning,
                    Category = SourceImportDiagnosticCategory.GuidResolution,
                    SourcePath = path,
                    Message = $"GUID index watcher could not monitor '{path}': {ex.Message}",
                });
            }
        }
    }

    private void OnSourceChanged(object sender, FileSystemEventArgs args)
    {
        string fileName = Path.GetFileName(args.FullPath);
        if (args.FullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "manifest.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "packages-lock.json", StringComparison.OrdinalIgnoreCase))
        {
            Invalidate();
        }
    }

    private void OnSourceRenamed(object sender, RenamedEventArgs args)
    {
        OnSourceChanged(sender, args);
        if (args.OldFullPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            Invalidate();
    }

    private static string? TryReadGuid(string metaPath)
    {
        foreach (string line in File.ReadLines(metaPath))
        {
            const string prefix = "guid:";
            if (line.StartsWith(prefix, StringComparison.Ordinal))
                return line[prefix.Length..].Trim();
        }

        return null;
    }

    private static bool IsPathBelow(string path, string root)
    {
        string relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            foreach (FileSystemWatcher watcher in _watchers)
                watcher.Dispose();
            _watchers.Clear();
            _disposed = true;
        }
    }
}
