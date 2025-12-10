using Extensions;
using Microsoft.DotNet.PlatformAbstractions;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;
using XREngine.Serialization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NodeDeserializers;

namespace XREngine
{
    public class AssetManager
    {
        public const string AssetExtension = "asset";

        private static bool IsOnJobThread
            => Engine.JobThreadId.HasValue && Engine.JobThreadId.Value == Thread.CurrentThread.ManagedThreadId;

        private static void RunOnJobThreadBlocking(Action action, JobPriority priority = JobPriority.Normal)
            => RunOnJobThreadBlocking(() => { action(); return true; }, priority);

        private static T RunOnJobThreadBlocking<T>(Func<T> work, JobPriority priority = JobPriority.Normal)
        {
            ArgumentNullException.ThrowIfNull(work);

            if (IsOnJobThread || !Engine.JobThreadId.HasValue)
                return work();

            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Engine.Jobs.Schedule(() => JobRoutine(work, tcs), priority: priority);
            return tcs.Task.GetAwaiter().GetResult();
        }

        private static Task RunOnJobThreadAsync(Action action, JobPriority priority = JobPriority.Normal)
            => RunOnJobThreadAsync(() => { action(); return true; }, priority);

        private static Task<T> RunOnJobThreadAsync<T>(Func<T> work, JobPriority priority = JobPriority.Normal)
        {
            ArgumentNullException.ThrowIfNull(work);

            if (IsOnJobThread || !Engine.JobThreadId.HasValue)
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
            if (!string.IsNullOrWhiteSpace(engineAssetsDirPath) && Directory.Exists(engineAssetsDirPath))
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

            VerifyDirectoryExists(GameAssetsPath);
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

        public event Action<FileSystemEventArgs>? EngineFileCreated;
        public event Action<FileSystemEventArgs>? EngineFileChanged;
        public event Action<FileSystemEventArgs>? EngineFileDeleted;
        public event Action<RenamedEventArgs>? EngineFileRenamed;

        public event Action<FileSystemEventArgs>? GameFileCreated;
        public event Action<FileSystemEventArgs>? GameFileChanged;
        public event Action<FileSystemEventArgs>? GameFileDeleted;
        public event Action<RenamedEventArgs>? GameFileRenamed;

        void OnEngineFileCreated(object sender, FileSystemEventArgs args)
        {
            OnFileCreated(args);
            EngineFileCreated?.Invoke(args);
        }
        void OnGameFileCreated(object sender, FileSystemEventArgs args)
        {
            OnFileCreated(args);
            HandleMetadataCreated(args.FullPath);
            GameFileCreated?.Invoke(args);
        }
        private static void OnFileCreated(FileSystemEventArgs args)
        {
            Debug.Out($"File '{args.FullPath}' was created.");
        }

        async void OnEngineFileChanged(object sender, FileSystemEventArgs args)
        {
            await OnFileChanged(args);
            EngineFileChanged?.Invoke(args);
        }
        async void OnGameFileChanged(object sender, FileSystemEventArgs args)
        {
            await OnFileChanged(args);
            HandleMetadataChanged(args.FullPath);
            GameFileChanged?.Invoke(args);
        }
        private async Task OnFileChanged(FileSystemEventArgs args)
        {
            // Skip reload if we just saved this file (within last 2 seconds)
            if (_recentlySavedPaths.TryGetValue(args.FullPath, out var saveTime))
            {
                if ((DateTime.UtcNow - saveTime).TotalSeconds < 2)
                {
                    _recentlySavedPaths.TryRemove(args.FullPath, out _);
                    return;
                }
                _recentlySavedPaths.TryRemove(args.FullPath, out _);
            }
            
            Debug.Out($"File '{args.FullPath}' was changed.");
            var asset = GetAssetByPath(args.FullPath);
            if (asset is not null)
                await asset.ReloadAsync(args.FullPath);
        }

        void OnEngineFileDeleted(object sender, FileSystemEventArgs args)
        {
            OnFileDeleted(args);
            EngineFileDeleted?.Invoke(args);
        }
        void OnGameFileDeleted(object sender, FileSystemEventArgs args)
        {
            OnFileDeleted(args);
            HandleMetadataDeleted(args.FullPath);
            GameFileDeleted?.Invoke(args);
        }
        private static void OnFileDeleted(FileSystemEventArgs args)
        {
            Debug.Out($"File '{args.FullPath}' was deleted.");
            //Leave files intact
            //if (LoadedAssetsByPathInternal.TryGetValue(args.FullPath, out var list))
            //{
            //    foreach (var asset in list)
            //        asset.Destroy();
            //    LoadedAssetsByPathInternal.Remove(args.FullPath);
            //}
        }

        void OnGameFileError(object sender, ErrorEventArgs args)
        {
            OnFileError(args);
        }
        void OnEngineFileError(object sender, ErrorEventArgs args)
        {
            OnFileError(args);
        }
        private static void OnFileError(ErrorEventArgs args)
        {
            Debug.LogWarning($"An error occurred in the file system watcher: {args.GetException().Message}");
        }

        void OnGameFileRenamed(object sender, RenamedEventArgs args)
        {
            OnFileRenamed(args);
            HandleMetadataRenamed(args.OldFullPath, args.FullPath);
            GameFileRenamed?.Invoke(args);
        }
        void OnEngineFileRenamed(object sender, RenamedEventArgs args)
        {
            OnFileRenamed(args);
            EngineFileRenamed?.Invoke(args);
        }

        private void OnFileRenamed(RenamedEventArgs args)
        {
            Debug.Out($"File '{args.OldFullPath}' was renamed to '{args.FullPath}'.");

            if (LoadedAssetsByPathInternal.TryGetValue(args.OldFullPath, out var asset))
            {
                LoadedAssetsByPathInternal.Remove(args.OldFullPath, out _);
                LoadedAssetsByPathInternal.TryAdd(args.FullPath, asset);

                asset.FilePath = args.FullPath;
            }
            if (LoadedAssetsByOriginalPathInternal.TryGetValue(args.OldFullPath, out asset))
            {
                LoadedAssetsByOriginalPathInternal.Remove(args.OldFullPath, out _);
                LoadedAssetsByOriginalPathInternal.TryAdd(args.FullPath, asset);

                asset.OriginalPath = args.FullPath;
                asset.Reload();
            }
        }

        public void SyncMetadataWithAssets()
        {
            if (string.IsNullOrWhiteSpace(GameAssetsPath) || string.IsNullOrWhiteSpace(GameMetadataPath))
                return;

            string assetsRoot = Path.GetFullPath(GameAssetsPath);
            string metadataRoot = Path.GetFullPath(GameMetadataPath);
            if (!Directory.Exists(assetsRoot))
                return;

            Directory.CreateDirectory(metadataRoot);

            foreach (string directory in Directory.EnumerateDirectories(assetsRoot, "*", SearchOption.AllDirectories))
                EnsureMetadataForAssetPath(directory, true);

            foreach (string file in Directory.EnumerateFiles(assetsRoot, "*", SearchOption.AllDirectories))
                EnsureMetadataForAssetPath(file, false);
        }

        private void HandleMetadataCreated(string path)
        {
            if (!IsPathUnderGameAssets(path))
                return;

            EnsureMetadataForAssetPath(path, SafeIsDirectory(path));
        }

        private void HandleMetadataChanged(string path)
        {
            if (!IsPathUnderGameAssets(path))
                return;

            EnsureMetadataForAssetPath(path, SafeIsDirectory(path));
        }

        private void HandleMetadataDeleted(string path)
        {
            RemoveMetadataForPath(path, SafeIsDirectory(path));
        }

        private void HandleMetadataRenamed(string oldPath, string newPath)
        {
            bool isDirectory = SafeIsDirectory(newPath) || SafeIsDirectory(oldPath);
            MoveMetadataForPath(oldPath, newPath, isDirectory);
        }

        private bool IsPathUnderGameAssets(string path)
        {
            if (string.IsNullOrWhiteSpace(GameAssetsPath) || string.IsNullOrWhiteSpace(GameMetadataPath) || string.IsNullOrWhiteSpace(path))
                return false;

            string normalizedAssets = Path.GetFullPath(GameAssetsPath);
            string normalizedPath = Path.GetFullPath(path);
            return normalizedPath.StartsWith(normalizedAssets, StringComparison.OrdinalIgnoreCase);
        }

        private static bool SafeIsDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    return true;
                if (File.Exists(path))
                    return false;

                FileAttributes attributes = File.GetAttributes(path);
                return attributes.HasFlag(FileAttributes.Directory);
            }
            catch
            {
                return !Path.HasExtension(path);
            }
        }

