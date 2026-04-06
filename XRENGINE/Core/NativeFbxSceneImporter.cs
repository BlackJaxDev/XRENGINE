using Assimp;
using System.Numerics;
using XREngine.Components.Scene.Mesh;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Fbx;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace XREngine;

internal static class NativeFbxSceneImporter
{
    internal readonly record struct ImportResult(
        SceneNode RootNode,
        IReadOnlyList<XRMaterial> Materials,
        IReadOnlyList<XRMesh> Meshes);

    private sealed class MeshChunkBuilder(int materialSlot)
    {
        public int MaterialSlot { get; } = materialSlot;
        public List<Vertex> Vertices { get; } = [];
        public List<ushort> Indices { get; } = [];

        public bool CanAppendPolygon(int polygonVertexCount)
            => Vertices.Count + polygonVertexCount <= ushort.MaxValue + 1;

        public void AppendPolygon(IReadOnlyList<Vertex> polygonVertices)
        {
            if (polygonVertices.Count < 3)
                return;

            int baseVertex = Vertices.Count;
            Vertices.AddRange(polygonVertices);
            for (int triangleIndex = 1; triangleIndex < polygonVertices.Count - 1; triangleIndex++)
            {
                Indices.Add((ushort)baseVertex);
                Indices.Add((ushort)(baseVertex + triangleIndex));
                Indices.Add((ushort)(baseVertex + triangleIndex + 1));
            }
        }
    }

    public static ImportResult Import(
        ModelImporter importer,
        string sourceFilePath,
        ModelImportOptions? importOptions,
        float scaleConversion,
        bool zUp,
        int importLayer,
        CancellationToken cancellationToken,
        Action<float>? onProgress,
        Matrix4x4? rootTransformMatrix)
    {
        ArgumentNullException.ThrowIfNull(importer);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFilePath);

        using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(sourceFilePath);
        FbxSceneSemanticsPolicy policy = new(
            (importOptions?.PreservePivots ?? true)
                ? FbxPivotImportPolicy.PreservePivotSemantics
                : FbxPivotImportPolicy.BakeIntoLocalTransform,
            FbxModelHierarchyPolicy.PreserveAuthoredNodes);

        FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural, policy);
        FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);

        SceneNode rootNode = new(Path.GetFileNameWithoutExtension(sourceFilePath)) { Layer = importLayer };
        ApplyLocalMatrix(rootNode, CreateImportRootMatrix(scaleConversion, zUp, rootTransformMatrix));

        Dictionary<long, SceneNode> nodesByObjectId = BuildSceneNodes(rootNode, semantic, importLayer, cancellationToken);
        List<XRMaterial> createdMaterials = [];
        List<XRMesh> createdMeshes = [];
        Dictionary<long, XRMaterial> materialCache = new();
        XRMaterial? defaultMaterial = null;

        List<FbxIntermediateMesh> meshes = semantic.IntermediateScene.Meshes.OrderBy(static mesh => mesh.ObjectIndex).ToList();
        for (int meshIndex = 0; meshIndex < meshes.Count; meshIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            FbxIntermediateMesh mesh = meshes[meshIndex];
            if (!geometry.TryGetMeshGeometry(mesh.ObjectId, out FbxMeshGeometry meshGeometry))
                continue;

            foreach (long modelObjectId in mesh.ModelObjectIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!nodesByObjectId.TryGetValue(modelObjectId, out SceneNode? sceneNode))
                    continue;
                if (!semantic.TryGetObject(modelObjectId, out FbxSceneObject nodeObject))
                    continue;

                List<SubMesh> subMeshes = BuildSubMeshesForNode(
                    importer,
                    sourceFilePath,
                    semantic,
                    mesh,
                    meshGeometry,
                    nodeObject,
                    createdMaterials,
                    createdMeshes,
                    materialCache,
                    ref defaultMaterial,
                    cancellationToken,
                    rootNode.Transform);

                if (subMeshes.Count == 0)
                    continue;

                AttachSubMeshesToNode(sceneNode, subMeshes, mesh.Name, importOptions);
            }

            onProgress?.Invoke((meshIndex + 1) / (float)meshes.Count);
        }

        return new ImportResult(rootNode, createdMaterials, createdMeshes);
    }

    private static Dictionary<long, SceneNode> BuildSceneNodes(SceneNode rootNode, FbxSemanticDocument semantic, int importLayer, CancellationToken cancellationToken)
    {
        Dictionary<long, SceneNode> nodesByObjectId = new(semantic.IntermediateScene.Nodes.Count);
        foreach (FbxIntermediateNode node in semantic.IntermediateScene.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            SceneNode parent = node.ParentNodeIndex is int parentIndex
                ? nodesByObjectId[semantic.IntermediateScene.Nodes[parentIndex].ObjectId]
                : rootNode;

            SceneNode sceneNode = new(parent, node.Name) { Layer = importLayer };
            ApplyLocalMatrix(sceneNode, node.LocalTransform);
            nodesByObjectId[node.ObjectId] = sceneNode;
        }

        return nodesByObjectId;
    }

    private static void AttachSubMeshesToNode(SceneNode sceneNode, IReadOnlyList<SubMesh> subMeshes, string fallbackName, ModelImportOptions? importOptions)
    {
        bool splitSubmeshesIntoSeparateModelComponents = importOptions?.SplitSubmeshesIntoSeparateModelComponents ?? false;
        if (splitSubmeshesIntoSeparateModelComponents)
        {
            for (int index = 0; index < subMeshes.Count; index++)
            {
                SubMesh subMesh = subMeshes[index];
                ModelComponent component = sceneNode.AddComponent<ModelComponent>()!;
                component.Name = string.IsNullOrWhiteSpace(subMesh.Name)
                    ? $"{fallbackName} SubMesh {index}"
                    : subMesh.Name;
                component.Model = new Model(subMesh);
                component.Model.Meshes.ThreadSafe = true;
            }

            return;
        }

        ModelComponent sharedComponent = sceneNode.AddComponent<ModelComponent>()!;
        sharedComponent.Name = string.IsNullOrWhiteSpace(sceneNode.Name) ? fallbackName : sceneNode.Name;
        sharedComponent.Model = new Model(subMeshes);
        sharedComponent.Model.Meshes.ThreadSafe = true;
    }

    private static List<SubMesh> BuildSubMeshesForNode(
        ModelImporter importer,
        string sourceFilePath,
        FbxSemanticDocument semantic,
        FbxIntermediateMesh mesh,
        FbxMeshGeometry meshGeometry,
        FbxSceneObject nodeObject,
        List<XRMaterial> createdMaterials,
        List<XRMesh> createdMeshes,
        Dictionary<long, XRMaterial> materialCache,
        ref XRMaterial? defaultMaterial,
        CancellationToken cancellationToken,
        TransformBase rootTransform)
    {
        List<FbxIntermediateMaterial> nodeMaterials = semantic.IntermediateScene.Materials
            .Where(material => material.ModelObjectIds.Contains(nodeObject.Id))
            .OrderBy(static material => material.ObjectIndex)
            .ToList();

        Dictionary<int, List<MeshChunkBuilder>> buildersByMaterialSlot = new();
        FbxIntermediateNode intermediateNode = semantic.IntermediateScene.Nodes.First(node => node.ObjectId == nodeObject.Id);

        int polygonVertexStart = 0;
        int polygonIndex = 0;
        while (polygonVertexStart < meshGeometry.PolygonVertexIndices.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<Vertex> polygonVertices = [];
            int polygonVertexIndex = polygonVertexStart;
            while (polygonVertexIndex < meshGeometry.PolygonVertexIndices.Count)
            {
                int encodedControlPointIndex = meshGeometry.PolygonVertexIndices[polygonVertexIndex];
                bool endOfPolygon = encodedControlPointIndex < 0;
                int controlPointIndex = endOfPolygon ? ~encodedControlPointIndex : encodedControlPointIndex;
                polygonVertices.Add(CreateVertex(meshGeometry, intermediateNode.GeometryTransform, controlPointIndex, polygonVertexIndex, polygonIndex));
                polygonVertexIndex++;
                if (endOfPolygon)
                    break;
            }

            int materialSlot = ResolveMaterialSlot(meshGeometry.Materials, polygonIndex, polygonVertexStart, nodeMaterials.Count);
            if (!buildersByMaterialSlot.TryGetValue(materialSlot, out List<MeshChunkBuilder>? chunks))
            {
                chunks = [];
                buildersByMaterialSlot[materialSlot] = chunks;
            }

            MeshChunkBuilder chunk = chunks.Count > 0 && chunks[^1].CanAppendPolygon(polygonVertices.Count)
                ? chunks[^1]
                : CreateChunk(chunks, materialSlot);
            chunk.AppendPolygon(polygonVertices);

            polygonVertexStart = polygonVertexIndex;
            polygonIndex++;
        }

        List<SubMesh> subMeshes = [];
        foreach ((int materialSlot, List<MeshChunkBuilder> chunks) in buildersByMaterialSlot.OrderBy(static pair => pair.Key))
        {
            XRMaterial material = ResolveMaterial(
                importer,
                sourceFilePath,
                semantic,
                nodeMaterials,
                materialSlot,
                materialCache,
                createdMaterials,
                ref defaultMaterial);

            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                MeshChunkBuilder chunk = chunks[chunkIndex];
                if (chunk.Vertices.Count == 0 || chunk.Indices.Count == 0)
                    continue;

                XRMesh xrMesh = new(chunk.Vertices, chunk.Indices);
                createdMeshes.Add(xrMesh);

                SubMesh subMesh = new(new SubMeshLOD(material, xrMesh, 0.0f)
                {
                    GenerateAsync = importer.ImportOptions?.GenerateMeshRenderersAsync ?? false,
                })
                {
                    Name = chunks.Count == 1 ? mesh.Name : $"{mesh.Name} {chunkIndex}",
                    RootTransform = rootTransform,
                };

                subMeshes.Add(subMesh);
            }
        }

        return subMeshes;

        static MeshChunkBuilder CreateChunk(List<MeshChunkBuilder> chunks, int materialSlot)
        {
            MeshChunkBuilder chunk = new(materialSlot);
            chunks.Add(chunk);
            return chunk;
        }
    }

    private static Vertex CreateVertex(
        FbxMeshGeometry meshGeometry,
        Matrix4x4 geometryTransform,
        int controlPointIndex,
        int polygonVertexIndex,
        int polygonIndex)
    {
        Vector3 position = controlPointIndex >= 0 && controlPointIndex < meshGeometry.ControlPoints.Count
            ? Vector3.Transform(meshGeometry.ControlPoints[controlPointIndex], geometryTransform)
            : Vector3.Zero;

        Vertex vertex = new(position);

        if (TryResolveLayerValue(meshGeometry.Normals.FirstOrDefault(), controlPointIndex, polygonVertexIndex, polygonIndex, out Vector3 normal)
            && normal.LengthSquared() > 0.0f)
            vertex.Normal = Vector3.Normalize(Vector3.TransformNormal(normal, geometryTransform));

        if (TryResolveLayerValue(meshGeometry.Tangents.FirstOrDefault(), controlPointIndex, polygonVertexIndex, polygonIndex, out Vector3 tangent)
            && tangent.LengthSquared() > 0.0f)
            vertex.Tangent = Vector3.Normalize(Vector3.TransformNormal(tangent, geometryTransform));

        if (meshGeometry.TextureCoordinates.Count > 0)
        {
            List<Vector2> textureCoordinateSets = new(meshGeometry.TextureCoordinates.Count);
            foreach (FbxLayerElement<Vector2> layer in meshGeometry.TextureCoordinates)
                textureCoordinateSets.Add(ResolveLayerValue(layer, controlPointIndex, polygonVertexIndex, polygonIndex, Vector2.Zero));
            vertex.TextureCoordinateSets = textureCoordinateSets;
        }

        if (meshGeometry.Colors.Count > 0)
        {
            List<Vector4> colorSets = new(meshGeometry.Colors.Count);
            foreach (FbxLayerElement<Vector4> layer in meshGeometry.Colors)
                colorSets.Add(ResolveLayerValue(layer, controlPointIndex, polygonVertexIndex, polygonIndex, Vector4.One));
            vertex.ColorSets = colorSets;
        }

        return vertex;
    }

    private static int ResolveMaterialSlot(FbxLayerElement<int>? layer, int polygonIndex, int polygonVertexStart, int materialCount)
    {
        if (materialCount <= 0 || layer is null)
            return 0;

        int resolved = ResolveLayerValue(layer, 0, polygonVertexStart, polygonIndex, 0);
        if (resolved < 0)
            return 0;
        if (resolved >= materialCount)
            return materialCount - 1;
        return resolved;
    }

    private static XRMaterial ResolveMaterial(
        ModelImporter importer,
        string sourceFilePath,
        FbxSemanticDocument semantic,
        IReadOnlyList<FbxIntermediateMaterial> nodeMaterials,
        int materialSlot,
        Dictionary<long, XRMaterial> materialCache,
        List<XRMaterial> createdMaterials,
        ref XRMaterial? defaultMaterial)
    {
        if (materialSlot >= 0 && materialSlot < nodeMaterials.Count)
        {
            FbxIntermediateMaterial intermediateMaterial = nodeMaterials[materialSlot];
            if (materialCache.TryGetValue(intermediateMaterial.ObjectId, out XRMaterial? cachedMaterial))
                return cachedMaterial;

            semantic.TryGetObject(intermediateMaterial.ObjectId, out FbxSceneObject sceneMaterial);
            string materialName = sceneMaterial?.DisplayName ?? intermediateMaterial.Name;
            bool transparentBlendHint = ShouldUseTransparentBlend(sceneMaterial);

            List<TextureSlot> textureSlots = BuildTextureSlots(semantic, intermediateMaterial.ObjectId, transparentBlendHint);
            XRMaterial material = importer.MaterialFactory(sourceFilePath, materialName, textureSlots, 0, ShadingMode.Phong, []);
            ApplyMaterialPropertyOverrides(material, sceneMaterial, textureSlots.Count == 0);

            materialCache[intermediateMaterial.ObjectId] = material;
            createdMaterials.Add(material);
            return material;
        }

        defaultMaterial ??= importer.MaterialFactory(sourceFilePath, "DefaultMaterial", [], 0, ShadingMode.Phong, []);
        if (!createdMaterials.Contains(defaultMaterial))
            createdMaterials.Add(defaultMaterial);
        return defaultMaterial;
    }

    private static List<TextureSlot> BuildTextureSlots(FbxSemanticDocument semantic, long materialObjectId, bool transparentBlendHint)
    {
        List<TextureSlot> textureSlots = [];
        foreach (FbxConnection connection in semantic.Connections)
        {
            if (connection.Destination.Id != materialObjectId || connection.Source.Id is not long textureObjectId)
                continue;
            if (!semantic.TryGetObject(textureObjectId, out FbxSceneObject textureObject) || textureObject.Category != FbxObjectCategory.Texture)
                continue;

            string? filePath = ResolveTextureFilePath(semantic, textureObject);
            if (string.IsNullOrWhiteSpace(filePath))
                continue;

            TextureType textureType = MapTextureType(connection.PropertyName, textureObject, filePath);
            int flags = transparentBlendHint && textureType is TextureType.Diffuse or TextureType.BaseColor ? 0x2 : 0;
            textureSlots.Add(new TextureSlot(filePath, textureType, 0, default, 0, 1.0f, default, default, default, flags));
        }

        return textureSlots;
    }

    private static string? ResolveTextureFilePath(FbxSemanticDocument semantic, FbxSceneObject textureObject)
    {
        string? path = GetInlineAttribute(textureObject, "RelativeFilename")
            ?? GetInlineAttribute(textureObject, "FileName")
            ?? GetInlineAttribute(textureObject, "Filename")
            ?? GetInlineAttribute(textureObject, "Path");
        if (!string.IsNullOrWhiteSpace(path))
            return path;

        if (!semantic.ObjectIndexById.TryGetValue(textureObject.Id, out int textureObjectIndex))
            return null;

        foreach (int inboundConnectionIndex in semantic.GetInboundConnectionIndices(textureObjectIndex))
        {
            FbxConnection connection = semantic.Connections[inboundConnectionIndex];
            if (connection.Source.Id is not long videoObjectId)
                continue;
            if (!semantic.TryGetObject(videoObjectId, out FbxSceneObject videoObject) || videoObject.Category != FbxObjectCategory.Video)
                continue;

            path = GetInlineAttribute(videoObject, "RelativeFilename")
                ?? GetInlineAttribute(videoObject, "FileName")
                ?? GetInlineAttribute(videoObject, "Filename")
                ?? GetInlineAttribute(videoObject, "Path");
            if (!string.IsNullOrWhiteSpace(path))
                return path;
        }

        return null;
    }

    private static TextureType MapTextureType(string? propertyName, FbxSceneObject textureObject, string filePath)
    {
        string basis = propertyName ?? textureObject.DisplayName;
        basis = basis + " " + Path.GetFileNameWithoutExtension(filePath);

        return basis.ToLowerInvariant() switch
        {
            var text when text.Contains("normal") => TextureType.NormalCamera,
            var text when text.Contains("bump") || text.Contains("height") => TextureType.Height,
            var text when text.Contains("specular") || text.Contains("shininess") => TextureType.Specular,
            var text when text.Contains("opacity") || text.Contains("transparent") || text.Contains("alpha") => TextureType.Opacity,
            var text when text.Contains("emiss") => TextureType.Emissive,
            var text when text.Contains("metal") => TextureType.Metalness,
            var text when text.Contains("rough") => TextureType.Roughness,
            var text when text.Contains("basecolor") || text.Contains("diffuse") => TextureType.BaseColor,
            _ => TextureType.Diffuse,
        };
    }

    private static void ApplyMaterialPropertyOverrides(XRMaterial material, FbxSceneObject? materialObject, bool noTextures)
    {
        if (materialObject is null)
            return;

        Vector3 diffuseColor = GetVectorProperty(materialObject, "DiffuseColor")
            ?? GetVectorProperty(materialObject, "BaseColor")
            ?? new Vector3((float)ColorF4.Magenta.R, (float)ColorF4.Magenta.G, (float)ColorF4.Magenta.B);
        float opacity = GetFloatProperty(materialObject, "Opacity") ?? 1.0f;
        float roughness = GetFloatProperty(materialObject, "Roughness") ?? 1.0f;
        float metallic = GetFloatProperty(materialObject, "Metalness") ?? 0.0f;
        Vector3 emissive = GetVectorProperty(materialObject, "EmissiveColor") ?? Vector3.Zero;

        if (noTextures)
            material.SetVector3("BaseColor", diffuseColor);
        material.SetFloat("Opacity", opacity);
        material.SetFloat("Roughness", roughness);
        material.SetFloat("Metallic", metallic);
        material.SetFloat("Emission", emissive.LengthSquared() > 0.0f ? 1.0f : 0.0f);
    }

    private static bool ShouldUseTransparentBlend(FbxSceneObject? materialObject)
    {
        if (materialObject is null)
            return false;

        float opacity = GetFloatProperty(materialObject, "Opacity") ?? 1.0f;
        float transparencyFactor = GetFloatProperty(materialObject, "TransparencyFactor") ?? 0.0f;
        Vector3 transparentColor = GetVectorProperty(materialObject, "TransparentColor") ?? Vector3.Zero;
        return opacity < 0.999f || transparencyFactor > 0.001f || transparentColor.LengthSquared() > 0.0f;
    }

    private static string? GetInlineAttribute(FbxSceneObject sceneObject, string key)
        => sceneObject.InlineAttributes.TryGetValue(key, out FbxSemanticValue value) ? value.AsString() : null;

    private static float? GetFloatProperty(FbxSceneObject sceneObject, string name)
    {
        if (!sceneObject.Properties.TryGetValue(name, out FbxPropertyEntry? property))
            return null;

        float value = (float)property.GetDoubleOrDefault(0, double.NaN);
        return float.IsNaN(value) ? null : value;
    }

    private static Vector3? GetVectorProperty(FbxSceneObject sceneObject, string name)
    {
        if (!sceneObject.Properties.TryGetValue(name, out FbxPropertyEntry? property))
            return null;

        Vector3 value = property.GetVector3OrDefault(new Vector3(float.NaN));
        return float.IsNaN(value.X) || float.IsNaN(value.Y) || float.IsNaN(value.Z)
            ? null
            : value;
    }

    private static bool TryResolveLayerValue<T>(FbxLayerElement<T>? layer, int controlPointIndex, int polygonVertexIndex, int polygonIndex, out T value)
    {
        if (layer is null)
        {
            value = default!;
            return false;
        }

        value = ResolveLayerValue(layer, controlPointIndex, polygonVertexIndex, polygonIndex, default!);
        return true;
    }

    private static T ResolveLayerValue<T>(FbxLayerElement<T> layer, int controlPointIndex, int polygonVertexIndex, int polygonIndex, T fallback)
    {
        int lookupIndex = layer.MappingType switch
        {
            FbxLayerElementMappingType.ByControlPoint => controlPointIndex,
            FbxLayerElementMappingType.ByPolygonVertex => polygonVertexIndex,
            FbxLayerElementMappingType.ByPolygon => polygonIndex,
            FbxLayerElementMappingType.AllSame => 0,
            _ => 0,
        };

        int directIndex = layer.ReferenceType switch
        {
            FbxLayerElementReferenceType.Direct => lookupIndex,
            FbxLayerElementReferenceType.Index or FbxLayerElementReferenceType.IndexToDirect
                => lookupIndex >= 0 && lookupIndex < layer.Indices.Count ? layer.Indices[lookupIndex] : lookupIndex,
            _ => lookupIndex,
        };

        return directIndex >= 0 && directIndex < layer.DirectValues.Count
            ? layer.DirectValues[directIndex]
            : fallback;
    }

    private static Matrix4x4 CreateImportRootMatrix(float scaleConversion, bool zUp, Matrix4x4? rootTransformMatrix)
    {
        Matrix4x4 importMatrix = Matrix4x4.Identity;
        if (scaleConversion != 1.0f)
            importMatrix *= Matrix4x4.CreateScale(scaleConversion);
        if (zUp)
            importMatrix *= Matrix4x4.CreateRotationX(float.DegreesToRadians(90.0f));
        if (rootTransformMatrix.HasValue)
            importMatrix *= rootTransformMatrix.Value;
        return importMatrix;
    }

    private static void ApplyLocalMatrix(SceneNode sceneNode, Matrix4x4 localMatrix)
    {
        Transform transform = sceneNode.GetTransformAs<Transform>(true)!;
        transform.DeriveLocalMatrix(localMatrix);
        transform.RecalculateMatrices(true, false);
        transform.SaveBindState();
    }
}