using System.Collections.Generic;
using System.Numerics;
using XREngine.Scene.Transforms;
using XREngine.Scene;
using XREngine.Data.Core;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public partial class XRMesh
{
    private static readonly string[] SkinningBufferKeys =
    [
        ECommonBufferType.BoneInfluenceCoreIndices.ToString(),
        ECommonBufferType.BoneInfluenceCoreWeights.ToString(),
        ECommonBufferType.BoneInfluenceSpillHeaders.ToString(),
        ECommonBufferType.BoneInfluenceSpillEntries.ToString(),
    ];

    /// <summary>
    /// Validates that a skinned mesh carries the canonical Core4 compute-skinning
    /// buffers. Packed buffers are the mesh's only weight data: they are built
    /// when a mesh is created or rebound (<see cref="RebuildSkinningBuffersFromVertices"/>)
    /// or loaded from cooked data, never lazily from per-vertex objects.
    /// </summary>
    public void EnsureComputeSkinningBuffers()
    {
        if (!HasSkinning)
            return;

        if (HasCanonicalComputeSkinningBuffers())
            return;

        throw new InvalidOperationException(BuildInvalidComputeSkinningMessage(GetComputeSkinningValidationError()));
    }

    /// <summary>
    /// Kept for palette builders that read <see cref="UtilizedBones"/>: the
    /// ordering is final once the packed buffers exist, because packing happens
    /// eagerly with the buffers' publication.
    /// </summary>
    public void EnsureSkinningBoneOrderFinalized()
        => _ = GetSkinningBoneOrderForPreparation();

    /// <summary>
    /// Returns the bone ordering the canonical buffers index. A bone rebind
    /// replaces <see cref="UtilizedBones"/> in place of the packed indices, so
    /// the property, not the published buffer state, is authoritative.
    /// </summary>
    internal (TransformBase tfm, Matrix4x4 invBindWorldMtx)[] GetSkinningBoneOrderForPreparation()
        => UtilizedBones;

    private bool HasCanonicalComputeSkinningBuffers()
    {
        XRMeshSkinningBufferState state = GetSkinningBufferStateSnapshot();
        return state.UtilizedBones.Length > 0 && HasCanonicalComputeSkinningBuffers(state);
    }

    private bool HasCanonicalComputeSkinningBuffers(XRMeshSkinningBufferState state)
    {
        if (VertexCount <= 0)
            return false;
        if (state.InfluenceEncoding is not (SkinningInfluenceEncoding.Core4Spill or SkinningInfluenceEncoding.Core4NoSpill))
            return false;
        if (state.CoreIndexFormat is not (SkinningCoreIndexFormat.Core4x8 or SkinningCoreIndexFormat.Core4x16))
            return false;
        if (!IsCanonicalCoreIndexBuffer(state.CoreIndices, state.CoreIndexFormat))
            return false;
        if (!IsCanonicalCoreWeightBuffer(state.CoreWeights))
            return false;

        if (!state.HasSpillInfluences)
            return state.InfluenceEncoding == SkinningInfluenceEncoding.Core4NoSpill;

        return state.InfluenceEncoding == SkinningInfluenceEncoding.Core4Spill &&
               IsCanonicalSpillHeaderBuffer(state.SpillHeaders) &&
               IsCanonicalSpillEntryBuffer(state.SpillEntries);
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
        => IsCanonicalCoreIndexBuffer(buffer, SkinningCoreIndexFormat);

    private bool IsCanonicalCoreIndexBuffer(XRDataBuffer? buffer, SkinningCoreIndexFormat format)
    {
        EComponentType expectedType = format == SkinningCoreIndexFormat.Core4x8
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

    /// <summary>
    /// Packs the bone weights of <paramref name="sourceVertices"/> (one per mesh
    /// vertex, in mesh order) into the canonical Core4 + spill buffers and
    /// publishes them with the final <see cref="UtilizedBones"/> ordering. Bones
    /// referenced by weights but missing from <see cref="UtilizedBones"/> are
    /// appended. The mesh keeps no reference to the source vertices.
    /// </summary>
    public void RebuildSkinningBuffersFromVertices(IReadOnlyList<Vertex> sourceVertices)
    {
        ArgumentNullException.ThrowIfNull(sourceVertices);
        if (sourceVertices.Count != VertexCount)
            throw new ArgumentException(
                $"Skinning source has {sourceVertices.Count} vertices; mesh '{Name}' has {VertexCount}.",
                nameof(sourceVertices));
        lock (_skinningBufferPreparationLock)
            _ = RebuildSkinningBuffersFromVerticesTransactionallyCore(sourceVertices);
    }

    private XRMeshSkinningBufferState RebuildSkinningBuffersFromVerticesTransactionallyCore(
        IReadOnlyList<Vertex> sourceVertices)
    {
        // The palette starts with the current bone table; bones referenced by
        // weights but absent from it are appended in first-use order.
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

        for (int vertexIndex = 0; vertexIndex < sourceVertices.Count; ++vertexIndex)
        {
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights = sourceVertices[vertexIndex].Weights;
            if (weights is null || weights.Count == 0)
                continue;

            foreach (var pair in weights)
            {
                if (boneToIndexTable.ContainsKey(pair.Key))
                    continue;

                boneToIndexTable.Add(pair.Key, utilizedBones.Count);
                utilizedBones.Add((pair.Key, pair.Value.bindInvWorldMatrix));
            }
        }

        // The source dictionaries are read in place; packing never copies them.
        int ReadVertexInfluences(int vertex, List<LogicalSkinningInfluence> destination)
        {
            Dictionary<TransformBase, (float weight, Matrix4x4 bindInvWorldMatrix)>? weights = sourceVertices[vertex].Weights;
            if (weights is null || weights.Count == 0)
                return 0;
            foreach (var pair in weights)
            {
                float weight = pair.Value.weight;
                if (weight > 0.0f && boneToIndexTable.TryGetValue(pair.Key, out int boneIndex) && boneIndex >= 0)
                    destination.Add(new LogicalSkinningInfluence(boneIndex, weight));
            }
            return weights.Count;
        }

        // A bone table without weights still packs: every vertex gets the
        // zero-influence sentinel, so the mesh stays on the skinned path.
        return RebuildSkinningBuffersTransactionallyCore(
            sourceVertices.Count > 0 && boneToIndexTable.Count > 0 ? [.. utilizedBones] : null,
            ReadVertexInfluences);
    }

    /// <summary>
    /// Packs influences supplied per vertex against <paramref name="palette"/>
    /// into a staging generation and swaps it into the mesh's buffers in one
    /// publication. A null palette publishes an unskinned state.
    /// </summary>
    private XRMeshSkinningBufferState RebuildSkinningBuffersTransactionallyCore(
        (TransformBase tfm, Matrix4x4 invBindWorldMtx)[]? palette,
        LogicalInfluenceSource influences)
    {
        BufferCollection targetBuffers = Buffers;
        BufferCollection.PreparedBufferTicket preparationTicket =
            targetBuffers.CapturePreparationTicket(SkinningBufferKeys, includeGeometryRevision: true);
        XRMeshSkinningBufferState previous = CaptureSkinningBufferState();
        XRMeshSkinningBufferState prepared = EmptySkinningBufferState();
        BufferCollection.PreparedBufferBatch? swap = null;
        XRMesh? staging = null;

        try
        {
            if (palette is { Length: > 0 })
            {
                // The temporary owner and its metadata are detached. Replacement
                // buffers created below still join the caller's atomic publication.
                using (XRObjectBase.SuppressObjectCacheRegistration())
                {
                    staging = new XRMesh(deferObjectCachePublication: true)
                    {
                        VertexCount = VertexCount,
                        UtilizedBones = palette,
                        SkinningShaderConvention = ESkinningShaderConvention.ExplicitRowMajorRowVector,
                    };
                }
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                staging.PopulateSkinningBuffers(influences);
                prepared = staging.CaptureSkinningBufferState();
                KeyValuePair<string, XRDataBuffer>[] replacements = CreateSkinningBufferReplacements(prepared);
                publication.Complete(
                    () => swap = CommitSkinningBufferState(
                        prepared,
                        replacements,
                        previous,
                        targetBuffers,
                        preparationTicket),
                    () => RollbackSkinningBufferState(previous, swap, targetBuffers),
                    () => CompleteSkinningBufferReplacement(prepared, swap, targetBuffers));
            }
            else
            {
                using RenderObjectPublicationScope publication = GenericRenderObject.BeginDeferredPublication();
                publication.Complete(
                    () => swap = CommitSkinningBufferState(
                        prepared,
                        [],
                        previous,
                        targetBuffers,
                        preparationTicket),
                    () => RollbackSkinningBufferState(previous, swap, targetBuffers),
                    () => CompleteSkinningBufferReplacement(prepared, swap, targetBuffers));
            }
        }
        finally
        {
            if (staging is not null)
            {
                staging.ApplySkinningBufferState(EmptySkinningBufferState());
                staging.AbortMeshConstruction();
            }
        }

        return prepared;
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

            // The scene hierarchy is cloned through YAML, where bind matrices are runtime-only.
            // Preserve the authoritative bind state carried by the mesh payload before a renderer
            // evaluates the rebound bone. The current local pose can legitimately differ from bind
            // (for example an authored 1.2x secondary-bone scale), so recomputing bind from local TRS
            // would bake that deformation into the bind pose and distort the skinned mesh.
            resolvedBone.BindMatrix = sourceBone.BindMatrix;
            resolvedBone.InverseBindMatrix = Matrix4x4.Invert(
                sourceBone.BindMatrix,
                out Matrix4x4 inverseResolvedBind)
                    ? inverseResolvedBind
                    : Matrix4x4.Identity;

            changed = true;
            remap ??= new Dictionary<TransformBase, TransformBase>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
            remap[sourceBone] = resolvedBone;
        }

        if (!changed || remap is null)
            return false;

        // Packed influences index UtilizedBones by position, so rebinding the bone
        // table is the whole remap; two source bones that resolve to one bone
        // simply keep two identical palette entries.
        UtilizedBones = reboundBones;
        RuntimeBoneReferenceRemap = remapVertexWeights ? null : remap;
        return true;
    }

    /// <summary>
    /// Replaces this mesh's inverse-bind matrices with the current bind state of its existing
    /// bone hierarchy without changing bone ordering or packed influence data.
    /// </summary>
    /// <remarks>
    /// Source importer metadata can provide a more authoritative skeleton pose than generic
    /// model-node decomposition. Once that pose has been applied and saved on the
    /// hierarchy, this method keeps the mesh palette and retained CPU vertex weights in the same
    /// coordinate frame.
    /// </remarks>
    public void RebaseSkinningBindPoseToCurrentHierarchy()
    {
        if (!HasSkinning)
            return;

        var inverseBinds = new Dictionary<TransformBase, Matrix4x4>(
            UtilizedBones.Length,
            System.Collections.Generic.ReferenceEqualityComparer.Instance);
        var rebasedBones = new (TransformBase tfm, Matrix4x4 invBindWorldMtx)[UtilizedBones.Length];
        for (int boneIndex = 0; boneIndex < UtilizedBones.Length; boneIndex++)
        {
            TransformBase bone = UtilizedBones[boneIndex].tfm;
            Matrix4x4 inverseBind = bone.InverseBindMatrix;
            inverseBinds[bone] = inverseBind;
            rebasedBones[boneIndex] = (bone, inverseBind);
        }

        UtilizedBones = rebasedBones;
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

    private BufferCollection.PreparedBufferBatch CommitSkinningBufferState(
        XRMeshSkinningBufferState prepared,
        KeyValuePair<string, XRDataBuffer>[] replacements,
        XRMeshSkinningBufferState previous,
        BufferCollection targetBuffers,
        BufferCollection.PreparedBufferTicket preparationTicket)
        => targetBuffers.SwapPreparedBatch(
            replacements,
            SkinningBufferKeys,
            expectedTicket: preparationTicket,
            installState: () => ApplySkinningBufferState(prepared),
            restoreStateOnFailure: () => ApplySkinningBufferState(previous));

    private void RollbackSkinningBufferState(
        XRMeshSkinningBufferState previous,
        BufferCollection.PreparedBufferBatch? swap,
        BufferCollection targetBuffers)
    {
        if (swap is not null)
            targetBuffers.RestorePreparedBatch(
                swap,
                () => ApplySkinningBufferState(previous));
    }

    private void CompleteSkinningBufferReplacement(
        XRMeshSkinningBufferState prepared,
        BufferCollection.PreparedBufferBatch? swap,
        BufferCollection targetBuffers)
    {
        if (swap is not null)
            targetBuffers.DisposeReplacedBuffers(swap);
        if (prepared.CoreIndices is not null && prepared.CoreWeights is not null)
            RecordSkinningBufferUpload();
    }

    /// <summary>
    /// Captures one immutable, coherent skinning-buffer generation for a compound consumer.
    /// </summary>
    public XRMeshSkinningBufferState GetSkinningBufferStateSnapshot()
        => Volatile.Read(ref _skinningBufferState);

    private XRMeshSkinningBufferState CaptureSkinningBufferState()
    {
        XRMeshSkinningBufferState buffers = GetSkinningBufferStateSnapshot();
        return buffers with
        {
            UtilizedBones = UtilizedBones,
            ShaderConvention = SkinningShaderConvention,
            InfluenceEncoding = SkinningInfluenceEncoding,
            CoreIndexFormat = SkinningCoreIndexFormat,
            HasSpillInfluences = HasSpillInfluences,
            MaxSpillInfluenceCount = MaxSpillInfluenceCount,
            MaxWeightCount = _maxWeightCount,
        };
    }

    private void ApplySkinningBufferState(XRMeshSkinningBufferState state)
    {
        // Publish this metadata together with the collection swap. Individual
        // SetField observers must not veto rollback or see a mixed palette/index
        // convention while the replacement group is being installed.
        using (XRBase.SuppressPropertyNotifications())
        {
            UtilizedBones = state.UtilizedBones;
            SkinningShaderConvention = state.ShaderConvention;
            SkinningInfluenceEncoding = state.InfluenceEncoding;
            SkinningCoreIndexFormat = state.CoreIndexFormat;
            HasSpillInfluences = state.HasSpillInfluences;
            MaxSpillInfluenceCount = state.MaxSpillInfluenceCount;
            SetField(ref _maxWeightCount, state.MaxWeightCount, nameof(MaxWeightCount));
        }
        Volatile.Write(ref _skinningBufferState, state);
    }

    private static XRMeshSkinningBufferState EmptySkinningBufferState()
        => new(
            null,
            null,
            null,
            null,
            [],
            ESkinningShaderConvention.ExplicitRowMajorRowVector,
            SkinningInfluenceEncoding.None,
            SkinningCoreIndexFormat.None,
            false,
            0,
            0);

    private static KeyValuePair<string, XRDataBuffer>[] CreateSkinningBufferReplacements(
        XRMeshSkinningBufferState state)
    {
        List<KeyValuePair<string, XRDataBuffer>> replacements = new(4);
        AddSkinningBufferReplacement(replacements, state.CoreIndices);
        AddSkinningBufferReplacement(replacements, state.CoreWeights);
        AddSkinningBufferReplacement(replacements, state.SpillHeaders);
        AddSkinningBufferReplacement(replacements, state.SpillEntries);
        return [.. replacements];
    }

    private static void AddSkinningBufferReplacement(
        List<KeyValuePair<string, XRDataBuffer>> replacements,
        XRDataBuffer? buffer)
    {
        if (buffer is not null)
            replacements.Add(new KeyValuePair<string, XRDataBuffer>(buffer.AttributeName, buffer));
    }

    /// <summary>
    /// Supplies one vertex's logical influences (palette bone index, positive
    /// weight) and returns how many source weights the vertex had before
    /// filtering, so a weighted vertex that packs to nothing is diagnosed.
    /// </summary>
    private delegate int LogicalInfluenceSource(int vertex, List<LogicalSkinningInfluence> destination);

    private void PopulateSkinningBuffers(LogicalInfluenceSource influences)
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
        SetField(ref _maxWeightCount, 0, nameof(MaxWeightCount));
        PopulateCompressedWeights(influences);
    }

    private void RecordSkinningBufferUpload()
        => RuntimeEngine.Rendering.Stats.RecordSkinningUpload(
            0L,
            0L,
            coreInfluenceBytes: (long)(BoneInfluenceCoreIndices!.Length + BoneInfluenceCoreWeights!.Length),
            spillHeaderBytes: (long)(BoneInfluenceSpillHeaders?.Length ?? 0u),
            spillEntryBytes: (long)(BoneInfluenceSpillEntries?.Length ?? 0u));

    private unsafe void PopulateCompressedWeights(LogicalInfluenceSource influenceSource)
    {
        using var _ = RuntimeRenderingHostServices.Profiling.StartProfileScope();

        int vertexCount = VertexCount;
        var coreIndices = BoneInfluenceCoreIndices!;
        var coreWeights = BoneInfluenceCoreWeights!;
        byte* coreIndex8 = SkinningCoreIndexFormat == SkinningCoreIndexFormat.Core4x8 ? (byte*)coreIndices.Address : null;
        ushort* coreIndex16 = SkinningCoreIndexFormat == SkinningCoreIndexFormat.Core4x16 ? (ushort*)coreIndices.Address : null;
        byte* coreWeightData = (byte*)coreWeights.Address;
        uint[] spillHeaders = new uint[vertexCount];
        List<uint> spillEntries = [];
        List<LogicalSkinningInfluence> logical = [];
        List<PackedSkinningInfluence> influences = [];

        // Diagnostic: vertices that carry skin weights but pack to zero usable core
        // influence collapse to the origin on the GPU (transformSkinPosition accumulates
        // nothing), which renders as missing/degenerate triangles. Counting them here
        // distinguishes a CPU packing-drop from a GPU bind/decode fault.
        int weightedVerticesDroppedToZeroInfluence = 0;
        int firstDroppedVertexIndex = -1;

        for (int vi = 0; vi < vertexCount; vi++)
        {
            int coreBase = vi * 4;
            logical.Clear();
            int sourceWeightCount = influenceSource(vi, logical);
            if (sourceWeightCount == 0)
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

            PackLogicalInfluences(logical, influences);
            SetField(ref _maxWeightCount, Math.Max(_maxWeightCount, logical.Count), nameof(MaxWeightCount));

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

    /// <summary>
    /// Orders one vertex's logical influences strongest first and quantizes
    /// them to unorm8 weights that sum to 255 into <paramref name="packed"/>.
    /// Leaves <paramref name="packed"/> empty when no influence has weight.
    /// </summary>
    private static void PackLogicalInfluences(
        List<LogicalSkinningInfluence> logical,
        List<PackedSkinningInfluence> packed)
    {
        packed.Clear();
        float totalWeight = 0.0f;
        for (int i = 0; i < logical.Count; i++)
            totalWeight += logical[i].Weight;
        if (logical.Count == 0 || totalWeight <= 0.0f)
            return;

        logical.Sort(static (left, right) =>
        {
            int weightOrder = right.Weight.CompareTo(left.Weight);
            return weightOrder != 0 ? weightOrder : left.BoneIndex.CompareTo(right.BoneIndex);
        });

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

}
