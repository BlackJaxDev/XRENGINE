using Assimp;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading.Tasks;
using XREngine.Animation;
using XREngine.Components.Animation;
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

    private const long DefaultMaterialCacheKey = long.MinValue;

    private sealed class MeshChunkBuilder(int materialSlot, int initialVertexCapacity, int initialIndexCapacity)
    {
        public int MaterialSlot { get; } = materialSlot;
        public List<Vertex> Vertices { get; } = new(Math.Max(4, initialVertexCapacity));
        public List<ushort> Indices { get; } = new(Math.Max(6, initialIndexCapacity));

        public bool CanAppendPolygon(int polygonVertexCount)
            => Vertices.Count + polygonVertexCount <= ushort.MaxValue + 1;

        public void AppendPolygon(ReadOnlySpan<Vertex> polygonVertices)
        {
            int polygonVertexCount = polygonVertices.Length;
            if (polygonVertexCount < 3)
                return;

            int requiredVertexCapacity = Vertices.Count + polygonVertexCount;
            if (Vertices.Capacity < requiredVertexCapacity)
                Vertices.Capacity = requiredVertexCapacity;

            int triangleIndexCount = (polygonVertexCount - 2) * 3;
            int requiredIndexCapacity = Indices.Count + triangleIndexCount;
            if (Indices.Capacity < requiredIndexCapacity)
                Indices.Capacity = requiredIndexCapacity;

            int baseVertex = Vertices.Count;
            for (int vertexIndex = 0; vertexIndex < polygonVertexCount; vertexIndex++)
                Vertices.Add(polygonVertices[vertexIndex]);

            for (int triangleIndex = 1; triangleIndex < polygonVertexCount - 1; triangleIndex++)
            {
                Indices.Add((ushort)baseVertex);
                Indices.Add((ushort)(baseVertex + triangleIndex));
                Indices.Add((ushort)(baseVertex + triangleIndex + 1));
            }
        }
    }

    private readonly record struct MeshBuildWorkItem(
        int WorkIndex,
        long ModelObjectId,
        SceneNode SceneNode,
        FbxIntermediateMesh Mesh,
        FbxMeshGeometry MeshGeometry,
        FbxIntermediateNode IntermediateNode,
        IReadOnlyList<FbxIntermediateMaterial> NodeMaterials,
        IReadOnlyDictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>>? SkinWeightsByControlPoint,
        IReadOnlyList<FbxBlendShapeChannelBinding> BlendShapeChannels);

    private readonly record struct MeshBuildResult(
        long ModelObjectId,
        SceneNode SceneNode,
        string MeshName,
        IReadOnlyList<SubMesh> SubMeshes,
        IReadOnlyList<FbxBlendShapeChannelBinding> BlendShapeChannels);

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

        XREngine.Fbx.FbxTrace.LogSink ??= static message => Debug.Assets(message);

        return XREngine.Fbx.FbxTrace.TraceOperation(
            "NativeImporter",
            $"Importing '{sourceFilePath}' with {DescribeImportOptions(importOptions, scaleConversion, zUp, importLayer, rootTransformMatrix)}.",
            result => $"Imported '{sourceFilePath}' into root '{result.RootNode.Name}': sceneNodes={CountSceneNodes(result.RootNode):N0}, materials={result.Materials.Count:N0}, meshes={result.Meshes.Count:N0}, animationClips={result.RootNode.GetComponents<AnimationClipComponent>().Count():N0}",
            () =>
            {
                using FbxStructuralDocument structural = FbxStructuralParser.ParseFile(sourceFilePath);
                FbxSceneSemanticsPolicy policy = new(
                    importOptions?.FbxPivotPolicy ?? FbxSceneSemanticsPolicy.Default.PivotImportPolicy,
                    FbxModelHierarchyPolicy.PreserveAuthoredNodes);

                FbxSemanticDocument semantic = FbxSemanticParser.Parse(structural, policy);
                FbxGeometryDocument geometry = FbxGeometryParser.Parse(structural, semantic);
                FbxDeformerDocument deformers = FbxDeformerParser.Parse(structural, semantic);
                FbxAnimationDocument animations = FbxAnimationParser.Parse(structural, semantic, deformers);

                SceneNode rootNode = new(Path.GetFileNameWithoutExtension(sourceFilePath)) { Layer = importLayer };
                ApplyLocalMatrix(rootNode, CreateImportRootMatrix(scaleConversion, zUp, rootTransformMatrix));

                Dictionary<long, SceneNode> nodesByObjectId = BuildSceneNodes(rootNode, semantic, importLayer, cancellationToken);
                XREngine.Fbx.FbxTrace.Info("NativeImporter", $"Created {nodesByObjectId.Count:N0} authored scene node(s) beneath imported root '{rootNode.Name}'.");

                List<XRMaterial> createdMaterials = [];
                List<XRMesh> createdMeshes = [];
                Dictionary<long, XRMaterial> materialCache = new();
                object materialCacheSync = new();
                object createdAssetsSync = new();

                Dictionary<long, FbxIntermediateMaterial[]> materialsByModelObjectId = BuildMaterialsByModelObjectId(semantic);
                Dictionary<long, FbxIntermediateNode> intermediateNodesByObjectId = BuildIntermediateNodesByObjectId(semantic);
                FbxIntermediateMesh[] meshes = [.. semantic.IntermediateScene.Meshes];
                Array.Sort(meshes, static (left, right) => left.ObjectIndex.CompareTo(right.ObjectIndex));
                bool flipUvY = importOptions?.PostProcessSteps.HasFlag(PostProcessSteps.FlipUVs) ?? true;

                List<MeshBuildWorkItem> workItems = [];
                for (int meshIndex = 0; meshIndex < meshes.Length; meshIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    FbxIntermediateMesh mesh = meshes[meshIndex];
                    if (!geometry.TryGetMeshGeometry(mesh.ObjectId, out FbxMeshGeometry meshGeometry))
                    {
                        XREngine.Fbx.FbxTrace.Warning("NativeImporter", $"Skipping intermediate mesh '{mesh.Name}' (objectId={mesh.ObjectId}) because no parsed geometry was found.");
                        continue;
                    }

                    bool hasSkinBinding = deformers.TryGetSkinBinding(mesh.ObjectId, out FbxSkinBinding skinBinding);
                    IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels = deformers.GetBlendShapeChannels(mesh.ObjectId);

                    for (int modelIndex = 0; modelIndex < mesh.ModelObjectIds.Count; modelIndex++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        long modelObjectId = mesh.ModelObjectIds[modelIndex];
                        if (!nodesByObjectId.TryGetValue(modelObjectId, out SceneNode? sceneNode))
                        {
                            XREngine.Fbx.FbxTrace.Verbose("NativeImporter", $"Skipping mesh '{mesh.Name}' model link to objectId={modelObjectId} because no scene node was created for it.");
                            continue;
                        }
                        if (!semantic.TryGetObject(modelObjectId, out _))
                        {
                            XREngine.Fbx.FbxTrace.Verbose("NativeImporter", $"Skipping mesh '{mesh.Name}' model link to objectId={modelObjectId} because the semantic object is missing.");
                            continue;
                        }
                        if (!intermediateNodesByObjectId.TryGetValue(modelObjectId, out FbxIntermediateNode? intermediateNode)
                            || intermediateNode is null)
                        {
                            XREngine.Fbx.FbxTrace.Verbose("NativeImporter", $"Skipping mesh '{mesh.Name}' model link to objectId={modelObjectId} because the intermediate node is missing.");
                            continue;
                        }

                        IReadOnlyList<FbxIntermediateMaterial> nodeMaterials = materialsByModelObjectId.TryGetValue(modelObjectId, out FbxIntermediateMaterial[]? resolvedNodeMaterials)
                            && resolvedNodeMaterials is not null
                            ? resolvedNodeMaterials
                            : Array.Empty<FbxIntermediateMaterial>();

                        IReadOnlyDictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>>? skinWeightsByControlPoint =
                            hasSkinBinding
                                ? BuildSkinWeightsByControlPoint(skinBinding, sceneNode.Transform.WorldMatrix, nodesByObjectId)
                                : null;

                        workItems.Add(new MeshBuildWorkItem(
                            workItems.Count,
                            modelObjectId,
                            sceneNode,
                            mesh,
                            meshGeometry,
                            intermediateNode,
                            nodeMaterials,
                            skinWeightsByControlPoint,
                            blendShapeChannels));
                    }
                }

                MeshBuildResult?[] buildResults = new MeshBuildResult?[workItems.Count];
                bool generateMeshRenderersAsync = importOptions?.GenerateMeshRenderersAsync ?? true;

                if (workItems.Count > 0)
                {
                    int completedWorkItems = 0;
                    ParallelOptions parallelOptions = new()
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1),
                    };

                    Parallel.For(0, workItems.Count, parallelOptions, workItemIndex =>
                    {
                        MeshBuildWorkItem workItem = workItems[workItemIndex];
                        List<SubMesh> subMeshes = BuildSubMeshesForNode(
                            importer,
                            sourceFilePath,
                            semantic,
                            workItem.Mesh,
                            workItem.MeshGeometry,
                            workItem.IntermediateNode,
                            workItem.NodeMaterials,
                            workItem.SkinWeightsByControlPoint,
                            workItem.BlendShapeChannels,
                            createdMaterials,
                            createdMeshes,
                            materialCache,
                            materialCacheSync,
                            createdAssetsSync,
                            flipUvY,
                            cancellationToken,
                            rootNode.Transform,
                            generateMeshRenderersAsync);

                        buildResults[workItemIndex] = new MeshBuildResult(
                            workItem.ModelObjectId,
                            workItem.SceneNode,
                            workItem.Mesh.Name,
                            subMeshes,
                            workItem.BlendShapeChannels);

                        int completed = Interlocked.Increment(ref completedWorkItems);
                        onProgress?.Invoke(completed / (float)workItems.Count);
                    });
                }

                for (int resultIndex = 0; resultIndex < buildResults.Length; resultIndex++)
                {
                    if (buildResults[resultIndex] is not MeshBuildResult result)
                        continue;

                    if (result.SubMeshes.Count == 0)
                    {
                        XREngine.Fbx.FbxTrace.Verbose("NativeImporter", $"Mesh '{result.MeshName}' produced no submeshes for node '{result.SceneNode.Name}' (objectId={result.ModelObjectId}).");
                        continue;
                    }

                    AttachSubMeshesToNode(result.SceneNode, result.SubMeshes, result.MeshName, importOptions, result.BlendShapeChannels);

                    XREngine.Fbx.FbxTrace.Verbose(
                        "NativeImporter",
                        $"Attached {result.SubMeshes.Count:N0} submesh(es) from mesh '{result.MeshName}' to node '{result.SceneNode.Name}' with blendShapeChannels={result.BlendShapeChannels.Count:N0}.");
                }

                int attachedAnimationClipCount = AttachAnimationClips(rootNode, semantic, animations, nodesByObjectId);
                XREngine.Fbx.FbxTrace.Info("NativeImporter", $"Attached {attachedAnimationClipCount:N0} animation clip(s) to imported root '{rootNode.Name}'.");

                return new ImportResult(rootNode, createdMaterials, createdMeshes);
            });
    }

    private static string DescribeImportOptions(ModelImportOptions? importOptions, float scaleConversion, bool zUp, int importLayer, Matrix4x4? rootTransformMatrix)
        => $"scale={scaleConversion}, zUp={zUp}, layer={importLayer}, fbxBackend={importOptions?.FbxBackend.ToString() ?? "Auto"}, pivotPolicy={importOptions?.FbxPivotPolicy.ToString() ?? FbxSceneSemanticsPolicy.Default.PivotImportPolicy.ToString()}, splitSubmeshes={importOptions?.SplitSubmeshesIntoSeparateModelComponents ?? false}, processMeshesAsync={importOptions?.ProcessMeshesAsynchronously?.ToString() ?? "inherit"}, batchSubmeshAdds={importOptions?.BatchSubmeshAddsDuringAsyncImport.ToString() ?? "inherit"}, rootTransformApplied={rootTransformMatrix.HasValue}";

    private static int CountSceneNodes(SceneNode rootNode)
    {
        int count = 1;
        foreach (TransformBase child in rootNode.Transform.Children)
        {
            if (child.SceneNode is not null)
                count += CountSceneNodes(child.SceneNode);
        }

        return count;
    }

    private static Dictionary<long, FbxIntermediateMaterial[]> BuildMaterialsByModelObjectId(FbxSemanticDocument semantic)
    {
        Dictionary<long, List<FbxIntermediateMaterial>> materialsByModelObjectId = new();
        IReadOnlyList<FbxIntermediateMaterial> materials = semantic.IntermediateScene.Materials;
        for (int materialIndex = 0; materialIndex < materials.Count; materialIndex++)
        {
            FbxIntermediateMaterial material = materials[materialIndex];
            for (int modelIndex = 0; modelIndex < material.ModelObjectIds.Count; modelIndex++)
            {
                long modelObjectId = material.ModelObjectIds[modelIndex];
                if (!materialsByModelObjectId.TryGetValue(modelObjectId, out List<FbxIntermediateMaterial>? nodeMaterials))
                {
                    nodeMaterials = [];
                    materialsByModelObjectId[modelObjectId] = nodeMaterials;
                }

                nodeMaterials.Add(material);
            }
        }

        Dictionary<long, FbxIntermediateMaterial[]> results = new(materialsByModelObjectId.Count);
        foreach ((long modelObjectId, List<FbxIntermediateMaterial> nodeMaterials) in materialsByModelObjectId)
        {
            nodeMaterials.Sort(static (left, right) => left.ObjectIndex.CompareTo(right.ObjectIndex));
            results[modelObjectId] = [.. nodeMaterials];
        }

        return results;
    }

    private static Dictionary<long, FbxIntermediateNode> BuildIntermediateNodesByObjectId(FbxSemanticDocument semantic)
    {
        Dictionary<long, FbxIntermediateNode> nodesByObjectId = new(semantic.IntermediateScene.Nodes.Count);
        IReadOnlyList<FbxIntermediateNode> nodes = semantic.IntermediateScene.Nodes;
        for (int nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
        {
            FbxIntermediateNode node = nodes[nodeIndex];
            nodesByObjectId[node.ObjectId] = node;
        }

        return nodesByObjectId;
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

    private static void AttachSubMeshesToNode(
        SceneNode sceneNode,
        IReadOnlyList<SubMesh> subMeshes,
        string fallbackName,
        ModelImportOptions? importOptions,
        IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels)
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
                ApplyDefaultBlendShapeWeights(component, blendShapeChannels);
            }

            return;
        }

        ModelComponent sharedComponent = sceneNode.AddComponent<ModelComponent>()!;
        sharedComponent.Name = string.IsNullOrWhiteSpace(sceneNode.Name) ? fallbackName : sceneNode.Name;
        sharedComponent.Model = new Model(subMeshes);
        sharedComponent.Model.Meshes.ThreadSafe = true;
        ApplyDefaultBlendShapeWeights(sharedComponent, blendShapeChannels);
    }

    private static List<SubMesh> BuildSubMeshesForNode(
        ModelImporter importer,
        string sourceFilePath,
        FbxSemanticDocument semantic,
        FbxIntermediateMesh mesh,
        FbxMeshGeometry meshGeometry,
        FbxIntermediateNode intermediateNode,
        IReadOnlyList<FbxIntermediateMaterial> nodeMaterials,
        IReadOnlyDictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>>? skinWeightsByControlPoint,
        IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels,
        List<XRMaterial> createdMaterials,
        List<XRMesh> createdMeshes,
        Dictionary<long, XRMaterial> materialCache,
        object materialCacheSync,
        object createdAssetsSync,
        bool flipUvY,
        CancellationToken cancellationToken,
        TransformBase rootTransform,
        bool generateMeshRenderersAsync)
    {
        int materialSlotCount = Math.Max(1, nodeMaterials.Count);
        List<MeshChunkBuilder>?[] buildersByMaterialSlot = new List<MeshChunkBuilder>?[materialSlotCount];
        IReadOnlyList<int> polygonVertexIndices = meshGeometry.PolygonVertexIndices;
        FbxLayerElement<Vector3>? normalLayer = meshGeometry.Normals.Count > 0 ? meshGeometry.Normals[0] : null;
        FbxLayerElement<Vector3>? tangentLayer = meshGeometry.Tangents.Count > 0 ? meshGeometry.Tangents[0] : null;
        string[]? blendShapeNames = blendShapeChannels.Count > 0 ? BuildBlendshapeNames(blendShapeChannels) : null;

        int polygonVertexStart = 0;
        int polygonIndex = 0;
        while (polygonVertexStart < polygonVertexIndices.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int polygonVertexCount = CountPolygonVertexCount(polygonVertexIndices, polygonVertexStart);
            Vertex[] rentedPolygonVertices = ArrayPool<Vertex>.Shared.Rent(polygonVertexCount);
            int polygonVertexIndex = polygonVertexStart;
            int localPolygonVertexIndex = 0;
            try
            {
                while (polygonVertexIndex < polygonVertexIndices.Count)
                {
                    int encodedControlPointIndex = polygonVertexIndices[polygonVertexIndex];
                    bool endOfPolygon = encodedControlPointIndex < 0;
                    int controlPointIndex = endOfPolygon ? ~encodedControlPointIndex : encodedControlPointIndex;
                    rentedPolygonVertices[localPolygonVertexIndex++] = CreateVertex(
                        meshGeometry,
                        intermediateNode.GeometryTransform,
                        controlPointIndex,
                        polygonVertexIndex,
                        polygonIndex,
                        normalLayer,
                        tangentLayer,
                        flipUvY,
                        skinWeightsByControlPoint,
                        blendShapeChannels);
                    polygonVertexIndex++;
                    if (endOfPolygon)
                        break;
                }

                int materialSlot = ResolveMaterialSlot(meshGeometry.Materials, polygonIndex, polygonVertexStart, nodeMaterials.Count);
                List<MeshChunkBuilder> chunks = buildersByMaterialSlot[materialSlot] ??= [];
                MeshChunkBuilder chunk = chunks.Count > 0 && chunks[^1].CanAppendPolygon(localPolygonVertexIndex)
                    ? chunks[^1]
                    : CreateChunk(chunks, materialSlot, polygonVertexIndices.Count - polygonVertexStart, localPolygonVertexIndex);
                chunk.AppendPolygon(rentedPolygonVertices.AsSpan(0, localPolygonVertexIndex));

                polygonVertexStart = polygonVertexIndex;
                polygonIndex++;
            }
            finally
            {
                ArrayPool<Vertex>.Shared.Return(rentedPolygonVertices, clearArray: true);
            }
        }

        List<SubMesh> subMeshes = [];
        for (int materialSlot = 0; materialSlot < buildersByMaterialSlot.Length; materialSlot++)
        {
            List<MeshChunkBuilder>? chunks = buildersByMaterialSlot[materialSlot];
            if (chunks is null || chunks.Count == 0)
                continue;

            XRMaterial material = ResolveMaterial(
                importer,
                sourceFilePath,
                semantic,
                nodeMaterials,
                materialSlot,
                materialCache,
                createdMaterials,
                materialCacheSync);

            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                MeshChunkBuilder chunk = chunks[chunkIndex];
                if (chunk.Vertices.Count == 0 || chunk.Indices.Count == 0)
                    continue;

                XRMesh xrMesh = new(chunk.Vertices, chunk.Indices);
                if (skinWeightsByControlPoint is not null && skinWeightsByControlPoint.Count > 0)
                    xrMesh.RebuildSkinningBuffersFromVertices();
                if (blendShapeNames is not null)
                {
                    xrMesh.BlendshapeNames = blendShapeNames;
                    xrMesh.RebuildBlendshapeBuffersFromVertices();
                }
                lock (createdAssetsSync)
                    createdMeshes.Add(xrMesh);

                SubMesh subMesh = new(new SubMeshLOD(material, xrMesh, 0.0f)
                {
                    GenerateAsync = generateMeshRenderersAsync,
                })
                {
                    Name = chunks.Count == 1 ? mesh.Name : $"{mesh.Name} {chunkIndex}",
                    RootTransform = rootTransform,
                };

                subMeshes.Add(subMesh);
            }
        }

        return subMeshes;

        static MeshChunkBuilder CreateChunk(List<MeshChunkBuilder> chunks, int materialSlot, int remainingPolygonVertexCount, int polygonVertexCount)
        {
            int vertexCapacity = Math.Min(ushort.MaxValue + 1, Math.Max(polygonVertexCount, remainingPolygonVertexCount));
            int indexCapacity = Math.Max(6, (Math.Max(3, polygonVertexCount) - 2) * 3);
            MeshChunkBuilder chunk = new(materialSlot, vertexCapacity, indexCapacity);
            chunks.Add(chunk);
            return chunk;
        }
    }

    private static int CountPolygonVertexCount(IReadOnlyList<int> polygonVertexIndices, int polygonVertexStart)
    {
        int polygonVertexCount = 0;
        for (int vertexIndex = polygonVertexStart; vertexIndex < polygonVertexIndices.Count; vertexIndex++)
        {
            polygonVertexCount++;
            if (polygonVertexIndices[vertexIndex] < 0)
                break;
        }

        return polygonVertexCount;
    }

    private static string[] BuildBlendshapeNames(IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels)
    {
        HashSet<string> uniqueNames = new(StringComparer.Ordinal);
        List<string> names = new(blendShapeChannels.Count);
        for (int index = 0; index < blendShapeChannels.Count; index++)
        {
            string name = blendShapeChannels[index].Name;
            if (uniqueNames.Add(name))
                names.Add(name);
        }

        return [.. names];
    }

    private static Vertex CreateVertex(
        FbxMeshGeometry meshGeometry,
        Matrix4x4 geometryTransform,
        int controlPointIndex,
        int polygonVertexIndex,
        int polygonIndex,
        FbxLayerElement<Vector3>? normalLayer,
        FbxLayerElement<Vector3>? tangentLayer,
        bool flipUvY,
        IReadOnlyDictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>>? skinWeightsByControlPoint,
        IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels)
    {
        Vector3 position = controlPointIndex >= 0 && controlPointIndex < meshGeometry.ControlPoints.Count
            ? Vector3.Transform(meshGeometry.ControlPoints[controlPointIndex], geometryTransform)
            : Vector3.Zero;

        Vertex vertex = new(position);
        Vector3 baseNormal = Vector3.Zero;
        bool hasNormal = false;
        Vector3 baseTangent = Vector3.Zero;
        bool hasTangent = false;

        if (TryResolveLayerValue(normalLayer, controlPointIndex, polygonVertexIndex, polygonIndex, out Vector3 normal)
            && normal.LengthSquared() > 0.0f)
        {
            baseNormal = Vector3.Normalize(Vector3.TransformNormal(normal, geometryTransform));
            vertex.Normal = baseNormal;
            hasNormal = true;
        }

        if (TryResolveLayerValue(tangentLayer, controlPointIndex, polygonVertexIndex, polygonIndex, out Vector3 tangent)
            && tangent.LengthSquared() > 0.0f)
        {
            baseTangent = Vector3.Normalize(Vector3.TransformNormal(tangent, geometryTransform));
            vertex.Tangent = baseTangent;
            hasTangent = true;
        }

        if (meshGeometry.TextureCoordinates.Count > 0)
        {
            List<Vector2> textureCoordinateSets = new(meshGeometry.TextureCoordinates.Count);
            foreach (FbxLayerElement<Vector2> layer in meshGeometry.TextureCoordinates)
            {
                Vector2 textureCoordinate = ResolveLayerValue(layer, controlPointIndex, polygonVertexIndex, polygonIndex, Vector2.Zero);
                if (flipUvY)
                    textureCoordinate.Y = 1.0f - textureCoordinate.Y;
                textureCoordinateSets.Add(textureCoordinate);
            }
            vertex.TextureCoordinateSets = textureCoordinateSets;
        }

        if (meshGeometry.Colors.Count > 0)
        {
            List<Vector4> colorSets = new(meshGeometry.Colors.Count);
            foreach (FbxLayerElement<Vector4> layer in meshGeometry.Colors)
                colorSets.Add(ResolveLayerValue(layer, controlPointIndex, polygonVertexIndex, polygonIndex, Vector4.One));
            vertex.ColorSets = colorSets;
        }

        if (skinWeightsByControlPoint is not null
            && controlPointIndex >= 0
            && skinWeightsByControlPoint.TryGetValue(controlPointIndex, out Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights)
            && weights.Count > 0)
        {
            vertex.Weights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>(weights.Count);
            foreach ((TransformBase bone, (float weight, Matrix4x4 bindInvWorldMatrix) data) in weights)
                vertex.Weights[bone] = data;
        }

        if (blendShapeChannels.Count > 0 && controlPointIndex >= 0)
        {
            foreach (FbxBlendShapeChannelBinding channel in blendShapeChannels)
            {
                bool hasPositionDelta = channel.PositionDeltasByControlPoint.TryGetValue(controlPointIndex, out Vector3 positionDelta);
                bool hasNormalDelta = channel.NormalDeltasByControlPoint.TryGetValue(controlPointIndex, out Vector3 normalDelta);
                if (!hasPositionDelta && !hasNormalDelta)
                    continue;

                Vector3 absolutePosition = vertex.Position + (hasPositionDelta ? Vector3.TransformNormal(positionDelta, geometryTransform) : Vector3.Zero);
                Vector3? absoluteNormal = hasNormal
                    ? Vector3.Normalize(baseNormal + (hasNormalDelta ? Vector3.TransformNormal(normalDelta, geometryTransform) : Vector3.Zero))
                    : null;

                vertex.Blendshapes ??= [];
                vertex.Blendshapes.Add((
                    channel.Name,
                    new VertexData
                    {
                        Position = absolutePosition,
                        Normal = absoluteNormal,
                        Tangent = hasTangent ? baseTangent : null,
                    }));
            }
        }

        return vertex;
    }

    private static Dictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>> BuildSkinWeightsByControlPoint(
        FbxSkinBinding skinBinding,
        Matrix4x4 meshWorldMatrix,
        IReadOnlyDictionary<long, SceneNode> nodesByObjectId)
    {
        Dictionary<int, Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>> weightsByControlPoint = new();
        foreach (FbxClusterBinding cluster in skinBinding.Clusters)
        {
            if (!nodesByObjectId.TryGetValue(cluster.BoneModelObjectId, out SceneNode? boneNode))
                continue;

            TransformBase boneTransform = boneNode.Transform;
            Matrix4x4 bindInvWorldMatrix = Matrix4x4.Invert(boneTransform.WorldMatrix, out Matrix4x4 inverseBoneWorld)
                ? meshWorldMatrix * inverseBoneWorld
                : cluster.InverseBindMatrix;
            foreach ((int controlPointIndex, float weight) in cluster.ControlPointWeights)
            {
                if (controlPointIndex < 0 || weight <= 0.0f)
                    continue;

                if (!weightsByControlPoint.TryGetValue(controlPointIndex, out Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? controlPointWeights))
                {
                    controlPointWeights = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>();
                    weightsByControlPoint[controlPointIndex] = controlPointWeights;
                }

                if (controlPointWeights.TryGetValue(boneTransform, out (float weight, Matrix4x4 bindInvWorldMatrix) existing))
                    controlPointWeights[boneTransform] = (existing.weight + weight, existing.bindInvWorldMatrix);
                else
                    controlPointWeights[boneTransform] = (weight, bindInvWorldMatrix);
            }
        }

        foreach (Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> controlPointWeights in weightsByControlPoint.Values)
        {
            float totalWeight = 0.0f;
            foreach (KeyValuePair<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)> pair in controlPointWeights)
                totalWeight += pair.Value.weight;
            if (totalWeight <= 0.0f)
                continue;

            TransformBase[] bones = ArrayPool<TransformBase>.Shared.Rent(controlPointWeights.Count);
            int boneCount = 0;
            foreach ((TransformBase bone, _) in controlPointWeights)
                bones[boneCount++] = bone;

            for (int boneIndex = 0; boneIndex < boneCount; boneIndex++)
            {
                TransformBase bone = bones[boneIndex];
                (float weight, Matrix4x4 bindInvWorldMatrix) data = controlPointWeights[bone];
                controlPointWeights[bone] = (data.weight / totalWeight, data.bindInvWorldMatrix);
            }

            ArrayPool<TransformBase>.Shared.Return(bones, clearArray: true);
        }

        return weightsByControlPoint;
    }

    private static void ApplyDefaultBlendShapeWeights(ModelComponent component, IReadOnlyList<FbxBlendShapeChannelBinding> blendShapeChannels)
    {
        foreach (FbxBlendShapeChannelBinding channel in blendShapeChannels)
        {
            if (channel.FullWeight <= 0.0f)
                continue;

            float normalizedWeight = channel.DefaultDeformPercent / channel.FullWeight;
            if (!float.IsFinite(normalizedWeight))
                continue;

            component.SetBlendShapeWeightNormalized(channel.Name, Math.Clamp(normalizedWeight, 0.0f, 1.0f), StringComparison.InvariantCultureIgnoreCase);
        }
    }

    private static int AttachAnimationClips(
        SceneNode rootNode,
        FbxSemanticDocument semantic,
        FbxAnimationDocument animations,
        IReadOnlyDictionary<long, SceneNode> nodesByObjectId)
    {
        if (animations.Stacks.Count == 0)
            return 0;

        Dictionary<long, FbxIntermediateMesh> meshesByObjectId = semantic.IntermediateScene.Meshes.ToDictionary(static mesh => mesh.ObjectId);
        int attachedClipCount = 0;
        foreach (FbxAnimationStackBinding stack in animations.Stacks)
        {
            ImportedClipBuilder builder = new();

            foreach (FbxNodeAnimationBinding nodeAnimation in stack.NodeAnimations)
            {
                if (!nodesByObjectId.TryGetValue(nodeAnimation.ModelObjectId, out SceneNode? sceneNode))
                    continue;
                if (!semantic.TryGetObject(nodeAnimation.ModelObjectId, out FbxSceneObject nodeObject))
                    continue;

                string nodePath = BuildNodePath(rootNode, sceneNode);
                Vector3 defaultTranslation = GetVectorProperty(nodeObject, "Lcl Translation") ?? Vector3.Zero;
                Vector3 defaultRotationDegrees = GetVectorProperty(nodeObject, "Lcl Rotation") ?? Vector3.Zero;
                Vector3 defaultScale = GetVectorProperty(nodeObject, "Lcl Scaling") ?? Vector3.One;

                AddScalarClip(builder, nodePath, "TranslationX", nodeAnimation.TranslationX, defaultTranslation.X);
                AddScalarClip(builder, nodePath, "TranslationY", nodeAnimation.TranslationY, defaultTranslation.Y);
                AddScalarClip(builder, nodePath, "TranslationZ", nodeAnimation.TranslationZ, defaultTranslation.Z);
                AddScalarClip(builder, nodePath, "ScaleX", nodeAnimation.ScaleX, defaultScale.X);
                AddScalarClip(builder, nodePath, "ScaleY", nodeAnimation.ScaleY, defaultScale.Y);
                AddScalarClip(builder, nodePath, "ScaleZ", nodeAnimation.ScaleZ, defaultScale.Z);

                AddQuaternionClips(builder, nodePath, nodeAnimation, defaultRotationDegrees);
            }

            foreach (FbxBlendShapeAnimationBinding blendShapeAnimation in stack.BlendShapeAnimations)
            {
                if (!meshesByObjectId.TryGetValue(blendShapeAnimation.GeometryObjectId, out FbxIntermediateMesh? mesh))
                    continue;

                foreach (long modelObjectId in mesh.ModelObjectIds)
                {
                    if (!nodesByObjectId.TryGetValue(modelObjectId, out SceneNode? modelNode))
                        continue;

                    string nodePath = BuildNodePath(rootNode, modelNode);
                    float defaultWeight = blendShapeAnimation.FullWeight > 0.0f
                        ? blendShapeAnimation.DefaultDeformPercent / blendShapeAnimation.FullWeight
                        : 0.0f;
                    PropAnimFloat weightAnimation = CreateFloatAnimation(blendShapeAnimation.WeightCurve, defaultWeight, NormalizeBlendShapeWeight(blendShapeAnimation.FullWeight));
                    builder.AddBlendShapeAnimation(nodePath, blendShapeAnimation.BlendShapeName, weightAnimation);
                }
            }

            if (!builder.HasAnimations)
                continue;

            SortedSet<long> stackKeyTimes = [.. stack.EnumerateKeyTimes()];
            AnimationClip clip = new(builder.Root)
            {
                Name = string.IsNullOrWhiteSpace(stack.Name) ? $"AnimationStack_{stack.StackObjectId}" : stack.Name,
                ClipKind = EAnimationClipKind.GenericTransform,
                Looped = false,
                LengthInSeconds = stackKeyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(stackKeyTimes.Max) : 0.0f,
                SampleRate = DetermineSampleRate(stackKeyTimes),
                TotalAnimCount = CountAnimatedMembers(builder.Root),
            };

            AnimationClipComponent component = rootNode.AddComponent<AnimationClipComponent>()!;
            component.Name = clip.Name;
            component.Animation = clip;
            attachedClipCount++;

            XREngine.Fbx.FbxTrace.Verbose(
                "NativeImporter",
                $"Animation stack '{clip.Name}' attached with nodeAnimations={stack.NodeAnimations.Count:N0}, blendShapeAnimations={stack.BlendShapeAnimations.Count:N0}, lengthSeconds={clip.LengthInSeconds:0.###}, sampleRate={clip.SampleRate:0.###}." );
        }

        return attachedClipCount;
    }

    private static void AddScalarClip(
        ImportedClipBuilder builder,
        string nodePath,
        string propertyName,
        FbxScalarCurve? curve,
        float defaultValue)
    {
        if (curve is null)
            return;

        PropAnimFloat anim = CreateFloatAnimation(curve, defaultValue, 1.0f);
        builder.AddTransformScalarPropertyAnimation(nodePath, propertyName, anim);
    }

    private static void AddQuaternionClips(ImportedClipBuilder builder, string nodePath, FbxNodeAnimationBinding nodeAnimation, Vector3 defaultRotationDegrees)
    {
        FbxScalarCurve?[] rotationCurves = [nodeAnimation.RotationX, nodeAnimation.RotationY, nodeAnimation.RotationZ];
        if (rotationCurves.All(static curve => curve is null))
            return;

        SortedSet<long> keyTimes = [];
        foreach (FbxScalarCurve? curve in rotationCurves)
        {
            if (curve is null)
                continue;
            foreach (long keyTime in curve.KeyTimes)
                keyTimes.Add(keyTime);
        }
        keyTimes.Add(0L);

        PropAnimFloat quaternionX = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);
        PropAnimFloat quaternionY = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);
        PropAnimFloat quaternionZ = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);
        PropAnimFloat quaternionW = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);

        foreach (long keyTime in keyTimes)
        {
            Vector3 sampledEulerDegrees = new(
                EvaluateCurveOrDefault(nodeAnimation.RotationX, keyTime, defaultRotationDegrees.X),
                EvaluateCurveOrDefault(nodeAnimation.RotationY, keyTime, defaultRotationDegrees.Y),
                EvaluateCurveOrDefault(nodeAnimation.RotationZ, keyTime, defaultRotationDegrees.Z));
            Quaternion rotation = CreateQuaternionFromEulerDegrees(sampledEulerDegrees);
            float second = FbxAnimationTime.ToSeconds(keyTime);
            quaternionX.Keyframes.Add(CreateLinearKeyframe(second, rotation.X));
            quaternionY.Keyframes.Add(CreateLinearKeyframe(second, rotation.Y));
            quaternionZ.Keyframes.Add(CreateLinearKeyframe(second, rotation.Z));
            quaternionW.Keyframes.Add(CreateLinearKeyframe(second, rotation.W));
        }

        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionX", quaternionX);
        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionY", quaternionY);
        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionZ", quaternionZ);
        builder.AddTransformScalarPropertyAnimation(nodePath, "QuaternionW", quaternionW);
    }

    private static PropAnimFloat CreateFloatAnimation(FbxScalarCurve curve, float defaultValue, float valueScale)
    {
        SortedSet<long> keyTimes = [.. curve.KeyTimes];
        keyTimes.Add(0L);

        PropAnimFloat anim = new(keyTimes.Count > 0 ? FbxAnimationTime.ToSeconds(keyTimes.Max) : 0.0f, false, true);
        foreach (long keyTime in keyTimes)
        {
            float value = EvaluateCurveOrDefault(curve, keyTime, defaultValue) * valueScale;
            anim.Keyframes.Add(CreateLinearKeyframe(FbxAnimationTime.ToSeconds(keyTime), value));
        }

        return anim;
    }

    private static FloatKeyframe CreateLinearKeyframe(float second, float value)
        => new()
        {
            SyncInOutValues = true,
            SyncInOutTangentDirections = true,
            SyncInOutTangentMagnitudes = true,
            Second = second,
            InValue = value,
            OutValue = value,
            InterpolationTypeIn = EVectorInterpType.Linear,
            InterpolationTypeOut = EVectorInterpType.Linear,
        };

    private static float EvaluateCurveOrDefault(FbxScalarCurve? curve, long keyTime, float defaultValue)
    {
        if (curve is null || !curve.HasKeys)
            return defaultValue;
        if (curve.KeyTimes[0] > keyTime)
            return defaultValue;
        return curve.Evaluate(keyTime);
    }

    private static Quaternion CreateQuaternionFromEulerDegrees(Vector3 eulerDegrees)
    {
        Vector3 radians = new(
            float.DegreesToRadians(eulerDegrees.X),
            float.DegreesToRadians(eulerDegrees.Y),
            float.DegreesToRadians(eulerDegrees.Z));
        return Quaternion.Normalize(Quaternion.CreateFromYawPitchRoll(radians.Y, radians.X, radians.Z));
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

    private static int DetermineSampleRate(IEnumerable<long> keyTimes)
    {
        long previous = -1;
        long minDelta = long.MaxValue;
        foreach (long keyTime in keyTimes.OrderBy(static value => value))
        {
            if (previous >= 0)
            {
                long delta = keyTime - previous;
                if (delta > 0 && delta < minDelta)
                    minDelta = delta;
            }

            previous = keyTime;
        }

        if (minDelta == long.MaxValue)
            return 30;

        return Math.Clamp((int)Math.Round(FbxAnimationTime.TicksPerSecond / (double)minDelta), 1, 240);
    }

    private static int CountAnimatedMembers(AnimationMember member)
    {
        int count = member.Animation is null ? 0 : 1;
        foreach (AnimationMember child in member.Children)
            count += CountAnimatedMembers(child);
        return count;
    }

    private static float NormalizeBlendShapeWeight(float fullWeight)
        => fullWeight > 0.0f ? 1.0f / fullWeight : 1.0f;

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
        object materialCacheSync)
    {
        long materialCacheKey = DefaultMaterialCacheKey;
        FbxIntermediateMaterial? intermediateMaterial = null;
        if (materialSlot >= 0 && materialSlot < nodeMaterials.Count)
        {
            intermediateMaterial = nodeMaterials[materialSlot];
            materialCacheKey = intermediateMaterial.ObjectId;
        }

        lock (materialCacheSync)
        {
            if (materialCache.TryGetValue(materialCacheKey, out XRMaterial? cachedMaterial))
                return cachedMaterial;

            XRMaterial material;
            if (intermediateMaterial is not null)
            {
                semantic.TryGetObject(intermediateMaterial.ObjectId, out FbxSceneObject sceneMaterial);
                string materialName = sceneMaterial?.DisplayName ?? intermediateMaterial.Name;
                bool transparentBlendHint = ShouldUseTransparentBlend(sceneMaterial);

                List<TextureSlot> textureSlots = BuildTextureSlots(semantic, intermediateMaterial.ObjectId, transparentBlendHint);
                material = importer.MaterialFactory(sourceFilePath, materialName, textureSlots, 0, ShadingMode.Phong, []);
                ApplyMaterialPropertyOverrides(material, sceneMaterial, textureSlots.Count == 0);
            }
            else
            {
                material = importer.MaterialFactory(sourceFilePath, "DefaultMaterial", [], 0, ShadingMode.Phong, []);
            }

            materialCache[materialCacheKey] = material;
            createdMaterials.Add(material);
            return material;
        }
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