        private bool TryGetMetadataPath(string assetPath, out string metadataPath, out string relativePath)
        {
            metadataPath = string.Empty;
            relativePath = string.Empty;

            if (!IsPathUnderGameAssets(assetPath))
                return false;

            string assetsRoot = Path.GetFullPath(GameAssetsPath);
            string assetFullPath = Path.GetFullPath(assetPath);
            relativePath = Path.GetRelativePath(assetsRoot, assetFullPath);
            if (relativePath.StartsWith("..", StringComparison.Ordinal) || relativePath == ".")
                return false;

            string target = Path.Combine(GameMetadataPath!, relativePath);
            metadataPath = $"{target}.meta";
            return true;
        }

        private void EnsureMetadataForAssetPath(string assetPath, bool isDirectory)
        {
            if (!TryGetMetadataPath(assetPath, out string metaPath, out string relativePath))
                return;

            lock (_metadataLock)
            {
                string? directory = Path.GetDirectoryName(metaPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                AssetMetadata meta = TryReadMetadata(metaPath) ?? new AssetMetadata();
                meta.Name = Path.GetFileName(assetPath);
                meta.RelativePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');
                meta.IsDirectory = isDirectory;

                if (isDirectory)
                {
                    if (meta.Guid == Guid.Empty)
                        meta.Guid = Guid.NewGuid();
                    meta.Import = null;
                }
                else
                {
                    string ext = Path.GetExtension(assetPath);
                    bool isAssetFile = ext.Equals($".{AssetExtension}", StringComparison.OrdinalIgnoreCase);

                    if (isAssetFile)
                    {
                        if (meta.Guid == Guid.Empty)
                        {
                            Guid extracted = TryExtractGuidFromAsset(assetPath);
                            meta.Guid = extracted == Guid.Empty ? Guid.NewGuid() : extracted;
                        }

                        meta.Import = null;
                    }
                    else
                    {
                        if (meta.Guid == Guid.Empty)
                            meta.Guid = Guid.NewGuid();

                        meta.Import ??= new AssetImportMetadata();
                        meta.Import.SourceExtension = ext.StartsWith(".", StringComparison.Ordinal) ? ext[1..] : ext;
                        meta.Import.SourceLastWriteTimeUtc = SafeGetLastWriteTimeUtc(assetPath);
                        meta.Import.ImporterType = ResolveImporterNameForExtension(ext);
                    }
                }

                meta.LastSyncedUtc = DateTime.UtcNow;
                WriteMetadataFile(metaPath, meta);
            }
        }

        private void RemoveMetadataForPath(string assetPath, bool isDirectory)
        {
            if (!TryGetMetadataPath(assetPath, out string metaPath, out _))
                return;

            lock (_metadataLock)
            {
                if (File.Exists(metaPath))
                    File.Delete(metaPath);

                TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(metaPath));
            }
        }

