using XREngine.ModelCaching;
using XREngine.Core.Files.Caching;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Preserves facade access to ModelingBridge-owned deterministic model-cache identity resolution.
/// This compatibility adapter can be removed when prefab authoring moves out of the facade in P6.5.
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
            new FacadeModelCachePolicyServices(assets));
        return policy.TryResolveIdentity(
            new ThirdPartyCachePathRequest(
                assets.GameCachePath,
                assets.GameAssetsPath,
                assets.EngineAssetsPath,
                sourceFilePath,
                assetType,
                callerVariantKey,
                suppliedImportOptions,
                AssetManager.AssetExtension),
            out resolution);
    }
}
