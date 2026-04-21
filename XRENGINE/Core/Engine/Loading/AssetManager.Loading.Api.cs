using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XREngine.Scene.Prefabs;
using XRAsset = XREngine.Core.Files.XRAsset;

namespace XREngine
{
    public partial class AssetManager
    {
        public Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false) where T : XRAsset, new()
            => LoadAsync<T>(filePath, progressCallback: null, priority, bypassJobThread);

        public async Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath, Action<AssetLoadProgress>? progressCallback, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false) where T : XRAsset, new()
        {
            if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
                await TryDownloadAssetFromRemoteAsync(filePath, typeof(T), priority, CancellationToken.None, additionalMetadata: null).ConfigureAwait(false);

            return await RunOnJobThreadAsync(() =>
            {
                using var progressScope = AssetLoadProgressContext.Begin(filePath, progressCallback);
                return LoadCore<T>(filePath);
            }, priority, bypassJobThread).ConfigureAwait(false);
        }

        public Task<XRAsset?> LoadAsync(string filePath, Type type, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
            => LoadAsync(filePath, type, progressCallback: null, priority, bypassJobThread);

        public async Task<XRAsset?> LoadAsync(string filePath, Type type, Action<AssetLoadProgress>? progressCallback, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
        {
            if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
                await TryDownloadAssetFromRemoteAsync(filePath, type, priority, CancellationToken.None, additionalMetadata: null).ConfigureAwait(false);

            return await RunOnJobThreadAsync(() =>
            {
                using var progressScope = AssetLoadProgressContext.Begin(filePath, progressCallback);
                return LoadCore(filePath, type);
            }, priority, bypassJobThread).ConfigureAwait(false);
        }

        public async Task<PrefabPartialLoadPlan?> PreparePrefabPartialLoadAsync(string filePath, JobPriority priority = JobPriority.Normal, bool bypassJobThread = false)
        {
            if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
                await TryDownloadAssetFromRemoteAsync(filePath, typeof(XRPrefabSource), priority, CancellationToken.None, additionalMetadata: null).ConfigureAwait(false);

            return await RunOnJobThreadAsync(() => PreparePrefabPartialLoad(filePath), priority, bypassJobThread).ConfigureAwait(false);
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

        public T? LoadGameAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(JobPriority priority, bool bypassJobThread, params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveGameAssetPath(relativePathFolders);
            return Load<T>(path, priority, bypassJobThread);
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

        public T LoadEngineAssetImmediate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(params string[] relativePathFolders) where T : XRAsset, new()
        {
            string path = ResolveEngineAssetPath(relativePathFolders);
            return Load<T>(path, JobPriority.Normal, bypassJobThread: true) ?? throw new FileNotFoundException($"Unable to find engine file at {path}");
        }

        public T? LoadImmediate<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(string filePath) where T : XRAsset, new()
            => Load<T>(filePath, JobPriority.Normal, bypassJobThread: true);

        public XRAsset? LoadImmediate(string filePath, Type type)
            => Load(filePath, type, JobPriority.Normal, bypassJobThread: true);
    }
}