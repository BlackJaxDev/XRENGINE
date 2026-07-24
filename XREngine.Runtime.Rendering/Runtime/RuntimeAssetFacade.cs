using XREngine.Core.Files;
using XREngine.Rendering;

namespace XREngine;

internal sealed class RuntimeAssetFacade
{
    public string EngineAssetsPath => RuntimeRenderingHostServices.Assets.EngineAssetsPath;
    public string GameAssetsPath => RuntimeRenderingHostServices.Assets.GameAssetsPath;
    public string? GameCachePath => RuntimeRenderingHostServices.Assets.GameCachePath;

    public object? GetOrCreateThirdPartyImportOptions(string sourcePath, Type assetType)
        => RuntimeRenderingHostServices.Assets.GetOrCreateThirdPartyImportOptions(sourcePath, assetType);

    public string ResolveEngineAssetPath(params string[] relativePathFolders)
        => RuntimeRenderingHostServices.Assets.ResolveEngineAssetPath(relativePathFolders);

    public T? Load<T>(
        string filePath,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
        => RuntimeRenderingHostServices.Assets.LoadAsset<T>(filePath, priority, bypassJobThread);

    public bool TryResolveThirdPartyCachePath(
        string filePath,
        Type assetType,
        string? cacheVariantKey,
        out string cachePath)
        => RuntimeRenderingHostServices.Assets.TryResolveThirdPartyCachePath(
            filePath,
            assetType,
            cacheVariantKey,
            out cachePath);

    public T? Load3rdPartyVariantWithCache<T>(
        string filePath,
        object? importOptions,
        string cacheVariantKey,
        JobPriority priority = JobPriority.Normal,
        bool bypassJobThread = false)
        where T : XRAsset, new()
        => RuntimeRenderingHostServices.Assets.LoadThirdPartyVariantWithCache<T>(
            filePath,
            importOptions,
            cacheVariantKey,
            priority,
            bypassJobThread);

    public void Evict(XRAsset asset, string resolvedPath)
        => RuntimeRenderingHostServices.Assets.EvictAsset(asset, resolvedPath);

    public Task<T> LoadEngineAssetAsync<T>(
        JobPriority priority,
        bool bypassJobThread,
        params string[] relativePathFolders)
        where T : XRAsset, new()
        => RuntimeRenderingHostServices.Assets.LoadEngineAssetAsync<T>(
            priority,
            bypassJobThread,
            relativePathFolders);

    public Task<T> LoadEngineAssetAsync<T>(params string[] relativePathFolders)
        where T : XRAsset, new()
        => LoadEngineAssetAsync<T>(JobPriority.Normal, false, relativePathFolders);

    public T? LoadEngineAsset<T>(JobPriority priority, params string[] pathParts)
        where T : XRAsset, new()
        => Load<T>(ResolveEngineAssetPath(pathParts), priority);
}
