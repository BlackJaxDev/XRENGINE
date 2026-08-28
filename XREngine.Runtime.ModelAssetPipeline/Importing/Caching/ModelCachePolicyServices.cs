using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene.Prefabs;

namespace XREngine.Rendering.Models.Caching;

/// <summary>
/// Adapts runtime asset state to ModelAssetPipeline cache policy.
/// </summary>
internal sealed class ModelCachePolicyServices(AssetManager assets) : IModelCachePolicyServices
{
    private readonly AssetManager _assets = assets ?? throw new ArgumentNullException(nameof(assets));

    public ModelImportOptions GetImportOptions(
        string sourcePath,
        Type assetType,
        object? suppliedOptions)
        => suppliedOptions as ModelImportOptions ?? new ModelImportOptions();

    public bool TryBuildCookOverrideSnapshot(
        string sourcePath,
        ModelCookSettings modelDefaults,
        out ModelCookOverrideSnapshot snapshot)
    {
        snapshot = ModelCookOverrideSnapshot.Empty;
        if (!TryResolveGeneratedAssetPath(sourcePath, out string generatedAssetPath)
            || !File.Exists(generatedAssetPath))
        {
            return true;
        }

        string normalizedPath = Path.GetFullPath(generatedAssetPath);
        try
        {
            XRPrefabSource? prefab = _assets.TryGetAssetByPath(normalizedPath, out XRAsset? loadedAsset)
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

    private bool TryResolveGeneratedAssetPath(string sourcePath, out string generatedAssetPath)
    {
        generatedAssetPath = string.Empty;
        if (string.IsNullOrWhiteSpace(sourcePath)
            || string.IsNullOrWhiteSpace(_assets.GameAssetsPath))
        {
            return false;
        }

        string assetsRoot = Path.GetFullPath(_assets.GameAssetsPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalizedSource = Path.GetFullPath(sourcePath);
        if (!normalizedSource.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
            return false;

        string? directory = Path.GetDirectoryName(normalizedSource);
        string fileName = Path.GetFileNameWithoutExtension(normalizedSource);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            return false;

        generatedAssetPath = Path.Combine(directory, $"{fileName}.{AssetManager.AssetExtension}");
        return true;
    }
}
