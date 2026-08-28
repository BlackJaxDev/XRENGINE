using System.Numerics;
using Assimp;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Files;
using XREngine.Data.Rendering;
using XREngine.Fbx;
using XREngine.Rendering;
using XREngine.Rendering.Models.Caching;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Prefabs;

namespace XREngine.Rendering.Models;

/// <summary>Imports ordinary model sources into runtime-neutral prefab assets.</summary>
internal sealed class ModelPrefabAssetLoadingServices(
    AssetManager assets,
    IRuntimeThirdPartyAssetLoadingServices fallback) : IRuntimeThirdPartyAssetLoadingServices
{
    private readonly AssetManager _assets = assets ?? throw new ArgumentNullException(nameof(assets));
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
        if (assetType != typeof(XRPrefabSource)
            || !ModelPrefabSourceExtensions.All.Contains(extension, StringComparer.OrdinalIgnoreCase))
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
        Dictionary<string, XRTexture2D?> textureRemap = options.TextureRemap ??= [];
        Dictionary<string, XRMaterial?> materialRemap = options.MaterialRemap ??= [];
        IReadOnlyDictionary<string, string> legacyPathRemap =
            options.LegacyTexturePathRemapValues ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> legacyMaterialRemap =
            options.LegacyMaterialNameRemapValues ?? new Dictionary<string, string>();

        void TrackTextureKey(string path) => textureRemap.TryAdd(path, null);
        void TrackMaterialKey(string name) => materialRemap.TryAdd(name, null);

        using ModelAssetImporter importer = new(filePath, onCompleted: null, materialFactory: null)
        {
            ImportOptions = options,
        };
        Func<string, XRTexture2D> defaultMakeTexture = importer.MakeTextureAction;

        XRTexture2D GetOrCreateTexture(string path)
        {
            TrackTextureKey(path);
            if (textureRemap.TryGetValue(path, out XRTexture2D? replacement) && replacement is not null)
                return replacement;
            if (legacyPathRemap.TryGetValue(path, out string? remapped) && !string.IsNullOrWhiteSpace(remapped))
                path = remapped;
            return defaultMakeTexture(path);
        }

        XRMaterial GetOrCreateMaterial(XRTexture[] textures, List<TextureSlot> slots, string name)
        {
            TrackMaterialKey(name);
            if (materialRemap.TryGetValue(name, out XRMaterial? replacement) && replacement is not null)
                return replacement;
            if (legacyMaterialRemap.TryGetValue(name, out string? replacementPath)
                && !string.IsNullOrWhiteSpace(replacementPath)
                && File.Exists(replacementPath)
                && _assets.Load<XRMaterial>(replacementPath) is XRMaterial loaded)
            {
                return loaded;
            }
            return ModelAssetImporter.MakeMaterialDeferred(textures, slots, name);
        }

        importer.MakeTextureAction = GetOrCreateTexture;
        importer.MakeMaterialAction = GetOrCreateMaterial;

        using IDisposable synchronousMeshImport = SynchronousModelMeshImportScope.Enter(options);
        using IDisposable apiSuppression = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable meshPublicationSuppression = ModelComponent.EnterRuntimeMeshBuildSuppressionScope();
        SceneNode? root = importer.Import(
            options.ImportSteps,
            preservePivots: options.FbxPivotPolicy == FbxPivotImportPolicy.PreservePivotSemantics,
            removeAssimpFBXNodes: options.CollapseGeneratedFbxHelperNodes,
            scaleConversion: options.ScaleConversion,
            zUp: options.ZUp,
            multiThread: options.MultiThread,
            processMeshesAsynchronously: false,
            batchSubmeshAddsDuringAsyncImport: options.BatchSubmeshAddsDuringAsyncImport,
            onProgress: progress =>
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                options.ProgressCallback?.Invoke(progress);
            });

        if (root is null)
            return null;

        ModelImportProducerReport? report = importer.LastProducerReport;
        if (report is not null)
        {
            foreach (ModelImportReferenceKey reference in report.ReferenceKeys)
            {
                if (reference.Kind == ModelImportReferenceKind.Texture)
                    TrackTextureKey(reference.Key);
                else if (reference.Kind == ModelImportReferenceKind.Material)
                    TrackMaterialKey(reference.Key);
            }
        }

        prefab.RootNode = root;
        prefab.Name ??= Path.GetFileNameWithoutExtension(filePath);
        prefab.OriginalPath = filePath;
        prefab.FilePath ??= context.DestinationAssetPath;
        ModelPrefabImportMetadata.SetProducerReport(prefab, report);
        return prefab;
    }
}
