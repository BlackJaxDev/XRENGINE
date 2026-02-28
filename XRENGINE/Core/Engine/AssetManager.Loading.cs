using Extensions;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
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
    public partial class AssetManager
    {
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
#if XRE_PUBLISHED
                    if (TryLoadPublishedAssetFromArchive(filePath, out T? publishedAsset))
                    {
                        PostLoaded(filePath, publishedAsset);
                        return publishedAsset;
                    }
#endif
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
#if XRE_PUBLISHED
                    if (TryLoadPublishedAssetFromArchive(filePath, type, out XRAsset? publishedAsset))
                    {
                        PostLoaded(filePath, publishedAsset);
                        return publishedAsset;
                    }
#endif
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

        public async Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false) where T : XRAsset, new()
        {
            if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
                await TryDownloadAssetFromRemoteAsync(filePath, typeof(T), priority, CancellationToken.None, additionalMetadata: null).ConfigureAwait(false);

            return await RunOnJobThreadAsync(() => LoadCore<T>(filePath), priority, bypassJobThread).ConfigureAwait(false);
        }

        public XRAsset? Load(string filePath, Type type, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
        {
            if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
                TryDownloadAssetFromRemoteAsync(filePath, type, priority, CancellationToken.None, additionalMetadata: null).GetAwaiter().GetResult();

            return RunOnJobThreadBlocking(() => LoadCore(filePath, type), priority, bypassJobThread);
        }

        public T? Load<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false) where T : XRAsset, new()
        {
            if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
                TryDownloadAssetFromRemoteAsync(filePath, typeof(T), priority, CancellationToken.None, additionalMetadata: null).GetAwaiter().GetResult();

            return RunOnJobThreadBlocking(() => LoadCore<T>(filePath), priority, bypassJobThread);
        }

        public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
            => LoadEngineAsset<T>(JobPriority.Normal, relativePathFolders);

        public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, bool bypassJobThread, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return Load<T>(path, priority, bypassJobThread) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return Load<T>(path, priority) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
            => LoadEngineAssetAsync<T>(JobPriority.Normal, relativePathFolders);

        public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, bool bypassJobThread, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return await LoadAsync<T>(path, priority, bypassJobThread) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return await LoadAsync<T>(path, priority) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public T LoadEngineAssetRemote<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
            => LoadEngineAssetRemoteAsync<T>(mode, priority, metadata, relativePathFolders).GetAwaiter().GetResult();

        public T? LoadGameAssetRemote<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
            => LoadGameAssetRemoteAsync<T>(mode, priority, metadata, relativePathFolders).GetAwaiter().GetResult();

        public async Task<T> LoadEngineAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return await LoadAssetRemoteAsync<T>(path, mode, priority, CancellationToken.None, metadata).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"Unable to load engine file at {path} via remote path.");
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

        public async Task<T?> LoadGameAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveGameAssetPath(relativePathFolders);
            return await LoadAssetRemoteAsync<T>(path, mode, priority, CancellationToken.None, metadata).ConfigureAwait(false);
        }

        public T? LoadByIdRemote<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Guid assetId, RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null) where T : XRAsset, new()
            => LoadByIdRemoteAsync<T>(assetId, mode, priority, metadata, CancellationToken.None).GetAwaiter().GetResult();

        public async Task<T?> LoadByIdRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(Guid assetId, RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote, JobPriority priority = JobPriority.Normal, IReadOnlyDictionary<string, string>? metadata = null, CancellationToken cancellationToken = default) where T : XRAsset, new()
        {
            if (assetId == Guid.Empty)
                return null;

            if (TryGetAssetByID(assetId, out var existing) && existing is T typed)
                return typed;

            if (TryResolveAssetPathById(assetId, out var localPath) && File.Exists(localPath))
                return await LoadAsync<T>(localPath, priority).ConfigureAwait(false);

            if (mode == RemoteAssetLoadMode.None)
                return null;

            bool downloaded = await TryDownloadAssetFromRemoteByIdAsync(assetId, typeof(T), priority, cancellationToken, metadata).ConfigureAwait(false);
            if (!downloaded)
                return null;

            if (TryResolveAssetPathById(assetId, out var downloadedPath) && File.Exists(downloadedPath))
                return await LoadAsync<T>(downloadedPath, priority).ConfigureAwait(false);

            return null;
        }

        // Immediate load/save helpers that bypass the job thread on demand
        public T LoadEngineAssetImmediate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return Load<T>(path, JobPriority.Normal, bypassJobThread: true) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public T? LoadImmediate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
            => Load<T>(filePath, JobPriority.Normal, bypassJobThread: true);

        public XRAsset? LoadImmediate(string filePath, Type type)
            => Load(filePath, type, JobPriority.Normal, bypassJobThread: true);

        //public async Task<T?> LoadEngine3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => await XR3rdPartyAsset.LoadAsync<T>(Path.Combine(EngineAssetsPath, Path.Combine(relativePathFolders)));

        //public async Task<T?> LoadGame3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => await XR3rdPartyAsset.LoadAsync<T>(Path.Combine(GameAssetsPath, Path.Combine(relativePathFolders)));

        //public T? LoadEngine3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => XR3rdPartyAsset.Load<T>(Path.Combine(EngineAssetsPath, Path.Combine(relativePathFolders)));

        //public T? LoadGame3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XR3rdPartyAsset, new()
        //    => XR3rdPartyAsset.Load<T>(Path.Combine(GameAssetsPath, Path.Combine(relativePathFolders)));

        private async Task<T?> LoadAssetRemoteAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, RemoteAssetLoadMode mode, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null) where T : XRAsset, new()
        {
            if (mode == RemoteAssetLoadMode.None || Engine.Jobs.RemoteTransport?.IsConnected != true)
                return await LoadAsync<T>(filePath, priority).ConfigureAwait(false);

            var metadata = additionalMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);

            metadata["path"] = filePath;
            metadata["type"] = typeof(T).AssemblyQualifiedName ?? typeof(T).FullName ?? typeof(T).Name;

            byte[]? payload = null;
            var transferMode = RemoteJobTransferMode.RequestFromRemote;

            if (mode == RemoteAssetLoadMode.SendLocalCopy)
            {
                transferMode = RemoteJobTransferMode.PushDataToRemote;
                if (File.Exists(filePath))
                    payload = await DirectStorageIO.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            }

            var request = new RemoteJobRequest
            {
                Operation = RemoteJobRequest.Operations.AssetLoad,
                TransferMode = transferMode,
                Payload = payload,
                Metadata = metadata,
            };

            RemoteJobResponse response;
            try
            {
                response = await Engine.Jobs.ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset load failed for '{filePath}': {ex.Message}");
                return await LoadAsync<T>(filePath, priority).ConfigureAwait(false);
            }

            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset load failed for '{filePath}': {response.Error ?? "Unknown error"}");
                return null;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset load returned no data for '{filePath}'.");
                return null;
            }

            string contents = Encoding.UTF8.GetString(response.Payload);
            var asset = Deserializer.Deserialize<T>(contents);
            PostLoaded(filePath, asset);
            return asset;
        }

        private static bool ShouldAttemptRemoteAssetDownload()
        {
            if (Engine.Jobs.RemoteTransport?.IsConnected != true)
                return false;

            return Engine.Networking is Engine.ClientNetworkingManager or Engine.PeerToPeerNetworkingManager;
        }

        private async Task<bool> TryDownloadAssetFromRemoteAsync(string filePath, Type assetType, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null)
        {
            if (!ShouldAttemptRemoteAssetDownload())
                return false;

            var metadata = additionalMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);

            metadata["path"] = filePath;
            metadata["type"] = assetType.AssemblyQualifiedName ?? assetType.FullName ?? assetType.Name;

            var request = new RemoteJobRequest
            {
                Operation = RemoteJobRequest.Operations.AssetLoad,
                TransferMode = RemoteJobTransferMode.RequestFromRemote,
                Metadata = metadata,
            };

            RemoteJobResponse response;
            try
            {
                response = await Engine.Jobs.ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset download failed for '{filePath}': {ex.Message}");
                return false;
            }

            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset download failed for '{filePath}': {response.Error ?? "Unknown error"}");
                return false;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset download returned no data for '{filePath}'.");
                return false;
            }

            if (response.Metadata is not null && response.Metadata.TryGetValue("path", out var serverPath) && !string.IsNullOrWhiteSpace(serverPath))
                filePath = serverPath;

            try
            {
                string? directory = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(filePath, response.Payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to persist remote asset '{filePath}': {ex.Message}");
                return false;
            }
        }

        private async Task<bool> TryDownloadAssetFromRemoteByIdAsync(Guid assetId, Type assetType, JobPriority priority, CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalMetadata = null)
        {
            if (assetId == Guid.Empty || !ShouldAttemptRemoteAssetDownload())
                return false;

            var metadata = additionalMetadata is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(additionalMetadata, StringComparer.OrdinalIgnoreCase);

            metadata["id"] = assetId.ToString("D");
            metadata["type"] = assetType.AssemblyQualifiedName ?? assetType.FullName ?? assetType.Name;

            var request = new RemoteJobRequest
            {
                Operation = RemoteJobRequest.Operations.AssetLoad,
                TransferMode = RemoteJobTransferMode.RequestFromRemote,
                Metadata = metadata,
            };

            RemoteJobResponse response;
            try
            {
                response = await Engine.Jobs.ScheduleRemote(request, priority, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Remote asset download failed for id '{assetId}': {ex.Message}");
                return false;
            }

            if (!response.Success)
            {
                Debug.LogWarning($"Remote asset download failed for id '{assetId}': {response.Error ?? "Unknown error"}");
                return false;
            }

            if (response.Payload is null || response.Payload.Length == 0)
            {
                Debug.LogWarning($"Remote asset download returned no data for id '{assetId}'.");
                return false;
            }

            string? targetPath = null;
            if (response.Metadata is not null && response.Metadata.TryGetValue("path", out var serverPath) && !string.IsNullOrWhiteSpace(serverPath))
            {
                targetPath = serverPath;
            }
            else if (TryResolveAssetPathById(assetId, out var resolvedPath))
            {
                targetPath = resolvedPath;
            }
            else
            {
                targetPath = Path.Combine(GameAssetsPath, $"{assetId:D}.{AssetExtension}");
            }

            try
            {
                string? directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                await File.WriteAllBytesAsync(targetPath, response.Payload, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"Failed to persist remote asset '{assetId}' to '{targetPath}': {ex.Message}");
                return false;
            }
        }

        private static T? DeserializeAssetFile<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAsset {filePath}");
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return Deserializer.Deserialize<T>(reader);
        }

        public static XRAsset? DeserializeAssetFile(string filePath, Type type)
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAsset {filePath}");
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            return Deserializer.Deserialize(reader, type) as XRAsset;
        }

        private static async Task<T?> DeserializeAssetFileAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath) where T : XRAsset, new()
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAssetAsync {filePath}");
            // YamlDotNet deserialization is synchronous; keep async signature by doing IO + parse on background thread.
            return await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return Deserializer.Deserialize<T>(reader);
            }).ConfigureAwait(false);
        }

        public static async Task<XRAsset?> DeserializeAssetFileAsync(string filePath, Type type)
        {
            using var t = Engine.Profiler.Start($"AssetManager.DeserializeAssetAsync {filePath}");
            // YamlDotNet deserialization is synchronous; keep async signature by doing IO + parse on background thread.
            return await Task.Run(() =>
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                return Deserializer.Deserialize(reader, type) as XRAsset;
            }).ConfigureAwait(false);
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
            if (string.IsNullOrWhiteSpace(GameCachePath))
                return false;

            string normalizedSource = Path.GetFullPath(filePath);

            // Try game assets first, then engine assets.
            string? relativePath = TryMakeRelativeTo(normalizedSource, GameAssetsPath);
            string cacheSubfolder = string.Empty;
            if (relativePath is null)
            {
                relativePath = TryMakeRelativeTo(normalizedSource, EngineAssetsPath);
                cacheSubfolder = "Engine";
            }

            if (relativePath is null)
                return false;

            string? relativeDirectory = Path.GetDirectoryName(relativePath);
            string originalFileName = Path.GetFileName(relativePath);
            string typeSuffix = assetType.FullName ?? assetType.Name;
            string cacheFileName = $"{originalFileName}.{typeSuffix}.{AssetExtension}";

            string cacheRoot = string.IsNullOrEmpty(cacheSubfolder)
                ? GameCachePath!
                : Path.Combine(GameCachePath!, cacheSubfolder);

            cachePath = string.IsNullOrWhiteSpace(relativeDirectory)
                ? Path.Combine(cacheRoot, cacheFileName)
                : Path.Combine(cacheRoot, relativeDirectory, cacheFileName);
            return true;
        }

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

        private AssetImportContext CreateImportContext(string filePath)
        {
            string? cacheDir = null;
            if (!string.IsNullOrWhiteSpace(GameCachePath))
            {
                string normalizedSource = Path.GetFullPath(filePath);
                string? relativePath = TryMakeRelativeTo(normalizedSource, GameAssetsPath);
                string cacheSubfolder = string.Empty;
                if (relativePath is null)
                {
                    relativePath = TryMakeRelativeTo(normalizedSource, EngineAssetsPath);
                    cacheSubfolder = "Engine";
                }

                if (relativePath is not null)
                {
                    string? relativeDirectory = Path.GetDirectoryName(relativePath);
                    string cacheRoot = string.IsNullOrEmpty(cacheSubfolder)
                        ? GameCachePath!
                        : Path.Combine(GameCachePath!, cacheSubfolder);
                    cacheDir = string.IsNullOrWhiteSpace(relativeDirectory)
                        ? cacheRoot
                        : Path.Combine(cacheRoot, relativeDirectory);
                }
            }

            return new AssetImportContext(filePath, cacheDir);
        }

        private T? Load3rdPartyAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
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
                    var context = CreateImportContext(filePath);
                    if (asset.Load3rdParty(filePath, context))
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

        private XRAsset? Load3rdPartyAsset(string filePath, string ext, [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type type)
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
                    var context = CreateImportContext(filePath);
                    if (asset.Load3rdParty(filePath, context))
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

        private async Task<T?> Load3rdPartyAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] T>(string filePath, string ext) where T : XRAsset, new()
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
                    var context = CreateImportContext(filePath);
                    if (await asset.Load3rdPartyAsync(filePath, context).ConfigureAwait(false))
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
    }
}
