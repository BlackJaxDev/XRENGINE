using Microsoft.DotNet.PlatformAbstractions;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Numerics;
using System.Reflection;
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
            GameFileChanged?.Invoke(args);
        }
        private async Task OnFileChanged(FileSystemEventArgs args)
        {
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
                            DirtyAssets.Add(asset);
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
        public string GameAssetsPath { get; set; } = Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Assets");
        public string PackagesPath { get; set; } = Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Packages");
        public string LibrariesPath { get; set; } = Path.Combine(ApplicationEnvironment.ApplicationBasePath, "Libraries");

        public ConcurrentDictionary<string, XRAsset> LoadedAssetsByOriginalPathInternal { get; } = [];
        public ConcurrentDictionary<string, XRAsset> LoadedAssetsByPathInternal { get; } = [];
        public ConcurrentDictionary<Guid, XRAsset> LoadedAssetsByIDInternal { get; } = [];
        public ConcurrentBag<XRAsset> DirtyAssets { get; } = [];

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
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return Load<T>(path) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }
        public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return await LoadAsync<T>(path) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
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
            AssetSaved?.Invoke(asset);
        }

        private void PostLoaded<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, T? file) where T : XRAsset
        {
            if (file is null)
                return;

            file.Name = Path.GetFileNameWithoutExtension(filePath);
            file.FilePath = filePath;

            CacheAsset(file);
            AssetLoaded?.Invoke(file);
        }

        public async Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
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
                AssetDiagnostics.RecordMissingAsset(filePath, typeof(T).Name, $"{nameof(AssetManager)}.{nameof(LoadAsync)}");
                return null;
            }

            file = await DeserializeAsync<T>(filePath);
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

        public T? Load<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
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

            file = Deserialize<T>(filePath);
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

        public async Task SaveAsync(XRAsset asset)
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
            using var t = Engine.Profiler.Start($"AssetManager.SaveAsync {asset.FilePath}");
            await asset.SerializeToAsync(asset.FilePath, Serializer);
            PostSaved(asset, false);
#if !DEBUG
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while saving the asset at '{asset.FilePath}'.");
            }
#endif
        }

        public void Save(XRAsset asset)
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
            //Debug.Out($"Saving asset to '{asset.FilePath}'...");
            using var t = Engine.Profiler.Start($"AssetManager.Save {asset.FilePath}");
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

        public void SaveTo(XRAsset asset, Environment.SpecialFolder folder, params string[] folderNames)
            => SaveTo(asset, Path.Combine([Environment.GetFolderPath(folder), ..folderNames]));
        public void SaveTo(XRAsset asset, string directory)
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
            //Debug.Out($"Saving asset to '{path}'...");
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

        public Task SaveToAsync(XRAsset asset, Environment.SpecialFolder folder, params string[] folderNames)
            => SaveToAsync(asset, Path.Combine([Environment.GetFolderPath(folder), .. folderNames]));
        public async Task SaveToAsync(XRAsset asset, string directory)
        {
#if !DEBUG
            try
            {
#endif
            string path = VerifyAssetPath(asset, directory);
            using var t = Engine.Profiler.Start($"AssetManager.SaveToAsync {path}");

            if (File.Exists(path) && !(AllowOverwriteCallback?.Invoke(path) ?? true))
                path = GetUniqueAssetPath(path);

            asset.FilePath = path;
            //Debug.Out($"Saving asset to '{path}'...");
            await asset.SerializeToAsync(path, Serializer);
            PostSaved(asset, true);
#if !DEBUG
            }
            catch (Exception e)
            {
                Debug.LogException(e, $"An error occurred while saving the asset to '{directory}'.");
            }
#endif
        }

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

            return builder.Build();
        }

        private static IReadOnlyList<IYamlTypeConverter> DiscoverYamlTypeConverters()
        {
            List<IYamlTypeConverter> converters = [];
            HashSet<Type> registeredTypes = [];

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types ?? Array.Empty<Type>();
                }

                foreach (var type in types)
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

        private static T? Deserialize<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            T? file = default;
#if !DEBUG
            try
            {
#endif
            //Debug.Out($"Loading asset from '{filePath}'...");
            using var t = Engine.Profiler.Start($"AssetManager.Deserialize {filePath}");
            string ext = Path.GetExtension(filePath)[1..].ToLowerInvariant();
            if (ext == AssetExtension)
                file = Deserializer.Deserialize<T>(File.ReadAllText(filePath));
            else
                file = Load3rdParty<T>(filePath, ext);
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

        private static async Task<T?> DeserializeAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            T? file = default;
#if !DEBUG
            try
            {
#endif
            //Debug.Out($"Loading asset from '{filePath}'...");
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAsync {filePath}");
            string ext = Path.GetExtension(filePath)[1..].ToLowerInvariant();
            if (ext == AssetExtension)
                file = await Task.Run(async () => Deserializer.Deserialize<T>(await File.ReadAllTextAsync(filePath)));
            else
                file = await Load3rdPartyAsync<T>(filePath, ext);
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

        private static T? Load3rdParty<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
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
        private static async Task<T?> Load3rdPartyAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
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
                            var asset = await assetTask;
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
                    if (await asset.Load3rdPartyAsync(filePath))
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
            foreach (var asset in DirtyAssets)
                Save(asset);
            DirtyAssets.Clear();
        }

        public async Task SaveAllAsync()
        {
            var tasks = DirtyAssets.Select(SaveAsync);
            await Task.WhenAll(tasks);
            DirtyAssets.Clear();
        }
    }
}
