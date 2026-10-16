using XREngine.Components.Scene.Mesh;
using XREngine.Fbx;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene.Prefabs;
using XREngine.Scene.Transforms;

namespace XREngine.Scene.Importers;

internal static partial class SerializedSceneImporter
{
    private static void AddConnectedFbxMaterialRemapAliases(
        string modelPath,
        Dictionary<string, XRMaterial> externalMaterials,
        ImportState state)
    {
        if (externalMaterials.Count == 0)
            return;

        FbxSemanticDocument semantic = state.Context.FbxSourceDocuments.GetSemantic(modelPath);
        HashSet<string> connectedNames = new(StringComparer.Ordinal);
        foreach (FbxConnection connection in semantic.Connections)
        {
            if (connection.Kind != FbxConnectionKind.ObjectToObject ||
                connection.Source.Id is not long materialId ||
                connection.Destination.Id is not long modelId ||
                !semantic.TryGetObject(materialId, out FbxSceneObject material) ||
                !semantic.TryGetObject(modelId, out FbxSceneObject model) ||
                material.Category != FbxObjectCategory.Material ||
                model.Category != FbxObjectCategory.Model)
                continue;

            connectedNames.Add(material.DisplayName);
        }

        Dictionary<string, string> sourceByAlias = new(StringComparer.Ordinal);
        foreach (string sourceName in connectedNames)
        {
            if (externalMaterials.ContainsKey(sourceName) || !sourceName.Contains('/'))
                continue;

            string alias = sourceName.Replace('/', '_');
            if (!externalMaterials.ContainsKey(alias) || connectedNames.Contains(alias))
                continue;

            if (sourceByAlias.TryGetValue(alias, out string? competingName))
            {
                throw new SourceVisualImportException(
                    $"FBX material remap '{alias}' matches both connected source materials " +
                    $"'{competingName}' and '{sourceName}' after slash normalization.");
            }

            sourceByAlias.Add(alias, sourceName);
        }

        foreach ((string alias, string sourceName) in sourceByAlias)
        {
            externalMaterials.Add(sourceName, externalMaterials[alias]);
            state.Context.AddDiagnostic(
                "UNITYMODEL0010",
                SourceImportDiagnosticSeverity.Info,
                SourceImportDiagnosticCategory.ModelIdentity,
                $"FBX material '{sourceName}' uses Unity material remap key '{alias}' after slash normalization.",
                modelPath);
        }
    }

    private static void ResolveFbxMaterialSlots(SceneNode root, string modelPath, ImportState state)
    {
        List<(SceneNode Node, SubMesh SubMesh)> imported = [];
        CollectImportedMaterialSlots(root, imported);
        if (imported.Count == 0)
            return;

        (FbxSemanticDocument semantic, FbxGeometryDocument geometry) =
            state.Context.FbxSourceDocuments.GetGeometrySnapshot(modelPath);
        Dictionary<string, Dictionary<string, int>> slotsByModelName = new(StringComparer.Ordinal);
        Dictionary<string, long> modelIdsByName = new(StringComparer.Ordinal);
        Dictionary<long, List<long>> geometryIdsByModelId = [];

        foreach (FbxConnection connection in semantic.Connections)
        {
            if (connection.Kind != FbxConnectionKind.ObjectToObject ||
                connection.Source.Id is not long sourceId ||
                connection.Destination.Id is not long modelId ||
                !semantic.TryGetObject(sourceId, out FbxSceneObject source) ||
                !semantic.TryGetObject(modelId, out FbxSceneObject model) ||
                model.Category != FbxObjectCategory.Model)
                continue;

            if (source.Category is not (FbxObjectCategory.Geometry or FbxObjectCategory.Material))
                continue;

            if (modelIdsByName.TryGetValue(model.DisplayName, out long previousId) && previousId != modelId)
                throw new SourceVisualImportException($"FBX model name '{model.DisplayName}' is not unique for material-slot mapping.");
            modelIdsByName[model.DisplayName] = modelId;

            if (source.Category == FbxObjectCategory.Geometry && geometry.TryGetMeshGeometry(sourceId, out _))
            {
                if (!geometryIdsByModelId.TryGetValue(modelId, out List<long>? geometryIds))
                    geometryIdsByModelId[modelId] = geometryIds = [];
                geometryIds.Add(sourceId);
            }

            if (source.Category != FbxObjectCategory.Material)
                continue;
            if (!slotsByModelName.TryGetValue(model.DisplayName, out Dictionary<string, int>? slots))
                slotsByModelName[model.DisplayName] = slots = new(StringComparer.Ordinal);
            if (!slots.TryAdd(source.DisplayName, slots.Count))
                throw new SourceVisualImportException(
                    $"FBX model '{model.DisplayName}' connects material name '{source.DisplayName}' more than once.");
        }

        Dictionary<long, List<int>> rendererSlotsByModelId = [];
        foreach ((SceneNode node, SubMesh subMesh) in imported)
        {
            string modelName = node.Name ?? string.Empty;
            string materialName = subMesh.ImportedSourceMaterialName ?? string.Empty;
            if (!modelIdsByName.TryGetValue(modelName, out long modelId))
                throw new SourceVisualImportException($"Imported renderer '{modelName}' has no unique FBX source model.");

            slotsByModelName.TryGetValue(modelName, out Dictionary<string, int>? slots);
            slots ??= new Dictionary<string, int>(StringComparer.Ordinal);

            int sourceSlot;
            if (subMesh.ImportedSourceMaterialSlot is int assignedSlot)
            {
                sourceSlot = assignedSlot;
                if (slots.Count > 0 && materialName.Length > 0 &&
                    (!slots.TryGetValue(materialName, out int namedSlot) || namedSlot != sourceSlot))
                    throw new SourceVisualImportException(
                        $"Imported material '{materialName}' on renderer '{modelName}' conflicts with FBX source slot {sourceSlot}.");
            }
            else if (slots.Count == 0)
            {
                sourceSlot = 0;
            }
            else if (!slots.TryGetValue(materialName, out sourceSlot))
            {
                throw new SourceVisualImportException(
                    $"Assimp material '{materialName}' on model '{modelName}' has no unique FBX source material slot.");
            }

            if (!rendererSlotsByModelId.TryGetValue(modelId, out List<int>? rendererSlots))
            {
                if (!geometryIdsByModelId.TryGetValue(modelId, out List<long>? geometryIds) || geometryIds.Count != 1 ||
                    !geometry.TryGetMeshGeometry(geometryIds[0], out FbxMeshGeometry mesh))
                    throw new SourceVisualImportException(
                        $"FBX renderer '{modelName}' needs one unambiguous mesh geometry for material-slot order.");
                rendererSlots = ReadRendererMaterialOrder(mesh, Math.Max(1, slots.Count), modelName);
                rendererSlotsByModelId.Add(modelId, rendererSlots);
            }

            int rendererSlot = rendererSlots.IndexOf(sourceSlot);
            if (rendererSlot < 0)
                throw new SourceVisualImportException(
                    $"Imported material '{materialName}' on renderer '{modelName}' uses FBX source slot {sourceSlot}, " +
                    "but no source polygon uses that slot.");
            if (subMesh.ImportedRendererMaterialSlot is int assignedRendererSlot && assignedRendererSlot != rendererSlot)
                throw new SourceVisualImportException(
                    $"Imported renderer '{modelName}' has conflicting Unity material slot {assignedRendererSlot}; expected {rendererSlot}.");

            subMesh.ImportedSourceMaterialSlot = sourceSlot;
            subMesh.ImportedRendererMaterialSlot = rendererSlot;
        }
    }

