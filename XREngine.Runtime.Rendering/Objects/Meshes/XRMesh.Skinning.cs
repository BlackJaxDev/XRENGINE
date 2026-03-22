using Assimp;
using System.Collections.Concurrent;
using System.Numerics;
using XREngine.Scene.Transforms;
using XREngine.Scene;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    public void RebuildSkinningBuffersFromVertices()
    {
        ClearSkinningBuffers();

        if (Vertices is not { Length: > 0 })
        {
            UtilizedBones = [];
            return;
        }

        var boneToIndexTable = new Dictionary<TransformBase, int>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var utilizedBones = new List<(TransformBase tfm, Matrix4x4 invBindWorldMtx)>(UtilizedBones.Length);

        for (int i = 0; i < UtilizedBones.Length; ++i)
        {
            var utilized = UtilizedBones[i];
            if (boneToIndexTable.ContainsKey(utilized.tfm))
                continue;

            boneToIndexTable.Add(utilized.tfm, utilizedBones.Count);
            utilizedBones.Add(utilized);
        }

        var weightsPerVertex = new Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[VertexCount];
        _maxWeightCount = 0;

        for (int vertexIndex = 0; vertexIndex < VertexCount; ++vertexIndex)
        {
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights = Vertices[vertexIndex].Weights;
            if (weights is null || weights.Count == 0)
                continue;

            var copiedWeights = new Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>(weights.Count, System.Collections.Generic.ReferenceEqualityComparer.Instance);
            foreach (var pair in weights)
            {
                copiedWeights[pair.Key] = pair.Value;
                if (boneToIndexTable.ContainsKey(pair.Key))
                    continue;

                boneToIndexTable.Add(pair.Key, utilizedBones.Count);
                utilizedBones.Add((pair.Key, pair.Value.bindInvWorldMatrix));
            }

            weightsPerVertex[vertexIndex] = copiedWeights;
            _maxWeightCount = Math.Max(_maxWeightCount, copiedWeights.Count);
        }

        if (boneToIndexTable.Count == 0)
        {
            UtilizedBones = [];
            return;
        }

        UtilizedBones = [.. utilizedBones];
        PopulateSkinningBuffers(boneToIndexTable, weightsPerVertex);
    }

    private void ClearSkinningBuffers()
    {
        Buffers.RemoveBuffer(ECommonBufferType.BoneMatrixOffset.ToString());
        Buffers.RemoveBuffer(ECommonBufferType.BoneMatrixCount.ToString());
        Buffers.RemoveBuffer($"{ECommonBufferType.BoneMatrixIndices}Buffer");
        Buffers.RemoveBuffer($"{ECommonBufferType.BoneMatrixWeights}Buffer");

        BoneWeightOffsets = null;
        BoneWeightCounts = null;
        BoneWeightIndices = null;
        BoneWeightValues = null;
        _maxWeightCount = 0;
    }

    private void InitializeSkinning(
        Mesh mesh,
        Dictionary<string, List<SceneNode>> nodeCache,
        Dictionary<int, List<int>>? faceRemap,
        Vertex[] sourceList)
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope(null);

        CollectBoneWeights(
            mesh,
            nodeCache,
            faceRemap,
            sourceList,
            out int boneCount,
            out var weightsPerVertex,
            out var boneToIndexTable);

        if (weightsPerVertex is { Length: > 0 } && boneCount > 0)
            PopulateSkinningBuffers(boneToIndexTable, weightsPerVertex);
    }

    private void CollectBoneWeights(
        Mesh mesh,
        Dictionary<string, List<SceneNode>> nodeCache,
        Dictionary<int, List<int>>? faceRemap,
        Vertex[] sourceList,
        out int boneCount,
        out Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[]? weightsPerVertex,
        out Dictionary<TransformBase, int> boneToIndexTable)
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope(null);

        boneCount = RuntimeRenderingHostServices.Current.AllowSkinning ? mesh.BoneCount : 0;
        int vertexCount = VertexCount;
        var weightsPerVertex2 = new Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[vertexCount];

        var concurrentInvBindMatrices = new ConcurrentDictionary<TransformBase, Matrix4x4>();
        var concurrentBoneToIndexTable = new ConcurrentDictionary<TransformBase, int>();
        _maxWeightCount = 0;
        int boneIndex = 0;

        object[] vertexLocks = new object[vertexCount];
        for (int i = 0; i < vertexCount; i++)
            vertexLocks[i] = new object();

        Parallel.For(0, boneCount, i =>
        {
            Bone bone = mesh.Bones[i];
            if (!bone.HasVertexWeights)
                return;

            string name = bone.Name;
            if (!TryGetTransform(nodeCache, name, out var transform) || transform is null)
            {
                RuntimeRenderingHostServices.Current.LogOutput($"Bone {name} has no corresponding node in the heirarchy.");
                return;
            }

            Matrix4x4 invBind = transform.InverseBindMatrix;
            concurrentInvBindMatrices[transform] = invBind;

            int weightCount = bone.VertexWeightCount;
            for (int j = 0; j < weightCount; j++)
            {
                var vw = bone.VertexWeights[j];
                int origId = vw.VertexID;
                float weight = vw.Weight;
                List<int> targetIndices = (faceRemap != null && faceRemap.TryGetValue(origId, out var remapped))
                    ? remapped
                    : [origId];

                foreach (int newId in targetIndices)
                {
                    lock (vertexLocks[newId])
                    {
                        var wpv = weightsPerVertex2[newId];
                        wpv ??= [];
                        weightsPerVertex2[newId] = wpv;

                        if (!wpv.TryGetValue(transform, out var existing))
                            wpv[transform] = (weight, invBind);
                        else if (existing.weight != weight)
                        {
                            wpv[transform] = ((existing.weight + weight) * 0.5f, existing.invBindMatrix);
                            RuntimeRenderingHostServices.Current.LogOutput($"Vertex {newId} has multiple weights for bone {name}.");
                        }
                        if (sourceList[newId].Weights == null)
                            sourceList[newId].Weights = wpv;

                        int origMax, currentMax;
                        do
                        {
                            origMax = _maxWeightCount;
                            currentMax = Math.Max(origMax, wpv.Count);
                        }
                        while (Interlocked.CompareExchange(ref _maxWeightCount, currentMax, origMax) != origMax);
                    }
                }
            }

            int idx = Interlocked.Increment(ref boneIndex) - 1;
            concurrentBoneToIndexTable.TryAdd(transform, idx);
        });

        boneToIndexTable = concurrentBoneToIndexTable.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        var utilizedBones = new (TransformBase tfm, Matrix4x4 invBindWorldMtx)[boneToIndexTable.Count];
        foreach (var pair in boneToIndexTable)
            if (concurrentInvBindMatrices.TryGetValue(pair.Key, out var cachedInvBind))
                utilizedBones[pair.Value] = (pair.Key, cachedInvBind);
        UtilizedBones = utilizedBones;

        weightsPerVertex = weightsPerVertex2;
    }

    private void PopulateSkinningBuffers(
        Dictionary<TransformBase, int> boneToIndexTable,
        Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
    {
        uint vertCount = (uint)VertexCount;
        bool intVarType = RuntimeRenderingHostServices.Current.UseIntegerUniformsInShaders;
        var indexVarType = intVarType ? EComponentType.Int : EComponentType.Float;

        bool optimizeTo4Weights = RuntimeRenderingHostServices.Current.OptimizeSkinningTo4Weights ||
                                  (RuntimeRenderingHostServices.Current.OptimizeSkinningWeightsIfPossible && MaxWeightCount <= 4);

        if (optimizeTo4Weights)
        {
            BoneWeightOffsets = new XRDataBuffer(ECommonBufferType.BoneMatrixOffset.ToString(), EBufferTarget.ArrayBuffer, vertCount, indexVarType, 4, false, intVarType);
            BoneWeightCounts = new XRDataBuffer(ECommonBufferType.BoneMatrixCount.ToString(), EBufferTarget.ArrayBuffer, vertCount, EComponentType.Float, 4, false, false);
        }
        else
        {
            BoneWeightOffsets = new XRDataBuffer(ECommonBufferType.BoneMatrixOffset.ToString(), EBufferTarget.ArrayBuffer, vertCount, indexVarType, 1, false, intVarType);
            BoneWeightCounts = new XRDataBuffer(ECommonBufferType.BoneMatrixCount.ToString(), EBufferTarget.ArrayBuffer, vertCount, indexVarType, 1, false, intVarType);

            BoneWeightIndices = new XRDataBuffer($"{ECommonBufferType.BoneMatrixIndices}Buffer", EBufferTarget.ShaderStorageBuffer, true);
            Buffers.Add(BoneWeightIndices.AttributeName, BoneWeightIndices);
            BoneWeightValues = new XRDataBuffer($"{ECommonBufferType.BoneMatrixWeights}Buffer", EBufferTarget.ShaderStorageBuffer, false);
            Buffers.Add(BoneWeightValues.AttributeName, BoneWeightValues);
        }

        Buffers.Add(BoneWeightOffsets.AttributeName, BoneWeightOffsets);
        Buffers.Add(BoneWeightCounts.AttributeName, BoneWeightCounts);

        PopulateWeightBuffers(boneToIndexTable, weightsPerVertex, optimizeTo4Weights);
    }

    private void PopulateWeightBuffers(
        Dictionary<TransformBase, int> boneToIndexTable,
        Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex,
        bool optimizeTo4Weights)
    {
        _maxWeightCount = 0;
        if (optimizeTo4Weights)
            PopulateOptWeightsParallel(boneToIndexTable, weightsPerVertex);
        else
            PopulateUnoptWeightsParallel(boneToIndexTable, weightsPerVertex);
    }

    private unsafe void PopulateUnoptWeightsParallel(
        Dictionary<TransformBase, int> boneToIndexTable,
        Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope(null);

        int vertexCount = VertexCount;
        uint[] counts = new uint[vertexCount];
        List<int>[] localBoneIndices = new List<int>[vertexCount];
        List<float>[] localBoneWeights = new List<float>[vertexCount];
        bool intVarType = RuntimeRenderingHostServices.Current.UseIntegerUniformsInShaders;

        Parallel.For(0, vertexCount, vi =>
        {
            var group = weightsPerVertex[vi];
            if (group == null)
            {
                counts[vi] = 0;
                localBoneIndices[vi] = [];
                localBoneWeights[vi] = [];
                return;
            }

            VertexWeightGroup.Normalize(group);
            int count = group.Count;
            counts[vi] = (uint)count;
            var indicesList = new List<int>(count);
            var weightsList = new List<float>(count);
            foreach (var pair in group)
            {
                int bIndex = boneToIndexTable[pair.Key];
                float bWeight = pair.Value.weight;
                if (bIndex < 0)
                {
                    bIndex = -1;
                    bWeight = 0f;
                }
                indicesList.Add(bIndex + 1);
                weightsList.Add(bWeight);
            }
            localBoneIndices[vi] = indicesList;
            localBoneWeights[vi] = weightsList;
            Interlocked.Exchange(ref _maxWeightCount, Math.Max(MaxWeightCount, count));
        });

        uint offset = 0;
        var offsetsBuf = BoneWeightOffsets!;
        var countsBuf = BoneWeightCounts!;
        for (int vi = 0; vi < vertexCount; vi++)
        {
            uint count = counts[vi];
            if (intVarType)
            {
                ((uint*)offsetsBuf.Address)[vi] = offset;
                ((uint*)countsBuf.Address)[vi] = count;
            }
            else
            {
                ((float*)offsetsBuf.Address)[vi] = offset;
                ((float*)countsBuf.Address)[vi] = count;
            }
            offset += count;
        }

        BoneWeightIndices!.Allocate<int>(offset);
        BoneWeightValues!.Allocate<int>(offset);
        offset = 0;
        for (int i = 0; i < vertexCount; i++)
        {
            uint count = counts[i];
            if (intVarType)
            {
                for (int j = 0; j < count; j++)
                {
                    ((int*)BoneWeightIndices.Address)[offset] = localBoneIndices[i][j];
                    ((float*)BoneWeightValues.Address)[offset] = localBoneWeights[i][j];
                    offset++;
                }
            }
            else
            {
                for (int j = 0; j < count; j++)
                {
                    ((float*)BoneWeightIndices.Address)[offset] = localBoneIndices[i][j];
                    ((float*)BoneWeightValues.Address)[offset] = localBoneWeights[i][j];
                    offset++;
                }
            }
        }
    }

    private unsafe void PopulateOptWeightsParallel(
        Dictionary<TransformBase, int> boneToIndexTable,
        Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope(null);

        int vertexCount = VertexCount;
        var weightOffsets = BoneWeightOffsets!;
        var weightCounts = BoneWeightCounts!;

        Parallel.For(0, vertexCount, vi =>
        {
            var group = weightsPerVertex[vi];
            int* idxData = (int*)weightOffsets.Address;
            float* wtData = (float*)weightCounts.Address;
            int baseIndex = vi * 4;

            if (group == null)
            {
                for (int k = 0; k < 4; k++)
                {
                    idxData[baseIndex + k] = 0;
                    wtData[baseIndex + k] = 0f;
                }
                return;
            }

            VertexWeightGroup.Optimize(group, 4);
            int count = group.Count;
            int current, computed;
            do
            {
                current = _maxWeightCount;
                computed = Math.Max(current, count);
            } while (Interlocked.CompareExchange(ref _maxWeightCount, computed, current) != current);

            int i = 0;
            foreach (var pair in group)
            {
                int bIndex = boneToIndexTable[pair.Key];
                float bWeight = pair.Value.weight;
                if (bIndex < 0)
                {
                    bIndex = -1;
                    bWeight = 0f;
                }
                idxData[baseIndex + i] = bIndex + 1;
                wtData[baseIndex + i] = bWeight;
                i++;
            }
            while (i < 4)
            {
                idxData[baseIndex + i] = 0;
                wtData[baseIndex + i] = 0f;
                i++;
            }
        });
    }

    private static unsafe bool TryGetTransform(Dictionary<string, List<SceneNode>> nodeCache, string name, out TransformBase? transform)
    {
        if (!nodeCache.TryGetValue(name, out var matches) || matches is null || matches.Count == 0)
        {
            RuntimeRenderingHostServices.Current.LogOutput($"{name} has no corresponding node in the heirarchy.");
            transform = null;
            return false;
        }
        transform = matches[0].Transform;
        return true;
    }
}
