using XREngine.Rendering.Models;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene.Importers;

/// <summary>
/// Watches only the reached source paths recorded by imported Unity prefab
/// manifests and schedules transactional reimport when one changes.
/// </summary>
public static class SerializedPrefabDependencyMonitor
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Registration> Registrations =
        new(StringComparer.OrdinalIgnoreCase);

    public static void Register(SerializedPrefabImportManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (string.IsNullOrWhiteSpace(manifest.OutputAssetPath) ||
            string.IsNullOrWhiteSpace(manifest.EntrySourcePath))
        {
            return;
        }

        string outputPath = Path.GetFullPath(manifest.OutputAssetPath);
        var registration = new Registration(manifest);
        lock (Sync)
        {
            if (Registrations.Remove(outputPath, out Registration? previous))
                previous.Dispose();
            Registrations[outputPath] = registration;
        }
    }

    public static void Unregister(string outputAssetPath)
    {
        if (string.IsNullOrWhiteSpace(outputAssetPath))
            return;

        string outputPath = Path.GetFullPath(outputAssetPath);
        lock (Sync)
        {
            if (Registrations.Remove(outputPath, out Registration? registration))
                registration.Dispose();
        }
    }

    private sealed class Registration : IDisposable
    {
        private readonly SerializedPrefabImportManifest _manifest;
        private readonly HashSet<string> _dependencyPaths;
        private readonly List<FileSystemWatcher> _watchers = [];
        private readonly Timer _debounceTimer;
        private int _reimporting;
        private bool _disposed;

        public Registration(SerializedPrefabImportManifest manifest)
        {
            _manifest = manifest;
            _dependencyPaths = new HashSet<string>(
                manifest.Dependencies
                    .Select(dependency => manifest.ResolveDependencySourcePath(dependency.NormalizedPath))
                    .Where(static path => !string.IsNullOrWhiteSpace(path))
                    .Cast<string>()
                    .Select(Path.GetFullPath),
                StringComparer.OrdinalIgnoreCase);
            _debounceTimer = new Timer(
                static state => ((Registration)state!).CheckAndReimport(),
                this,
                Timeout.Infinite,
                Timeout.Infinite);

            foreach (string root in GetWatcherRoots(manifest, _dependencyPaths))
            {
                if (!Directory.Exists(root))
                    continue;

                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter =
                        NotifyFilters.FileName |
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size |
                        NotifyFilters.CreationTime,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnChanged;
                watcher.Created += OnChanged;
                watcher.Deleted += OnChanged;
                watcher.Renamed += OnRenamed;
                _watchers.Add(watcher);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _debounceTimer.Dispose();
            foreach (FileSystemWatcher watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
                watcher.Changed -= OnChanged;
                watcher.Created -= OnChanged;
                watcher.Deleted -= OnChanged;
                watcher.Renamed -= OnRenamed;
                watcher.Dispose();
            }
            _watchers.Clear();
        }

        private void OnChanged(object sender, FileSystemEventArgs args)
            => ScheduleIfReached(args.FullPath);

        private void OnRenamed(object sender, RenamedEventArgs args)
        {
            ScheduleIfReached(args.OldFullPath);
            ScheduleIfReached(args.FullPath);
        }

        private void ScheduleIfReached(string path)
        {
            if (_disposed || !_dependencyPaths.Contains(Path.GetFullPath(path)))
                return;

            _debounceTimer.Change(500, Timeout.Infinite);
        }

        private void CheckAndReimport()
        {
            if (_disposed ||
                Interlocked.CompareExchange(ref _reimporting, 1, 0) != 0)
            {
                return;
            }

            _ = ReimportAsync();
        }

        private async Task ReimportAsync()
        {
            try
            {
                if (!_manifest.HasDependencyChanges())
                    return;

                AssetManager? assets = Engine.Assets;
                if (assets is null ||
                    string.IsNullOrWhiteSpace(_manifest.OutputAssetPath))
                {
                    return;
                }

                var options = new ModelImportOptions
                {
                    SourceProjectRootOverride = _manifest.SourceProjectRoot,
                    ProcessMeshesAsynchronously = false,
                    GenerateMeshRenderersAsync = false,
                };
                bool imported = await assets.ImportExternalThirdPartyFileAsync(
                    _manifest.EntrySourcePath,
                    _manifest.OutputAssetPath,
                    options,
                    overwrite: true).ConfigureAwait(false);
                if (!imported)
                {
                    Debug.LogWarning(
                        $"Dependency-triggered Unity prefab reimport failed for '{_manifest.EntrySourcePath}'.");
                }
            }
            catch (Exception ex)
            {
                Debug.LogException(
                    ex,
                    $"Dependency-triggered Unity prefab reimport failed for '{_manifest.EntrySourcePath}'.");
            }
            finally
            {
                Interlocked.Exchange(ref _reimporting, 0);
            }
        }

        private static IEnumerable<string> GetWatcherRoots(
            SerializedPrefabImportManifest manifest,
            IEnumerable<string> dependencyPaths)
        {
            string projectRoot = Path.GetFullPath(manifest.SourceProjectRoot);
            string assetsRoot = Path.Combine(projectRoot, "Assets");
            string packagesRoot = Path.Combine(projectRoot, "Packages");
            string packageCacheRoot = Path.Combine(projectRoot, "Library", "PackageCache");
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string dependencyPath in dependencyPaths)
            {
                string fullPath = Path.GetFullPath(dependencyPath);
                if (IsUnder(fullPath, assetsRoot))
                    roots.Add(assetsRoot);
                else if (IsUnder(fullPath, packagesRoot))
                    roots.Add(packagesRoot);
                else if (IsUnder(fullPath, packageCacheRoot))
                    roots.Add(packageCacheRoot);
                else if (Path.GetDirectoryName(fullPath) is string directory)
                    roots.Add(directory);
            }

            return roots;
        }

        private static bool IsUnder(string path, string directory)
        {
            string prefix = Path.GetFullPath(directory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
    }
}
