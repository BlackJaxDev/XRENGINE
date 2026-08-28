using XREngine.Core.Files;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Caching;
using XREngine.Scene;
using XREngine.Scene.Importers;
using XREngine.Scene.Prefabs;

namespace XREngine.Editor.Importers.SerializedAssets;

/// <summary>
/// Editor-owned loader for Unity prefab source files. It preserves the generic
/// asset-import context while keeping Unity authoring evidence out of Core assets.
/// </summary>
internal sealed class EditorSerializedPrefabAssetLoadingServices(
    IRuntimeThirdPartyAssetLoadingServices fallback) : IRuntimeThirdPartyAssetLoadingServices
{
    private const string PrefabExtension = "prefab";
    private const string SceneExtension = "unity";
    private readonly IRuntimeThirdPartyAssetLoadingServices _fallback =
        fallback ?? throw new ArgumentNullException(nameof(fallback));

    public XRAsset? Load(
        string filePath,
        string extension,
        Type assetType,
        object? importOptions = null,
        AssetImportContext? importContext = null,
        XRAsset? targetAsset = null)
    {
        string normalizedExtension = extension.TrimStart('.');
        if (string.Equals(normalizedExtension, SceneExtension, StringComparison.OrdinalIgnoreCase)
            && assetType == typeof(XRScene))
        {
            AssetImportContext sceneContext = importContext ?? new AssetImportContext(filePath, cacheDirectory: null);
            sceneContext.CancellationToken.ThrowIfCancellationRequested();
            XRScene scene = targetAsset switch
            {
                null => new XRScene(),
                XRScene supplied => supplied,
                _ => throw new ArgumentException(
                    $"Target asset '{targetAsset.GetType().FullName}' is not a scene.",
                    nameof(targetAsset)),
            };
            if (!scene.Load3rdParty(filePath))
                return null;

            scene.OriginalPath = filePath;
            scene.FilePath ??= sceneContext.DestinationAssetPath;
            return scene;
        }

        if (!string.Equals(normalizedExtension, PrefabExtension, StringComparison.OrdinalIgnoreCase)
            || assetType != typeof(XRPrefabSource))
        {
            return _fallback.Load(filePath, extension, assetType, importOptions, importContext, targetAsset);
        }

        AssetImportContext context = importContext ?? new AssetImportContext(filePath, cacheDirectory: null);
        context.CancellationToken.ThrowIfCancellationRequested();
        XRPrefabSource prefab = targetAsset switch
        {
            null => new XRPrefabSource(),
            XRPrefabSource supplied => supplied,
            _ => throw new ArgumentException(
                $"Target asset '{targetAsset.GetType().FullName}' is not a prefab source.",
                nameof(targetAsset)),
        };
        ModelImportOptions options = importOptions as ModelImportOptions ?? new ModelImportOptions();
        string? destinationPath = context.DestinationAssetPath ?? prefab.FilePath;
        SerializedPrefabConversionResult conversion = SerializedSceneImporter.ImportPrefabWithManifest(
            filePath,
            destinationPath,
            options.SourceProjectRootOverride,
            context.CancellationToken,
            progress: (progress, _) => options.ProgressCallback?.Invoke(progress));
        context.CancellationToken.ThrowIfCancellationRequested();

        if (conversion.RootNode is null)
            return null;
        if (!conversion.MeshletCookingCompleted)
        {
            throw new InvalidDataException(
                $"Unity prefab conversion for '{filePath}' returned an uncooked hierarchy.");
        }

        prefab.RootNode = conversion.RootNode;
        prefab.Name ??= Path.GetFileNameWithoutExtension(filePath);
        prefab.OriginalPath = filePath;
        prefab.FilePath ??= destinationPath;
        SerializedPrefabImportManifestStore.Set(prefab, conversion.Manifest);
        ModelPrefabImportMetadata.SetProducerReport(
            prefab,
            SerializedModelImportProducerAdapter.CreateReport(filePath, options, conversion.Manifest));
        return prefab;
    }
}
