using Assimp;
using ImageMagick;
using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using XREngine.Animation;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Rendering;
using XREngine.Gltf;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace XREngine;

internal static class NativeGltfSceneImporter
{
    internal readonly record struct ImportResult(
        SceneNode RootNode,
        IReadOnlyList<XRMaterial> Materials,
        IReadOnlyList<XRMesh> Meshes);

    private sealed record SkinInfo(TransformBase?[] JointTransforms, Matrix4x4[] InverseBindMatrices);

    private sealed record ResolvedTextureBinding(
        string Key,
        XRTexture2D Texture,
        TextureSlot Slot);

    private sealed record MaterialTexturePayload(
        XRTexture[] TextureList,
        List<TextureSlot> TextureSlots,
        XRTexture2D? BaseColorTexture,
        bool HasMetallicTexture,
        bool HasRoughnessTexture,
        bool HasEmissiveTexture);

    private sealed record PrimitiveChunk(IReadOnlyList<Vertex> Vertices, List<ushort> Indices, int ChunkIndex);

    private sealed class PrimitiveChunkBuilder(int chunkIndex)
    {
        private readonly Dictionary<int, ushort> _vertexRemap = [];

        public int ChunkIndex { get; } = chunkIndex;
        public List<Vertex> Vertices { get; } = [];
        public List<ushort> Indices { get; } = [];

        public bool CanAppendTriangle(int index0, int index1, int index2)
        {
            int additionalVertices = (!_vertexRemap.ContainsKey(index0) ? 1 : 0)
                + (!_vertexRemap.ContainsKey(index1) ? 1 : 0)
                + (!_vertexRemap.ContainsKey(index2) ? 1 : 0);
            return Vertices.Count + additionalVertices <= ushort.MaxValue + 1;
        }

        public void AppendTriangle(Vertex[] sourceVertices, int index0, int index1, int index2)
        {
            Indices.Add(MapVertex(sourceVertices, index0));
            Indices.Add(MapVertex(sourceVertices, index1));
            Indices.Add(MapVertex(sourceVertices, index2));
        }

        private ushort MapVertex(Vertex[] sourceVertices, int sourceIndex)
        {
            if (_vertexRemap.TryGetValue(sourceIndex, out ushort remappedIndex))
                return remappedIndex;

            remappedIndex = checked((ushort)Vertices.Count);
            _vertexRemap.Add(sourceIndex, remappedIndex);
            Vertices.Add(sourceVertices[sourceIndex].HardCopy());
            return remappedIndex;
        }
    }

    private sealed class ImportedClipBuilder
    {
        private readonly AnimationMember _root = new("Root", EAnimationMemberType.Group);
        private readonly AnimationMember _sceneNode = new("SceneNode", EAnimationMemberType.Property);
        private readonly Dictionary<string, AnimationMember> _nodeCache = new(StringComparer.Ordinal);

        public ImportedClipBuilder()
            => _root.Children.Add(_sceneNode);

        public AnimationMember Root => _root;

        public bool HasAnimations => CountAnimations(_root) > 0;

        public void AddTransformScalarPropertyAnimation(string nodePath, string propertyName, PropAnimFloat anim)
        {
            AnimationMember node = GetSceneNodeByPath(nodePath);
            AnimationMember transform = GetOrAddChild(node, "Transform", EAnimationMemberType.Property);
            AnimationMember property = GetOrAddChild(transform, propertyName, EAnimationMemberType.Property);
            property.Animation = anim;
        }

        public void AddBlendShapeAnimation(string nodePath, string blendshapeName, PropAnimFloat anim)
        {
            AnimationMember node = GetSceneNodeByPath(nodePath);
            AnimationMember getComponent = GetOrAddMethod(node, "GetComponent", ["ModelComponent"], -1, true);
            AnimationMember method = GetOrAddMethod(getComponent, "SetBlendShapeWeightNormalized", [blendshapeName, 0.0f, StringComparison.InvariantCultureIgnoreCase], 1, false);
            method.Animation = anim;
        }

