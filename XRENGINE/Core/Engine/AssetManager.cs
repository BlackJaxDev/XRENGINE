using Extensions;
using Microsoft.DotNet.PlatformAbstractions;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Diagnostics;
using XREngine.Serialization;

namespace XREngine
{
    public enum RemoteAssetLoadMode
    {
        None = 0,
        RequestFromRemote = 1,
        SendLocalCopy = 2,
    }

    public partial class AssetManager
    {
        public const string AssetExtension = "asset";
        private const string ImportOptionsFileExtension = "import.yaml";
        private const string EngineAssetsPathEnvVar = "XRE_ENGINE_ASSETS_PATH";
        private const string GameAssetsPathEnvVar = "XRE_GAME_ASSETS_PATH";
#if XRE_PUBLISHED
        private static string? _publishedConfigArchivePath;
        private static string? _publishedGameContentArchivePath;
        private static string? _publishedEngineContentArchivePath;
#endif

        private static bool IsOnJobThread
            => JobManager.IsJobWorkerThread;

        private static void RunOnJobThreadBlocking(Action action, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
            => RunOnJobThreadBlocking(() => { action(); return true; }, priority, bypassJobThread);

        private static T RunOnJobThreadBlocking<T>(Func<T> work, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
        {
            ArgumentNullException.ThrowIfNull(work);

            // Never schedule+block from inside a job worker thread; doing so can exhaust the worker pool
            // and deadlock (no workers left to run the scheduled continuation).
            if (bypassJobThread || IsOnJobThread)
                return work();

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Engine.Jobs.Schedule(() => JobRoutine(work, tcs), priority: priority);
            return tcs.Task.GetAwaiter().GetResult();
        }

        private static Task RunOnJobThreadAsync(Action action, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
            => RunOnJobThreadAsync(() => { action(); return true; }, priority, bypassJobThread);

        private static Task<T> RunOnJobThreadAsync<T>(Func<T> work, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
        {
            ArgumentNullException.ThrowIfNull(work);

            // If we're already on a job worker thread, execute inline.
            if (bypassJobThread || IsOnJobThread)
                return Task.FromResult(work());

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Engine.Jobs.Schedule(() => JobRoutine(work, tcs), priority: priority);
            return tcs.Task;
        }

        private static IEnumerable JobRoutine<T>(Func<T> work, TaskCompletionSource<T> tcs)
        {
            try
            {
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }

            yield break;
        }

        public AssetManager(string? engineAssetsDirPath = null)
        {
            string? resolvedEngineAssetsPath = null;
            string? engineAssetsOverridePath = Environment.GetEnvironmentVariable(EngineAssetsPathEnvVar);
            if (!string.IsNullOrWhiteSpace(engineAssetsOverridePath))
            {
                resolvedEngineAssetsPath = Path.GetFullPath(engineAssetsOverridePath);
            }
            else if (!string.IsNullOrWhiteSpace(engineAssetsDirPath) && Directory.Exists(engineAssetsDirPath))
            {
                resolvedEngineAssetsPath = engineAssetsDirPath;
            }
            else
            {
                string? basePath = ApplicationEnvironment.ApplicationBasePath;
                //Iterate up the directory tree until we find a Build directory that contains CommonAssets
                while (basePath is not null)
                {
                    string candidate = Path.Combine(basePath, "Build", "CommonAssets");
                    if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "Shaders")))
                    {
                        resolvedEngineAssetsPath = candidate;
                        break;
                    }
                    basePath = Path.GetDirectoryName(basePath);
                }

                resolvedEngineAssetsPath ??= Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Build", "CommonAssets");
            }

            EngineAssetsPath = resolvedEngineAssetsPath;

            string? gameAssetsOverridePath = Environment.GetEnvironmentVariable(GameAssetsPathEnvVar);
            if (!string.IsNullOrWhiteSpace(gameAssetsOverridePath))
            {
                GameAssetsPath = gameAssetsOverridePath;
            }
            else
            {
                // In sandbox mode (no project loaded), the editor commonly runs with
                // Environment.CurrentDirectory pointing at the repo/project root.
                // Prefer an existing '<cwd>/Assets' over '<exe>/Assets' to avoid writing
                // import options/cache next to the build output.
                string cwdAssets = Path.Combine(Environment.CurrentDirectory, "Assets");
                if (!Directory.Exists(GameAssetsPath) && Directory.Exists(cwdAssets))
                    GameAssetsPath = cwdAssets;
            }

            VerifyDirectoryExists(GameAssetsPath);
            Debug.Out($"Asset IO backend: {(DirectStorageIO.IsEnabled ? "DirectStorage-ready" : "Standard")} ({DirectStorageIO.Status})");

            // Best-effort defaults for sandbox mode. Project load will overwrite these.
            EnsureGameMetadataPathInitialized();
            EnsureGameCachePathInitialized();
            if (!Directory.Exists(EngineAssetsPath))
                throw new DirectoryNotFoundException($"Could not find the engine assets directory at '{EngineAssetsPath}'.");

            GameWatcher.Path = GameAssetsPath;
            GameWatcher.Filter = "*.*";
            GameWatcher.IncludeSubdirectories = true;
            GameWatcher.EnableRaisingEvents = true;
            GameWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;
            GameWatcher.Created += OnGameFileCreated;
            GameWatcher.Changed += OnGameFileChanged;
            GameWatcher.Deleted += OnGameFileDeleted;
            GameWatcher.Error += OnGameFileError;
            GameWatcher.Renamed += OnGameFileRenamed;

            EngineWatcher.Path = EngineAssetsPath;
            EngineWatcher.Filter = "*.*";
            EngineWatcher.IncludeSubdirectories = true;
            EngineWatcher.EnableRaisingEvents = true;
            EngineWatcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName;
            EngineWatcher.Created += OnEngineFileCreated;
            EngineWatcher.Changed += OnEngineFileChanged;
            EngineWatcher.Deleted += OnEngineFileDeleted;
            EngineWatcher.Error += OnEngineFileError;
            EngineWatcher.Renamed += OnEngineFileRenamed;
        }

        public bool MonitorGameAssetsForChanges
        {
            get => GameWatcher.EnableRaisingEvents;
            set => GameWatcher.EnableRaisingEvents = value;
        }

        public bool MonitorEngineAssetsForChanges
        {
            get => EngineWatcher.EnableRaisingEvents;
            set => EngineWatcher.EnableRaisingEvents = value;
        }

        private static bool VerifyDirectoryExists(string? directoryPath)
        {
            //If the path is null or empty, return false
            if (string.IsNullOrWhiteSpace(directoryPath))
                return false;

            //If the path is a file, get the directory
            if (Path.HasExtension(directoryPath))
            {
                directoryPath = Path.GetDirectoryName(directoryPath);

                //If the directory is null or empty, return false
                if (string.IsNullOrWhiteSpace(directoryPath))
                    return false;
            }

            //If the current directory exists, return true
            if (Directory.Exists(directoryPath))
                return true;

            //Recursively create the parent directories
            string? parent = Path.GetDirectoryName(directoryPath);
            if (VerifyDirectoryExists(parent))
                Directory.CreateDirectory(directoryPath);

            return true;
        }

        private static string NormalizeDirectoryPath(string path, string argumentName)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException($"{argumentName} cannot be null or empty.", argumentName);

            string fullPath = Path.GetFullPath(path);
            VerifyDirectoryExists(fullPath);
            return fullPath;
        }

