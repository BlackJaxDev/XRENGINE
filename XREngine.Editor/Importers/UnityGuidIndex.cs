using System.Collections.Concurrent;
using System.Text.Json;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Process-wide, invalidatable GUID index for one Unity project snapshot.
/// </summary>
public sealed class UnityGuidIndex : IDisposable
{
    private static readonly ConcurrentDictionary<string, UnityGuidIndex> CachedIndexes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();
    private readonly Dictionary<string, UnityGuidResolution> _byGuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _guidByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<UnityImportDiagnostic> _diagnostics = [];
    private readonly List<FileSystemWatcher> _watchers = [];
    private bool _initialized;
    private bool _invalidated;
    private bool _disposed;

    private UnityGuidIndex(string projectRoot)
    {
        ProjectRoot = Path.GetFullPath(projectRoot);
    }

    public string ProjectRoot { get; }
    public int ScanCount { get; private set; }
    public int Generation { get; private set; }

    public IReadOnlyList<UnityImportDiagnostic> Diagnostics
    {
        get
        {
            EnsureInitialized();
            lock (_sync)
                return [.. _diagnostics];
        }
    }

    public static UnityGuidIndex GetOrCreate(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        string normalized = Path.GetFullPath(projectRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return CachedIndexes.GetOrAdd(normalized, static root => new UnityGuidIndex(root));
    }

    public static void Refresh(string projectRoot)
        => GetOrCreate(projectRoot).Invalidate();

    public UnityGuidResolution Resolve(string guid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guid);
        EnsureInitialized();
        lock (_sync)
        {
            return _byGuid.TryGetValue(guid, out UnityGuidResolution? resolution)
                ? resolution
                : new UnityGuidResolution { Guid = guid };
        }
    }

    public string? ResolvePath(string? guid)
        => string.IsNullOrWhiteSpace(guid) ? null : Resolve(guid).Selected?.AssetPath;

    public bool TryGetGuid(string assetPath, out string? guid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetPath);
        EnsureInitialized();
        lock (_sync)
            return _guidByPath.TryGetValue(Path.GetFullPath(assetPath), out guid);
    }

    public string NormalizePortablePath(string assetPath)
    {
        string fullPath = Path.GetFullPath(assetPath);
        foreach (UnitySearchRoot root in EnumerateSearchRoots())
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
            _invalidated = true;
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

    private void BuildIndex()
    {
        _byGuid.Clear();
        _guidByPath.Clear();
        _diagnostics.Clear();

        var candidatesByGuid = new Dictionary<string, List<UnityGuidCandidate>>(StringComparer.OrdinalIgnoreCase);
        List<UnitySearchRoot> roots = [.. EnumerateSearchRoots()];
        foreach (UnitySearchRoot root in roots)
        {
            try
            {
                foreach (string metaPath in Directory
                    .EnumerateFiles(root.PhysicalPath, "*.meta", SearchOption.AllDirectories)
                    .Order(StringComparer.OrdinalIgnoreCase))
                {
                    string? guid = TryReadGuid(metaPath);
                    if (string.IsNullOrWhiteSpace(guid))
                        continue;

                    string assetPath = metaPath[..^5];
                    if (!File.Exists(assetPath) && !Directory.Exists(assetPath))
                        continue;

                    string relative = Path.GetRelativePath(root.PhysicalPath, assetPath).Replace('\\', '/');
                    var candidate = new UnityGuidCandidate
                    {
                        Guid = guid,
                        AssetPath = Path.GetFullPath(assetPath),
                        MetaPath = Path.GetFullPath(metaPath),
                        PortablePath = string.IsNullOrEmpty(relative)
                            ? root.PortablePrefix
                            : $"{root.PortablePrefix.TrimEnd('/')}/{relative}",
                        Precedence = root.Precedence,
                    };

                    if (!candidatesByGuid.TryGetValue(guid, out List<UnityGuidCandidate>? candidates))
                    {
                        candidates = [];
                        candidatesByGuid.Add(guid, candidates);
                    }

                    if (!candidates.Any(existing => string.Equals(existing.AssetPath, candidate.AssetPath, StringComparison.OrdinalIgnoreCase)))
                        candidates.Add(candidate);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _diagnostics.Add(new UnityImportDiagnostic
                {
                    Code = "UNITYGUID0001",
                    Severity = UnityImportDiagnosticSeverity.Warning,
                    Category = UnityImportDiagnosticCategory.GuidResolution,
                    SourcePath = root.PhysicalPath,
                    Message = $"Could not scan Unity GUID root '{root.PhysicalPath}': {ex.Message}",
                });
            }
        }

        foreach ((string guid, List<UnityGuidCandidate> candidates) in candidatesByGuid)
        {
            UnityGuidCandidate[] ordered =
            [
                .. candidates
                    .OrderBy(static candidate => candidate.Precedence)
                    .ThenBy(static candidate => candidate.PortablePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static candidate => candidate.AssetPath, StringComparer.OrdinalIgnoreCase),
            ];
            var resolution = new UnityGuidResolution
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
                _diagnostics.Add(new UnityImportDiagnostic
                {
                    Code = "UNITYGUID0002",
                    Severity = UnityImportDiagnosticSeverity.Warning,
                    Category = UnityImportDiagnosticCategory.GuidResolution,
                    SourcePath = ordered[0].AssetPath,
                    Message = $"Duplicate Unity GUID '{guid}' resolved deterministically to '{ordered[0].AssetPath}'. Candidates: {paths}",
                });
            }
        }

        ResetWatchers(roots);
        _initialized = true;
        _invalidated = false;
        ScanCount++;
        Generation++;
    }

    private IEnumerable<UnitySearchRoot> EnumerateSearchRoots()
    {
        string assetsRoot = Path.Combine(ProjectRoot, "Assets");
        if (Directory.Exists(assetsRoot))
        {
            yield return new UnitySearchRoot
            {
                PhysicalPath = Path.GetFullPath(assetsRoot),
                PortablePrefix = "Assets",
                Precedence = 0,
            };
        }

        string packagesRoot = Path.Combine(ProjectRoot, "Packages");
        if (Directory.Exists(packagesRoot))
        {
            yield return new UnitySearchRoot
            {
                PhysicalPath = Path.GetFullPath(packagesRoot),
                PortablePrefix = "Packages",
                Precedence = 10,
            };
        }

        foreach (UnitySearchRoot root in EnumerateManifestPackageRoots(packagesRoot))
            yield return root;
    }

    private IEnumerable<UnitySearchRoot> EnumerateManifestPackageRoots(string packagesRoot)
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
                yield return new UnitySearchRoot
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

            yield return new UnitySearchRoot
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

    private void ResetWatchers(IEnumerable<UnitySearchRoot> roots)
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
                _diagnostics.Add(new UnityImportDiagnostic
                {
                    Code = "UNITYGUID0003",
                    Severity = UnityImportDiagnosticSeverity.Warning,
                    Category = UnityImportDiagnosticCategory.GuidResolution,
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