        private AnimationMember GetSceneNodeByPath(string nodePath)
        {
            if (_nodeCache.TryGetValue(nodePath, out AnimationMember? cached))
                return cached;

            AnimationMember current = _sceneNode;
            if (!string.IsNullOrWhiteSpace(nodePath))
            {
                foreach (string segment in nodePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    current = GetOrAddMethod(current, "FindDescendantByName", [segment, StringComparison.InvariantCultureIgnoreCase], -1, true);
            }

            _nodeCache[nodePath] = current;
            return current;
        }

        private static int CountAnimations(AnimationMember member)
        {
            int count = member.Animation is null ? 0 : 1;
            foreach (AnimationMember child in member.Children)
                count += CountAnimations(child);
            return count;
        }

        private static AnimationMember GetOrAddChild(AnimationMember parent, string memberName, EAnimationMemberType memberType)
        {
            foreach (AnimationMember child in parent.Children)
            {
                if (child.MemberName == memberName && child.MemberType == memberType)
                    return child;
            }

            AnimationMember created = new(memberName, memberType);
            parent.Children.Add(created);
            return created;
        }

        private static AnimationMember GetOrAddMethod(AnimationMember parent, string methodName, object?[] methodArgs, int animatedArgIndex, bool cacheReturnValue)
        {
            foreach (AnimationMember child in parent.Children)
            {
                if (child.MemberName != methodName || child.MemberType != EAnimationMemberType.Method)
                    continue;
                if (child.AnimatedMethodArgumentIndex != animatedArgIndex)
                    continue;
                if (child.MethodArguments.Length != methodArgs.Length)
                    continue;

                bool matches = true;
                for (int index = 0; index < methodArgs.Length; index++)
                {
                    if (!Equals(child.MethodArguments[index], methodArgs[index]))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                    return child;
            }

            AnimationMember created = new(methodName, EAnimationMemberType.Method)
            {
                MethodArguments = methodArgs,
                AnimatedMethodArgumentIndex = animatedArgIndex,
                CacheReturnValue = cacheReturnValue,
            };
            parent.Children.Add(created);
            return created;
        }
    }

    private static readonly ConcurrentDictionary<string, byte> s_textureTransformWarnings = [];
    private static readonly HashSet<string> s_supportedRequiredExtensions = new(StringComparer.Ordinal)
    {
        "KHR_materials_unlit",
        "KHR_mesh_quantization",
        "EXT_meshopt_compression",
        "EXT_texture_webp",
    };
    private static readonly HashSet<string> s_explicitlyUnsupportedExtensions = new(StringComparer.Ordinal)
    {
        "KHR_draco_mesh_compression",
        "KHR_texture_basisu",
    };

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

        using GltfAssetDocument document = GltfAssetDocument.Open(sourceFilePath);
        ValidateDocumentCompatibility(document.Root, sourceFilePath);

        SceneNode rootNode = new(Path.GetFileNameWithoutExtension(sourceFilePath))
        {
            Layer = importLayer,
        };
        ApplyLocalMatrix(rootNode, CreateImportRootMatrix(scaleConversion, zUp, rootTransformMatrix));

        Dictionary<int, SceneNode> nodesByIndex = BuildSceneGraph(document.Root, rootNode, importLayer);
        Dictionary<int, SkinInfo> skinsByIndex = BuildSkinInfos(document, nodesByIndex);

        List<XRMaterial> createdMaterials = [];
        List<XRMesh> createdMeshes = [];
        Dictionary<int, XRMaterial> materialCache = [];
        ConcurrentDictionary<string, XRTexture2D> textureCache = new(StringComparer.Ordinal);

        int meshNodeCount = nodesByIndex.Count(static pair => pair.Value is not null) == 0
            ? 0
            : nodesByIndex.Count(pair => document.Root.Nodes[pair.Key].Mesh is int);
        int processedMeshNodes = 0;

        foreach ((int nodeIndex, SceneNode sceneNode) in nodesByIndex.OrderBy(static pair => pair.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();

            GltfNode gltfNode = document.Root.Nodes[nodeIndex];
            if (gltfNode.Mesh is not int meshIndex || meshIndex < 0 || meshIndex >= document.Root.Meshes.Count)
                continue;

            GltfMesh gltfMesh = document.Root.Meshes[meshIndex];
            SkinInfo? skin = gltfNode.Skin is int skinIndex && skinsByIndex.TryGetValue(skinIndex, out SkinInfo? resolvedSkin)
                ? resolvedSkin
                : null;

            List<SubMesh> subMeshes = BuildSubMeshesForNode(
                importer,
                sourceFilePath,
                document,
                rootNode.Transform,
                gltfNode,
                gltfMesh,
                skin,
                importOptions,
                materialCache,
                textureCache,
                createdMaterials,
                createdMeshes,
                cancellationToken);

            if (subMeshes.Count > 0)
                AttachSubMeshesToNode(sceneNode, subMeshes, gltfMesh.Name ?? sceneNode.Name ?? $"Mesh {meshIndex}", importOptions, ResolveDefaultBlendShapeWeights(gltfNode, gltfMesh));

            processedMeshNodes++;
            if (meshNodeCount > 0)
                onProgress?.Invoke(processedMeshNodes / (float)meshNodeCount);
        }

        AttachAnimationClips(document, rootNode, nodesByIndex);

        return new ImportResult(rootNode, createdMaterials, createdMeshes);
    }

    private static Dictionary<int, SceneNode> BuildSceneGraph(GltfRoot document, SceneNode importRootNode, int importLayer)
    {
        Dictionary<int, SceneNode> nodesByIndex = [];
        foreach (int rootNodeIndex in ResolveSceneRootNodeIndices(document))
            BuildSceneNodeRecursive(document, rootNodeIndex, importRootNode, importLayer, nodesByIndex);
        return nodesByIndex;
    }

    private static SceneNode BuildSceneNodeRecursive(
        GltfRoot document,
        int nodeIndex,
        SceneNode parentSceneNode,
        int importLayer,
        Dictionary<int, SceneNode> nodesByIndex)
    {
        if (nodesByIndex.TryGetValue(nodeIndex, out SceneNode? cached))
            return cached;

        GltfNode gltfNode = document.Nodes[nodeIndex];
        string nodeName = ResolveNodeName(document, nodeIndex);
        SceneNode sceneNode = new(parentSceneNode)
        {
            Name = nodeName,
            Layer = importLayer,
        };
        ApplyLocalMatrix(sceneNode, CreateNodeLocalMatrix(gltfNode));

        nodesByIndex.Add(nodeIndex, sceneNode);

        foreach (int childIndex in gltfNode.Children)
        {
            if (childIndex < 0 || childIndex >= document.Nodes.Count)
                continue;

            BuildSceneNodeRecursive(document, childIndex, sceneNode, importLayer, nodesByIndex);
        }

        return sceneNode;
    }

    private static IEnumerable<int> ResolveSceneRootNodeIndices(GltfRoot document)
    {
        if (document.Scenes.Count > 0)
        {
            int sceneIndex = document.ResolveDefaultSceneIndex();
            if (sceneIndex >= 0 && sceneIndex < document.Scenes.Count && document.Scenes[sceneIndex].Nodes.Count > 0)
                return document.Scenes[sceneIndex].Nodes;
        }

        HashSet<int> childIndices = [];
        for (int nodeIndex = 0; nodeIndex < document.Nodes.Count; nodeIndex++)
        {
            foreach (int childIndex in document.Nodes[nodeIndex].Children)
                childIndices.Add(childIndex);
        }

        List<int> inferredRoots = [];
        for (int nodeIndex = 0; nodeIndex < document.Nodes.Count; nodeIndex++)
        {
            if (!childIndices.Contains(nodeIndex))
                inferredRoots.Add(nodeIndex);
        }

        return inferredRoots;
    }

    private static string ResolveNodeName(GltfRoot document, int nodeIndex)
    {
        GltfNode node = document.Nodes[nodeIndex];
        if (!string.IsNullOrWhiteSpace(node.Name))
            return node.Name!;
        if (node.Mesh is int meshIndex && meshIndex >= 0 && meshIndex < document.Meshes.Count && !string.IsNullOrWhiteSpace(document.Meshes[meshIndex].Name))
            return document.Meshes[meshIndex].Name!;
        return $"Node {nodeIndex}";
    }

    private static Dictionary<int, SkinInfo> BuildSkinInfos(GltfAssetDocument document, IReadOnlyDictionary<int, SceneNode> nodesByIndex)
    {
        Dictionary<int, SkinInfo> skinsByIndex = [];

        for (int skinIndex = 0; skinIndex < document.Root.Skins.Count; skinIndex++)
        {
            GltfSkin skin = document.Root.Skins[skinIndex];
            TransformBase?[] jointTransforms = new TransformBase?[skin.Joints.Count];
            for (int jointIndex = 0; jointIndex < skin.Joints.Count; jointIndex++)
            {
                int nodeIndex = skin.Joints[jointIndex];
                if (nodesByIndex.TryGetValue(nodeIndex, out SceneNode? jointNode))
                    jointTransforms[jointIndex] = jointNode.Transform;
            }

            Matrix4x4[] inverseBindMatrices = skin.InverseBindMatrices is int accessorIndex
                ? document.ReadMatrix4Accessor(accessorIndex)
                : [];

            if (inverseBindMatrices.Length < jointTransforms.Length)
            {
                Array.Resize(ref inverseBindMatrices, jointTransforms.Length);
                for (int matrixIndex = 0; matrixIndex < inverseBindMatrices.Length; matrixIndex++)
                {
                    if (inverseBindMatrices[matrixIndex] == default)
                        inverseBindMatrices[matrixIndex] = Matrix4x4.Identity;
                }
            }

            skinsByIndex.Add(skinIndex, new SkinInfo(jointTransforms, inverseBindMatrices));
        }

        return skinsByIndex;
    }

    private static List<SubMesh> BuildSubMeshesForNode(
        ModelImporter importer,
        string sourceFilePath,
        GltfAssetDocument document,
        TransformBase importRootTransform,
        GltfNode gltfNode,
        GltfMesh gltfMesh,
        SkinInfo? skin,
        ModelImportOptions? importOptions,
        Dictionary<int, XRMaterial> materialCache,
        ConcurrentDictionary<string, XRTexture2D> textureCache,
        List<XRMaterial> createdMaterials,
        List<XRMesh> createdMeshes,
        CancellationToken cancellationToken)
    {
        List<SubMesh> subMeshes = [];
        IReadOnlyList<string> blendshapeNames = ResolveBlendshapeNames(gltfMesh);

        for (int primitiveIndex = 0; primitiveIndex < gltfMesh.Primitives.Count; primitiveIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            GltfPrimitive primitive = gltfMesh.Primitives[primitiveIndex];
            Vertex[] vertices = CreateVertices(document, gltfMesh, primitive, blendshapeNames, skin);
            if (vertices.Length == 0)
                continue;

            List<int> triangleIndices = BuildTriangleIndices(document, primitive, vertices.Length, sourceFilePath, primitiveIndex);
            if (triangleIndices.Count == 0)
                continue;

            List<PrimitiveChunk> chunks = SplitPrimitiveIntoChunks(vertices, triangleIndices);
            XRMaterial material = ResolveMaterial(importer, sourceFilePath, document, primitive.Material, importOptions, materialCache, textureCache, createdMaterials);

            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                PrimitiveChunk chunk = chunks[chunkIndex];
                XRMesh xrMesh = new(chunk.Vertices, chunk.Indices);

                if (chunk.Vertices.Any(static vertex => vertex.Weights is { Count: > 0 }))
                    xrMesh.RebuildSkinningBuffersFromVertices();
                if (blendshapeNames.Count > 0)
                {
                    xrMesh.BlendshapeNames = [.. blendshapeNames];
                    xrMesh.RebuildBlendshapeBuffersFromVertices();
                }

                createdMeshes.Add(xrMesh);

                SubMesh subMesh = new(new SubMeshLOD(material, xrMesh, 0.0f)
                {
                    GenerateAsync = importOptions?.GenerateMeshRenderersAsync ?? true,
                })
                {
                    Name = chunks.Count == 1
                        ? ResolvePrimitiveName(gltfMesh, primitiveIndex)
                        : $"{ResolvePrimitiveName(gltfMesh, primitiveIndex)} {chunkIndex}",
                    RootTransform = importRootTransform,
                };
                subMeshes.Add(subMesh);
            }
        }

        return subMeshes;
    }

    private static string ResolvePrimitiveName(GltfMesh mesh, int primitiveIndex)
    {
        string meshName = string.IsNullOrWhiteSpace(mesh.Name) ? "Mesh" : mesh.Name!;
        return mesh.Primitives.Count <= 1 ? meshName : $"{meshName} Primitive {primitiveIndex}";
    }

    private static Vertex[] CreateVertices(
        GltfAssetDocument document,
        GltfMesh mesh,
        GltfPrimitive primitive,
        IReadOnlyList<string> blendshapeNames,
        SkinInfo? skin)
    {
        if (!primitive.TryGetAttributeAccessor("POSITION", out int positionAccessorIndex))
            return [];

        Vector3[] positions = document.ReadVector3Accessor(positionAccessorIndex);
        int vertexCount = positions.Length;
        Vertex[] vertices = new Vertex[vertexCount];

        Vector3[]? normals = TryReadVector3Accessor(document, primitive, "NORMAL");
        Vector4[]? tangents = TryReadVector4Accessor(document, primitive, "TANGENT");
        Dictionary<int, Vector2[]> texCoordSets = ReadIndexedVector2Sets(document, primitive, "TEXCOORD_");
        Dictionary<int, Vector4[]> colorSets = ReadColorSets(document, primitive);

        UInt4[]? joints0 = TryReadUInt4Accessor(document, primitive, "JOINTS_0");
        Vector4[]? weights0 = TryReadVector4Accessor(document, primitive, "WEIGHTS_0");
        UInt4[]? joints1 = TryReadUInt4Accessor(document, primitive, "JOINTS_1");
        Vector4[]? weights1 = TryReadVector4Accessor(document, primitive, "WEIGHTS_1");

        List<MorphTargetVertexData> morphTargets = ReadMorphTargets(document, primitive, blendshapeNames);

        List<KeyValuePair<int, Vector2[]>> orderedTexCoords = [.. texCoordSets.OrderBy(static pair => pair.Key)];
        List<KeyValuePair<int, Vector4[]>> orderedColors = [.. colorSets.OrderBy(static pair => pair.Key)];

        for (int vertexIndex = 0; vertexIndex < vertexCount; vertexIndex++)
        {
            Vertex vertex = new(positions[vertexIndex]);

            if (normals is not null && vertexIndex < normals.Length)
                vertex.Normal = normals[vertexIndex];

            if (tangents is not null && vertexIndex < tangents.Length)
            {
                Vector4 tangent = tangents[vertexIndex];
                vertex.Tangent = new Vector3(tangent.X, tangent.Y, tangent.Z);
                vertex.BitangentSign = tangent.W < 0.0f ? -1.0f : 1.0f;
            }

            if (orderedTexCoords.Count > 0)
            {
                List<Vector2> textureCoordinates = new(orderedTexCoords.Count);
                for (int texCoordIndex = 0; texCoordIndex < orderedTexCoords.Count; texCoordIndex++)
                    textureCoordinates.Add(orderedTexCoords[texCoordIndex].Value[vertexIndex]);
                vertex.TextureCoordinateSets = textureCoordinates;
            }

            if (orderedColors.Count > 0)
            {
                List<Vector4> colors = new(orderedColors.Count);
                for (int colorIndex = 0; colorIndex < orderedColors.Count; colorIndex++)
                    colors.Add(orderedColors[colorIndex].Value[vertexIndex]);
                vertex.ColorSets = colors;
            }

            if (skin is not null)
                ApplySkinWeights(vertex, vertexIndex, joints0, weights0, joints1, weights1, skin);

            if (morphTargets.Count > 0)
                ApplyMorphTargets(vertex, vertexIndex, morphTargets, blendshapeNames);

            vertices[vertexIndex] = vertex;
        }

        return vertices;
    }