        private void EnsureGameCachePathInitialized()
        {
            if (!string.IsNullOrWhiteSpace(_gameCachePath))
                return;

            if (string.IsNullOrWhiteSpace(GameAssetsPath))
                return;

            if (!TryInferProjectRootFromGameAssetsPath(out string? projectRoot) || string.IsNullOrWhiteSpace(projectRoot))
                return;

            // Default to '<ProjectRoot>/Cache' which matches the project's structure.
            GameCachePath = Path.Combine(projectRoot, "Cache");
        }

        private void EnsureGameMetadataPathInitialized()
        {
            if (!string.IsNullOrWhiteSpace(_gameMetadataPath))
                return;

            if (!TryInferProjectRootFromGameAssetsPath(out string? projectRoot) || string.IsNullOrWhiteSpace(projectRoot))
                return;

            // Default to '<ProjectRoot>/Metadata' which matches XRProject structure.
            GameMetadataPath = Path.Combine(projectRoot, "Metadata");
        }

        private bool TryInferProjectRootFromGameAssetsPath([NotNullWhen(true)] out string? projectRoot)
        {
            projectRoot = null;

            if (string.IsNullOrWhiteSpace(GameAssetsPath))
                return false;

            try
            {
                string assetsPath = Path.GetFullPath(GameAssetsPath);

                // Common case: GameAssetsPath points at '<ProjectRoot>/Assets'.
                if (string.Equals(Path.GetFileName(assetsPath), "Assets", StringComparison.OrdinalIgnoreCase))
                {
                    projectRoot = Path.GetDirectoryName(assetsPath);
                    return !string.IsNullOrWhiteSpace(projectRoot);
                }

                // If GameAssetsPath is actually the project root, prefer it when it contains an Assets folder.
                if (Directory.Exists(Path.Combine(assetsPath, "Assets")))
                {
                    projectRoot = assetsPath;
                    return true;
                }

                // Fallback: use the parent directory of the provided assets path.
                projectRoot = Path.GetDirectoryName(assetsPath);
                return !string.IsNullOrWhiteSpace(projectRoot);
            }
            catch
            {
                return false;
            }
        }

