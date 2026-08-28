using XREngine.Gltf;
using XREngine.Rendering.Models.Caching;

namespace XREngine;

/// <summary>
/// Builds the normalized producer metadata while the native glTF document is still open.
/// </summary>
internal static class NativeGltfImportReportBuilder
{
    public static ModelImportProducerMetadata Build(string sourceFilePath, GltfRoot document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);
        ArgumentNullException.ThrowIfNull(document);

        List<ModelImportDependency> dependencies =
        [
            ModelImportDependency.FromFile(
                sourceFilePath,
                ModelImportDependencyKind.EntrySource,
                isRequired: true,
                producerKey: "gltf:entry"),
        ];
        List<ModelImportSourceEntity> sourceEntities = [];
        List<ModelImportReferenceKey> referenceKeys = [];

        for (int bufferIndex = 0; bufferIndex < document.Buffers.Count; bufferIndex++)
        {
            string? dependencyPath = ModelImportPathNormalizer.ResolveLocalReference(
                sourceFilePath,
                document.Buffers[bufferIndex].Uri);
            if (dependencyPath is null)
                continue;

            dependencies.Add(ModelImportDependency.FromFile(
                dependencyPath,
                ModelImportDependencyKind.Structural,
                isRequired: true,
                producerKey: $"gltf:buffer:{bufferIndex}"));
        }

        for (int imageIndex = 0; imageIndex < document.Images.Count; imageIndex++)
        {
            GltfImage image = document.Images[imageIndex];
            string? dependencyPath = ModelImportPathNormalizer.ResolveLocalReference(sourceFilePath, image.Uri);
            if (dependencyPath is not null)
            {
                dependencies.Add(ModelImportDependency.FromFile(
                    dependencyPath,
                    ModelImportDependencyKind.ReferencedTexture,
                    isRequired: false,
                    producerKey: $"gltf:image:{imageIndex}"));
            }
        }

        for (int nodeIndex = 0; nodeIndex < document.Nodes.Count; nodeIndex++)
        {
            sourceEntities.Add(new ModelImportSourceEntity(
                $"gltf:node:{nodeIndex}",
                ModelImportEntityKind.Node,
                document.Nodes[nodeIndex].Name,
                isStable: false));
        }

        for (int meshIndex = 0; meshIndex < document.Meshes.Count; meshIndex++)
        {
            GltfMesh mesh = document.Meshes[meshIndex];
            sourceEntities.Add(new ModelImportSourceEntity(
                $"gltf:mesh:{meshIndex}",
                ModelImportEntityKind.Mesh,
                mesh.Name,
                isStable: false));

            IReadOnlyList<string> morphTargetNames = GltfImportKeyUtilities.GetMorphTargetNames(mesh);
            int morphTargetCount = mesh.Primitives.Count == 0
                ? 0
                : mesh.Primitives.Max(static primitive => primitive.Targets.Count);
            for (int morphIndex = 0; morphIndex < morphTargetCount; morphIndex++)
            {
                string? diagnosticName = morphIndex < morphTargetNames.Count
                    ? morphTargetNames[morphIndex]
                    : null;
                sourceEntities.Add(new ModelImportSourceEntity(
                    $"gltf:mesh:{meshIndex}:morph:{morphIndex}",
                    ModelImportEntityKind.MorphTarget,
                    diagnosticName,
                    isStable: false));
            }
        }

        IReadOnlyList<string> materialKeys = GltfImportKeyUtilities.GetMaterialKeys(document);
        for (int materialIndex = 0; materialIndex < document.Materials.Count; materialIndex++)
        {
            string key = materialKeys[materialIndex];
            sourceEntities.Add(new ModelImportSourceEntity(
                $"gltf:material:{materialIndex}",
                ModelImportEntityKind.Material,
                key,
                isStable: false));
            referenceKeys.Add(new ModelImportReferenceKey(ModelImportReferenceKind.Material, key));
        }

        IReadOnlyList<string> textureKeys = GltfImportKeyUtilities.GetTextureKeys(document);
        for (int textureIndex = 0; textureIndex < document.Textures.Count; textureIndex++)
        {
            sourceEntities.Add(new ModelImportSourceEntity(
                $"gltf:texture:{textureIndex}",
                ModelImportEntityKind.Texture,
                textureKeys[textureIndex],
                isStable: false));
        }

        foreach (string textureKey in GltfImportKeyUtilities.EnumerateReferencedTextureKeys(document))
            referenceKeys.Add(new ModelImportReferenceKey(ModelImportReferenceKind.Texture, textureKey));

        for (int animationIndex = 0; animationIndex < document.Animations.Count; animationIndex++)
        {
            string animationKey = string.IsNullOrWhiteSpace(document.Animations[animationIndex].Name)
                ? $"Animation_{animationIndex}"
                : document.Animations[animationIndex].Name!;
            sourceEntities.Add(new ModelImportSourceEntity(
                $"gltf:animation:{animationIndex}",
                ModelImportEntityKind.Animation,
                animationKey,
                isStable: false));
            referenceKeys.Add(new ModelImportReferenceKey(ModelImportReferenceKind.Animation, animationKey));
        }

        for (int skinIndex = 0; skinIndex < document.Skins.Count; skinIndex++)
        {
            sourceEntities.Add(new ModelImportSourceEntity(
                $"gltf:skin:{skinIndex}",
                ModelImportEntityKind.Skeleton,
                document.Skins[skinIndex].Name,
                isStable: false));
        }

        return new ModelImportProducerMetadata(dependencies, sourceEntities, referenceKeys);
    }
}
