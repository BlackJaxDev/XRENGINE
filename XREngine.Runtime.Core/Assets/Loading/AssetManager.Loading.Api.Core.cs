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

    public Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        string filePath,
        Action<AssetLoadProgress>? progressCallback,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
        => LoadAsyncCore<T>(filePath, progressCallback, priority, bypassJobThread, CancellationToken.None);

    /// <summary>
    /// Loads an asset and lets the caller cancel the load.
    /// </summary>
    /// <remarks>
    /// Cancellation is cooperative. The load checks the token before it starts, after it gets the path gate,
    /// before it reads or imports the file, and before it puts the asset in the cache.
    /// A canceled load throws <see cref="OperationCanceledException"/>, releases the path gate,
    /// and adds no entry to the cache. Each caller owns its own load attempt, so a token cancels only
    /// the call that received it. A different caller that waits on the same path gate continues.
    /// The returned task completes only after the load work stops, so no cache change occurs after
    /// the task reports cancellation.
    /// </remarks>
    /// <param name="filePath">The asset file path.</param>
    /// <param name="priority">The job priority of the load.</param>
    /// <param name="bypassJobThread">True to run the load on the calling thread.</param>
    /// <param name="cancellationToken">The token that cancels this load.</param>
    /// <returns>The loaded asset, or <c>null</c> if the load fails.</returns>
    public Task<T?> LoadAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        string filePath,
        JobPriority priority,
        bool bypassJobThread,
        CancellationToken cancellationToken)
        where T : XRAsset, new()
        => LoadAsyncCore<T>(filePath, progressCallback: null, priority, bypassJobThread, cancellationToken);

    private async Task<T?> LoadAsyncCore<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T>(
        string filePath,
        Action<AssetLoadProgress>? progressCallback,
        JobPriority priority,
        bool bypassJobThread,
        CancellationToken cancellationToken)
        where T : XRAsset, new()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (UsesRuntimeAssetCatalog)
            return (T?)await LoadFromRuntimeSourceAsync(filePath, typeof(T), progressCallback, cancellationToken).ConfigureAwait(false);

        if (!SupportsSynchronousAssetWork
            && TryGetAssetByPath(Path.GetFullPath(filePath), out XRAsset? cached)
            && !cached.IsDestroyed)
            return (T)RequireCachedRuntimeAsset(filePath, typeof(T));

        EnsureHostFileLoadRoute(bypassJobThread);

        if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
        {
            await TryDownloadAssetFromRemoteAsync(
                filePath,
                typeof(T),
                priority,
                cancellationToken,
                additionalMetadata: null).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        EnsureHostFileLoadRoute(bypassJobThread);
        return await RunOnJobThreadAsync(() =>
        {
            using IDisposable progressScope = AssetLoadProgressContext.Begin(filePath, progressCallback);
            return LoadCore<T>(filePath, cancellationToken);
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
        if (UsesRuntimeAssetCatalog)
            return await LoadFromRuntimeSourceAsync(filePath, type, progressCallback).ConfigureAwait(false);

        if (!SupportsSynchronousAssetWork
            && TryGetAssetByPath(Path.GetFullPath(filePath), out XRAsset? cached)
            && !cached.IsDestroyed)
            return RequireCachedRuntimeAsset(filePath, type);

        EnsureHostFileLoadRoute(bypassJobThread);

        if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
        {
            await TryDownloadAssetFromRemoteAsync(
                filePath,
                type,
                priority,
                CancellationToken.None,
                additionalMetadata: null).ConfigureAwait(false);
        }

        EnsureHostFileLoadRoute(bypassJobThread);
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
        if (!SupportsSynchronousAssetWork)
            return (T?)RequireCachedRuntimeAsset(filePath, typeof(T));

        EnsureHostFileLoadRoute(bypassJobThread);

        if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
        {
            TryDownloadAssetFromRemoteAsync(
                filePath,
                typeof(T),
                priority,
                CancellationToken.None,
                additionalMetadata: null).GetAwaiter().GetResult();
        }

        EnsureHostFileLoadRoute(bypassJobThread);
        return RunOnJobThreadBlocking(() => LoadCore<T>(filePath), priority, bypassJobThread);
    }

    public XRAsset? Load(
        string filePath,
        Type type,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
    {
        ArgumentNullException.ThrowIfNull(type);
        if (!SupportsSynchronousAssetWork)
            return RequireCachedRuntimeAsset(filePath, type);

        EnsureHostFileLoadRoute(bypassJobThread);

        if (!File.Exists(filePath) && ShouldAttemptRemoteAssetDownload())
        {
            TryDownloadAssetFromRemoteAsync(
                filePath,
                type,
                priority,
                CancellationToken.None,
                additionalMetadata: null).GetAwaiter().GetResult();
        }

        EnsureHostFileLoadRoute(bypassJobThread);
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
