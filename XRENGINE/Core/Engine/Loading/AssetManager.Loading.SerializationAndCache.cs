using System;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using XREngine.Animation;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Diagnostics;
using XREngine.Rendering;
using XREngine.Scene.Prefabs;
using AssetImportContext = XREngine.Core.Files.AssetImportContext;
using XRAsset = XREngine.Core.Files.XRAsset;

namespace XREngine
{
    public partial class AssetManager
    {
        /// <summary>
        /// Resets the YAML read context, clearing any state related to YAML deserialization.
        /// </summary>
        private static void ResetYamlReadContext()
        {
            YamlDefaultTypeContext.ResetReadState();
            YamlTransformReferenceContext.ResetReadState();
        }

        /// <summary>
        /// Deserializes an asset file of the specified type.
        /// </summary>
        /// <typeparam name="T">The type of the asset to deserialize, which must be a subclass of <see cref="XRAsset"/> and have a public parameterless constructor.</typeparam>
        /// <param name="filePath">The file path of the asset to deserialize.</param>
        /// <returns>The deserialized asset, or <c>null</c> if deserialization fails.</returns>
        private static T? DeserializeAssetFile<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            EnsureYamlAssetRuntimeSupported(filePath);
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAsset {filePath}");
            AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.OpeningFile, "Opening asset file...", 0.12f);
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            using var scope = AssetDeserializationContext.Push(filePath);
            ResetYamlReadContext();
            AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.ParsingAssetGraph, "Parsing YAML asset graph...", 0.35f);
            return Deserializer.Deserialize<T>(reader);
        }

        /// <summary>
        /// Prepares a partial load plan for a prefab asset.
        /// </summary>
        /// <param name="filePath">The file path of the prefab asset.</param>
        /// <returns>A <see cref="PrefabPartialLoadPlan"/> if the prefab can be partially loaded; otherwise, <c>null</c>.</returns>
        private static PrefabPartialLoadPlan? PreparePrefabPartialLoad(string filePath)
        {
            EnsureYamlAssetRuntimeSupported(filePath);
            filePath = Path.GetFullPath(filePath);
            if (!File.Exists(filePath))
                return null;

            using var t = Engine.Profiler.Start($"AssetManager.PreparePrefabPartialLoad {filePath}");
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            using var scope = AssetDeserializationContext.Push(filePath);
            using var cacheSuppression = XRObjectBase.SuppressObjectCacheRegistration();
            using var collector = new DeferredAssetReferenceContext.Collector();
            ResetYamlReadContext();

            XRPrefabSource? partialPrefab = Deserializer.Deserialize<XRPrefabSource>(reader);
            if (partialPrefab is null)
                return null;

            partialPrefab.FilePath = filePath;
            partialPrefab.SourceAsset = partialPrefab;
            return new PrefabPartialLoadPlan(partialPrefab, collector.References);
        }

        /// <summary>
        /// Deserializes an asset file of the specified type.
        /// </summary>
        /// <param name="filePath">The file path of the asset to deserialize.</param>
        /// <param name="type">The type of the asset to deserialize.</param>
        /// <returns>The deserialized asset, or <c>null</c> if deserialization fails.</returns>
        public static XRAsset? DeserializeAssetFile(string filePath, Type type)
        {
            EnsureYamlAssetRuntimeSupported(filePath);
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAsset {filePath}");
            AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.OpeningFile, "Opening asset file...", 0.12f);
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            using var scope = AssetDeserializationContext.Push(filePath);
            ResetYamlReadContext();
            AssetLoadProgressContext.ReportStage(AssetLoadProgressStage.ParsingAssetGraph, "Parsing YAML asset graph...", 0.35f);
            return Deserializer.Deserialize(reader, type) as XRAsset;
        }

        /// <summary>
        /// Asynchronously deserializes an asset file of the specified type.
        /// </summary>
        /// <typeparam name="T">The type of the asset to deserialize, which must be a subclass of <see cref="XRAsset"/> and have a public parameterless constructor.</typeparam>
        /// <param name="filePath">The file path of the asset to deserialize.</param>
        /// <returns>The deserialized asset, or <c>null</c> if deserialization fails.</returns>
        private static async Task<T?> DeserializeAssetFileAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            EnsureYamlAssetRuntimeSupported(filePath);
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAssetAsync {filePath}");
            return await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                using var scope = AssetDeserializationContext.Push(filePath);
                ResetYamlReadContext();
                return Deserializer.Deserialize<T>(reader);
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Asynchronously deserializes an asset file of the specified type.
        /// </summary>
        /// <param name="filePath">The file path of the asset to deserialize.</param>
        /// <param name="type">The type of the asset to deserialize.</param>
        /// <returns>The deserialized asset, or <c>null</c> if deserialization fails.</returns>
        public static async Task<XRAsset?> DeserializeAssetFileAsync(string filePath, Type type)
        {
            EnsureYamlAssetRuntimeSupported(filePath);
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAssetAsync {filePath}");
            return await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                using var scope = AssetDeserializationContext.Push(filePath);
                ResetYamlReadContext();
                return Deserializer.Deserialize(reader, type) as XRAsset;
            }).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads a third-party asset with caching support. If a valid cached version of the asset exists and is up to date with the source file, it will be loaded and returned. Otherwise, the asset will be loaded from the source file, and a new cache entry will be created if possible.
        /// </summary>
        /// <typeparam name="T">The type of the asset to load, which must be a subclass of <see cref="XRAsset"/> and have a public parameterless constructor.</typeparam>
        /// <param name="filePath">The file path of the asset to load.</param>
        /// <param name="ext">The file extension of the asset to load.</param>
        /// <returns>The loaded asset, or <c>null</c> if loading fails.</returns>
        private T? Load3rdPartyWithCache<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            bool useCache = ShouldUseThirdPartyCache(typeof(T));
            bool hasTimestamp = TryGetSourceTimestamp(filePath, out DateTime timestampUtc);
            string cachePath = string.Empty;
            bool hasCachePath = useCache && TryResolveCachePath(filePath, typeof(T), cacheVariantKey: null, out cachePath);

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

        /// <summary>
        /// Asynchronously loads a third-party asset with caching support. If a valid cached version of the asset exists and is up to date with the source file, it will be loaded and returned. Otherwise, the asset will be loaded from the source file, and a new cache entry will be created if possible.
        /// </summary>
        /// <typeparam name="T">The type of the asset to load, which must be a subclass of <see cref="XRAsset"/> and have a public parameterless constructor.</typeparam>
        /// <param name="filePath">The file path of the asset to load.</param>
        /// <param name="ext">The file extension of the asset to load.</param>
        /// <param name="type">The type of the asset to load.</param>
        /// <returns>The loaded asset, or <c>null</c> if loading fails.</returns>
        private XRAsset? Load3rdPartyWithCache(string filePath, string ext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
        {
            bool useCache = ShouldUseThirdPartyCache(type);
            bool hasTimestamp = TryGetSourceTimestamp(filePath, out DateTime timestampUtc);
            string cachePath = string.Empty;
            bool hasCachePath = useCache && TryResolveCachePath(filePath, type, cacheVariantKey: null, out cachePath);

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

        /// <summary>
        /// Asynchronously loads a third-party asset with caching support. If a valid cached version of the asset exists and is up to date with the source file, it will be loaded and returned. Otherwise, the asset will be loaded from the source file, and a new cache entry will be created if possible.
        /// </summary>
        /// <typeparam name="T">The type of the asset to load, which must be a subclass of <see cref="XRAsset"/> and have a public parameterless constructor.</typeparam>
        /// <param name="filePath">The file path of the asset to load.</param>
        /// <param name="ext">The file extension of the asset to load.</param>
        /// <returns>The loaded asset, or <c>null</c> if loading fails.</returns>
        private async Task<T?> Load3rdPartyWithCacheAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
        {
            bool useCache = ShouldUseThirdPartyCache(typeof(T));
            bool hasTimestamp = TryGetSourceTimestamp(filePath, out DateTime timestampUtc);
            string cachePath = string.Empty;
            bool hasCachePath = useCache && TryResolveCachePath(filePath, typeof(T), cacheVariantKey: null, out cachePath);

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

        /// <summary>
        /// Attempts to get the last write time of the source file.
        /// </summary>
        /// <param name="filePath">The file path of the source file.</param>
        /// <param name="timestampUtc">The last write time of the source file in UTC.</param>
        /// <returns><c>true</c> if the timestamp was successfully retrieved; otherwise, <c>false</c>.</returns>
        private static bool TryGetSourceTimestamp(string filePath, out DateTime timestampUtc)
        {
            timestampUtc = File.GetLastWriteTimeUtc(filePath);
            return timestampUtc != DateTime.MinValue;
        }

        /// <summary>
        /// Resolves the authority path for a texture asset, which may be the original source file or a cached version depending on cache validity and streaming settings.
        /// </summary>
        /// <param name="filePath">The file path of the texture asset.</param>
        /// <returns>The resolved authority path for the texture asset.</returns>
        internal string ResolveTextureStreamingAuthorityPath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return filePath;

            string normalizedPath = Path.GetFullPath(filePath);
            if (string.Equals(Path.GetExtension(normalizedPath), $".{AssetExtension}", StringComparison.OrdinalIgnoreCase))
                return normalizedPath;

            if (!File.Exists(normalizedPath))
                return normalizedPath;

            if (!TryResolveCachePath(normalizedPath, typeof(XRTexture2D), cacheVariantKey: null, out string cachePath))
                return normalizedPath;

            if (IsCacheAssetFresh(cachePath, normalizedPath, typeof(XRTexture2D)))
                return cachePath;

            if (XRTexture2D.ShouldSuppressTextureStreamingCacheWarmup)
                return normalizedPath;

            QueueTextureStreamingCacheImport(normalizedPath, cachePath);
            return normalizedPath;
        }

        /// <summary>
        /// Queues an asynchronous job to import a texture asset into the streaming cache. This is used to warm the cache for textures that are not yet cached or have stale cache entries, allowing them to be loaded from the cache on subsequent accesses. The import job will be scheduled with low priority and will handle errors gracefully, ensuring that the pending import is cleared from the tracking set regardless of success or failure.
        /// </summary>
        /// <param name="sourcePath">The file path of the source texture asset.</param>
        /// <param name="cachePath">The file path of the cached texture asset.</param>
        private void QueueTextureStreamingCacheImport(string sourcePath, string cachePath)
        {
            if (!_pendingTextureStreamingCacheImports.TryAdd(sourcePath, 0))
                return;

            Engine.Jobs.Schedule(
                CacheImportRoutine,
                error: ex =>
                {
                    Debug.LogWarning($"Failed to warm texture cache for '{sourcePath}'. {ex.Message}");
                    _pendingTextureStreamingCacheImports.TryRemove(sourcePath, out _);
                },
                completed: () => _pendingTextureStreamingCacheImports.TryRemove(sourcePath, out _),
                canceled: () => _pendingTextureStreamingCacheImports.TryRemove(sourcePath, out _),
                priority: JobPriority.Low);

            IEnumerable CacheImportRoutine()
            {
                if (!IsCacheAssetFresh(cachePath, sourcePath, typeof(XRTexture2D)))
                    TryImportThirdPartyCacheAsset(sourcePath, typeof(XRTexture2D), importOptions: null, cacheVariantKey: null, cachePath);

                yield break;
            }
        }

        /// <summary>
        /// Resolves the authority path for a third-party asset, 
        /// which may be the original source file or a cached version depending on cache validity and import settings.
        /// This method is used to determine which file path should be used when loading an asset, 
        /// allowing the system to transparently use cached versions of assets when available and valid while falling back to the original source files when necessary.
        /// The method checks for cache validity based on timestamps and import options, and it can also trigger asynchronous cache imports for assets that are not yet cached or have stale cache entries.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="filePath"></param>
        /// <param name="importOptions"></param>
        /// <param name="cacheVariantKey"></param>
        /// <returns></returns>
        internal string ResolveThirdPartyCacheAuthorityPath<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(
            string filePath,
            object? importOptions = null,
            string? cacheVariantKey = null) where T : XRAsset, new()
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return filePath;

            string normalizedPath = Path.GetFullPath(filePath);
            if (string.Equals(Path.GetExtension(normalizedPath), $".{AssetExtension}", StringComparison.OrdinalIgnoreCase))
                return normalizedPath;

            if (!ShouldUseThirdPartyCache(typeof(T)))
                return normalizedPath;

            if (!File.Exists(normalizedPath))
                return normalizedPath;

            if (!TryResolveCachePath(normalizedPath, typeof(T), cacheVariantKey, out string cachePath))
                return normalizedPath;

            if (IsCacheAssetFresh(cachePath, normalizedPath, typeof(T)))
                return cachePath;

            return TryImportThirdPartyCacheAsset(normalizedPath, typeof(T), importOptions, cacheVariantKey, cachePath)
                ? cachePath
                : normalizedPath;
        }

        /// <summary>
        /// Loads a third-party asset with caching support. If a valid cached version of the asset exists and is up to date with the source file, it will be loaded and returned. Otherwise, the asset will be loaded from the source file, and a new cache entry will be created if possible.
        /// This method is designed to be called from the main thread and will block while the asset is being loaded, ensuring that the returned asset is fully loaded and ready for use. It internally runs the loading logic on a job thread to avoid blocking the main thread during file I/O and asset processing, while still providing a synchronous API for callers that require it.
        /// </summary>
        /// <typeparam name="T">The type of the asset to load, which must be a subclass of <see cref="XRAsset"/> and have a public parameterless constructor.</typeparam>
        /// <param name="filePath">The file path of the asset to load.</param>
        /// <param name="importOptions">Optional import options that may affect cache validity and asset loading.</param>
        /// <param name="cacheVariantKey">An optional key used to differentiate cache variants for the same source file and asset type, allowing for multiple cached versions of an asset based on different import settings or contexts.</param>
        /// <param name="priority">The priority to use when scheduling the loading job. This can be used to influence the order in which loading jobs are processed, with higher priority jobs being executed before lower priority ones.</param>
        /// <param name="bypassJobThread">If set to <c>true</c>, the loading logic will be executed on the calling thread instead of being scheduled on a job thread. This can be useful in scenarios where the caller is already on a background thread or when job scheduling overhead needs to be avoided for very fast loads. However, it should be used with caution to avoid blocking critical threads.</param>
        /// <returns>The loaded asset, or <c>null</c> if loading fails.</returns>
        internal T? Load3rdPartyVariantWithCache<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, object? importOptions, string cacheVariantKey, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false) where T : XRAsset, new()
            => RunOnJobThreadBlocking(() => Load3rdPartyVariantWithCacheCore<T>(filePath, importOptions, cacheVariantKey), priority, bypassJobThread);

        /// <summary>
        /// Core logic for loading a third-party asset with caching support. This method is intended to be run on a job thread and contains the actual implementation for checking cache validity, loading from cache or source, and writing to cache as needed. It is separated from the public API method to allow for flexibility in how the loading logic is executed (e.g., synchronously on the main thread or asynchronously on a job thread) while keeping the core functionality centralized in one place.
        /// </summary>
        /// <typeparam name="T">The type of the asset to load, which must be a subclass of <see cref="XRAsset"/> and have a public parameterless constructor.</typeparam>
        /// <param name="filePath">The file path of the asset to load.</param>
        /// <param name="importOptions">Optional import options that may affect cache validity and asset loading.</param>
        /// <param name="cacheVariantKey">An optional key used to differentiate cache variants for the same source file and asset type, allowing for multiple cached versions of an asset based on different import settings or contexts.</param>
        /// <returns>The loaded asset, or <c>null</c> if loading fails.</returns>
        private T? Load3rdPartyVariantWithCacheCore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, object? importOptions, string cacheVariantKey) where T : XRAsset, new()
        {
            filePath = Path.GetFullPath(filePath);
            if (!File.Exists(filePath))
            {
                AssetDiagnostics.RecordMissingAsset(filePath, typeof(T).Name, $"{nameof(AssetManager)}.{nameof(Load3rdPartyVariantWithCache)}");
                return null;
            }

            if (!TryGetSourceTimestamp(filePath, out DateTime timestampUtc))
                return null;

            bool useCache = ShouldUseThirdPartyCache(typeof(T));
            string cachePath = string.Empty;
            if (useCache && !TryResolveCachePath(filePath, typeof(T), cacheVariantKey, out cachePath))
                return null;

            if (useCache && TryLoadCachedAsset(cachePath, filePath, timestampUtc, out T? cachedAsset))
            {
                cachedAsset!.FilePath = filePath;
                cachedAsset.OriginalPath = filePath;
                cachedAsset.OriginalLastWriteTimeUtc = timestampUtc;
                return cachedAsset;
            }

            var asset = new T
            {
                FilePath = filePath,
                OriginalPath = filePath,
                OriginalLastWriteTimeUtc = timestampUtc,
            };

            var context = CreateImportContext(filePath, cacheVariantKey);
            if (!asset.Load3rdParty(filePath, importOptions, context))
                return null;

            if (useCache)
                TryWriteCacheAsset(cachePath, asset);
            return asset;
        }

        /// <summary>
        /// Attempts to import a third-party asset and write it to the cache. This method is used when a cached version of an asset is not available or is stale, allowing the system to import the asset from the source file and create a new cache entry for future use. The method checks if the asset type supports caching, retrieves the source file's timestamp, creates an instance of the asset, and attempts to load it using the provided import options and context. If the import is successful, it writes the asset to the cache and returns whether the cache file now exists.
        /// </summary>
        /// <param name="filePath">The file path of the source asset to import.</param>
        /// <param name="assetType">The type of the asset to import, which must be a subclass of <see cref="XRAsset"/> and have a public parameterless constructor.</param>
        /// <param name="importOptions">Optional import options that may affect how the asset is imported and whether the cache is considered valid.</param>
        /// <param name="cacheVariantKey">An optional key used to differentiate cache variants for the same source file and asset type, allowing for multiple cached versions of an asset based on different import settings or contexts.</param>
        /// <param name="cachePath">The file path where the cached asset should be written if the import is successful.</param>
        /// <returns><c>true</c> if the asset was successfully imported and the cache was written; otherwise, <c>false</c>.</returns>
        private bool TryImportThirdPartyCacheAsset(
            string filePath,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type assetType,
            object? importOptions,
            string? cacheVariantKey,
            string cachePath)
        {
            if (!ShouldUseThirdPartyCache(assetType))
                return false;

            if (!TryGetSourceTimestamp(filePath, out DateTime timestampUtc))
                return false;

            if (Activator.CreateInstance(assetType) is not XRAsset asset)
                return false;

            asset.Name = Path.GetFileNameWithoutExtension(filePath);
            asset.FilePath = cachePath;
            asset.OriginalPath = filePath;
            asset.OriginalLastWriteTimeUtc = timestampUtc;

            AssetImportContext context = CreateImportContext(filePath, cacheVariantKey);
            if (!asset.Load3rdParty(filePath, importOptions, context))
                return false;

            TryWriteCacheAsset(cachePath, asset);
            return File.Exists(cachePath);
        }

        /// <summary>
        /// Attempts to resolve the cache file path for a given source asset file, asset type, and optional cache variant key. The method determines the appropriate cache directory based on the source file's location relative to known asset directories and the provided variant key, then constructs the full cache file path using a consistent naming convention that includes the original file name, asset type, and cache extension. This method does not check for the existence of the cache file; it only computes the expected path where the cached asset would be stored if it were created.
        /// </summary>
        /// <param name="filePath">The file path of the source asset.</param>
        /// <param name="assetType">The type of the asset.</param>
        /// <param name="cacheVariantKey">An optional key to differentiate cache variants.</param>
        /// <param name="cachePath">The resolved cache file path.</param>
        /// <returns><c>true</c> if the cache path was successfully resolved; otherwise, <c>false</c>.</returns>
        private bool TryResolveCachePath(string filePath, Type assetType, string? cacheVariantKey, out string cachePath)
        {
            cachePath = string.Empty;
            if (!TryResolveCacheDirectory(filePath, cacheVariantKey, out string cacheDirectory))
                return false;

            string normalizedSource = Path.GetFullPath(filePath);
            string originalFileName = Path.GetFileName(normalizedSource);
            string typeSuffix = assetType.FullName ?? assetType.Name;
            string cacheFileName = $"{originalFileName}.{typeSuffix}.{AssetExtension}";

            cachePath = Path.Combine(cacheDirectory, cacheFileName);
            return true;
        }

        /// <summary>
        /// Attempts to resolve the cache file path for a given source asset file and asset type, without using a cache variant key. This is a convenience overload of the <see cref="TryResolveCachePath(string, Type, string?, out string)"/> method for cases where cache variants are not needed, allowing callers to simply provide the source file path and asset type to get the corresponding cache path. Internally, it calls the more general method with a null variant key.
        /// </summary>
        /// <param name="filePath">The file path of the source asset.</param>
        /// <param name="assetType">The type of the asset.</param>
        /// <param name="cachePath">The resolved cache file path.</param>
        /// <returns><c>true</c> if the cache path was successfully resolved; otherwise, <c>false</c>.</returns>
        private bool TryResolveCachePath(string filePath, Type assetType, out string cachePath)
            => TryResolveCachePath(filePath, assetType, cacheVariantKey: null, out cachePath);

        /// <summary>
        /// Attempts to compute a relative path from the specified root directory to the normalized source file path. This method is used to determine if the source file is located within a known asset directory (e.g., game assets or engine assets) and to compute the relative path that can be used for cache organization. If the source file is not located within the specified root directory, or if the root directory is null or empty, the method returns null, indicating that a relative path cannot be computed.
        /// </summary>
        /// <param name="normalizedSource">The normalized source file path.</param>
        /// <param name="root">The root directory to which the relative path should be computed.</param>
        /// <returns>The relative path if it can be computed; otherwise, <c>null</c>.</returns>
        private static string? TryMakeRelativeTo(string normalizedSource, string? root)
        {
            if (string.IsNullOrWhiteSpace(root))
                return null;

            string normalizedRoot = Path.GetFullPath(root);
            if (!normalizedSource.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return null;

            string relative = Path.GetRelativePath(normalizedRoot, normalizedSource);
            return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
        }

        /// <summary>
        /// Attempts to resolve the cache directory for a given source asset file and optional cache variant key. The method determines the appropriate cache directory based on the source file's location relative to known asset directories (game assets or engine assets) and the provided variant key, allowing for organized storage of cached assets that mirrors the structure of the source assets. If the source file is not located within any known asset directories, it falls back to using an "External" directory within the cache, with a subdirectory named based on a hash of the source file path to avoid collisions. The method returns true if a cache directory path was successfully computed, regardless of whether that directory currently exists on disk.
        /// </summary>
        /// <param name="filePath">The file path of the source asset.</param>
        /// <param name="cacheVariantKey">An optional key to differentiate cache variants.</param>
        /// <param name="cacheDirectory">The resolved cache directory path.</param>
        /// <returns><c>true</c> if the cache directory was successfully resolved; otherwise, <c>false</c>.</returns>
        private bool TryResolveCacheDirectory(string filePath, string? cacheVariantKey, out string cacheDirectory)
        {
            cacheDirectory = string.Empty;
            if (string.IsNullOrWhiteSpace(GameCachePath))
                return false;

            string normalizedSource = Path.GetFullPath(filePath);

            string? relativePath = TryMakeRelativeTo(normalizedSource, GameAssetsPath);
            string cacheRoot = GameCachePath!;
            if (relativePath is null)
            {
                relativePath = TryMakeRelativeTo(normalizedSource, EngineAssetsPath);
                if (relativePath is not null)
                    cacheRoot = Path.Combine(GameCachePath!, "Engine");
            }

            if (relativePath is not null)
            {
                string? relativeDirectory = Path.GetDirectoryName(relativePath);
                cacheDirectory = string.IsNullOrWhiteSpace(relativeDirectory)
                    ? cacheRoot
                    : Path.Combine(cacheRoot, relativeDirectory);
            }
            else
            {
                cacheDirectory = Path.Combine(
                    GameCachePath!,
                    "External",
                    GetExternalCacheDirectoryName(normalizedSource));
            }

            if (!string.IsNullOrWhiteSpace(cacheVariantKey))
                cacheDirectory = Path.Combine(cacheDirectory, cacheVariantKey);

            return true;
        }

        /// <summary>
        /// Determines whether the cached asset at the specified cache path is fresh compared to the source asset at the given source path, based on their last write timestamps and any relevant import options. The method checks if the cache file exists, verifies that it is usable (for textures), compares the cache timestamp to the source timestamp, and also checks for any import options that may affect cache validity. If the cache asset is deemed fresh, it can be safely loaded; otherwise, it should be considered stale and not used.
        /// </summary>
        /// <param name="cachePath">The file path of the cached asset.</param>
        /// <param name="sourcePath">The file path of the source asset.</param>
        /// <param name="assetType">The type of the asset.</param>
        /// <returns><c>true</c> if the cached asset is fresh; otherwise, <c>false</c>.</returns>
        private bool IsCacheAssetFresh(string cachePath, string sourcePath, Type assetType)
        {
            if (!File.Exists(cachePath))
                return false;

            if (assetType == typeof(XRTexture2D) && !XRTexture2D.IsTextureStreamingAssetUsable(cachePath))
                return false;

            DateTime cacheTimestampUtc = File.GetLastWriteTimeUtc(cachePath);
            if (cacheTimestampUtc == DateTime.MinValue)
                return false;

            if (!TryGetSourceTimestamp(sourcePath, out DateTime sourceTimestampUtc))
                return false;

            if (cacheTimestampUtc < sourceTimestampUtc)
                return false;

            if (TryResolveImportOptionsPath(sourcePath, assetType, out string importOptionsPath)
                && File.Exists(importOptionsPath))
            {
                DateTime importOptionsTimestampUtc = File.GetLastWriteTimeUtc(importOptionsPath);
                if (importOptionsTimestampUtc != DateTime.MinValue && cacheTimestampUtc < importOptionsTimestampUtc)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Determines whether the specified asset type should use the third-party caching mechanism.
        /// The cache pipeline now supports all currently imported third-party asset types, including
        /// <see cref="AnimationClip"/>, so this remains a central opt-out point for any future
        /// type-specific exceptions.
        /// </summary>
        /// <param name="assetType">The type of the asset to check for caching eligibility.</param>
        /// <returns><c>true</c> if the asset type should use the third-party caching mechanism; otherwise, <c>false</c>.</returns>
        private static bool ShouldUseThirdPartyCache(Type assetType)
        {
            return true;
        }

        /// <summary>
        /// Generates a directory name for caching external assets based on a hash of the normalized source file path. This is used to create unique cache directories for assets that are not located within the known game or engine asset directories, preventing collisions and allowing for organized storage of cached assets that come from arbitrary locations. The method uses SHA256 hashing to generate a consistent and compact directory name based on the source file path, ensuring that the same source path will always map to the same cache directory.
        /// </summary>
        /// <param name="normalizedSource">The normalized source file path for which to generate a cache directory name.</param>
        /// <returns>A string representing the generated cache directory name.</returns>
        private static string GetExternalCacheDirectoryName(string normalizedSource)
        {
            byte[] sourceBytes = Encoding.UTF8.GetBytes(normalizedSource);
            byte[] hash = SHA256.HashData(sourceBytes);
            return Convert.ToHexString(hash.AsSpan(0, 8));
        }

        /// <summary>
        /// Attempts to load a cached asset of type <typeparamref name="T"/> from the specified cache path, validating its freshness against the source asset's timestamp. If the cached asset is successfully loaded and deemed fresh, it is returned via the out parameter and the method returns true; otherwise, the out parameter is set to null and the method returns false. Exceptions during the load process are caught and logged as warnings, but not re-thrown, to avoid disrupting the calling code. This method is intended to be used for loading cached assets on the main thread; for asynchronous cache loads, use <see cref="TryLoadCachedAssetAsync{T}(string, string, DateTime)"/> instead.
        /// </summary>
        /// <param name="cachePath">The file path of the cached asset to load. This should be a path within the cache directory structure.</param>
        /// <param name="originalPath">The original file path of the source asset corresponding to the cached asset. This is used for validation and is assigned to the loaded asset's <see cref="XRAsset.OriginalPath"/> property if the load is successful.</param>
        /// <param name="sourceTimestampUtc">The last write time of the source asset in UTC, used to validate the freshness of the cached asset. If the cached asset's timestamp is older than this value, it is considered stale and will not be loaded.</param>
        /// <param name="asset">When this method returns, contains the loaded asset of type <typeparamref name="T"/> if the load is successful and the cached asset is fresh; otherwise, null.</param>
        /// <returns>true if the cached asset was successfully loaded and is fresh; otherwise, false.</returns>
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
                try
                {
                    if (File.Exists(cachePath))
                        File.Delete(cachePath);
                }
                catch
                {
                }

                return false;
            }
        }

        /// <summary>
        /// Attempts to load a cached asset of the specified type from the given cache path, validating its freshness against the source asset's timestamp. If the cached asset is successfully loaded and deemed fresh, it is returned via the out parameter and the method returns true; otherwise, the out parameter is set to null and the method returns false. Exceptions during the load process are caught and logged as warnings, but not re-thrown, to avoid disrupting the calling code. This method is intended to be used for loading cached assets on the main thread; for asynchronous cache loads, use <see cref="TryLoadCachedAssetAsync{T}(string, string, DateTime)"/> instead.
        /// </summary>
        /// <param name="cachePath">The file path of the cached asset to load. This should be a path within the cache directory structure.</param>
        /// <param name="originalPath">The original file path of the source asset corresponding to the cached asset. This is used for validation and is assigned to the loaded asset's <see cref="XRAsset.OriginalPath"/> property if the load is successful.</param>
        /// <param name="sourceTimestampUtc">The last write time of the source asset in UTC, used to validate the freshness of the cached asset. If the cached asset's timestamp is older than this value, it is considered stale and will not be loaded.</param>
        /// <param name="asset">When this method returns, contains the loaded asset of the specified type if the load is successful and the cached asset is fresh; otherwise, null.</param>
        /// <returns>true if the cached asset was successfully loaded and is fresh; otherwise, false.</returns>
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

        /// <summary>
        /// Asynchronously attempts to load a cached asset of type <typeparamref name="T"/> from the specified cache path, validating its freshness against the source asset's timestamp.
        /// If the cached asset is successfully loaded and deemed fresh, it is returned; otherwise, null is returned. Exceptions during the load process are caught and logged as warnings, but not re-thrown, to avoid disrupting the calling code.
        /// This method is intended to be used for loading cached assets without blocking the main thread; for synchronous cache loads, use <see cref="TryLoadCachedAsset{T}(string, string, DateTime, out T)"/> instead.
        /// </summary> 
        /// <typeparam name="T">The type of the asset to load, which must be a subclass of <see cref="XRAsset"/> and have a public parameterless constructor.</typeparam>
        /// <param name="cachePath">The file path of the cached asset to load. This should be a path within the cache directory structure.</param>
        /// <param name="originalPath">The original file path of the source asset corresponding to the cached asset. This is used for validation and is assigned to the loaded asset's <see cref="XRAsset.OriginalPath"/> property if the load is successful.</param>
        /// <param name="sourceTimestampUtc">The last write time of the source asset in UTC, used to validate the freshness of the cached asset. If the cached asset's timestamp is older than this value, it is considered stale and will not be loaded.</param>
        /// <returns>A task that represents the asynchronous load operation. The task result contains the loaded asset of type <typeparamref name="T"/> if the load is successful and the cached asset is fresh; otherwise, the result is null.</returns>
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

        /// <summary>
        /// Attempts to write the given asset to the specified cache path.
        /// Exceptions during the write process are caught and logged as warnings, 
        /// but not re-thrown, to avoid disrupting the calling code. 
        /// This method ensures that any necessary directories are created before attempting to write the file.
        /// This method is intended to be used for writing cached assets on the main thread; 
        /// for asynchronous cache writes, use <see cref="TryWriteCacheAssetAsync(string, XRAsset)"/> instead.
        /// </summary>
        /// <param name="cachePath">The file path where the asset should be written. 
        /// This should be a path within the cache directory structure.</param>
        /// <param name="asset">The XRAsset instance to be serialized and written to the cache. 
        /// The asset's data will be serialized using the AssetManager's configured Serializer.</param>
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

        /// <summary>
        /// Asynchronously writes the given asset to the specified cache path.
        /// Exceptions during the write process are caught and logged as warnings,
        /// but not re-thrown, to avoid disrupting the calling code. 
        /// This method is intended to be used for writing cached assets without blocking the main thread, 
        /// and it ensures that any necessary directories are created before attempting to write the file.
        /// </summary>
        /// <param name="cachePath">The file path where the asset should be written. This should be a path within the cache directory structure.</param>
        /// <param name="asset">The XRAsset instance to be serialized and written to the cache. The asset's data will be serialized using the AssetManager's configured Serializer.</param>
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

        /// <summary>
        /// Creates an <see cref="AssetImportContext"/> for the given source file path, 
        /// resolving the appropriate cache directory if possible. 
        /// This overload is used when no cache variant key is needed to differentiate cache entries 
        /// for the same source file and asset type.
        /// The returned context contains the resolved cache directory (if any) and is used to communicate 
        /// cache-related information to asset import implementations via the <see cref="AssetImportContext"/> parameter 
        /// on <see cref="XRAsset.Load3rdParty(string, object?, AssetImportContext)"/>.
        /// </summary>
        /// <param name="filePath">The source file path for the asset being imported.</param>
        /// <returns>An <see cref="AssetImportContext"/> containing the resolved cache directory (if any) and other relevant information for the asset import process.</returns>
        private AssetImportContext CreateImportContext(string filePath)
            => CreateImportContext(filePath, cacheVariantKey: null);

        /// <summary>
        /// Creates an <see cref="AssetImportContext"/> for the given source file path and optional cache variant key, 
        /// resolving the appropriate cache directory if possible.
        /// The returned context contains the resolved cache directory (if any) and is used to communicate 
        /// cache-related information to asset import implementations via the <see cref="AssetImportContext"/> parameter 
        /// on <see cref="XRAsset.Load3rdParty(string, object?, AssetImportContext)"/>.
        /// </summary>
        /// <param name="filePath">The source file path for the asset being imported.</param>
        /// <param name="cacheVariantKey">An optional key used to differentiate cache variants for the same source file and asset type. This is useful when multiple different import options may be used for the same source file, and those options affect the imported asset in a way that requires separate cache entries (e.g. different texture compression settings for the same source texture).</param>
        /// <returns>An <see cref="AssetImportContext"/> containing the resolved cache directory (if any) and other relevant information for the asset import process.</returns>
        private AssetImportContext CreateImportContext(string filePath, string? cacheVariantKey)
        {
            string? cacheDir = TryResolveCacheDirectory(filePath, cacheVariantKey, out string resolvedCacheDir)
                ? resolvedCacheDir
                : null;
            return new AssetImportContext(filePath, cacheDir);
        }
    }
}
