using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;

namespace XREngine.ModelCaching;

/// <summary>
/// Adapts the still-facade-owned prefab authoring graph to ModelingBridge cache policy. This
/// adapter disappears with the prefab migration in P6.5; the cache algorithm already lives in
/// ModelingBridge.
/// </summary>
public sealed class FacadeModelCachePolicyServices(AssetManager assets) : IModelCachePolicyServices
{
    private readonly AssetManager _assets = assets ?? throw new ArgumentNullException(nameof(assets));

    public ModelImportOptions GetImportOptions(
        string sourcePath,
        Type assetType,
        object? suppliedOptions)
        => suppliedOptions as ModelImportOptions
            ?? _assets.GetOrCreateThirdPartyImportOptions(sourcePath, assetType) as ModelImportOptions
            ?? new ModelImportOptions();

    public bool TryBuildCookOverrideSnapshot(
        string sourcePath,
        ModelCookSettings modelDefaults,
        out ModelCookOverrideSnapshot snapshot)
    {
        snapshot = ModelCookOverrideSnapshot.Empty;
        if (!_assets.GetThirdPartyImportService().TryResolveGeneratedAssetPathForThirdPartySource(
            sourcePath,
            out string generatedAssetPath)
            || !File.Exists(generatedAssetPath))
        {
            return true;
        }

        string normalizedPath = Path.GetFullPath(generatedAssetPath);
        try
        {
            XRPrefabSource? prefab = _assets.LoadedAssetsByPathInternal.TryGetValue(
                normalizedPath,
                out XRAsset? loadedAsset)
                    ? loadedAsset as XRPrefabSource
                    : null;
            if (prefab is null)
            {
                using IDisposable suppression = XRObjectBase.SuppressObjectCacheRegistration();
                prefab = AssetManager.DeserializeAssetFile(normalizedPath, typeof(XRPrefabSource)) as XRPrefabSource;
            }

            if (prefab is null)
                return false;

            snapshot = ModelCookOverrideSnapshotBuilder.Build(prefab, modelDefaults);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.LogWarning(
                $"[ModelCache] Could not read authored cook overrides from '{normalizedPath}'. " +
                $"Cache lookup is disabled for this import. {exception.Message}");
            return false;
        }
    }

    public void EnsureSourceBackendRegistered(string sourcePath)
    {
        if (string.Equals(Path.GetExtension(sourcePath), ".prefab", StringComparison.OrdinalIgnoreCase))
            UnityModelImportProducerAdapter.EnsureRegistered();
    }
}