        private static string? NormalizeOptionalDirectoryPath(string? path, string argumentName)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            return NormalizeDirectoryPath(path, argumentName);
        }

        private void UpdateGameAssetsPath(string path)
        {
            string normalized = NormalizeDirectoryPath(path, nameof(GameAssetsPath));
            if (string.Equals(_gameAssetsPath, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _gameAssetsPath = normalized;
            GameWatcher.Path = _gameAssetsPath;
        }

        #region Properties and Fields

        public FileSystemWatcher GameWatcher { get; } = new FileSystemWatcher();
        public FileSystemWatcher EngineWatcher { get; } = new FileSystemWatcher();
        /// <summary>
        /// This is the path to /Build/CommonAssets/ in the root folder of the engine.
        /// </summary>
        public string EngineAssetsPath { get; }

        private string _gameAssetsPath = Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Assets");
        public string GameAssetsPath
        {
            get => _gameAssetsPath;
            set => UpdateGameAssetsPath(value);
        }

        private string? _gameMetadataPath;
        public string? GameMetadataPath
        {
            get => _gameMetadataPath;
            set => _gameMetadataPath = NormalizeOptionalDirectoryPath(value, nameof(GameMetadataPath));
        }

        private string? _gameCachePath;
        public string? GameCachePath
        {
            get => _gameCachePath;
            set => _gameCachePath = NormalizeOptionalDirectoryPath(value, nameof(GameCachePath));
        }

        private string _packagesPath = Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Packages");
        public string PackagesPath
        {
            get => _packagesPath;
            set => _packagesPath = NormalizeDirectoryPath(value, nameof(PackagesPath));
        }

        private string _librariesPath = Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Libraries");
        public string LibrariesPath
        {
            get => _librariesPath;
            set => _librariesPath = NormalizeDirectoryPath(value, nameof(LibrariesPath));
        }

        public ConcurrentDictionary<string, XRAsset> LoadedAssetsByOriginalPathInternal { get; } = [];
        public ConcurrentDictionary<string, XRAsset> LoadedAssetsByPathInternal { get; } = [];
        public ConcurrentDictionary<Guid, XRAsset> LoadedAssetsByIDInternal { get; } = [];
        public ConcurrentDictionary<Guid, XRAsset> DirtyAssets { get; } = [];
        
        // Track recently saved assets to prevent immediate reload from file watcher
        private readonly ConcurrentDictionary<string, DateTime> _recentlySavedPaths = new(StringComparer.OrdinalIgnoreCase);
        private const double RecentlySavedIgnoreSeconds = 2;
        private const double RecentlySavedPruneAfterSeconds = 10;
        private int _recentlySavedPruneCounter;
        private readonly object _metadataLock = new();
        private static readonly Lazy<Dictionary<string, Type>> ThirdPartyExtensionMap = new(CreateThirdPartyExtensionMap);
        private readonly ConcurrentDictionary<string, object> _thirdPartyImportOptionsCache = new(StringComparer.OrdinalIgnoreCase);

        public Func<string, bool>? AllowOverwriteCallback { get; set; } = path => true;

        public event Action<XRAsset>? AssetLoaded;
        public event Action<XRAsset>? AssetSaved;
        public event Action<XRAsset>? AssetMarkedDirty;

        #endregion

        #region Recently Saved Tracking

        private void MarkRecentlySaved(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            _recentlySavedPaths[path] = DateTime.UtcNow;

            // Opportunistic prune to prevent unbounded growth.
            if (Interlocked.Increment(ref _recentlySavedPruneCounter) % 128 != 0)
                return;

            DateTime now = DateTime.UtcNow;
            foreach (var kvp in _recentlySavedPaths)
            {
                if ((now - kvp.Value).TotalSeconds > RecentlySavedPruneAfterSeconds)
                    _recentlySavedPaths.TryRemove(kvp.Key, out _);
            }
        }

        private bool ShouldIgnoreWatcherEvent(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            if (!_recentlySavedPaths.TryGetValue(path, out var saveTime))
                return false;

            if ((DateTime.UtcNow - saveTime).TotalSeconds <= RecentlySavedIgnoreSeconds)
                return true;

            _recentlySavedPaths.TryRemove(path, out _);
            return false;
        }

        #endregion

        #region Asset Lookup

        public XRAsset? GetAssetByID(Guid id)
            => LoadedAssetsByIDInternal.TryGetValue(id, out XRAsset? asset) ? asset : null;

        public bool TryGetAssetByID(Guid id, [NotNullWhen(true)] out XRAsset? asset)
            => LoadedAssetsByIDInternal.TryGetValue(id, out asset);

        public XRAsset? GetAssetByPath(string path)
            => LoadedAssetsByPathInternal.TryGetValue(path, out var asset) ? asset : null;

        public bool TryGetAssetByPath(string path, [NotNullWhen(true)] out XRAsset? asset)
            => LoadedAssetsByPathInternal.TryGetValue(path, out asset);

        public XRAsset? GetAssetByOriginalPath(string path)
            => LoadedAssetsByOriginalPathInternal.TryGetValue(path, out var asset) ? asset : null;

        public bool TryGetAssetByOriginalPath(string path, [NotNullWhen(true)] out XRAsset? asset)
            => LoadedAssetsByOriginalPathInternal.TryGetValue(path, out asset);

        public bool TryResolveAssetPathById(Guid assetId, [NotNullWhen(true)] out string? assetPath)
        {
            assetPath = null;

            if (assetId == Guid.Empty)
                return false;

            if (LoadedAssetsByIDInternal.TryGetValue(assetId, out var asset) && !string.IsNullOrWhiteSpace(asset.FilePath) && File.Exists(asset.FilePath))
            {
                assetPath = asset.FilePath;
                return true;
            }

            if (string.IsNullOrWhiteSpace(GameMetadataPath) || !Directory.Exists(GameMetadataPath))
                return false;

            try
            {
                foreach (string metaFile in Directory.EnumerateFiles(GameMetadataPath, "*.meta", SearchOption.AllDirectories))
                {
                    var meta = TryReadMetadata(metaFile);
                    if (meta?.Guid != assetId || string.IsNullOrWhiteSpace(meta.RelativePath))
                        continue;

                    string candidate = Path.Combine(GameAssetsPath, meta.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    if (File.Exists(candidate))
                    {
                        assetPath = candidate;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to resolve asset id '{assetId}' from metadata: {ex.Message}");
            }

            return false;
        }

        #endregion

        #region Asset Caching and Tracking

        private void CacheAsset(XRAsset asset)
        {
            string path = asset.FilePath ?? string.Empty;
            XRAsset UpdatePathDict(string existingPath, XRAsset existingAsset)
            {
                if (existingAsset is not null)
                {
                    if (existingAsset != asset && !existingAsset.EmbeddedAssets.Contains(asset))
                        existingAsset.EmbeddedAssets.Add(asset);
                    return existingAsset;
                }
                else
                    return asset;
            }
            LoadedAssetsByPathInternal.AddOrUpdate(path, asset, UpdatePathDict);

            if (asset.ID == Guid.Empty)
            {
                Debug.LogWarning("An asset was loaded with an empty ID.");
            }

            if (asset.ID != Guid.Empty)
            {
                XRAsset UpdateIDDict(Guid existingID, XRAsset existingAsset)
                {
                    Debug.Out($"An asset with the ID {existingID} already exists in the asset manager. The new asset will be added to the list of assets with the same ID.");
                    return existingAsset;
                }
                LoadedAssetsByIDInternal.AddOrUpdate(asset.ID, asset, UpdateIDDict);
            }

            asset.PropertyChanged += AssetPropertyChanged;
        }

        /// <summary>
        /// Ensures an asset instance is tracked by the asset manager (path/id caches, dirty tracking hooks).
        /// Useful for assets that are created in-memory and assigned a FilePath, without going through Load/SaveTo.
        /// </summary>
        public void EnsureTracked(XRAsset asset)
        {
            ArgumentNullException.ThrowIfNull(asset);

            if (asset.ID != Guid.Empty && LoadedAssetsByIDInternal.TryGetValue(asset.ID, out var byId) && ReferenceEquals(byId, asset))
                return;

            if (!string.IsNullOrWhiteSpace(asset.FilePath) && LoadedAssetsByPathInternal.TryGetValue(asset.FilePath, out var byPath) && ReferenceEquals(byPath, asset))
                return;

            CacheAsset(asset);

            // If the asset was marked dirty before it was tracked (common for in-memory settings objects),
            // it would have missed the IsDirty PropertyChanged hook. Backfill DirtyAssets here.
            if (asset.IsDirty && asset.ID != Guid.Empty)
            {
                DirtyAssets.TryAdd(asset.ID, asset);
                AssetMarkedDirty?.Invoke(asset);
            }
        }

        void AssetPropertyChanged(object? s, Data.Core.IXRPropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(XRAsset.FilePath):
                    {
                        if (s is XRAsset asset)
                        {
                            if (e.PreviousValue is string prev)
                                LoadedAssetsByPathInternal.TryRemove(prev, out _);
                            LoadedAssetsByPathInternal.TryAdd(asset.FilePath ?? string.Empty, asset);
                        }
                    }
                    break;
                case nameof(XRAsset.ID):
                    {
                        if (s is XRAsset asset)
                        {
                            if (e.PreviousValue is Guid prev)
                                LoadedAssetsByIDInternal.TryRemove(prev, out _);
                            LoadedAssetsByIDInternal.TryAdd(asset.ID, asset);
                        }
                    }
                    break;
                case nameof(XRAsset.IsDirty):
                    {
                        if (s is XRAsset asset && asset.IsDirty)
                        {
                            DirtyAssets.TryAdd(asset.ID, asset);
                            AssetMarkedDirty?.Invoke(asset);
                        }
                    }
                    break;
            }
        }

        #endregion

        #region Post-Save / Post-Load Hooks

        private void PostSaved(XRAsset asset, bool newAsset)
        {
            if (newAsset)
                CacheAsset(asset);
            asset.ClearDirty();
            DirtyAssets.TryRemove(asset.ID, out _);

            // If this is the root asset, clear any embedded dirty entries that point to it.
            foreach (var kvp in DirtyAssets.ToArray())
            {
                if (ReferenceEquals(kvp.Value.SourceAsset, asset))
                {
                    kvp.Value.ClearDirty();
                    DirtyAssets.TryRemove(kvp.Key, out _);
                }
            }
            
            // Track save time to prevent file watcher from reloading
            if (!string.IsNullOrEmpty(asset.FilePath))
                _recentlySavedPaths[asset.FilePath] = DateTime.UtcNow;
            
            AssetSaved?.Invoke(asset);
        }

        private void PostLoaded<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, T? file) where T : XRAsset
        {
            if (file is null)
                return;

            file.Name = Path.GetFileNameWithoutExtension(filePath);
            file.FilePath = filePath;

            // Refresh asset graph to populate SourceAsset/EmbeddedAssets relationships
            XRAssetGraphUtility.RefreshAssetGraph(file);

            CacheAsset(file);

            if (file is IOverrideableSettingsOwner overrideableOwner)
                overrideableOwner.TrackOverrideableSettings();

            // Clear dirty flag since the asset was just loaded from disk - any dirty state
            // was from construction/deserialization, not actual user changes
            file.ClearDirty();
            foreach (var embedded in file.EmbeddedAssets)
                embedded.ClearDirty();

            AssetLoaded?.Invoke(file);
        }

        #endregion

        #region Path Resolution and Utilities

        /// <summary>
        /// Creates a full path to an asset in the engine's asset directory.
        /// </summary>
        /// <param name="relativePathFolders"></param>
        /// <returns></returns>
        public string ResolveEngineAssetPath(params string[] relativePathFolders)
            => Path.Combine(EngineAssetsPath, Path.Combine(relativePathFolders));

        /// <summary>
        /// Creates a full path to an asset in the game's asset directory.
        /// </summary>
        /// <param name="relativePathFolders"></param>
        /// <returns></returns>
        public string ResolveGameAssetPath(params string[] relativePathFolders)
            => Path.Combine(GameAssetsPath, Path.Combine(relativePathFolders));

        public void Dispose()
        {
            foreach (var asset in LoadedAssetsByIDInternal.Values)
                asset.Destroy();
            LoadedAssetsByIDInternal.Clear();
            LoadedAssetsByPathInternal.Clear();
            LoadedAssetsByOriginalPathInternal.Clear();
        }

        public static string VerifyAssetPath(XRAsset asset, string directory)
        {
            VerifyDirectoryExists(directory);
            string fileName = string.IsNullOrWhiteSpace(asset.Name) ? asset.GetType().Name : asset.Name;
            //Add the asset extension for regular assets
            if (!Path.HasExtension(fileName))
                fileName = $"{fileName}.{AssetExtension}";
            return Path.Combine(directory, fileName);
        }

        public static string GetUniqueAssetPath(string path)
        {
            if (!File.Exists(path))
                return path;

            string? dir = Path.GetDirectoryName(path);
            if (dir is null)
                return path;

            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            int i = 1;
            string newPath;
            do
            {
                newPath = Path.Combine(dir, $"{name} ({i++}){ext}");
            } while (File.Exists(newPath));
            return newPath;
        }

        #endregion
    }
}