    private static Vector3[]? TryReadVector3Accessor(GltfAssetDocument document, GltfPrimitive primitive, string attributeName)
        => primitive.TryGetAttributeAccessor(attributeName, out int accessorIndex)
            ? document.ReadVector3Accessor(accessorIndex)
            : null;

    private static Vector4[]? TryReadVector4Accessor(GltfAssetDocument document, GltfPrimitive primitive, string attributeName)
        => primitive.TryGetAttributeAccessor(attributeName, out int accessorIndex)
            ? document.ReadVector4Accessor(accessorIndex)
            : null;

    private static UInt4[]? TryReadUInt4Accessor(GltfAssetDocument document, GltfPrimitive primitive, string attributeName)
        => primitive.TryGetAttributeAccessor(attributeName, out int accessorIndex)
            ? document.ReadUInt4Accessor(accessorIndex)
            : null;

    private static Dictionary<int, Vector2[]> ReadIndexedVector2Sets(GltfAssetDocument document, GltfPrimitive primitive, string prefix)
    {
        Dictionary<int, Vector2[]> sets = [];
        foreach ((string attributeName, int accessorIndex) in primitive.Attributes)
        {
            if (!attributeName.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            if (!int.TryParse(attributeName[prefix.Length..], out int setIndex))
                continue;

            sets[setIndex] = document.ReadVector2Accessor(accessorIndex);
        }

        return sets;
    }

    private static Dictionary<int, Vector4[]> ReadColorSets(GltfAssetDocument document, GltfPrimitive primitive)
    {
        Dictionary<int, Vector4[]> sets = [];
        foreach ((string attributeName, int accessorIndex) in primitive.Attributes)
        {
            if (!attributeName.StartsWith("COLOR_", StringComparison.Ordinal))
                continue;

            if (!int.TryParse(attributeName["COLOR_".Length..], out int setIndex))
                continue;

            GltfAccessor accessor = document.Root.Accessors[accessorIndex];
            if (accessor.Type.Equals("VEC3", StringComparison.Ordinal))
            {
                Vector3[] values = document.ReadVector3Accessor(accessorIndex);
                Vector4[] colors = new Vector4[values.Length];
                for (int valueIndex = 0; valueIndex < values.Length; valueIndex++)
                    colors[valueIndex] = new Vector4(values[valueIndex], 1.0f);
                sets[setIndex] = colors;
            }
            else
                sets[setIndex] = document.ReadVector4Accessor(accessorIndex);
        }

        return sets;
    }

    private sealed record MorphTargetVertexData(Vector3[]? Positions, Vector3[]? Normals, Vector3[]? Tangents);

    private static List<MorphTargetVertexData> ReadMorphTargets(GltfAssetDocument document, GltfPrimitive primitive, IReadOnlyList<string> blendshapeNames)
    {
        List<MorphTargetVertexData> morphTargets = new(Math.Max(primitive.Targets.Count, blendshapeNames.Count));
        for (int targetIndex = 0; targetIndex < primitive.Targets.Count; targetIndex++)
        {
            Dictionary<string, int> target = primitive.Targets[targetIndex];
            Vector3[]? positions = target.TryGetValue("POSITION", out int positionAccessor) ? document.ReadVector3Accessor(positionAccessor) : null;
            Vector3[]? normals = target.TryGetValue("NORMAL", out int normalAccessor) ? document.ReadVector3Accessor(normalAccessor) : null;
            Vector3[]? tangents = target.TryGetValue("TANGENT", out int tangentAccessor) ? document.ReadVector3Accessor(tangentAccessor) : null;
            morphTargets.Add(new MorphTargetVertexData(positions, normals, tangents));
        }

        return morphTargets;
    }

    private static void ApplySkinWeights(
        Vertex vertex,
        int vertexIndex,
        UInt4[]? joints0,
        Vector4[]? weights0,
        UInt4[]? joints1,
        Vector4[]? weights1,
        SkinInfo skin)
    {
        Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights = null;

        if (joints0 is not null && weights0 is not null && vertexIndex < joints0.Length && vertexIndex < weights0.Length)
            AddWeightSet(ref weights, joints0[vertexIndex], weights0[vertexIndex], skin);

        if (joints1 is not null && weights1 is not null && vertexIndex < joints1.Length && vertexIndex < weights1.Length)
            AddWeightSet(ref weights, joints1[vertexIndex], weights1[vertexIndex], skin);

        if (weights is null || weights.Count == 0)
            return;

        float totalWeight = 0.0f;
        foreach ((_, (float weight, _)) in weights)
            totalWeight += weight;

        if (totalWeight > 0.0f)
        {
            TransformBase[] bones = [.. weights.Keys];
            for (int boneIndex = 0; boneIndex < bones.Length; boneIndex++)
            {
                TransformBase bone = bones[boneIndex];
                (float weight, Matrix4x4 bindInvWorldMatrix) data = weights[bone];
                weights[bone] = (data.weight / totalWeight, data.bindInvWorldMatrix);
            }
        }

        vertex.Weights = weights;
    }

    private static void AddWeightSet(
        ref Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights,
        UInt4 joints,
        Vector4 values,
        SkinInfo skin)
    {
        AddWeight(ref weights, joints.X, values.X, skin);
        AddWeight(ref weights, joints.Y, values.Y, skin);
        AddWeight(ref weights, joints.Z, values.Z, skin);
        AddWeight(ref weights, joints.W, values.W, skin);
    }

    private static void AddWeight(
        ref Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights,
        uint jointIndex,
        float value,
        SkinInfo skin)
    {
        if (value <= 0.0f || jointIndex >= skin.JointTransforms.Length)
            return;

        TransformBase? joint = skin.JointTransforms[jointIndex];
        if (joint is null)
            return;

        weights ??= [];
        Matrix4x4 inverseBind = jointIndex < skin.InverseBindMatrices.Length ? skin.InverseBindMatrices[jointIndex] : Matrix4x4.Identity;
        if (weights.TryGetValue(joint, out (float weight, Matrix4x4 bindInvWorldMatrix) existing))
            weights[joint] = (existing.weight + value, existing.bindInvWorldMatrix);
        else
            weights[joint] = (value, inverseBind);
    }

    private static void ApplyMorphTargets(Vertex vertex, int vertexIndex, IReadOnlyList<MorphTargetVertexData> morphTargets, IReadOnlyList<string> blendshapeNames)
    {
        for (int targetIndex = 0; targetIndex < morphTargets.Count; targetIndex++)
        {
            MorphTargetVertexData morphTarget = morphTargets[targetIndex];
            if (morphTarget.Positions is null && morphTarget.Normals is null && morphTarget.Tangents is null)
                continue;

            string blendshapeName = targetIndex < blendshapeNames.Count && !string.IsNullOrWhiteSpace(blendshapeNames[targetIndex])
                ? blendshapeNames[targetIndex]
                : $"MorphTarget {targetIndex}";

            Vector3 absolutePosition = vertex.Position;
            if (morphTarget.Positions is { Length: > 0 } positions && vertexIndex < positions.Length)
                absolutePosition += positions[vertexIndex];

            Vector3? absoluteNormal = vertex.Normal;
            if (morphTarget.Normals is { Length: > 0 } normals && vertexIndex < normals.Length)
            {
                Vector3 baseNormal = vertex.Normal ?? Vector3.UnitY;
                Vector3 combined = baseNormal + normals[vertexIndex];
                absoluteNormal = combined.LengthSquared() > 0.0f ? Vector3.Normalize(combined) : baseNormal;
            }

            Vector3? absoluteTangent = vertex.Tangent;
            if (morphTarget.Tangents is { Length: > 0 } tangents && vertexIndex < tangents.Length && vertex.Tangent is Vector3 baseTangent)
            {
                Vector3 combined = baseTangent + tangents[vertexIndex];
                absoluteTangent = combined.LengthSquared() > 0.0f ? Vector3.Normalize(combined) : baseTangent;
            }

            vertex.Blendshapes ??= [];
            vertex.Blendshapes.Add((blendshapeName, new VertexData
            {
                Position = absolutePosition,
                Normal = absoluteNormal,
                Tangent = absoluteTangent,
                BitangentSign = vertex.BitangentSign,
            }));
        }
    }

    private static List<int> BuildTriangleIndices(GltfAssetDocument document, GltfPrimitive primitive, int vertexCount, string sourceFilePath, int primitiveIndex)
    {
        uint[] sourceIndices = primitive.Indices is int indexAccessor
            ? document.ReadUIntScalarAccessor(indexAccessor)
            : BuildSequentialIndices(vertexCount);

        List<int> triangleIndices = [];
        switch (primitive.Mode)
        {
            case 4:
            {
                for (int index = 0; index + 2 < sourceIndices.Length; index += 3)
                    AddTriangle(triangleIndices, sourceIndices[index], sourceIndices[index + 1], sourceIndices[index + 2], vertexCount);
                break;
            }
            case 5:
            {
                for (int index = 0; index + 2 < sourceIndices.Length; index++)
                {
                    if ((index & 1) == 0)
                        AddTriangle(triangleIndices, sourceIndices[index], sourceIndices[index + 1], sourceIndices[index + 2], vertexCount);
                    else
                        AddTriangle(triangleIndices, sourceIndices[index + 1], sourceIndices[index], sourceIndices[index + 2], vertexCount);
                }
                break;
            }
            case 6:
            {
                for (int index = 1; index + 1 < sourceIndices.Length; index++)
                    AddTriangle(triangleIndices, sourceIndices[0], sourceIndices[index], sourceIndices[index + 1], vertexCount);
                break;
            }
            default:
                Debug.MeshesWarning($"[NativeGltfImporter] Skipping unsupported primitive mode {primitive.Mode} for '{sourceFilePath}' primitive {primitiveIndex}. Only TRIANGLES, TRIANGLE_STRIP, and TRIANGLE_FAN are currently imported.");
                break;
        }

        return triangleIndices;

        static void AddTriangle(List<int> output, uint index0, uint index1, uint index2, int count)
        {
            if (index0 == index1 || index1 == index2 || index0 == index2)
                return;
            if (index0 >= count || index1 >= count || index2 >= count)
                return;

            output.Add((int)index0);
            output.Add((int)index1);
            output.Add((int)index2);
        }
    }

    private static uint[] BuildSequentialIndices(int vertexCount)
    {
        uint[] indices = new uint[vertexCount];
        for (int index = 0; index < vertexCount; index++)
            indices[index] = (uint)index;
        return indices;
    }

    private static List<PrimitiveChunk> SplitPrimitiveIntoChunks(Vertex[] sourceVertices, IReadOnlyList<int> triangleIndices)
    {
        List<PrimitiveChunk> chunks = [];
        PrimitiveChunkBuilder builder = new(0);

        for (int triangleIndex = 0; triangleIndex + 2 < triangleIndices.Count; triangleIndex += 3)
        {
            int index0 = triangleIndices[triangleIndex];
            int index1 = triangleIndices[triangleIndex + 1];
            int index2 = triangleIndices[triangleIndex + 2];

            if (!builder.CanAppendTriangle(index0, index1, index2) && builder.Vertices.Count > 0)
            {
                chunks.Add(new PrimitiveChunk(builder.Vertices, builder.Indices, builder.ChunkIndex));
                builder = new PrimitiveChunkBuilder(builder.ChunkIndex + 1);
            }

            builder.AppendTriangle(sourceVertices, index0, index1, index2);
        }

        if (builder.Vertices.Count > 0)
            chunks.Add(new PrimitiveChunk(builder.Vertices, builder.Indices, builder.ChunkIndex));

        return chunks;
    }

    private static XRMaterial ResolveMaterial(
        ModelImporter importer,
        string sourceFilePath,
        GltfAssetDocument document,
        int? materialIndex,
        ModelImportOptions? importOptions,
        Dictionary<int, XRMaterial> materialCache,
        ConcurrentDictionary<string, XRTexture2D> textureCache,
        List<XRMaterial> createdMaterials)
    {
        int cacheKey = materialIndex ?? -1;
        if (materialCache.TryGetValue(cacheKey, out XRMaterial? cachedMaterial))
            return cachedMaterial;

        XRMaterial material;
        if (materialIndex is int resolvedMaterialIndex && resolvedMaterialIndex >= 0 && resolvedMaterialIndex < document.Root.Materials.Count)
        {
            GltfMaterial gltfMaterial = document.Root.Materials[resolvedMaterialIndex];
            string materialKey = GltfImportKeyUtilities.GetMaterialKey(document.Root, resolvedMaterialIndex);

            if (importOptions?.MaterialRemap is { } materialRemap
                && materialRemap.TryGetValue(materialKey, out XRMaterial? replacementMaterial)
                && replacementMaterial is not null)
            {
                materialCache[cacheKey] = replacementMaterial;
                return replacementMaterial;
            }

            MaterialTexturePayload texturePayload = ResolveMaterialTextures(importer, sourceFilePath, document, gltfMaterial, importOptions, textureCache);

            if (HasUnlitExtension(gltfMaterial))
                material = CreateUnlitMaterial(gltfMaterial, materialKey, texturePayload.BaseColorTexture);
            else
                material = importer.MakeMaterialAction(texturePayload.TextureList, texturePayload.TextureSlots, materialKey);

            ApplyMaterialOverrides(material, gltfMaterial, texturePayload);
        }
        else
        {
            material = importer.MakeMaterialAction([], [], "DefaultMaterial");
            ApplyMaterialOverrides(material, null, new MaterialTexturePayload([], [], null, false, false, false));
        }

        materialCache[cacheKey] = material;
        createdMaterials.Add(material);
        return material;
    }

    private static MaterialTexturePayload ResolveMaterialTextures(
        ModelImporter importer,
        string sourceFilePath,
        GltfAssetDocument document,
        GltfMaterial material,
        ModelImportOptions? importOptions,
        ConcurrentDictionary<string, XRTexture2D> textureCache)
    {
        List<XRTexture> textures = [];
        List<TextureSlot> textureSlots = [];
        XRTexture2D? baseColorTexture = null;
        bool hasMetallicTexture = false;
        bool hasRoughnessTexture = false;
        bool hasEmissiveTexture = false;

        string alphaMode = NormalizeAlphaMode(material.AlphaMode);

        if (material.PbrMetallicRoughness?.BaseColorTexture is { } baseColorInfo)
        {
            ResolvedTextureBinding? binding = ResolveTextureBinding(sourceFilePath, document, baseColorInfo, importOptions, textureCache, TextureType.BaseColor, null, alphaMode == "BLEND" ? 0x2 : 0);
            if (binding is not null)
            {
                baseColorTexture = binding.Texture;
                textures.Add(binding.Texture);
                textureSlots.Add(binding.Slot);

                if (alphaMode == "MASK")
                {
                    textureSlots.Add(new TextureSlot(binding.Slot.FilePath, TextureType.Opacity, 0, default, binding.Slot.UVIndex, 1.0f, default, binding.Slot.WrapModeU, binding.Slot.WrapModeV, 0));
                    textures.Add(binding.Texture);
                }
            }
        }

        if (material.NormalTexture is { } normalInfo)
        {
            ResolvedTextureBinding? binding = ResolveTextureBinding(sourceFilePath, document, normalInfo, importOptions, textureCache, TextureType.NormalCamera, null, 0);
            if (binding is not null)
            {
                textures.Add(binding.Texture);
                textureSlots.Add(binding.Slot);
            }
        }

        if (material.PbrMetallicRoughness?.MetallicRoughnessTexture is { } metallicRoughnessInfo)
        {
            ResolvedTextureBinding? metallicBinding = ResolveTextureBinding(sourceFilePath, document, metallicRoughnessInfo, importOptions, textureCache, TextureType.Metalness, "metallic", 0);
            if (metallicBinding is not null)
            {
                textures.Add(metallicBinding.Texture);
                textureSlots.Add(metallicBinding.Slot);
                hasMetallicTexture = true;
            }

            ResolvedTextureBinding? roughnessBinding = ResolveTextureBinding(sourceFilePath, document, metallicRoughnessInfo, importOptions, textureCache, TextureType.Roughness, "roughness", 0);
            if (roughnessBinding is not null)
            {
                textures.Add(roughnessBinding.Texture);
                textureSlots.Add(roughnessBinding.Slot);
                hasRoughnessTexture = true;
            }
        }

        if (material.EmissiveTexture is { } emissiveInfo)
        {
            ResolvedTextureBinding? binding = ResolveTextureBinding(sourceFilePath, document, emissiveInfo, importOptions, textureCache, TextureType.Emissive, null, 0);
            if (binding is not null)
            {
                textures.Add(binding.Texture);
                textureSlots.Add(binding.Slot);
                hasEmissiveTexture = true;
            }
        }

        if (material.OcclusionTexture is { } occlusionInfo)
        {
            ResolveTextureBinding(sourceFilePath, document, occlusionInfo, importOptions, textureCache, TextureType.Unknown, "occlusion", 0);
        }

        return new MaterialTexturePayload([.. textures], textureSlots, baseColorTexture, hasMetallicTexture, hasRoughnessTexture, hasEmissiveTexture);
    }

    private static ResolvedTextureBinding? ResolveTextureBinding(
        string sourceFilePath,
        GltfAssetDocument document,
        GltfTextureInfo textureInfo,
        ModelImportOptions? importOptions,
        ConcurrentDictionary<string, XRTexture2D> textureCache,
        TextureType textureType,
        string? purpose,
        int flags)
    {
        if (textureInfo.Index < 0 || textureInfo.Index >= document.Root.Textures.Count)
            return null;

        string textureKey = GltfImportKeyUtilities.GetTextureKey(document.Root, textureInfo.Index, purpose);
        XRTexture2D texture = ResolveTexture(sourceFilePath, document, textureInfo.Index, purpose, importOptions, textureCache, textureKey);
        GltfTexture gltfTexture = document.Root.Textures[textureInfo.Index];
        GltfSampler? sampler = gltfTexture.Sampler is int samplerIndex && samplerIndex >= 0 && samplerIndex < document.Root.Samplers.Count
            ? document.Root.Samplers[samplerIndex]
            : null;

        int uvIndex = ResolveTextureCoordinateSet(textureInfo, textureKey);
        ETexWrapMode wrapU = ResolveWrapMode(sampler?.WrapS);
        ETexWrapMode wrapV = ResolveWrapMode(sampler?.WrapT);
        TextureWrapMode slotWrapU = ResolveTextureSlotWrapMode(sampler?.WrapS);
        TextureWrapMode slotWrapV = ResolveTextureSlotWrapMode(sampler?.WrapT);

        TextureSlot slot = new(textureKey, textureType, 0, default, uvIndex, 1.0f, default, slotWrapU, slotWrapV, flags);
        return new ResolvedTextureBinding(textureKey, texture, slot);
    }

    private static XRTexture2D ResolveTexture(
        string sourceFilePath,
        GltfAssetDocument document,
        int textureIndex,
        string? purpose,
        ModelImportOptions? importOptions,
        ConcurrentDictionary<string, XRTexture2D> textureCache,
        string textureKey)
    {
        if (importOptions?.TextureRemap is { } remap
            && remap.TryGetValue(textureKey, out XRTexture2D? replacementTexture)
            && replacementTexture is not null)
            return replacementTexture;

        return textureCache.GetOrAdd(textureKey, static (key, state) =>
        {
            GltfTexture gltfTexture = state.document.Root.Textures[state.textureIndex];
            GltfSampler? sampler = gltfTexture.Sampler is int samplerIndex && samplerIndex >= 0 && samplerIndex < state.document.Root.Samplers.Count
                ? state.document.Root.Samplers[samplerIndex]
                : null;

            try
            {
                using MagickImage image = state.purpose switch
                {
                    "metallic" => CreateSingleChannelImage(state.document, state.sourceFilePath, state.textureIndex, 2),
                    "roughness" => CreateSingleChannelImage(state.document, state.sourceFilePath, state.textureIndex, 1),
                    _ => LoadTextureImage(state.document, state.sourceFilePath, state.textureIndex),
                };
                return CreateTextureFromImage(key, image, sampler, Path.GetFileNameWithoutExtension(key));
            }
            catch (Exception ex)
            {
                Debug.MeshesWarning($"[NativeGltfImporter] Failed to create texture '{key}' from '{state.sourceFilePath}'. {ex.Message}");
                return CreateFallbackTexture(key, sampler);
            }
        }, (document, sourceFilePath, textureIndex, purpose));
    }

    private static MagickImage LoadTextureImage(GltfAssetDocument document, string sourceFilePath, int textureIndex)
    {
        int imageIndex = ResolveTextureImageIndex(document.Root, textureIndex);
        if (imageIndex < 0 || imageIndex >= document.Root.Images.Count)
            throw new InvalidOperationException($"Texture {textureIndex} does not reference a valid image.");

        GltfImage image = document.Root.Images[imageIndex];
        byte[] sourceBytes = ResolveImageBytes(document, sourceFilePath, image);
        return new MagickImage(sourceBytes);
    }

    private static MagickImage CreateSingleChannelImage(GltfAssetDocument document, string sourceFilePath, int textureIndex, int channelIndex)
    {
        using MagickImage source = LoadTextureImage(document, sourceFilePath, textureIndex);
        MagickImage result = new(new MagickColor(0, 0, 0, 1), source.Width, source.Height);

        using IPixelCollection<float> sourcePixels = source.GetPixels();
        using IPixelCollection<float> resultPixels = result.GetPixels();
        double maxValue = Quantum.Max;

        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                IPixel<float> pixel = sourcePixels.GetPixel(x, y);
                float value = pixel.GetChannel((uint)channelIndex);
                resultPixels.SetPixel(x, y, [value, value, value, (float)maxValue]);
            }
        }

