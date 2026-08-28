using XREngine.Core.Files.Caching;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Resolves ModelAssetPipeline-owned deterministic model-cache identities for runtime assets.
/// </summary>
internal static class AssetManagerModelCacheIdentityExtensions
{
    public static bool TryResolveModelCacheIdentity(
        this AssetManager assets,
        string sourceFilePath,
        Type assetType,
        string? callerVariantKey,
        object? suppliedImportOptions,
        out ModelCachePathResolution? resolution)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(assetType);

        ModelCachePathPolicy policy = new(
            assetType,
            new ModelCachePolicyServices(assets));
        return policy.TryResolveIdentity(
            new ThirdPartyCachePathRequest(
                assets.GameCachePath,
                assets.GameAssetsPath,
                assets.EngineAssetsPath,
                sourceFilePath,
                assetType,
                callerVariantKey,
                suppliedImportOptions,
                AssetManager.AssetExtension,
                assets.TryResolveGenericThirdPartyCachePath),
            out resolution);
    }
}
