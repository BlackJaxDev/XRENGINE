using Assimp;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using XREngine.Scene.Transforms;
using XREngine.Scene;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    public void EnsureComputeSkinningBuffers()
    {
        if (!HasSkinning)
            return;

        if (HasCanonicalComputeSkinningBuffers())
            return;

        if (CanRebuildSkinningBuffersFromVertices())
            RebuildSkinningBuffersFromVertices();

        if (!HasCanonicalComputeSkinningBuffers())
            throw new InvalidOperationException(BuildInvalidComputeSkinningMessage(GetComputeSkinningValidationError()));
    }

    /// <summary>
    /// Ensures the mesh's <see cref="UtilizedBones"/> ordering is finalized to the same order the
    /// per-vertex compressed core bone indices are (or will be) packed against.
    /// <para>
    /// <see cref="RebuildSkinningBuffersFromVertices"/> can reorder and extend <see cref="UtilizedBones"/>
    /// while packing the core indices. If a renderer builds its bone palette from the pre-rebuild
    /// ordering and the rebuild happens afterwards (e.g. lazily during the compute pre-pass), the
    /// per-vertex indices will reference the wrong palette slots, corrupting skinning for that mesh.
    /// Callers that read <see cref="UtilizedBones"/> to build a palette should call this first.
    /// </para>
    /// Unlike <see cref="EnsureComputeSkinningBuffers"/>, this never throws: meshes that cannot be
    /// rebuilt (no source vertices) are assumed to already carry canonical, cooked buffers.
    /// </summary>
    public void EnsureSkinningBoneOrderFinalized()
    {
        if (!HasSkinning)
            return;

        if (HasCanonicalComputeSkinningBuffers())
            return;

        if (CanRebuildSkinningBuffersFromVertices())
            RebuildSkinningBuffersFromVertices();
    }

    private bool CanRebuildSkinningBuffersFromVertices()
        => Vertices is { Length: > 0 } vertices &&
           vertices.Length == VertexCount &&
           UtilizedBones is { Length: > 0 };

    private bool HasCanonicalComputeSkinningBuffers()
    {
        if (!HasSkinning)
            return false;
        if (VertexCount <= 0)
            return false;
        if (SkinningInfluenceEncoding is not (SkinningInfluenceEncoding.Core4Spill or SkinningInfluenceEncoding.Core4NoSpill))
            return false;
        if (SkinningCoreIndexFormat is not (SkinningCoreIndexFormat.Core4x8 or SkinningCoreIndexFormat.Core4x16))
            return false;
        if (!IsCanonicalCoreIndexBuffer(BoneInfluenceCoreIndices))
            return false;
        if (!IsCanonicalCoreWeightBuffer(BoneInfluenceCoreWeights))
            return false;

        if (!HasSpillInfluences)
            return SkinningInfluenceEncoding == SkinningInfluenceEncoding.Core4NoSpill;

        return SkinningInfluenceEncoding == SkinningInfluenceEncoding.Core4Spill &&
               IsCanonicalSpillHeaderBuffer(BoneInfluenceSpillHeaders) &&
               IsCanonicalSpillEntryBuffer(BoneInfluenceSpillEntries);
    }

    private string GetComputeSkinningValidationError()
    {
        if (!HasSkinning)
            return "mesh has no utilized bones";
        if (VertexCount <= 0)
            return "mesh has no vertices";
        if (SkinningInfluenceEncoding is not (SkinningInfluenceEncoding.Core4Spill or SkinningInfluenceEncoding.Core4NoSpill))
            return $"influence encoding is '{SkinningInfluenceEncoding}'";
        if (SkinningCoreIndexFormat is not (SkinningCoreIndexFormat.Core4x8 or SkinningCoreIndexFormat.Core4x16))
            return $"core index format is '{SkinningCoreIndexFormat}'";
        if (!IsCanonicalCoreIndexBuffer(BoneInfluenceCoreIndices))
            return "core influence index buffer is missing or has an invalid layout";
        if (!IsCanonicalCoreWeightBuffer(BoneInfluenceCoreWeights))
            return "core influence weight buffer is missing or has an invalid layout";
        if (!HasSpillInfluences && SkinningInfluenceEncoding != SkinningInfluenceEncoding.Core4NoSpill)
            return "no-spill mesh is not encoded as Core4NoSpill";
        if (HasSpillInfluences && SkinningInfluenceEncoding != SkinningInfluenceEncoding.Core4Spill)
            return "spill mesh is not encoded as Core4Spill";
        if (HasSpillInfluences && !IsCanonicalSpillHeaderBuffer(BoneInfluenceSpillHeaders))
            return "spill header buffer is missing or has an invalid layout";
        if (HasSpillInfluences && !IsCanonicalSpillEntryBuffer(BoneInfluenceSpillEntries))
            return "spill entry buffer is missing or has an invalid layout";
        return "unknown invalid skinning buffer state";
    }

    private string BuildInvalidComputeSkinningMessage(string reason)
        => $"Skinned mesh '{Name ?? "<unnamed>"}' is not in the required Core4 compute-skinning runtime format ({reason}). Recook or reimport the source mesh.";

    private bool IsCanonicalCoreIndexBuffer(XRDataBuffer? buffer)
    {
        EComponentType expectedType = SkinningCoreIndexFormat == SkinningCoreIndexFormat.Core4x8
            ? EComponentType.Byte
            : EComponentType.UShort;

        return buffer is not null &&
               buffer.ElementCount >= (uint)VertexCount &&
               buffer.ComponentType == expectedType &&
               buffer.ComponentCount == 4u &&
               buffer.Integral;
    }

    private bool IsCanonicalCoreWeightBuffer(XRDataBuffer? buffer)
        => buffer is not null &&
           buffer.ElementCount >= (uint)VertexCount &&
           buffer.ComponentType == EComponentType.Byte &&
           buffer.ComponentCount == 4u &&
           buffer.Normalize &&
           !buffer.Integral;

    private bool IsCanonicalSpillHeaderBuffer(XRDataBuffer? buffer)
        => buffer is not null &&
           buffer.ElementCount >= (uint)VertexCount &&
           buffer.ComponentType == EComponentType.UInt &&
           buffer.ComponentCount == 1u &&
           buffer.Integral;

    private static bool IsCanonicalSpillEntryBuffer(XRDataBuffer? buffer)
        => buffer is not null &&
           buffer.ComponentType == EComponentType.UInt &&
           buffer.ComponentCount == 1u &&
           buffer.Integral;

    public void RebuildSkinningBuffersFromVertices()
    {
        ClearSkinningBuffers();
        SkinningShaderConvention = ESkinningShaderConvention.ExplicitRowMajorRowVector;

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

    public bool NeedsSerializedTransformRebind()
    {
        if (!HasSkinning)
            return false;

        for (int i = 0; i < UtilizedBones.Length; i++)
        {
            TransformBase bone = UtilizedBones[i].tfm;
            if (bone.SceneNode is null && bone.EffectiveSerializedReferenceId != Guid.Empty)
                return true;
        }

        return false;
    }

    public bool NeedsSerializedTransformRebind(TransformBase searchRoot)
    {
        ArgumentNullException.ThrowIfNull(searchRoot);

        if (!HasSkinning)
            return false;

        for (int i = 0; i < UtilizedBones.Length; i++)
        {
            TransformBase bone = UtilizedBones[i].tfm;
            Guid referenceId = bone.EffectiveSerializedReferenceId;
            if (referenceId == Guid.Empty)
                continue;

            if (IsSelfOrDescendantOf(searchRoot, bone))
                continue;

            TransformBase? resolved = searchRoot.FindSelfOrDescendantBySerializedReferenceId(referenceId);
            if (resolved is not null && !ReferenceEquals(resolved, bone))
                return true;
        }

        return false;
    }

    public bool RebindSerializedTransformReferences(TransformBase searchRoot, bool remapVertexWeights = true)
    {
        ArgumentNullException.ThrowIfNull(searchRoot);

        if (!HasSkinning)
            return false;

        Dictionary<TransformBase, TransformBase>? remap = null;
        bool changed = false;

        var reboundBones = new (TransformBase tfm, Matrix4x4 invBindWorldMtx)[UtilizedBones.Length];
        for (int i = 0; i < UtilizedBones.Length; i++)
        {
            (TransformBase sourceBone, Matrix4x4 inverseBind) = UtilizedBones[i];
            TransformBase resolvedBone = ResolveSerializedBoneReference(searchRoot, sourceBone);
            reboundBones[i] = (resolvedBone, inverseBind);

            if (ReferenceEquals(sourceBone, resolvedBone))
                continue;

            changed = true;
            remap ??= new Dictionary<TransformBase, TransformBase>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            remap[sourceBone] = resolvedBone;
        }

        if (!changed || remap is null)
            return false;

        UtilizedBones = reboundBones;
        if (remapVertexWeights)
        {
            RemapVertexWeights(remap);
            RuntimeBoneReferenceRemap = null;
        }
        else
        {
            RuntimeBoneReferenceRemap = remap;
        }

        return true;
    }

    private static TransformBase ResolveSerializedBoneReference(TransformBase searchRoot, TransformBase sourceBone)
    {
        if (IsSelfOrDescendantOf(searchRoot, sourceBone))
            return sourceBone;

        Guid referenceId = sourceBone.EffectiveSerializedReferenceId;
        if (referenceId == Guid.Empty)
            return sourceBone;

        return searchRoot.FindSelfOrDescendantBySerializedReferenceId(referenceId) ?? sourceBone;
    }

    private static bool IsSelfOrDescendantOf(TransformBase root, TransformBase candidate)
    {
        for (TransformBase? current = candidate; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, root))
                return true;
        }

        return false;
    }

    private void RemapVertexWeights(IReadOnlyDictionary<TransformBase, TransformBase> remap)
    {
        if (Vertices is not { Length: > 0 })
            return;

        for (int vertexIndex = 0; vertexIndex < Vertices.Length; vertexIndex++)
        {
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights = Vertices[vertexIndex].Weights;
            if (weights is null || weights.Count == 0)
                continue;

            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? remapped = null;
            foreach (var pair in weights)
            {
                if (!remap.ContainsKey(pair.Key))
                    continue;

                remapped = new Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>(weights.Count, System.Collections.Generic.ReferenceEqualityComparer.Instance);
                break;
            }

            if (remapped is null)
                continue;

            foreach (var pair in weights)
            {
                TransformBase bone = remap.TryGetValue(pair.Key, out TransformBase? reboundBone)
                    ? reboundBone
                    : pair.Key;

                if (remapped.TryGetValue(bone, out (float weight, Matrix4x4 bindInvWorldMatrix) existing))
                {
                    remapped[bone] = (existing.weight + pair.Value.weight, pair.Value.bindInvWorldMatrix);
                }
                else
                {
                    remapped.Add(bone, pair.Value);
                }
            }

            Vertices[vertexIndex].Weights = remapped;
        }
    }

    private void ClearSkinningBuffers()
    {
        Buffers.RemoveBuffer(ECommonBufferType.BoneInfluenceCoreIndices.ToString());
        Buffers.RemoveBuffer(ECommonBufferType.BoneInfluenceCoreWeights.ToString());
        Buffers.RemoveBuffer(ECommonBufferType.BoneInfluenceSpillHeaders.ToString());
        Buffers.RemoveBuffer(ECommonBufferType.BoneInfluenceSpillEntries.ToString());

        BoneInfluenceCoreIndices = null;
        BoneInfluenceCoreWeights = null;
        BoneInfluenceSpillHeaders = null;
        BoneInfluenceSpillEntries = null;
        SkinningInfluenceEncoding = SkinningInfluenceEncoding.None;
        SkinningCoreIndexFormat = SkinningCoreIndexFormat.None;
        HasSpillInfluences = false;
        MaxSpillInfluenceCount = 0;
        _maxWeightCount = 0;
    }

    private void InitializeSkinning(
        Mesh mesh,
        Dictionary<string, List<SceneNode>> nodeCache,
        Dictionary<int, List<int>>? faceRemap,
        Vertex[] sourceList)
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope();

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
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope();

        boneCount = mesh.BoneCount;
        int vertexCount = VertexCount;
        var weightsPerVertex2 = new Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[vertexCount];

        var concurrentInvBindMatrices = new ConcurrentDictionary<TransformBase, Matrix4x4>();
        var concurrentBoneToIndexTable = new ConcurrentDictionary<TransformBase, int>();
        _maxWeightCount = 0;
        int boneIndex = 0;

        // Mesh import is already on an async worker; keep bone collection sequential so the
        // fan-out can't starve Update / FixedUpdate / CollectVisible threads.
        for (int i = 0; i < boneCount; i++)
        {
            Bone bone = mesh.Bones[i];
            if (!bone.HasVertexWeights)
                continue;

            string name = bone.Name;
            if (!TryGetTransform(nodeCache, name, out var transform) || transform is null)
            {
                Debug.Meshes($"Bone {name} has no corresponding node in the heirarchy.");
                continue;
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
                    var wpv = weightsPerVertex2[newId];
                    wpv ??= [];
                    weightsPerVertex2[newId] = wpv;

                    if (!wpv.TryGetValue(transform, out var existing))
                        wpv[transform] = (weight, invBind);
                    else if (existing.weight != weight)
                    {
                        wpv[transform] = ((existing.weight + weight) * 0.5f, existing.invBindMatrix);
                        Debug.Meshes($"Vertex {newId} has multiple weights for bone {name}.");
                    }
                    if (sourceList[newId].Weights == null)
                        sourceList[newId].Weights = wpv;

                    if (wpv.Count > _maxWeightCount)
                        _maxWeightCount = wpv.Count;
                }
            }

            int idx = Interlocked.Increment(ref boneIndex) - 1;
            concurrentBoneToIndexTable.TryAdd(transform, idx);
        }

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
        int utilizedBoneCount = UtilizedBones.Length;
        if (utilizedBoneCount > ushort.MaxValue)
            throw new NotSupportedException($"Compressed skinning supports at most {ushort.MaxValue} utilized bones; mesh '{Name}' uses {utilizedBoneCount}.");

        SkinningInfluenceEncoding = SkinningInfluenceEncoding.Core4NoSpill;
        SkinningCoreIndexFormat = utilizedBoneCount <= byte.MaxValue
            ? SkinningCoreIndexFormat.Core4x8
            : SkinningCoreIndexFormat.Core4x16;
        HasSpillInfluences = false;
        MaxSpillInfluenceCount = 0;

        EComponentType coreIndexType = SkinningCoreIndexFormat == SkinningCoreIndexFormat.Core4x8
            ? EComponentType.Byte
            : EComponentType.UShort;

        BoneInfluenceCoreIndices = new XRDataBuffer(ECommonBufferType.BoneInfluenceCoreIndices.ToString(), EBufferTarget.ArrayBuffer, vertCount, coreIndexType, 4, false, true)
        {
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false
        };
        BoneInfluenceCoreWeights = new XRDataBuffer(ECommonBufferType.BoneInfluenceCoreWeights.ToString(), EBufferTarget.ArrayBuffer, vertCount, EComponentType.Byte, 4, true, false)
        {
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false
        };
        PopulateWeightBuffers(boneToIndexTable, weightsPerVertex);

        Buffers.Add(BoneInfluenceCoreIndices.AttributeName, BoneInfluenceCoreIndices);
        Buffers.Add(BoneInfluenceCoreWeights.AttributeName, BoneInfluenceCoreWeights);
        if (BoneInfluenceSpillHeaders is not null)
            Buffers.Add(BoneInfluenceSpillHeaders.AttributeName, BoneInfluenceSpillHeaders);
        if (BoneInfluenceSpillEntries is not null)
            Buffers.Add(BoneInfluenceSpillEntries.AttributeName, BoneInfluenceSpillEntries);

        RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
            0L,
            0L,
            coreInfluenceBytes: (long)(BoneInfluenceCoreIndices.Length + BoneInfluenceCoreWeights.Length),
            spillHeaderBytes: (long)(BoneInfluenceSpillHeaders?.Length ?? 0u),
            spillEntryBytes: (long)(BoneInfluenceSpillEntries?.Length ?? 0u));
    }

    private void PopulateWeightBuffers(
        Dictionary<TransformBase, int> boneToIndexTable,
        Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
    {
        _maxWeightCount = 0;
        PopulateCompressedWeights(boneToIndexTable, weightsPerVertex);
    }

    private unsafe void PopulateCompressedWeights(
        Dictionary<TransformBase, int> boneToIndexTable,
        Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)>?[] weightsPerVertex)
    {
        using var _ = RuntimeRenderingHostServices.Current.StartProfileScope();

        int vertexCount = VertexCount;
        var coreIndices = BoneInfluenceCoreIndices!;
        var coreWeights = BoneInfluenceCoreWeights!;
        byte* coreIndex8 = SkinningCoreIndexFormat == SkinningCoreIndexFormat.Core4x8 ? (byte*)coreIndices.Address : null;
        ushort* coreIndex16 = SkinningCoreIndexFormat == SkinningCoreIndexFormat.Core4x16 ? (ushort*)coreIndices.Address : null;
        byte* coreWeightData = (byte*)coreWeights.Address;
        uint[] spillHeaders = new uint[vertexCount];
        List<uint> spillEntries = [];

        // Diagnostic: vertices that carry skin weights but pack to zero usable core
        // influence collapse to the origin on the GPU (transformSkinPosition accumulates
        // nothing), which renders as missing/degenerate triangles. Counting them here
        // distinguishes a CPU packing-drop from a GPU bind/decode fault.
        int weightedVerticesDroppedToZeroInfluence = 0;
        int firstDroppedVertexIndex = -1;

        for (int vi = 0; vi < vertexCount; vi++)
        {
            var group = weightsPerVertex[vi];
            int coreBase = vi * 4;
            if (group is null || group.Count == 0)
            {
                for (int k = 0; k < 4; k++)
                {
                    if (coreIndex8 is not null)
                        coreIndex8[coreBase + k] = 0;
                    else
                        coreIndex16![coreBase + k] = 0;
                    coreWeightData[coreBase + k] = 0;
                }
                spillHeaders[vi] = 0u;
                continue;
            }

            List<PackedSkinningInfluence> influences = BuildPackedInfluences(boneToIndexTable, group, out int logicalInfluenceCount);
            _maxWeightCount = Math.Max(_maxWeightCount, logicalInfluenceCount);

            if (influences.Count == 0)
            {
                weightedVerticesDroppedToZeroInfluence++;
                if (firstDroppedVertexIndex < 0)
                    firstDroppedVertexIndex = vi;
            }

            int i = 0;
            for (; i < influences.Count && i < 4; i++)
            {
                PackedSkinningInfluence influence = influences[i];
                if (coreIndex8 is not null)
                    coreIndex8[coreBase + i] = checked((byte)influence.BoneIndexPlusOne);
                else
                    coreIndex16![coreBase + i] = influence.BoneIndexPlusOne;
                coreWeightData[coreBase + i] = influence.WeightUNorm8;
            }

            while (i < 4)
            {
                if (coreIndex8 is not null)
                    coreIndex8[coreBase + i] = 0;
                else
                    coreIndex16![coreBase + i] = 0;
                coreWeightData[coreBase + i] = 0;
                i++;
            }

            int extraCount = Math.Max(0, influences.Count - 4);
            if (extraCount == 0)
            {
                spillHeaders[vi] = 0u;
                continue;
            }

            if (extraCount > byte.MaxValue)
                throw new NotSupportedException($"Compressed skinning supports at most {byte.MaxValue} spill influences per vertex; mesh '{Name}' vertex {vi} has {extraCount}.");

            uint spillOffset = (uint)spillEntries.Count;
            if (spillOffset > 0x00FF_FFFFu)
                throw new NotSupportedException($"Compressed skinning spill list for mesh '{Name}' exceeds the 24-bit offset limit.");

            spillHeaders[vi] = spillOffset | ((uint)extraCount << 24);
            HasSpillInfluences = true;
            MaxSpillInfluenceCount = Math.Max(MaxSpillInfluenceCount, extraCount);

            for (int spillIndex = 4; spillIndex < influences.Count; spillIndex++)
            {
                PackedSkinningInfluence influence = influences[spillIndex];
                spillEntries.Add(influence.BoneIndexPlusOne | ((uint)influence.WeightUNorm8 << 16));
            }
        }

        if (weightedVerticesDroppedToZeroInfluence > 0)
        {
            Debug.LogWarning(
                $"[Skinning] Mesh '{Name ?? "<unnamed>"}': {weightedVerticesDroppedToZeroInfluence}/{vertexCount} weighted vertices packed to ZERO core influence " +
                $"(first at vertex {firstDroppedVertexIndex}). These collapse to the origin on the GPU (missing/degenerate triangles). " +
                $"UtilizedBones={UtilizedBones?.Length ?? 0}. Cause is CPU packing: referenced bones absent from the bone index table or all weights non-positive.");
        }

        if (!HasSpillInfluences)
        {
            BoneInfluenceSpillHeaders = null;
            BoneInfluenceSpillEntries = null;
            SkinningInfluenceEncoding = SkinningInfluenceEncoding.Core4NoSpill;
            return;
        }

        SkinningInfluenceEncoding = SkinningInfluenceEncoding.Core4Spill;
        BoneInfluenceSpillHeaders = new XRDataBuffer(ECommonBufferType.BoneInfluenceSpillHeaders.ToString(), EBufferTarget.ShaderStorageBuffer, (uint)vertexCount, EComponentType.UInt, 1, false, true)
        {
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false
        };

        uint* spillHeaderData = (uint*)BoneInfluenceSpillHeaders.Address;
        for (int i = 0; i < vertexCount; i++)
            spillHeaderData[i] = spillHeaders[i];

        uint spillElementCount = (uint)spillEntries.Count;
        BoneInfluenceSpillEntries = new XRDataBuffer(ECommonBufferType.BoneInfluenceSpillEntries.ToString(), EBufferTarget.ShaderStorageBuffer, spillElementCount, EComponentType.UInt, 1, false, true)
        {
            Usage = EBufferUsage.StaticDraw,
            DisposeOnPush = false
        };

        uint* spillEntryData = (uint*)BoneInfluenceSpillEntries.Address;
        for (int i = 0; i < spillEntries.Count; i++)
            spillEntryData[i] = spillEntries[i];
    }

    private static List<PackedSkinningInfluence> BuildPackedInfluences(
        Dictionary<TransformBase, int> boneToIndexTable,
        Dictionary<TransformBase, (float weight, Matrix4x4 invBindMatrix)> group,
        out int logicalInfluenceCount)
    {
        List<LogicalSkinningInfluence> logical = new(group.Count);
        float totalWeight = 0.0f;
        foreach (var pair in group)
        {
            float weight = pair.Value.weight;
            if (weight <= 0.0f || !boneToIndexTable.TryGetValue(pair.Key, out int boneIndex) || boneIndex < 0)
                continue;

            logical.Add(new LogicalSkinningInfluence(boneIndex, weight));
            totalWeight += weight;
        }

        logicalInfluenceCount = logical.Count;
        if (logical.Count == 0 || totalWeight <= 0.0f)
            return [];

        logical.Sort(static (left, right) =>
        {
            int weightOrder = right.Weight.CompareTo(left.Weight);
            return weightOrder != 0 ? weightOrder : left.BoneIndex.CompareTo(right.BoneIndex);
        });

        List<PackedSkinningInfluence> packed = new(logical.Count);
        for (int i = 0; i < logical.Count; i++)
        {
            LogicalSkinningInfluence influence = logical[i];
            int quantized = (int)MathF.Round(influence.Weight / totalWeight * byte.MaxValue, MidpointRounding.AwayFromZero);
            if (quantized <= 0)
                continue;

            packed.Add(new PackedSkinningInfluence(
                checked((ushort)(influence.BoneIndex + 1)),
                (byte)Math.Min(byte.MaxValue, quantized)));
        }

        if (packed.Count == 0)
            packed.Add(new PackedSkinningInfluence(checked((ushort)(logical[0].BoneIndex + 1)), byte.MaxValue));

        NormalizePackedWeights(packed);
        return packed;
    }

    private static void NormalizePackedWeights(List<PackedSkinningInfluence> packed)
    {
        int sum = 0;
        for (int i = 0; i < packed.Count; i++)
            sum += packed[i].WeightUNorm8;

        while (sum > byte.MaxValue)
        {
            int excess = sum - byte.MaxValue;
            PackedSkinningInfluence largestInfluence = packed[0];
            if (largestInfluence.WeightUNorm8 > excess)
            {
                packed[0] = largestInfluence with { WeightUNorm8 = (byte)(largestInfluence.WeightUNorm8 - excess) };
                return;
            }

            if (packed.Count <= 1)
            {
                packed[0] = largestInfluence with { WeightUNorm8 = byte.MaxValue };
                return;
            }

            PackedSkinningInfluence tail = packed[^1];
            sum -= tail.WeightUNorm8;
            packed.RemoveAt(packed.Count - 1);
        }

        int delta = byte.MaxValue - sum;
        if (delta == 0)
            return;

        PackedSkinningInfluence largest = packed[0];
        int adjusted = Math.Clamp(largest.WeightUNorm8 + delta, 1, byte.MaxValue);
        packed[0] = largest with { WeightUNorm8 = (byte)adjusted };
    }

    private readonly record struct LogicalSkinningInfluence(int BoneIndex, float Weight);
    private readonly record struct PackedSkinningInfluence(ushort BoneIndexPlusOne, byte WeightUNorm8);

    private static unsafe bool TryGetTransform(Dictionary<string, List<SceneNode>> nodeCache, string name, out TransformBase? transform)
    {
        if (!nodeCache.TryGetValue(name, out var matches) || matches is null || matches.Count == 0)
        {
            Debug.Meshes($"{name} has no corresponding node in the heirarchy.");
            transform = null;
            return false;
        }
        transform = matches[0].Transform;
        return true;
    }
}