        private void MoveMetadataForPath(string oldPath, string newPath, bool isDirectory)
        {
            if (!TryGetMetadataPath(oldPath, out string oldMeta, out _))
            {
                EnsureMetadataForAssetPath(newPath, isDirectory);
                return;
            }

            if (!TryGetMetadataPath(newPath, out string newMeta, out _))
            {
                RemoveMetadataForPath(oldPath, isDirectory);
                return;
            }

            lock (_metadataLock)
            {
                string? newDir = Path.GetDirectoryName(newMeta);
                if (!string.IsNullOrWhiteSpace(newDir))
                    Directory.CreateDirectory(newDir);

                if (File.Exists(oldMeta))
                {
                    try
                    {
                        File.Move(oldMeta, newMeta, true);
                    }
                    catch
                    {
                        File.Copy(oldMeta, newMeta, true);
                        File.Delete(oldMeta);
                    }
                }

                TryPruneEmptyMetadataDirectories(Path.GetDirectoryName(oldMeta));
            }

            EnsureMetadataForAssetPath(newPath, isDirectory);
        }

        private static AssetMetadata? TryReadMetadata(string metaPath)
        {
            if (!File.Exists(metaPath))
                return null;

            try
            {
                string text = File.ReadAllText(metaPath);
                return Deserializer.Deserialize<AssetMetadata>(text);
            }
            catch
            {
                return null;
            }
        }

        private static void WriteMetadataFile(string metaPath, AssetMetadata meta)
        {
            string yaml = Serializer.Serialize(meta);
            File.WriteAllText(metaPath, yaml);
        }

        private static Guid TryExtractGuidFromAsset(string assetPath)
        {
            try
            {
                foreach (string line in File.ReadLines(assetPath))
                {
                    string trimmed = line.Trim();
                    if (!trimmed.StartsWith("ID:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string guidText = trimmed[3..].Trim();
                    if (Guid.TryParse(guidText, out Guid guid))
                        return guid;
                }
            }
            catch
            {
                // Ignore parse errors and fall back to generating a new GUID.
            }

            return Guid.Empty;
        }

        private static DateTime? SafeGetLastWriteTimeUtc(string path)
        {
            try
            {
                DateTime timestamp = File.GetLastWriteTimeUtc(path);
                return timestamp == DateTime.MinValue ? null : timestamp;
            }
            catch
            {
                return null;
            }
        }

        private static string? ResolveImporterNameForExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return null;

            string normalized = extension.TrimStart('.').ToLowerInvariant();
            var map = ThirdPartyExtensionMap.Value;

            return map.TryGetValue(normalized, out Type? type)
                ? type.FullName ?? type.Name
                : null;
        }

        private static Dictionary<string, Type> CreateThirdPartyExtensionMap()
        {
            var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
            Type baseType = typeof(XRAsset);

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types ?? [];
                }

                foreach (Type? type in types)
                {
                    if (type is null || !baseType.IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                        continue;

                    var attribute = type.GetCustomAttribute<XR3rdPartyExtensionsAttribute>();
                    if (attribute is null)
                        continue;

                    foreach (var (ext, _) in attribute.Extensions)
                    {
                        if (string.IsNullOrWhiteSpace(ext))
                            continue;

                        string normalized = ext.TrimStart('.');
                        if (!map.ContainsKey(normalized))
                            map.Add(normalized, type);
                    }
                }
            }

            return map;
        }

        private void TryPruneEmptyMetadataDirectories(string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(GameMetadataPath))
                return;

            string root = Path.GetFullPath(GameMetadataPath);
            string current = Path.GetFullPath(directory);

            while (current.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(current))
                    break;

                using var enumerator = Directory.EnumerateFileSystemEntries(current).GetEnumerator();
                if (enumerator.MoveNext())
                    break;

