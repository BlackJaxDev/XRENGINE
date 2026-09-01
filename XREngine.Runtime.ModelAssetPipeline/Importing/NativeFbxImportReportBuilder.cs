using XREngine.Fbx;
using XREngine.Rendering.Models.Caching;

namespace XREngine;

/// <summary>
/// Builds normalized producer metadata from native FBX semantic objects and their stable IDs.
/// </summary>
internal static class NativeFbxImportReportBuilder
{
    public static ModelImportProducerMetadata Build(
        string sourceFilePath,
        FbxSemanticDocument semantic,
        float scaleConversion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentNullException.ThrowIfNull(semantic);

        List<ModelImportDependency> dependencies =
        [
            ModelImportDependency.FromFile(
                sourceFilePath,
                ModelImportDependencyKind.EntrySource,
                isRequired: true,
                producerKey: "fbx:entry"),
        ];
        List<ModelImportSourceEntity> sourceEntities = [];
        List<ModelImportReferenceKey> referenceKeys = [];

        foreach (FbxSceneObject sceneObject in semantic.Objects.OrderBy(static value => value.NodeIndex))
        {
            ModelImportEntityKind kind = MapEntityKind(sceneObject.Category);
            sourceEntities.Add(new ModelImportSourceEntity(
                $"fbx:{sceneObject.Category.ToString().ToLowerInvariant()}:{sceneObject.Id}",
                kind,
                sceneObject.DisplayName,
                isStable: true));
        }

        foreach (FbxIntermediateMaterial material in semantic.IntermediateScene.Materials
            .OrderBy(static value => value.ObjectIndex))
        {
            string key = semantic.TryGetObject(material.ObjectId, out FbxSceneObject sceneMaterial)
                ? sceneMaterial.DisplayName
                : material.Name;
            if (!string.IsNullOrWhiteSpace(key))
                referenceKeys.Add(new ModelImportReferenceKey(ModelImportReferenceKind.Material, key));
        }

        foreach (FbxIntermediateTexture texture in semantic.IntermediateScene.Textures
            .OrderBy(static value => value.ObjectIndex))
        {
            if (!semantic.TryGetObject(texture.ObjectId, out FbxSceneObject textureObject))
                continue;

            string? rawPath = NativeFbxSceneImporter.ResolveTextureFilePath(semantic, textureObject);
            if (string.IsNullOrWhiteSpace(rawPath))
                continue;

            referenceKeys.Add(new ModelImportReferenceKey(ModelImportReferenceKind.Texture, rawPath));
            string? dependencyPath = ModelImportPathNormalizer.ResolveLocalReference(sourceFilePath, rawPath);
            if (dependencyPath is not null)
            {
                dependencies.Add(ModelImportDependency.FromFile(
                    dependencyPath,
                    ModelImportDependencyKind.ReferencedTexture,
                    isRequired: false,
                    producerKey: $"fbx:texture:{texture.ObjectId}"));
            }
        }

        foreach (FbxIntermediateAnimationStack stack in semantic.IntermediateScene.AnimationStacks
            .OrderBy(static value => value.ObjectIndex))
        {
            string key = string.IsNullOrWhiteSpace(stack.Name)
                ? $"AnimationStack_{stack.ObjectId}"
                : stack.Name;
            referenceKeys.Add(new ModelImportReferenceKey(ModelImportReferenceKind.Animation, key));
        }

        float? modelUnitsPerMeter = semantic.GlobalSettings is FbxGlobalSettings globalSettings
            && FbxModelUnitScale.TryResolveModelUnitsPerMeter(
                globalSettings.AxisSystem,
                scaleConversion,
                out float resolvedModelUnitsPerMeter)
                    ? resolvedModelUnitsPerMeter
                    : null;
        return new ModelImportProducerMetadata(
            dependencies,
            sourceEntities,
            referenceKeys,
            modelUnitsPerMeter: modelUnitsPerMeter);
    }

    private static ModelImportEntityKind MapEntityKind(FbxObjectCategory category)
        => category switch
        {
            FbxObjectCategory.Model or FbxObjectCategory.NodeAttribute => ModelImportEntityKind.Node,
            FbxObjectCategory.Geometry => ModelImportEntityKind.Mesh,
            FbxObjectCategory.Material => ModelImportEntityKind.Material,
            FbxObjectCategory.Texture or FbxObjectCategory.Video => ModelImportEntityKind.Texture,
            FbxObjectCategory.AnimationCurve
                or FbxObjectCategory.AnimationCurveNode
                or FbxObjectCategory.AnimationLayer
                or FbxObjectCategory.AnimationStack => ModelImportEntityKind.Animation,
            FbxObjectCategory.Deformer or FbxObjectCategory.Pose => ModelImportEntityKind.Skeleton,
            _ => ModelImportEntityKind.Other,
        };
}
