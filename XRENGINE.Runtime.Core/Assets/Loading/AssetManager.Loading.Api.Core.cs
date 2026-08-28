using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Engine;
using XREngine.Core.Files;
using XREngine.Core.Files.Caching;
using XREngine.Data;
using XREngine.Data.Core;

namespace XREngine;

public partial class AssetManager
{
    public Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        string filePath,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
        => LoadAsync<T>(filePath, progressCallback: null, priority, bypassJobThread);

    public async Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        string filePath,
        Action<AssetLoadProgress>? progressCallback,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
    {
        if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
        {
            await TryDownloadAssetFromRemoteAsync(
                filePath,
                typeof(T),
                priority,
                CancellationToken.None,
                additionalMetadata: null).ConfigureAwait(false);
        }

        return await RunOnJobThreadAsync(() =>
        {
            using IDisposable progressScope = AssetLoadProgressContext.Begin(filePath, progressCallback);
            return LoadCore<T>(filePath);
        }, priority, bypassJobThread).ConfigureAwait(false);
    }

    public Task<XRAsset?> LoadAsync(
        string filePath,
        Type type,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        => LoadAsync(filePath, type, progressCallback: null, priority, bypassJobThread);

    public async Task<XRAsset?> LoadAsync(
        string filePath,
        Type type,
        Action<AssetLoadProgress>? progressCallback,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
        {
            await TryDownloadAssetFromRemoteAsync(
                filePath,
                type,
                priority,
                CancellationToken.None,
                additionalMetadata: null).ConfigureAwait(false);
        }

        return await RunOnJobThreadAsync(() =>
        {
            using IDisposable progressScope = AssetLoadProgressContext.Begin(filePath, progressCallback);
            return LoadCore(filePath, type);
        }, priority, bypassJobThread).ConfigureAwait(false);
    }

    public T? Load<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        string filePath,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
    {
        if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
        {
            TryDownloadAssetFromRemoteAsync(
                filePath,
                typeof(T),
                priority,
                CancellationToken.None,
                additionalMetadata: null).GetAwaiter().GetResult();
        }

        return RunOnJobThreadBlocking(() => LoadCore<T>(filePath), priority, bypassJobThread);
    }

    public XRAsset? Load(
        string filePath,
        Type type,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
        {
            TryDownloadAssetFromRemoteAsync(
                filePath,
                type,
                priority,
                CancellationToken.None,
                additionalMetadata: null).GetAwaiter().GetResult();
        }

        return RunOnJobThreadBlocking(() => LoadCore(filePath, type), priority, bypassJobThread);
    }

    public T? LoadImmediate<T>(string filePath) where T : XRAsset, new()
        => Load<T>(filePath, JobPriority.Normal, bypassJobThread: true);

    public XRAsset? LoadImmediate(string filePath, Type type)
        => Load(filePath, type, JobPriority.Normal, bypassJobThread: true);

    public T? LoadGameAsset<T>(params string[] relativePathFolders) where T : XRAsset, new()
        => LoadGameAsset<T>(JobPriority.Normal, bypassJobThread: false, relativePathFolders);

    public T? LoadGameAsset<T>(
        JobPriority priority,
        bool bypassJobThread,
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => Load<T>(ResolveGameAssetPath(relativePathFolders), priority, bypassJobThread);

    public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => LoadEngineAsset<T>(JobPriority.Normal, bypassJobThread: false, relativePathFolders);

    public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        JobPriority priority,
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => LoadEngineAsset<T>(priority, bypassJobThread: false, relativePathFolders);

    public T LoadEngineAsset<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        JobPriority priority,
        bool bypassJobThread,
        params string[] relativePathFolders)
        where T : XRAsset, new()
    {
        string path = ResolveEngineAssetPath(relativePathFolders);
        return Load<T>(path, priority, bypassJobThread)
            ?? throw new FileNotFoundException($"Unable to find engine asset at '{path}'.");
    }

    public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        params string[] relativePathFolders)
        where T : XRAsset, new()
    {
        string path = ResolveEngineAssetPath(relativePathFolders);
        return await LoadAsync<T>(path).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Unable to find engine asset at '{path}'.");
    }

    public Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        JobPriority priority,
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => LoadEngineAssetAsync<T>(priority, bypassJobThread: false, relativePathFolders);

    public async Task<T> LoadEngineAssetAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        JobPriority priority,
        bool bypassJobThread,
        params string[] relativePathFolders)
        where T : XRAsset, new()
    {
        string path = ResolveEngineAssetPath(relativePathFolders);
        return await LoadAsync<T>(path, priority, bypassJobThread).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Unable to find engine asset at '{path}'.");
    }

    public Task<T?> LoadGameAssetAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => LoadAsync<T>(ResolveGameAssetPath(relativePathFolders));

    public T LoadEngineAssetImmediate<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        params string[] relativePathFolders)
        where T : XRAsset, new()
    {
        string path = ResolveEngineAssetPath(relativePathFolders);
        return Load<T>(path, JobPriority.Normal, bypassJobThread: true)
            ?? throw new FileNotFoundException($"Unable to find engine asset at '{path}'.");
    }

}
