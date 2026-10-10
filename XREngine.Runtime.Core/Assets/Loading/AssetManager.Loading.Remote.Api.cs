using System.Diagnostics.CodeAnalysis;
using XREngine.Core.Engine;
using XREngine.Core.Files;

namespace XREngine;

public partial class AssetManager
{
    public async Task<T> LoadEngineAssetRemoteAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote,
        JobPriority priority = JobPriority.Normal,
        IReadOnlyDictionary<string, string>? metadata = null,
        params string[] relativePathFolders)
        where T : XRAsset, new()
    {
        string path = ResolveEngineAssetPath(relativePathFolders);
        return await LoadAssetRemoteAsync<T>(path, mode, priority, CancellationToken.None, metadata).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Unable to load engine asset at '{path}' through the remote path.");
    }

    public Task<T?> LoadGameAssetRemoteAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote,
        JobPriority priority = JobPriority.Normal,
        IReadOnlyDictionary<string, string>? metadata = null,
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => LoadAssetRemoteAsync<T>(
            ResolveGameAssetPath(relativePathFolders),
            mode,
            priority,
            CancellationToken.None,
            metadata);

    public async Task<T?> LoadByIdRemoteAsync<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] T>(
        Guid assetId,
        RemoteAssetLoadMode mode = RemoteAssetLoadMode.RequestFromRemote,
        JobPriority priority = JobPriority.Normal,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
        where T : XRAsset, new()
    {
        if (assetId == Guid.Empty)
            return null;

        if (TryGetAssetByID(assetId, out XRAsset? existing) && existing is T typed)
            return typed;

        if (UsesRuntimeAssetCatalog)
            throw new NotSupportedException("AssetSource.CatalogPathRequired: an unloaded packaged asset must be requested by its catalog path; remote jobs and host metadata lookup are unavailable.");

        EnsureHostFileAssetAccess();

        if (TryResolveAssetPathById(assetId, out string? localPath) && File.Exists(localPath))
            return await LoadAsync<T>(localPath, priority).ConfigureAwait(false);

        if (mode == RemoteAssetLoadMode.None)
            return null;

        string? downloadedPath = await TryDownloadAssetFromRemoteByIdAsync(
            assetId,
            typeof(T),
            priority,
            cancellationToken,
            metadata).ConfigureAwait(false);
        return downloadedPath is not null && File.Exists(downloadedPath)
            ? await LoadAsync<T>(downloadedPath, priority).ConfigureAwait(false)
            : null;
    }
}