        return result;
    }

    private static int ResolveTextureImageIndex(GltfRoot document, int textureIndex)
    {
        GltfTexture texture = document.Textures[textureIndex];
        if (texture.Source is int sourceIndex)
            return sourceIndex;

        if (texture.Extensions is not null)
        {
            if (TryResolveExtensionSource(texture.Extensions, "KHR_texture_basisu", out int basisuSource))
                return basisuSource;
            if (TryResolveExtensionSource(texture.Extensions, "EXT_texture_webp", out int webpSource))
                return webpSource;
        }

        return -1;
    }

    private static bool TryResolveExtensionSource(Dictionary<string, JsonElement> extensions, string extensionName, out int source)
    {
        source = -1;
        if (!extensions.TryGetValue(extensionName, out JsonElement extensionObject) || extensionObject.ValueKind != JsonValueKind.Object)
            return false;
        if (!extensionObject.TryGetProperty("source", out JsonElement sourceValue) || sourceValue.ValueKind != JsonValueKind.Number)
            return false;
        return sourceValue.TryGetInt32(out source);
    }

    private static byte[] ResolveImageBytes(GltfAssetDocument document, string sourceFilePath, GltfImage image)
    {
        if (image.BufferView is int bufferViewIndex)
            return document.ReadBufferViewBytes(bufferViewIndex);

        if (string.IsNullOrWhiteSpace(image.Uri))
            throw new InvalidOperationException("glTF image does not specify a uri or bufferView.");

        if (TryDecodeDataUri(image.Uri, out byte[]? dataUriBytes))
            return dataUriBytes;

        if (Uri.TryCreate(image.Uri, UriKind.Absolute, out Uri? absoluteUri))
        {
            if (!absoluteUri.IsFile)
                throw new NotSupportedException($"glTF image uri '{image.Uri}' in '{sourceFilePath}' is not a local file path. Use local relative paths or data URIs, or force Assimp for compatibility.");

            return File.ReadAllBytes(absoluteUri.LocalPath);
        }

        string sourceDirectory = Path.GetDirectoryName(sourceFilePath) ?? Environment.CurrentDirectory;
        string relativePath = Uri.UnescapeDataString(image.Uri.Replace('/', Path.DirectorySeparatorChar));
        string resolvedPath = Path.GetFullPath(Path.Combine(sourceDirectory, relativePath));
        return File.ReadAllBytes(resolvedPath);
    }