                Directory.Delete(current);

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent))
                    break;

                current = parent;
            }
        }

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
                return;
            }

            XRAsset UpdateIDDict(Guid existingID, XRAsset existingAsset)
            {
                Debug.Out($"An asset with the ID {existingID} already exists in the asset manager. The new asset will be added to the list of assets with the same ID.");
                return existingAsset;
            }
            LoadedAssetsByIDInternal.AddOrUpdate(asset.ID, asset, UpdateIDDict);

            asset.PropertyChanged += AssetPropertyChanged;
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
        private readonly object _metadataLock = new();
        private static readonly Lazy<Dictionary<string, Type>> ThirdPartyExtensionMap = new(CreateThirdPartyExtensionMap);

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

        public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
            => LoadEngineAsset<T>(JobPriority.Normal, relativePathFolders);

        public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return Load<T>(path, priority) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
            => LoadEngineAssetAsync<T>(JobPriority.Normal, relativePathFolders);

        public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return await LoadAsync<T>(path, priority) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public T? LoadGameAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveGameAssetPath(relativePathFolders);
            return Load<T>(path);
        }

        public async Task<T?> LoadGameAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveGameAssetPath(relativePathFolders);
            return await LoadAsync<T>(path);
        }

        //public async Task<T?> LoadEngine3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => await XR3rdPartyAsset.LoadAsync<T>(Path.Combine(EngineAssetsPath, Path.Combine(relativePathFolders)));

        //public async Task<T?> LoadGame3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => await XR3rdPartyAsset.LoadAsync<T>(Path.Combine(GameAssetsPath, Path.Combine(relativePathFolders)));

        //public T? LoadEngine3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => XR3rdPartyAsset.Load<T>(Path.Combine(EngineAssetsPath, Path.Combine(relativePathFolders)));

        //public T? LoadGame3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => XR3rdPartyAsset.Load<T>(Path.Combine(GameAssetsPath, Path.Combine(relativePathFolders)));

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

        public Func<string, bool>? AllowOverwriteCallback { get; set; } = path => true;

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

        public event Action<XRAsset>? AssetLoaded;
        public event Action<XRAsset>? AssetSaved;
        public event Action<XRAsset>? AssetMarkedDirty;

        private void PostSaved(XRAsset asset, bool newAsset)
        {
            if (newAsset)
                CacheAsset(asset);
            asset.ClearDirty();
            DirtyAssets.TryRemove(asset.ID, out _);
            
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
            AssetLoaded?.Invoke(file);
        }

        private T? LoadCore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
        {
            T? file;
#if !DEBUG
            try
            {
#endif
                if (TryGetAssetByPath(filePath, out XRAsset? existingAsset))
                    return existingAsset is T tAsset ? tAsset : null;

                if (!File.Exists(filePath))
                {
                    AssetDiagnostics.RecordMissingAsset(filePath, typeof(T).Name, $"{nameof(AssetManager)}.{nameof(Load)}");
                    return null;
                }

                string extension = Path.GetExtension(filePath);
                if (string.IsNullOrWhiteSpace(extension) || extension.Length <= 1)
                {
                    Debug.LogWarning($"Unable to load asset at '{filePath}' because the file has no extension.");
                    return null;
                }

                string normalizedExtension = extension[1..].ToLowerInvariant();
                file = normalizedExtension == AssetExtension
                    ? DeserializeAssetFile<T>(filePath)
                    : Load3rdPartyWithCache<T>(filePath, normalizedExtension);
                PostLoaded(filePath, file);
#if !DEBUG
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while loading the asset at '{filePath}'.");
                return null;
            }
#endif
            return file;
        }

        private XRAsset? LoadCore(string filePath, Type type)
        {
            XRAsset? file;
#if !DEBUG
            try
            {
#endif
                if (TryGetAssetByPath(filePath, out XRAsset? existingAsset))
                    return existingAsset.GetType().IsAssignableTo(type) ? existingAsset : null;

                if (!File.Exists(filePath))
                {
                    AssetDiagnostics.RecordMissingAsset(filePath, type.Name, $"{nameof(AssetManager)}.{nameof(Load)}");
                    return null;
                }

                string extension = Path.GetExtension(filePath);
                if (string.IsNullOrWhiteSpace(extension) || extension.Length <= 1)
                {
                    Debug.LogWarning($"Unable to load asset at '{filePath}' because the file has no extension.");
                    return null;
                }

                string normalizedExtension = extension[1..].ToLowerInvariant();
                file = normalizedExtension == AssetExtension
                    ? DeserializeAssetFile(filePath, type)
                    : Load3rdPartyWithCache(filePath, normalizedExtension, type);
                PostLoaded(filePath, file);
#if !DEBUG
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while loading the asset at '{filePath}'.");
                return null;
            }
#endif
            return file;
        }

        private void SaveExistingAssetCore(XRAsset asset)
        {
            if (asset.FilePath is null)
            {
                Debug.LogWarning("Cannot save an asset without a file path.");
                return;
            }
#if !DEBUG
            try
            {
#endif
                using var t = Engine.Profiler.Start($"AssetManager.Save {asset.FilePath}");
                XRAssetGraphUtility.RefreshAssetGraph(asset);
                asset.SerializeTo(asset.FilePath, Serializer);
                PostSaved(asset, false);
#if !DEBUG
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while saving the asset at '{asset.FilePath}'.");
            }
#endif
        }

        private void SaveToDirectoryCore(XRAsset asset, string directory)
        {
#if !DEBUG
            try
            {
#endif
                string path = VerifyAssetPath(asset, directory);
                using var t = Engine.Profiler.Start($"AssetManager.SaveTo {path}");

                if (File.Exists(path) && !(AllowOverwriteCallback?.Invoke(path) ?? true))
                    path = GetUniqueAssetPath(path);

                asset.FilePath = path;
                XRAssetGraphUtility.RefreshAssetGraph(asset);
                asset.SerializeTo(path, Serializer);
                PostSaved(asset, true);
#if !DEBUG
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while saving the asset to '{directory}'.");
            }
#endif
        }

        public Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, JobPriority priority = JobPriority.Normal) where T : XRAsset, new()
            => RunOnJobThreadAsync(() => LoadCore<T>(filePath), priority);

        public XRAsset? Load(string filePath, Type type, JobPriority priority = JobPriority.Normal)
            => RunOnJobThreadBlocking(() => LoadCore(filePath, type), priority);

        public T? Load<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, JobPriority priority = JobPriority.Normal) where T : XRAsset, new()
            => RunOnJobThreadBlocking(() => LoadCore<T>(filePath), priority);

        public Task SaveAsync(XRAsset asset, JobPriority priority = JobPriority.Normal)
            => RunOnJobThreadAsync(() => { SaveExistingAssetCore(asset); return true; }, priority);

        public void Save(XRAsset asset, JobPriority priority = JobPriority.Normal)
            => RunOnJobThreadBlocking(() => { SaveExistingAssetCore(asset); return true; }, priority);

        public void SaveTo(XRAsset asset, Environment.SpecialFolder folder, params string[] folderNames)
            => SaveTo(asset, Path.Combine([Environment.GetFolderPath(folder), ..folderNames]));

        public void SaveTo(XRAsset asset, string directory, JobPriority priority = JobPriority.Normal)
            => RunOnJobThreadBlocking(() => { SaveToDirectoryCore(asset, directory); return true; }, priority);

        public Task SaveToAsync(XRAsset asset, Environment.SpecialFolder folder, params string[] folderNames)
            => SaveToAsync(asset, Path.Combine([Environment.GetFolderPath(folder), .. folderNames]));

        public Task SaveToAsync(XRAsset asset, string directory, JobPriority priority = JobPriority.Normal)
            => RunOnJobThreadAsync(() => { SaveToDirectoryCore(asset, directory); return true; }, priority);

        public Task SaveGameAssetToAsync(XRAsset asset, params string[] folderNames)
            => SaveToAsync(asset, Path.Combine(GameAssetsPath, Path.Combine(folderNames)));
        public void SaveGameAssetTo(XRAsset asset, params string[] folderNames)
            => SaveTo(asset, Path.Combine(GameAssetsPath, Path.Combine(folderNames)));

        #region Prefab helpers

        public SceneNode? InstantiatePrefab(XRPrefabSource prefab,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(prefab);
            return SceneNodePrefabService.Instantiate(prefab, world, parent, maintainWorldTransform);
        }

        public SceneNode? InstantiatePrefab(Guid prefabAssetId,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            if (prefabAssetId == Guid.Empty)
                return null;

            return GetAssetByID(prefabAssetId) is XRPrefabSource prefab
                ? InstantiatePrefab(prefab, world, parent, maintainWorldTransform)
                : null;
        }

        public SceneNode? InstantiatePrefab(string assetPath,
                                            XRWorldInstance? world = null,
                                            SceneNode? parent = null,
                                            bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var prefab = Load<XRPrefabSource>(assetPath);
            return prefab is null
                ? null
                : InstantiatePrefab(prefab, world, parent, maintainWorldTransform);
        }

        public async Task<SceneNode?> InstantiatePrefabAsync(string assetPath,
                                                             XRWorldInstance? world = null,
                                                             SceneNode? parent = null,
                                                             bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var prefab = await LoadAsync<XRPrefabSource>(assetPath).ConfigureAwait(false);
            return prefab is null
                ? null
                : InstantiatePrefab(prefab, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(XRPrefabVariant variant,
                                             XRWorldInstance? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            ArgumentNullException.ThrowIfNull(variant);
            return SceneNodePrefabService.InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(Guid variantAssetId,
                                             XRWorldInstance? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            if (variantAssetId == Guid.Empty)
                return null;

            return GetAssetByID(variantAssetId) is XRPrefabVariant variant
                ? InstantiateVariant(variant, world, parent, maintainWorldTransform)
                : null;
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public SceneNode? InstantiateVariant(string assetPath,
                                             XRWorldInstance? world = null,
                                             SceneNode? parent = null,
                                             bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var variant = Load<XRPrefabVariant>(assetPath);
            return variant is null
                ? null
                : InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        [RequiresUnreferencedCode("Prefab override reflection requires runtime metadata.")]
        public async Task<SceneNode?> InstantiateVariantAsync(string assetPath,
                                                              XRWorldInstance? world = null,
                                                              SceneNode? parent = null,
                                                              bool maintainWorldTransform = false)
        {
            if (string.IsNullOrWhiteSpace(assetPath))
                return null;

            var variant = await LoadAsync<XRPrefabVariant>(assetPath).ConfigureAwait(false);
            return variant is null
                ? null
                : InstantiateVariant(variant, world, parent, maintainWorldTransform);
        }

        #endregion

        private static readonly IReadOnlyList<IYamlTypeConverter> RegisteredYamlTypeConverters = DiscoverYamlTypeConverters();

        internal static IReadOnlyList<IYamlTypeConverter> YamlTypeConverters => RegisteredYamlTypeConverters;

        public static readonly ISerializer Serializer = CreateSerializer();

        public static readonly IDeserializer Deserializer = CreateDeserializer();

        private static ISerializer CreateSerializer()
        {
            var builder = new SerializerBuilder()
                //.IgnoreFields()
                .EnablePrivateConstructors() //TODO: probably avoid using this
                .EnsureRoundtrip()
                .WithEventEmitter(nextEmitter => new DepthTrackingEventEmitter(nextEmitter))
                //.WithTypeConverter(new XRAssetYamlConverter())
                .IncludeNonPublicProperties()
                //.WithTagMapping("!Transform", typeof(Transform))
                //.WithTagMapping("!UIBoundableTransform", typeof(UIBoundableTransform))
                //.WithTagMapping("!UITransform", typeof(UITransform))
                .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitDefaults | DefaultValuesHandling.OmitEmptyCollections);

            foreach (var converter in RegisteredYamlTypeConverters)
                builder.WithTypeConverter(converter);

            builder.WithTypeConverter(new XRAssetYamlConverter());

            return builder.Build();
        }

        private static IDeserializer CreateDeserializer()
        {
            var builder = new DeserializerBuilder()
                .IgnoreUnmatchedProperties()
                .EnablePrivateConstructors()
                .WithEnforceNullability()
                .WithEnforceRequiredMembers()
                .WithDuplicateKeyChecking()
                .WithNodeDeserializer(
                    inner => new DepthTrackingNodeDeserializer(inner),
                    s => s.InsteadOf<ObjectNodeDeserializer>())
                //.WithNodeDeserializer(new XRAssetDeserializer(), w => w.OnTop())
                ;

            foreach (var converter in RegisteredYamlTypeConverters)
                builder.WithTypeConverter(converter);

            builder.WithNodeDeserializer(new XRAssetDeserializer(), w => w.OnTop());

            return builder.Build();
        }

        [RequiresUnreferencedCode("Calls System.Reflection.Assembly.GetTypes()")]
        private static List<IYamlTypeConverter> DiscoverYamlTypeConverters()
        {
            List<IYamlTypeConverter> converters = [];
            HashSet<Type> registeredTypes = [];

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type?[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types ?? [];
                }

                foreach (Type? type in types)
                {
                    if (type is null || type.IsAbstract || type.IsInterface)
                        continue;

                    if (!typeof(IYamlTypeConverter).IsAssignableFrom(type))
                        continue;

                    if (type.GetCustomAttribute<YamlTypeConverterAttribute>() is null)
                        continue;

                    if (!registeredTypes.Add(type))
                        continue;

                    if (Activator.CreateInstance(type) is IYamlTypeConverter instance)
                        converters.Add(instance);
                }
            }

            return converters;
        }

        private static T? DeserializeAssetFile<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAsset {filePath}");
            string contents = File.ReadAllText(filePath);
            return Deserializer.Deserialize<T>(contents);
        }

        public static XRAsset? DeserializeAssetFile(string filePath, Type type)
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAsset {filePath}");
            string contents = File.ReadAllText(filePath);
            return Deserializer.Deserialize(contents, type) as XRAsset;
        }

        private static async Task<T?> DeserializeAssetFileAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAssetAsync {filePath}");
            string contents = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return Deserializer.Deserialize<T>(contents);
        }

        public static async Task<XRAsset?> DeserializeAssetFileAsync(string filePath, Type type)
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAssetAsync {filePath}");
            string contents = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return Deserializer.Deserialize(contents, type) as XRAsset;
        }

        private T? Load3rdPartyWithCache<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            bool hasTimestamp = TryGetSourceTimestamp(filePath, out DateTime timestampUtc);
            bool hasCachePath = TryResolveCachePath(filePath, typeof(T), out string cachePath);

            if (hasTimestamp && hasCachePath && TryLoadCachedAsset(cachePath, filePath, timestampUtc, out T? cachedAsset))
            {
                cachedAsset!.OriginalLastWriteTimeUtc = timestampUtc;
                return cachedAsset;
            }

            var asset = Load3rdPartyAsset<T>(filePath, ext);
            if (asset is null)
                return null;

            if (hasTimestamp)
                asset.OriginalLastWriteTimeUtc = timestampUtc;

            if (hasTimestamp && hasCachePath)
                TryWriteCacheAsset(cachePath, asset);

            return asset;
        }

        private XRAsset? Load3rdPartyWithCache(string filePath, string ext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
        {
            bool hasTimestamp = TryGetSourceTimestamp(filePath, out DateTime timestampUtc);
            bool hasCachePath = TryResolveCachePath(filePath, type, out string cachePath);

            if (hasTimestamp && hasCachePath && TryLoadCachedAsset(cachePath, filePath, timestampUtc, type, out XRAsset? cachedAsset))
            {
                cachedAsset!.OriginalLastWriteTimeUtc = timestampUtc;
                return cachedAsset;
            }

            var asset = Load3rdPartyAsset(filePath, ext, type);
            if (asset is null)
                return null;

            if (hasTimestamp)
                asset.OriginalLastWriteTimeUtc = timestampUtc;

            if (hasTimestamp && hasCachePath)
                TryWriteCacheAsset(cachePath, asset);

            return asset;
        }

        private async Task<T?> Load3rdPartyWithCacheAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            bool hasTimestamp = TryGetSourceTimestamp(filePath, out DateTime timestampUtc);
            bool hasCachePath = TryResolveCachePath(filePath, typeof(T), out string cachePath);

            if (hasTimestamp && hasCachePath)
            {
                var cached = await TryLoadCachedAssetAsync<T>(cachePath, filePath, timestampUtc).ConfigureAwait(false);
                if (cached is not null)
                {
                    cached.OriginalLastWriteTimeUtc = timestampUtc;
                    return cached;
                }
            }

            var asset = await Load3rdPartyAssetAsync<T>(filePath, ext).ConfigureAwait(false);
            if (asset is null)
                return null;

            if (hasTimestamp)
                asset.OriginalLastWriteTimeUtc = timestampUtc;

            if (hasTimestamp && hasCachePath)
                await TryWriteCacheAssetAsync(cachePath, asset).ConfigureAwait(false);

            return asset;
        }

        private static bool TryGetSourceTimestamp(string filePath, out DateTime timestampUtc)
        {
            timestampUtc = File.GetLastWriteTimeUtc(filePath);
            return timestampUtc != DateTime.MinValue;
        }

        private bool TryResolveCachePath(string filePath, Type assetType, out string cachePath)
        {
            cachePath = string.Empty;
            if (string.IsNullOrWhiteSpace(GameCachePath) || string.IsNullOrWhiteSpace(GameAssetsPath))
                return false;

            string projectAssetsRoot = GameAssetsPath;
            string normalizedAssets = Path.GetFullPath(projectAssetsRoot);
            string normalizedSource = Path.GetFullPath(filePath);

            if (!normalizedSource.StartsWith(normalizedAssets, StringComparison.OrdinalIgnoreCase))
                return false;

            string relativePath = Path.GetRelativePath(normalizedAssets, normalizedSource);
            if (relativePath.StartsWith(".."))
                return false;

            string? relativeDirectory = Path.GetDirectoryName(relativePath);
            string originalFileName = Path.GetFileName(relativePath);
            string typeSuffix = assetType.FullName ?? assetType.Name;
            string cacheFileName = $"{originalFileName}.{typeSuffix}.{AssetExtension}";
            cachePath = string.IsNullOrWhiteSpace(relativeDirectory)
                ? Path.Combine(GameCachePath!, cacheFileName)
                : Path.Combine(GameCachePath!, relativeDirectory, cacheFileName);
            return true;
        }

        private static bool TryLoadCachedAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string cachePath, string originalPath, DateTime sourceTimestampUtc, out T? asset) where T : XRAsset, new()
        {
            asset = null;
            if (!File.Exists(cachePath))
                return false;

            try
            {
                var cachedAsset = DeserializeAssetFile<T>(cachePath);
                if (cachedAsset?.OriginalLastWriteTimeUtc is null)
                    return false;

                if (cachedAsset.OriginalLastWriteTimeUtc.Value < sourceTimestampUtc)
                    return false;

                cachedAsset.OriginalPath = originalPath;
                asset = cachedAsset;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read cached asset '{cachePath}'. {ex.Message}");
                return false;
            }
        }

        private static bool TryLoadCachedAsset(string cachePath, string originalPath, DateTime sourceTimestampUtc, Type type, out XRAsset? asset)
        {
            asset = null;
            if (!File.Exists(cachePath))
                return false;

            try
            {
                var cachedAsset = DeserializeAssetFile(cachePath, type);
                if (cachedAsset?.OriginalLastWriteTimeUtc is null)
                    return false;

                if (cachedAsset.OriginalLastWriteTimeUtc.Value < sourceTimestampUtc)
                    return false;

                cachedAsset.OriginalPath = originalPath;
                asset = cachedAsset;
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read cached asset '{cachePath}'. {ex.Message}");
                return false;
            }
        }

        private async Task<T?> TryLoadCachedAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string cachePath, string originalPath, DateTime sourceTimestampUtc) where T : XRAsset, new()
        {
            if (!File.Exists(cachePath))
                return null;

            try
            {
                var cachedAsset = await DeserializeAssetFileAsync<T>(cachePath).ConfigureAwait(false);
                if (cachedAsset?.OriginalLastWriteTimeUtc is null)
                    return null;

                if (cachedAsset.OriginalLastWriteTimeUtc.Value < sourceTimestampUtc)
                    return null;

                cachedAsset.OriginalPath = originalPath;
                return cachedAsset;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to read cached asset '{cachePath}'. {ex.Message}");
                return null;
            }
        }

        private void TryWriteCacheAsset(string cachePath, XRAsset asset)
        {
            try
            {
                string? directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                asset.SerializeTo(cachePath, Serializer);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to write cached asset '{cachePath}'. {ex.Message}");
            }
        }

        private async Task TryWriteCacheAssetAsync(string cachePath, XRAsset asset)
        {
            try
            {
                string? directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await asset.SerializeToAsync(cachePath, Serializer).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to write cached asset '{cachePath}'. {ex.Message}");
            }
        }

        private static T? Load3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            var exts = typeof(T).GetCustomAttribute<XR3rdPartyExtensionsAttribute>()?.Extensions;
            var match = exts?.FirstOrDefault(e => e.ext == ext);
            if (match is not null)
            {
                if (match.Value.staticLoad)
                {
                    var method = typeof(T).GetMethod("Load3rdPartyStatic", BindingFlags.Public | BindingFlags.Static);
                    if (method is not null)
                    {
                        var asset = method.Invoke(null, [filePath]) as T;
                        if (asset is not null)
                        {
                            asset.OriginalPath = filePath;
                            return asset;
                        }
                        else
                        {
                            Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but the static loader method does not return the correct type or returned null.");
                            return null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but does not have a static load method.");
                        return null;
                    }
                }
                else
                {
                    var asset = new T
                    {
                        OriginalPath = filePath
                    };
                    if (asset.Load3rdParty(filePath))
                        return asset;
                    else
                    {
                        Debug.LogWarning($"Failed to load 3rd party asset '{filePath}' as type '{typeof(T).Name}'.");
                        return null;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"The file extension '{ext}' is not supported by the asset type '{typeof(T).Name}'.");
                return null;
            }
        }

        private static XRAsset? Load3rdPartyAsset(string filePath, string ext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
        {
            var exts = type.GetCustomAttribute<XR3rdPartyExtensionsAttribute>()?.Extensions;
            var match = exts?.FirstOrDefault(e => e.ext == ext);
            if (match is not null)
            {
                if (match.Value.staticLoad)
                {
                    var method = type.GetMethod("Load3rdPartyStatic", BindingFlags.Public | BindingFlags.Static);
                    if (method is not null)
                    {
                        var asset = method.Invoke(null, [filePath]) as XRAsset;
                        if (asset is not null)
                        {
                            asset.OriginalPath = filePath;
                            return asset;
                        }
                        else
                        {
                            Debug.LogWarning($"The asset type '{type.Name}' has a 3rd party extension '{ext}' but the static loader method does not return the correct type or returned null.");
                            return null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"The asset type '{type.Name}' has a 3rd party extension '{ext}' but does not have a static load method.");
                        return null;
                    }
                }
                else
                {
                    if (type.CreateInstance() is not XRAsset asset)
                    {
                        Debug.LogWarning($"Failed to construct 3rd party asset '{filePath}' as type '{type.Name}'.");
                        return null;
                    }

                    asset.OriginalPath = filePath;
                    
                    if (asset.Load3rdParty(filePath))
                        return asset;
                    else
                    {
                        Debug.LogWarning($"Failed to load 3rd party asset '{filePath}' as type '{type.Name}'.");
                        return null;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"The file extension '{ext}' is not supported by the asset type '{type.Name}'.");
                return null;
            }
        }

        private static async Task<T?> Load3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            var exts = typeof(T).GetCustomAttribute<XR3rdPartyExtensionsAttribute>()?.Extensions;
            var match = exts?.FirstOrDefault(e => e.ext == ext);
            if (match is not null)
            {
                if (match.Value.staticLoad)
                {
                    var method = typeof(T).GetMethod("Load3rdPartyStaticAsync", BindingFlags.Public | BindingFlags.Static);
                    if (method is not null)
                    {
                        var assetTask = method.Invoke(null, [filePath]) as Task<T?>;
                        if (assetTask is not null)
                        {
                            var asset = await assetTask.ConfigureAwait(false);
                            if (asset is not null)
                            {
                                asset.OriginalPath = filePath;
                                return asset;
                            }
                            else
                            {
                                Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but the static loader method returned null.");
                                return null;
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but the static loader method does not return the correct type.");
                            return null;
                        }
                    }
                    else
                    {
                        Debug.LogWarning($"The asset type '{typeof(T).Name}' has a 3rd party extension '{ext}' but does not have a static loader method.");
                        return null;
                    }
                }
                else
                {
                    var asset = new T
                    {
                        OriginalPath = filePath
                    };
                    if (await asset.Load3rdPartyAsync(filePath).ConfigureAwait(false))
                        return asset;
                    else
                    {
                        Debug.LogWarning($"Failed to load 3rd party asset '{filePath}' as type '{typeof(T).Name}'.");
                        return null;
                    }
                }
            }
            else
            {
                Debug.LogWarning($"The file extension '{ext}' is not supported by the asset type '{typeof(T).Name}'.");
                return null;
            }
        }
        
        public void SaveAll()
        {
            foreach (var kvp in DirtyAssets)
                Save(kvp.Value);
            DirtyAssets.Clear();
        }

        public async Task SaveAllAsync()
        {
            var tasks = DirtyAssets.Values.Select(x => SaveAsync(x));
            await Task.WhenAll(tasks);
            DirtyAssets.Clear();
        }
    }
}