    private static List<int> ReadRendererMaterialOrder(FbxMeshGeometry mesh, int materialCount, string modelName)
    {
        FbxLayerElement<int>? layer = mesh.Materials;
        if (layer is not null && layer.MappingType is not (FbxLayerElementMappingType.ByPolygon or FbxLayerElementMappingType.AllSame))
            throw new SourceVisualImportException($"FBX renderer '{modelName}' has unsupported material mapping '{layer.MappingType}'.");
        if (layer is not null && layer.ReferenceType is not (FbxLayerElementReferenceType.Direct or
            FbxLayerElementReferenceType.Index or FbxLayerElementReferenceType.IndexToDirect))
            throw new SourceVisualImportException($"FBX renderer '{modelName}' has unsupported material reference '{layer.ReferenceType}'.");

        int polygonCount = 0;
        foreach (int index in mesh.PolygonVertexIndices)
        {
            if (index < 0)
                polygonCount++;
        }

        List<int> order = [];
        for (int polygonIndex = 0; polygonIndex < polygonCount; polygonIndex++)
        {
            int lookupIndex = layer?.MappingType == FbxLayerElementMappingType.ByPolygon ? polygonIndex : 0;
            int directIndex = lookupIndex;
            if (layer?.ReferenceType is FbxLayerElementReferenceType.Index or FbxLayerElementReferenceType.IndexToDirect &&
                layer.Indices.Count > 0)
            {
                if (lookupIndex >= layer.Indices.Count)
                    throw new SourceVisualImportException($"FBX renderer '{modelName}' has incomplete material index data.");
                directIndex = layer.Indices[lookupIndex];
            }

            int sourceSlot = 0;
            if (layer is not null)
            {
                if (directIndex < 0 || directIndex >= layer.DirectValues.Count)
                    throw new SourceVisualImportException($"FBX renderer '{modelName}' has incomplete material values.");
                sourceSlot = layer.DirectValues[directIndex];
            }

            if (sourceSlot < 0 || sourceSlot >= materialCount)
                throw new SourceVisualImportException(
                    $"FBX renderer '{modelName}' uses material slot {sourceSlot} outside its {materialCount} connections.");
            if (!order.Contains(sourceSlot))
                order.Add(sourceSlot);
        }

        return order;
    }

    private static void CollectImportedMaterialSlots(
        SceneNode node,
        List<(SceneNode Node, SubMesh SubMesh)> imported)
    {
        foreach (ModelComponent component in node.GetComponents<ModelComponent>())
        {
            if (component.Model is null)
                continue;
            foreach (SubMesh subMesh in component.Model.Meshes)
                imported.Add((node, subMesh));
        }

        foreach (TransformBase childTransform in node.Transform.Children)
        {
            if (childTransform.SceneNode is SceneNode child)
                CollectImportedMaterialSlots(child, imported);
        }
    }
}