    private static bool TryDecodeDataUri(string uri, out byte[] bytes)
    {
        bytes = [];
        if (!uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        int commaIndex = uri.IndexOf(',');
        if (commaIndex < 0)
            return false;

        string metadata = uri[5..commaIndex];
        string payload = uri[(commaIndex + 1)..];
        bytes = metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.FromBase64String(payload)
            : System.Text.Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        return true;
    }

    private static XRTexture2D CreateTextureFromImage(string textureKey, MagickImage image, GltfSampler? sampler, string? displayName)
    {
        XRTexture2D texture = new()
        {
            Name = string.IsNullOrWhiteSpace(displayName) ? textureKey : displayName,
            FilePath = textureKey,
            MagFilter = ResolveMagFilter(sampler?.MagFilter),
            MinFilter = ResolveMinFilter(sampler?.MinFilter),
            UWrap = ResolveWrapMode(sampler?.WrapS),
            VWrap = ResolveWrapMode(sampler?.WrapT),
            AlphaAsTransparency = true,
            AutoGenerateMipmaps = true,
            Resizable = false,
        };
        // Only upload the base mip; the GPU generates the rest via glGenerateMipmap.
        texture.Mipmaps = [new Mipmap2D(image)];
        return texture;
    }

    private static XRTexture2D CreateFallbackTexture(string textureKey, GltfSampler? sampler)
    {
        XRTexture2D texture = new()
        {
            Name = Path.GetFileNameWithoutExtension(textureKey),
            FilePath = textureKey,
            MagFilter = ResolveMagFilter(sampler?.MagFilter),
            MinFilter = ResolveMinFilter(sampler?.MinFilter),
            UWrap = ResolveWrapMode(sampler?.WrapS),
            VWrap = ResolveWrapMode(sampler?.WrapT),
            AlphaAsTransparency = true,
            AutoGenerateMipmaps = false,
            Resizable = false,
        };
        using MagickImage filler = (MagickImage)XRTexture2D.FillerImage.Clone();
        texture.Mipmaps = [new Mipmap2D(filler)];
        return texture;
    }

    private static int ResolveTextureCoordinateSet(GltfTextureInfo textureInfo, string textureKey)
    {
        int uvIndex = textureInfo.TexCoord;
        if (textureInfo.Extensions is null || !textureInfo.Extensions.TryGetValue("KHR_texture_transform", out JsonElement transformExtension) || transformExtension.ValueKind != JsonValueKind.Object)
            return uvIndex;

        if (transformExtension.TryGetProperty("texCoord", out JsonElement texCoordValue) && texCoordValue.ValueKind == JsonValueKind.Number && texCoordValue.TryGetInt32(out int overrideUvIndex))
            uvIndex = overrideUvIndex;

        bool hasUnsupportedTransform =
            (transformExtension.TryGetProperty("offset", out JsonElement offset) && offset.ValueKind == JsonValueKind.Array)
            || (transformExtension.TryGetProperty("scale", out JsonElement scale) && scale.ValueKind == JsonValueKind.Array)
            || (transformExtension.TryGetProperty("rotation", out JsonElement rotation) && rotation.ValueKind == JsonValueKind.Number && Math.Abs(rotation.GetDouble()) > double.Epsilon);

        if (hasUnsupportedTransform && s_textureTransformWarnings.TryAdd(textureKey, 0))
            Debug.MeshesWarning($"[NativeGltfImporter] KHR_texture_transform offset/scale/rotation is not yet applied for texture '{textureKey}'. The importer will still honor the texCoord override.");

        return uvIndex;
    }

    private static ETexWrapMode ResolveWrapMode(int? wrapMode)
        => wrapMode switch
        {
            33071 => ETexWrapMode.ClampToEdge,
            33648 => ETexWrapMode.MirroredRepeat,
            _ => ETexWrapMode.Repeat,
        };

    private static TextureWrapMode ResolveTextureSlotWrapMode(int? wrapMode)
        => wrapMode switch
        {
            33071 => TextureWrapMode.Clamp,
            33648 => TextureWrapMode.Mirror,
            _ => TextureWrapMode.Wrap,
        };

    private static ETexMagFilter ResolveMagFilter(int? magFilter)
        => magFilter == 9728 ? ETexMagFilter.Nearest : ETexMagFilter.Linear;

    private static ETexMinFilter ResolveMinFilter(int? minFilter)
        => minFilter switch
        {
            9728 => ETexMinFilter.Nearest,
            9729 => ETexMinFilter.Linear,
            9984 => ETexMinFilter.NearestMipmapNearest,
            9985 => ETexMinFilter.LinearMipmapNearest,
            9986 => ETexMinFilter.NearestMipmapLinear,
            _ => ETexMinFilter.LinearMipmapLinear,
        };

    private static bool HasUnlitExtension(GltfMaterial material)
        => material.Extensions?.ContainsKey("KHR_materials_unlit") == true;

    private static XRMaterial CreateUnlitMaterial(GltfMaterial material, string materialName, XRTexture2D? baseColorTexture)
    {
        float[] baseColorFactor = material.PbrMetallicRoughness?.BaseColorFactor ?? [1.0f, 1.0f, 1.0f, 1.0f];
        ColorF4 color = new(baseColorFactor[0], baseColorFactor[1], baseColorFactor[2], baseColorFactor.Length > 3 ? baseColorFactor[3] : 1.0f);

        XRMaterial unlitMaterial = baseColorTexture is not null
            ? XRMaterial.CreateUnlitTextureMaterialForward(baseColorTexture)
            : XRMaterial.CreateUnlitColorMaterialForward(color);

        if (baseColorTexture is null)
            unlitMaterial.SetVector4("MatColor", new Vector4(color.R, color.G, color.B, color.A));

        unlitMaterial.Name = materialName;
        ApplyAlphaMode(unlitMaterial, material, hasAlphaTexture: baseColorTexture?.HasAlphaChannel ?? false, baseColorAlpha: color.A, forceForward: true);
        if (material.DoubleSided == true)
            unlitMaterial.RenderOptions.CullMode = ECullMode.None;
        return unlitMaterial;
    }

    private static void ApplyMaterialOverrides(XRMaterial material, GltfMaterial? gltfMaterial, MaterialTexturePayload texturePayload)
    {
        if (gltfMaterial is null)
        {
            material.Name ??= "DefaultMaterial";
            return;
        }

        float[] baseColorFactor = gltfMaterial.PbrMetallicRoughness?.BaseColorFactor ?? [1.0f, 1.0f, 1.0f, 1.0f];
        float alpha = baseColorFactor.Length > 3 ? baseColorFactor[3] : 1.0f;
        material.SetVector3("BaseColor", new Vector3(baseColorFactor[0], baseColorFactor[1], baseColorFactor[2]));
        material.SetFloat("Opacity", alpha);
        material.SetFloat("Metallic", gltfMaterial.PbrMetallicRoughness?.MetallicFactor ?? (texturePayload.HasMetallicTexture ? 1.0f : 0.0f));
        material.SetFloat("Roughness", gltfMaterial.PbrMetallicRoughness?.RoughnessFactor ?? 1.0f);

        float emission = 0.0f;
        if (gltfMaterial.EmissiveFactor is { Length: >= 3 } emissiveFactor)
            emission = Math.Max(emissiveFactor[0], Math.Max(emissiveFactor[1], emissiveFactor[2]));
        else if (texturePayload.HasEmissiveTexture)
            emission = 1.0f;
        material.SetFloat("Emission", emission);

        if (gltfMaterial.DoubleSided == true)
            material.RenderOptions.CullMode = ECullMode.None;

        ApplyAlphaMode(material, gltfMaterial, texturePayload.BaseColorTexture?.HasAlphaChannel ?? false, alpha, forceForward: false);
    }

    private static void ApplyAlphaMode(XRMaterial material, GltfMaterial materialDefinition, bool hasAlphaTexture, float baseColorAlpha, bool forceForward)
    {
        string alphaMode = NormalizeAlphaMode(materialDefinition.AlphaMode);
        if (alphaMode == "MASK")
        {
            material.AlphaCutoff = materialDefinition.AlphaCutoff ?? 0.5f;
            material.TransparencyMode = ETransparencyMode.Masked;
            return;
        }

        if (alphaMode == "BLEND")
        {
            material.TransparencyMode = ETransparencyMode.WeightedBlendedOit;
            if (forceForward)
                material.RenderPass = ShaderHelper.ResolveTransparentRenderPass(ETransparencyMode.WeightedBlendedOit);
            return;
        }

        if (baseColorAlpha < 1.0f && !hasAlphaTexture)
        {
            material.TransparencyMode = ETransparencyMode.WeightedBlendedOit;
            if (forceForward)
                material.RenderPass = ShaderHelper.ResolveTransparentRenderPass(ETransparencyMode.WeightedBlendedOit);
        }
    }

    private static string NormalizeAlphaMode(string? alphaMode)
        => string.IsNullOrWhiteSpace(alphaMode) ? "OPAQUE" : alphaMode.ToUpperInvariant();

    private static IReadOnlyList<string> ResolveBlendshapeNames(GltfMesh mesh)
    {
        IReadOnlyList<string> explicitNames = GltfImportKeyUtilities.GetMorphTargetNames(mesh);
        if (explicitNames.Count > 0)
            return explicitNames;

        int morphTargetCount = mesh.Primitives.Count == 0 ? 0 : mesh.Primitives.Max(static primitive => primitive.Targets.Count);
        List<string> fallbackNames = new(morphTargetCount);
        for (int targetIndex = 0; targetIndex < morphTargetCount; targetIndex++)
            fallbackNames.Add($"MorphTarget {targetIndex}");
        return fallbackNames;
    }

    private static IReadOnlyList<float> ResolveDefaultBlendShapeWeights(GltfNode node, GltfMesh mesh)
    {
        if (node.Weights is { Length: > 0 })
            return node.Weights;
        if (mesh.Weights is { Length: > 0 })
            return mesh.Weights;
        return [];
    }

    private static void AttachSubMeshesToNode(
        SceneNode sceneNode,
        IReadOnlyList<SubMesh> subMeshes,
        string fallbackName,
        ModelImportOptions? importOptions,
        IReadOnlyList<float> defaultBlendShapeWeights)
    {
        bool splitSubmeshesIntoSeparateModelComponents = importOptions?.SplitSubmeshesIntoSeparateModelComponents ?? false;
        if (splitSubmeshesIntoSeparateModelComponents)
        {
            for (int index = 0; index < subMeshes.Count; index++)
            {
                SubMesh subMesh = subMeshes[index];
                ModelComponent component = sceneNode.AddComponent<ModelComponent>()!;
                component.Name = string.IsNullOrWhiteSpace(subMesh.Name) ? $"{fallbackName} SubMesh {index}" : subMesh.Name;
                component.Model = new Model(subMesh);
                component.Model.Meshes.ThreadSafe = true;
                ApplyDefaultBlendShapeWeights(component, defaultBlendShapeWeights, subMesh);
            }

            return;
        }

        ModelComponent sharedComponent = sceneNode.AddComponent<ModelComponent>()!;
        sharedComponent.Name = string.IsNullOrWhiteSpace(sceneNode.Name) ? fallbackName : sceneNode.Name;
        sharedComponent.Model = new Model(subMeshes);
        sharedComponent.Model.Meshes.ThreadSafe = true;
        ApplyDefaultBlendShapeWeights(sharedComponent, defaultBlendShapeWeights, subMeshes.FirstOrDefault());
    }

    private static void ApplyDefaultBlendShapeWeights(ModelComponent component, IReadOnlyList<float> defaultWeights, SubMesh? subMesh)
    {
        string[] blendshapeNames = subMesh?.LODs.FirstOrDefault()?.Mesh?.BlendshapeNames ?? [];
        int weightCount = Math.Min(defaultWeights.Count, blendshapeNames.Length);
        for (int index = 0; index < weightCount; index++)
        {
            string blendshapeName = blendshapeNames[index];
            if (string.IsNullOrWhiteSpace(blendshapeName))
                continue;

            component.SetBlendShapeWeightNormalized(blendshapeName, Math.Clamp(defaultWeights[index], 0.0f, 1.0f), StringComparison.InvariantCultureIgnoreCase);
        }
    }

    private static int AttachAnimationClips(GltfAssetDocument document, SceneNode rootNode, IReadOnlyDictionary<int, SceneNode> nodesByIndex)
    {
        if (document.Root.Animations.Count == 0)
            return 0;

        int attachedClipCount = 0;
        for (int animationIndex = 0; animationIndex < document.Root.Animations.Count; animationIndex++)
        {
            GltfAnimation animation = document.Root.Animations[animationIndex];
            ImportedClipBuilder builder = new();
            SortedSet<float> keyTimes = [];

            for (int channelIndex = 0; channelIndex < animation.Channels.Count; channelIndex++)
            {
                GltfAnimationChannel channel = animation.Channels[channelIndex];
                if (channel.Sampler < 0 || channel.Sampler >= animation.Samplers.Count)
                    continue;
                if (channel.Target.Node is not int nodeIndex || !nodesByIndex.TryGetValue(nodeIndex, out SceneNode? sceneNode))
                    continue;

                GltfAnimationSampler sampler = animation.Samplers[channel.Sampler];
                float[] inputTimes = document.ReadFloatScalarAccessor(sampler.Input);
                foreach (float keyTime in inputTimes)
                    keyTimes.Add(keyTime);

                string nodePath = BuildNodePath(rootNode, sceneNode);
                string path = channel.Target.Path ?? string.Empty;
                if (path.Equals("translation", StringComparison.Ordinal))
                {
                    Vector3[] values = ReadAnimationVector3Values(document, sampler);
                    AddVector3AnimationClips(builder, nodePath, inputTimes, values, sampler.Interpolation, "TranslationX", "TranslationY", "TranslationZ");
                }
                else if (path.Equals("scale", StringComparison.Ordinal))
                {
                    Vector3[] values = ReadAnimationVector3Values(document, sampler);
                    AddVector3AnimationClips(builder, nodePath, inputTimes, values, sampler.Interpolation, "ScaleX", "ScaleY", "ScaleZ");
                }
                else if (path.Equals("rotation", StringComparison.Ordinal))
                {
                    Vector4[] values = ReadAnimationVector4Values(document, sampler);
                    AddQuaternionAnimationClips(builder, nodePath, inputTimes, values, sampler.Interpolation);
                }
                else if (path.Equals("weights", StringComparison.Ordinal) && document.Root.Nodes[nodeIndex].Mesh is int meshIndex && meshIndex >= 0 && meshIndex < document.Root.Meshes.Count)
                {
                    GltfMesh mesh = document.Root.Meshes[meshIndex];
                    IReadOnlyList<string> blendshapeNames = ResolveBlendshapeNames(mesh);
                    if (blendshapeNames.Count == 0)
                        continue;

                    float[] values = document.ReadFloatScalarAccessor(sampler.Output);
                    AddWeightAnimations(builder, nodePath, inputTimes, values, sampler.Interpolation, blendshapeNames);
                }
            }

            if (!builder.HasAnimations)
                continue;

            AnimationClip clip = new(builder.Root)
            {
                Name = string.IsNullOrWhiteSpace(animation.Name) ? $"Animation_{animationIndex}" : animation.Name,
                ClipKind = EAnimationClipKind.GenericTransform,
                Looped = false,
                LengthInSeconds = keyTimes.Count > 0 ? keyTimes.Max : 0.0f,
                SampleRate = DetermineSampleRate(keyTimes),
                TotalAnimCount = CountAnimatedMembers(builder.Root),
            };

            if (RuntimeAnimationComponentActivator.AddAnimationClipComponent(rootNode, clip) is not null)
                attachedClipCount++;
        }

        return attachedClipCount;
    }

    private static Vector3[] ReadAnimationVector3Values(GltfAssetDocument document, GltfAnimationSampler sampler)
    {
        GltfAccessor accessor = document.Root.Accessors[sampler.Output];
        if (NormalizeInterpolation(sampler.Interpolation) == "CUBICSPLINE")
        {
            Vector3[] packedValues = document.ReadVector3Accessor(sampler.Output);
            Vector3[] values = new Vector3[packedValues.Length / 3];
            for (int index = 0; index < values.Length; index++)
                values[index] = packedValues[index * 3 + 1];
            return values;
        }

        return accessor.Type.Equals("VEC3", StringComparison.Ordinal)
            ? document.ReadVector3Accessor(sampler.Output)
            : [];
    }

    private static Vector4[] ReadAnimationVector4Values(GltfAssetDocument document, GltfAnimationSampler sampler)
    {
        if (NormalizeInterpolation(sampler.Interpolation) == "CUBICSPLINE")
        {
            Vector4[] packedValues = document.ReadVector4Accessor(sampler.Output);
            Vector4[] values = new Vector4[packedValues.Length / 3];
            for (int index = 0; index < values.Length; index++)
                values[index] = NormalizeQuaternion(packedValues[index * 3 + 1]);
            return values;
        }

        Vector4[] quaternions = document.ReadVector4Accessor(sampler.Output);
        for (int index = 0; index < quaternions.Length; index++)
            quaternions[index] = NormalizeQuaternion(quaternions[index]);
        return quaternions;
    }

    private static void AddVector3AnimationClips(
        ImportedClipBuilder builder,
        string nodePath,
        IReadOnlyList<float> times,
        IReadOnlyList<Vector3> values,
        string? interpolation,
        string propertyX,
        string propertyY,
        string propertyZ)
    {
        if (times.Count == 0 || values.Count == 0)
            return;

        builder.AddTransformScalarPropertyAnimation(nodePath, propertyX, CreateFloatAnimation(times, values.Select(static value => value.X).ToArray(), interpolation));
        builder.AddTransformScalarPropertyAnimation(nodePath, propertyY, CreateFloatAnimation(times, values.Select(static value => value.Y).ToArray(), interpolation));
        builder.AddTransformScalarPropertyAnimation(nodePath, propertyZ, CreateFloatAnimation(times, values.Select(static value => value.Z).ToArray(), interpolation));
    }

    private static void AddQuaternionAnimationClips(ImportedClipBuilder builder, string nodePath, IReadOnlyList<float> times, IReadOnlyList<Vector4> values, string? interpolation)
    {
        if (times.Count == 0 || values.Count == 0)
            return;

        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionX", CreateFloatAnimation(times, values.Select(static value => value.X).ToArray(), interpolation));
        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionY", CreateFloatAnimation(times, values.Select(static value => value.Y).ToArray(), interpolation));
        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionZ", CreateFloatAnimation(times, values.Select(static value => value.Z).ToArray(), interpolation));
        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionW", CreateFloatAnimation(times, values.Select(static value => value.W).ToArray(), interpolation));
    }

    private static void AddWeightAnimations(
        ImportedClipBuilder builder,
        string nodePath,
        IReadOnlyList<float> times,
        IReadOnlyList<float> values,
        string? interpolation,
        IReadOnlyList<string> blendshapeNames)
    {
        if (times.Count == 0 || values.Count == 0 || blendshapeNames.Count == 0)
            return;

        string normalizedInterpolation = NormalizeInterpolation(interpolation);
        int targetCount = blendshapeNames.Count;
        int stride = normalizedInterpolation == "CUBICSPLINE" ? targetCount * 3 : targetCount;
        if (values.Count < times.Count * stride)
            return;

        for (int targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            float[] componentValues = new float[times.Count];
            float[]? inTangents = normalizedInterpolation == "CUBICSPLINE" ? new float[times.Count] : null;
            float[]? outTangents = normalizedInterpolation == "CUBICSPLINE" ? new float[times.Count] : null;
            for (int keyIndex = 0; keyIndex < times.Count; keyIndex++)
            {
                int baseIndex = keyIndex * stride;
                if (normalizedInterpolation == "CUBICSPLINE")
                {
                    inTangents![keyIndex] = values[baseIndex + targetIndex];
                    componentValues[keyIndex] = values[baseIndex + targetCount + targetIndex];
                    outTangents![keyIndex] = values[baseIndex + targetCount * 2 + targetIndex];
                }
                else
                    componentValues[keyIndex] = values[baseIndex + targetIndex];
            }

            PropAnimFloat animation = CreateFloatAnimation(times, componentValues, interpolation, inTangents, outTangents);
            builder.AddBlendShapeAnimation(nodePath, blendshapeNames[targetIndex], animation);
        }
    }

    private static PropAnimFloat CreateFloatAnimation(
        IReadOnlyList<float> times,
        IReadOnlyList<float> values,
        string? interpolation,
        IReadOnlyList<float>? inTangents = null,
        IReadOnlyList<float>? outTangents = null)
    {
        string normalizedInterpolation = NormalizeInterpolation(interpolation);
        PropAnimFloat animation = new(times.Count > 0 ? times[^1] : 0.0f, false, true);
        for (int keyIndex = 0; keyIndex < times.Count && keyIndex < values.Count; keyIndex++)
        {
            FloatKeyframe keyframe = new()
            {
                SyncInOutValues = true,
                SyncInOutTangentDirections = normalizedInterpolation != "CUBICSPLINE",
                SyncInOutTangentMagnitudes = normalizedInterpolation != "CUBICSPLINE",
                Second = times[keyIndex],
                InValue = values[keyIndex],
                OutValue = values[keyIndex],
                InterpolationTypeIn = normalizedInterpolation switch
                {
                    "STEP" => EVectorInterpType.Step,
                    "CUBICSPLINE" => EVectorInterpType.Hermite,
                    _ => EVectorInterpType.Linear,
                },
                InterpolationTypeOut = normalizedInterpolation switch
                {
                    "STEP" => EVectorInterpType.Step,
                    "CUBICSPLINE" => EVectorInterpType.Hermite,
                    _ => EVectorInterpType.Linear,
                },
            };

            if (normalizedInterpolation == "CUBICSPLINE")
            {
                keyframe.InTangent = keyIndex < (inTangents?.Count ?? 0) ? -(inTangents?[keyIndex] ?? 0.0f) : 0.0f;
                keyframe.OutTangent = keyIndex < (outTangents?.Count ?? 0) ? outTangents?[keyIndex] ?? 0.0f : 0.0f;
            }
            else
            {
                keyframe.InTangent = 0.0f;
                keyframe.OutTangent = 0.0f;
            }

            animation.Keyframes.Add(keyframe);
        }

        return animation;
    }

    private static string NormalizeInterpolation(string? interpolation)
        => string.IsNullOrWhiteSpace(interpolation) ? "LINEAR" : interpolation.ToUpperInvariant();

    private static Vector4 NormalizeQuaternion(Vector4 quaternion)
    {
        Quaternion value = Quaternion.Normalize(new Quaternion(quaternion.X, quaternion.Y, quaternion.Z, quaternion.W));
        return new Vector4(value.X, value.Y, value.Z, value.W);
    }

    private static string BuildNodePath(SceneNode rootNode, SceneNode targetNode)
    {
        if (ReferenceEquals(rootNode, targetNode))
            return string.Empty;

        Stack<string> segments = new();
        SceneNode? current = targetNode;
        while (current is not null && !ReferenceEquals(current, rootNode))
        {
            if (!string.IsNullOrWhiteSpace(current.Name))
                segments.Push(current.Name);
            current = current.Transform.Parent?.SceneNode;
        }

        return string.Join('/', segments);
    }

    private static int DetermineSampleRate(IEnumerable<float> keyTimes)
    {
        float previous = -1.0f;
        float minDelta = float.MaxValue;
        foreach (float keyTime in keyTimes.OrderBy(static value => value))
        {
            if (previous >= 0.0f)
            {
                float delta = keyTime - previous;
                if (delta > 0.0f && delta < minDelta)
                    minDelta = delta;
            }

            previous = keyTime;
        }

        if (minDelta == float.MaxValue)
            return 30;

        return Math.Clamp((int)Math.Round(1.0f / minDelta), 1, 240);
    }

    private static int CountAnimatedMembers(AnimationMember member)
    {
        int count = member.Animation is null ? 0 : 1;
        foreach (AnimationMember child in member.Children)
            count += CountAnimatedMembers(child);
        return count;
    }

    private static void ValidateDocumentCompatibility(GltfRoot document, string sourceFilePath)
    {
        List<string> unsupportedRequiredExtensions = [];
        foreach (string requiredExtension in document.ExtensionsRequired)
        {
            if (s_explicitlyUnsupportedExtensions.Contains(requiredExtension))
                unsupportedRequiredExtensions.Add(requiredExtension);
            else if (requiredExtension.Equals("KHR_texture_transform", StringComparison.Ordinal))
            {
                if (UsesUnsupportedTextureTransform(document))
                    unsupportedRequiredExtensions.Add(requiredExtension);
            }
            else if (!s_supportedRequiredExtensions.Contains(requiredExtension))
                unsupportedRequiredExtensions.Add(requiredExtension);
        }

        if (unsupportedRequiredExtensions.Count > 0)
        {
            string extensions = string.Join(", ", unsupportedRequiredExtensions.OrderBy(static extension => extension, StringComparer.Ordinal));
            throw new NotSupportedException($"glTF asset '{sourceFilePath}' requires extensions that the native importer does not currently support: {extensions}. Use GltfBackend=Assimp for the compatibility path.");
        }

        for (int textureIndex = 0; textureIndex < document.Textures.Count; textureIndex++)
        {
            GltfTexture texture = document.Textures[textureIndex];
            if (texture.Extensions?.ContainsKey("KHR_texture_basisu") == true)
                throw new NotSupportedException($"glTF asset '{sourceFilePath}' uses KHR_texture_basisu on texture {textureIndex}. The native importer does not decode basisu/KTX2 textures yet. Use GltfBackend=Assimp for compatibility.");
        }

        for (int meshIndex = 0; meshIndex < document.Meshes.Count; meshIndex++)
        {
            GltfMesh mesh = document.Meshes[meshIndex];
            for (int primitiveIndex = 0; primitiveIndex < mesh.Primitives.Count; primitiveIndex++)
            {
                GltfPrimitive primitive = mesh.Primitives[primitiveIndex];
                if (primitive.Extensions?.ContainsKey("KHR_draco_mesh_compression") == true)
                    throw new NotSupportedException($"glTF asset '{sourceFilePath}' uses KHR_draco_mesh_compression on mesh {meshIndex} primitive {primitiveIndex}. The native importer does not decode Draco-compressed primitives yet. Use GltfBackend=Assimp for compatibility.");
            }
        }

        if (UsesUnsupportedTextureTransform(document))
            throw new NotSupportedException($"glTF asset '{sourceFilePath}' uses KHR_texture_transform offset/scale/rotation data that the native importer does not yet apply. Use GltfBackend=Assimp for compatibility.");
    }

    private static bool UsesUnsupportedTextureTransform(GltfRoot document)
    {
        foreach (GltfMaterial material in document.Materials)
        {
            if (HasUnsupportedTextureTransform(material.PbrMetallicRoughness?.BaseColorTexture)
                || HasUnsupportedTextureTransform(material.PbrMetallicRoughness?.MetallicRoughnessTexture)
                || HasUnsupportedTextureTransform(material.NormalTexture)
                || HasUnsupportedTextureTransform(material.OcclusionTexture)
                || HasUnsupportedTextureTransform(material.EmissiveTexture))
                return true;
        }

        return false;
    }

    private static bool HasUnsupportedTextureTransform(GltfTextureInfo? textureInfo)
    {
        if (textureInfo?.Extensions is null
            || !textureInfo.Extensions.TryGetValue("KHR_texture_transform", out JsonElement transformExtension)
            || transformExtension.ValueKind != JsonValueKind.Object)
            return false;

        if (transformExtension.TryGetProperty("offset", out JsonElement offset)
            && offset.ValueKind == JsonValueKind.Array)
            return true;

        if (transformExtension.TryGetProperty("scale", out JsonElement scale)
            && scale.ValueKind == JsonValueKind.Array)
            return true;

        if (transformExtension.TryGetProperty("rotation", out JsonElement rotation)
            && rotation.ValueKind == JsonValueKind.Number
            && Math.Abs(rotation.GetDouble()) > double.Epsilon)
            return true;

        return false;
    }

    private static Matrix4x4 CreateNodeLocalMatrix(GltfNode node)
    {
        if (node.Matrix is { Length: 16 } matrixValues)
        {
            Matrix4x4 matrix = new(
                matrixValues[0], matrixValues[1], matrixValues[2], matrixValues[3],
                matrixValues[4], matrixValues[5], matrixValues[6], matrixValues[7],
                matrixValues[8], matrixValues[9], matrixValues[10], matrixValues[11],
                matrixValues[12], matrixValues[13], matrixValues[14], matrixValues[15]);
            return Matrix4x4.Transpose(matrix);
        }

        Vector3 translation = node.Translation is { Length: >= 3 }
            ? new Vector3(node.Translation[0], node.Translation[1], node.Translation[2])
            : Vector3.Zero;
        Quaternion rotation = node.Rotation is { Length: >= 4 }
            ? Quaternion.Normalize(new Quaternion(node.Rotation[0], node.Rotation[1], node.Rotation[2], node.Rotation[3]))
            : Quaternion.Identity;
        Vector3 scale = node.Scale is { Length: >= 3 }
            ? new Vector3(node.Scale[0], node.Scale[1], node.Scale[2])
            : Vector3.One;

        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(translation);
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
