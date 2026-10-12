using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using XREngine.Data;
using XREngine.Data.Rendering;

namespace XREngine.Rendering;

public sealed partial class AdvancedGpuDeformationResources
{
    private const double ColdMeshPreparationBudgetMs = 4.0;
    private const ulong AbsentMeshPreparationRetirementFrames = 240UL;
    private PendingGpuDeformationMeshPreparation? _pendingMeshPreparation;
    private readonly ConditionalWeakTable<XRMesh, UnsupportedGpuDeformationMeshPreparationWitness>
        _unsupportedMeshPreparationSources = new();
    private readonly ConditionalWeakTable<XRMesh, AdvancedGpuDeformationMeshAliasWitness>
        _meshAliasWitnesses = new();
    private static readonly ConditionalWeakTable<XRMesh, ImportedGpuDeformationMeshPayload>
        ImportedMeshPayloads = new();
    private static readonly object ImportedMeshPayloadsSync = new();
    private ulong _meshPreparationBudgetFrame = ulong.MaxValue;
    private long _meshPreparationBudgetStarted;
    private long _lastMeshPreparationDiagnostic;

    /// <summary>
    /// Packs managed deformation input while an imported mesh is still owned by
    /// its build worker. Native skinning buffers and static GPU generations remain
    /// owned by the render thread.
    /// </summary>
    public static void PrepareImportedMesh(
        XRMesh mesh,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (!mesh.HasSkinning || mesh.VertexCount <= 0 ||
            !AdvancedPackedVertexCodec.HasReadableAttributes(mesh))
            return;

        cancellationToken.ThrowIfCancellationRequested();
        PendingGpuDeformationMeshPreparation pending = CreatePendingMesh(
            mesh, 0u, 0UL, EAdvancedDeformationMeshPreparationPolicy.AuthoredVertices);
        uint steps = 0u;
        while (pending.Stage < PendingGpuDeformationMeshPreparation.Spill)
        {
            if ((steps++ & 0xFFFu) == 0u)
                cancellationToken.ThrowIfCancellationRequested();
            AdvanceManagedMeshPreparation(pending);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!MatchesPendingSource(pending, EAdvancedDeformationMeshPreparationPolicy.AuthoredVertices) ||
            pending.RecordCount != pending.PackedRecordCount ||
            pending.DeltaCount != pending.PackedDeltaCount)
            throw new InvalidOperationException(
                "Imported deformation payload changed during exclusive mesh preparation.");

        ImportedGpuDeformationMeshPayload payload = new()
        {
            GeometryRevision = pending.GeometryRevision,
            InputWitness = pending.InputWitness,
            VertexCount = pending.VertexCount,
            Names = pending.Names,
            ActiveBlendshapeCount = pending.ActiveBlendshapeCount,
            BlendshapeIndices = pending.Blendshapes.IsValid ? pending.Blendshapes.Indices : null,
            BlendshapeIndicesRevision = pending.BlendshapeIndicesRevision,
            Vertices = pending.VerticesScratch,
            Ranges = pending.RangesScratch,
            Records = pending.RecordsScratch,
            Deltas = pending.DeltasScratch,
        };
        lock (ImportedMeshPayloadsSync)
        {
            ImportedMeshPayloads.Remove(mesh);
            ImportedMeshPayloads.Add(mesh, payload);
        }
    }

    /// <summary>Prepares one cold mesh over render frames without publishing partial static inputs.</summary>
    public AdvancedGpuDeformationMeshPreparationStatus TryPrepareMesh(
        XRMesh mesh,
        uint topologyGeneration,
        out AdvancedGpuDeformationMeshSlice slice)
    {
        ThrowIfFrameClosed();
        ArgumentNullException.ThrowIfNull(mesh);
        if (_meshSlices.TryGetValue(mesh, out slice))
        {
            // The alias witness proves that the packed source rows still match the
            // mesh. The input witness proves that they use the active preparation
            // policy and, for packed inputs, the same canonical morph buffers.
            if (slice.TopologyGeneration == topologyGeneration &&
                _meshAliasWitnesses.TryGetValue(mesh, out var witness) &&
                witness.Matches(mesh, in slice) &&
                _staticGeneration.InputWitnesses.TryGetValue(mesh, out var inputWitness) &&
                inputWitness.Policy == _meshPreparationPolicy &&
                (!UsesPackedAggregateInputs || inputWitness.Matches(mesh, _meshPreparationPolicy)))
                return AdvancedGpuDeformationMeshPreparationStatus.Ready;
            _meshSlices.Remove(mesh);
            _meshAliasWitnesses.Remove(mesh);
            _staticGeneration.InputWitnesses.Remove(mesh);
        }
        if (_unsupportedMeshPreparationSources.TryGetValue(mesh, out var unsupported) &&
            unsupported.Matches(mesh, topologyGeneration, _meshPreparationPolicy))
        {
            slice = default;
            return AdvancedGpuDeformationMeshPreparationStatus.Unsupported;
        }

        ulong renderFrame = RuntimeEngine.Rendering.State.RenderFrameId;
        if (_meshPreparationBudgetFrame != renderFrame)
        {
            _meshPreparationBudgetFrame = renderFrame;
            _meshPreparationBudgetStarted = Stopwatch.GetTimestamp();
        }

        PendingGpuDeformationMeshPreparation? pending = _pendingMeshPreparation;
        if (pending is not null &&
            (pending.Unsupported || !MatchesPendingSource(pending, _meshPreparationPolicy) ||
             (ReferenceEquals(pending.Mesh, mesh) &&
              pending.TopologyGeneration != topologyGeneration) ||
             (renderFrame >= pending.LastOwnerVisitFrame &&
              renderFrame - pending.LastOwnerVisitFrame >
                AbsentMeshPreparationRetirementFrames)))
            _pendingMeshPreparation = pending = null;
        if (pending is not null &&
            !ReferenceEquals(pending.Mesh, mesh))
        {
            if (!AdvancePendingMesh(pending) && pending.Unsupported)
            {
                RememberUnsupportedMesh(pending);
                _pendingMeshPreparation = null;
                _unsupportedMeshCount++;
            }
            slice = default;
            return AdvancedGpuDeformationMeshPreparationStatus.Pending;
        }
        if (pending is null)
        {
            if (mesh.VertexCount <= 0 || !AdvancedPackedVertexCodec.HasReadableAttributes(mesh))
            {
                _unsupportedMeshCount++;
                slice = default;
                return AdvancedGpuDeformationMeshPreparationStatus.Unsupported;
            }
            if (OutOfMeshPreparationBudget())
            {
                slice = default;
                return AdvancedGpuDeformationMeshPreparationStatus.Pending;
            }

            if (!TryCreatePendingFromImportedPayload(mesh, topologyGeneration,
                    renderFrame, _meshPreparationPolicy, out pending))
                pending = CreatePendingMesh(mesh, topologyGeneration, renderFrame, _meshPreparationPolicy);
            _pendingMeshPreparation = pending;
        }
        pending.LastOwnerVisitFrame = renderFrame;

        if (!AdvancePendingMesh(pending))
        {
            if (pending.Unsupported)
            {
                RememberUnsupportedMesh(pending);
                _pendingMeshPreparation = null;
                _unsupportedMeshCount++;
                slice = default;
                return AdvancedGpuDeformationMeshPreparationStatus.Unsupported;
            }
            LogPendingMeshProgress(pending, renderFrame);
            slice = default;
            return AdvancedGpuDeformationMeshPreparationStatus.Pending;
        }

        if (!MatchesPendingSource(pending, _meshPreparationPolicy))
        {
            _pendingMeshPreparation = null;
            slice = default;
            return AdvancedGpuDeformationMeshPreparationStatus.Pending;
        }
        if (!TryCommitPendingMesh(pending, out slice))
        {
            LogPendingMeshProgress(pending, renderFrame);
            return AdvancedGpuDeformationMeshPreparationStatus.Pending;
        }

        _pendingMeshPreparation = null;
        ReleaseAdoptedImportedPayload(pending);
        return AdvancedGpuDeformationMeshPreparationStatus.Ready;
    }

    /// <summary>
    /// Drops the import-time payload once a commit adopted it. The static
    /// generation now holds its own copies, so keeping the entry would retain a
    /// second managed copy of every skinned mesh's packed deformation input for
    /// the mesh's lifetime. A later re-preparation (content reset or a geometry
    /// change) packs from the mesh again within the cold budget. A payload from
    /// a newer import is a different instance and stays.
    /// </summary>
    private static void ReleaseAdoptedImportedPayload(PendingGpuDeformationMeshPreparation pending)
    {
        lock (ImportedMeshPayloadsSync)
        {
            if (ImportedMeshPayloads.TryGetValue(pending.Mesh, out var payload) &&
                ReferenceEquals(payload.Records, pending.RecordsScratch))
                ImportedMeshPayloads.Remove(pending.Mesh);
        }
    }

    private static bool MatchesPendingSource(
        PendingGpuDeformationMeshPreparation pending,
        EAdvancedDeformationMeshPreparationPolicy policy)
    {
        XRMesh mesh = pending.Mesh;
        if (pending.GeometryRevision != mesh.GeometryRevision ||
            !pending.InputWitness.Matches(mesh, policy) ||
            pending.VertexCount != mesh.VertexCount ||
            !AdvancedPackedVertexCodec.HasReadableAttributes(mesh) ||
            !ReferenceEquals(pending.Names, mesh.BlendshapeNames))
            return false;

        if (pending.CanonicalMorphs is not null)
            return pending.ActiveBlendshapeCount == mesh.BlendshapeNames.Length &&
                   MatchesCanonicalMorphWitness(pending);

        if (!pending.Blendshapes.IsValid)
            return pending.ActiveBlendshapeCount == 0 &&
                   !XRMeshBlendshapeActiveListReader.TryCreate(mesh, out _);

        XRMeshBlendshapeActiveListReader blendshapes = pending.Blendshapes;
        return ReferenceEquals(blendshapes.Counts, mesh.BlendshapeCounts) &&
               ReferenceEquals(blendshapes.Indices, mesh.BlendshapeIndices) &&
               ReferenceEquals(blendshapes.Deltas, mesh.BlendshapeDeltas) &&
               blendshapes.Counts.Revision == pending.BlendshapeCountsRevision &&
               blendshapes.Indices.Revision == pending.BlendshapeIndicesRevision &&
               blendshapes.Deltas.Revision == pending.BlendshapeDeltasRevision;
    }

    private static PendingGpuDeformationMeshPreparation CreatePendingMesh(
        XRMesh mesh,
        uint topologyGeneration,
        ulong renderFrame,
        EAdvancedDeformationMeshPreparationPolicy policy)
    {
        int vertexCount = mesh.VertexCount;
        bool hasBlendshapes = XRMeshBlendshapeActiveListReader.TryCreate(
            mesh,
            out XRMeshBlendshapeActiveListReader blendshapes);
        int blendshapeCount = policy == EAdvancedDeformationMeshPreparationPolicy.CanonicalSparseMorphs
            ? mesh.BlendshapeNames.Length
            : hasBlendshapes ? blendshapes.ShapeCount : 0;
        PendingGpuDeformationMeshPreparation pending = new()
        {
            Mesh = mesh,
            TopologyGeneration = topologyGeneration,
            GeometryRevision = mesh.GeometryRevision,
            InputWitness = AdvancedGpuDeformationInputWitness.Capture(mesh, policy),
            VertexCount = vertexCount,
            Names = mesh.BlendshapeNames,
            ActiveBlendshapeCount = blendshapeCount,
            Blendshapes = blendshapes,
            BlendshapeCountsRevision = hasBlendshapes ? blendshapes.Counts.Revision : 0UL,
            BlendshapeIndicesRevision = hasBlendshapes ? blendshapes.Indices.Revision : 0UL,
            BlendshapeDeltasRevision = hasBlendshapes ? blendshapes.Deltas.Revision : 0UL,
            LastOwnerVisitFrame = renderFrame,
            Stage = PendingGpuDeformationMeshPreparation.Count,
            VerticesScratch = new AdvancedDeformedVertex[vertexCount],
            RangesScratch = new AdvancedBlendshapeRange[blendshapeCount],
        };
        if (policy == EAdvancedDeformationMeshPreparationPolicy.CanonicalSparseMorphs &&
            !TryInitializeCanonicalMorphs(pending) && blendshapeCount != 0 &&
            (!hasBlendshapes || blendshapes.ShapeCount != blendshapeCount))
            throw new NotSupportedException("Aggregate deformation requires complete canonical sparse morph or active-list input records.");
        return pending;
    }

    private static bool TryCreatePendingFromImportedPayload(
        XRMesh mesh,
        uint topologyGeneration,
        ulong renderFrame,
        EAdvancedDeformationMeshPreparationPolicy policy,
        out PendingGpuDeformationMeshPreparation pending)
    {
        if (!ImportedMeshPayloads.TryGetValue(mesh, out var payload))
        {
            pending = null!;
            return false;
        }
        // Each backend retains the payload prepared from its selected morph encoding.
        if (payload.InputWitness.Policy != policy)
        {
            pending = null!;
            return false;
        }
        bool hasBlendshapes = XRMeshBlendshapeActiveListReader.TryCreate(
            mesh,
            out XRMeshBlendshapeActiveListReader blendshapes);
        if (payload.GeometryRevision == mesh.GeometryRevision && payload.InputWitness.Matches(mesh, policy) &&
            payload.VertexCount == mesh.VertexCount &&
            ReferenceEquals(payload.Names, mesh.BlendshapeNames) &&
            payload.ActiveBlendshapeCount == (hasBlendshapes ? blendshapes.ShapeCount : 0) &&
            ReferenceEquals(payload.BlendshapeIndices, hasBlendshapes ? blendshapes.Indices : null) &&
            payload.BlendshapeIndicesRevision == (hasBlendshapes ? blendshapes.Indices.Revision : 0UL))
        {
            pending = new PendingGpuDeformationMeshPreparation
            {
                Mesh = mesh,
                TopologyGeneration = topologyGeneration,
                GeometryRevision = payload.GeometryRevision,
                InputWitness = payload.InputWitness,
                VertexCount = payload.VertexCount,
                Names = payload.Names,
                ActiveBlendshapeCount = payload.ActiveBlendshapeCount,
                Blendshapes = blendshapes,
                BlendshapeCountsRevision = hasBlendshapes ? blendshapes.Counts.Revision : 0UL,
                BlendshapeIndicesRevision = hasBlendshapes ? blendshapes.Indices.Revision : 0UL,
                BlendshapeDeltasRevision = hasBlendshapes ? blendshapes.Deltas.Revision : 0UL,
                LastOwnerVisitFrame = renderFrame,
                Stage = PendingGpuDeformationMeshPreparation.Spill,
                RecordCount = checked((uint)payload.Records.Length),
                DeltaCount = checked((uint)payload.Deltas.Length),
                PackedRecordCount = checked((uint)payload.Records.Length),
                PackedDeltaCount = checked((uint)payload.Deltas.Length),
                VerticesScratch = payload.Vertices,
                RangesScratch = payload.Ranges,
                RecordsScratch = payload.Records,
                DeltasScratch = payload.Deltas,
            };
            return true;
        }
        lock (ImportedMeshPayloadsSync)
        {
            if (ImportedMeshPayloads.TryGetValue(mesh, out var current) &&
                ReferenceEquals(current, payload))
                ImportedMeshPayloads.Remove(mesh);
        }
        pending = null!;
        return false;
    }

    private bool OutOfMeshPreparationBudget()
        => Stopwatch.GetElapsedTime(_meshPreparationBudgetStarted).TotalMilliseconds >=
           ColdMeshPreparationBudgetMs;

    private void RememberUnsupportedMesh(PendingGpuDeformationMeshPreparation pending)
    {
        XRMeshSkinningBufferState state = pending.SkinningState ??
            pending.Mesh.GetSkinningBufferStateSnapshot();
        _unsupportedMeshPreparationSources.Remove(pending.Mesh);
        _unsupportedMeshPreparationSources.Add(pending.Mesh,
            new UnsupportedGpuDeformationMeshPreparationWitness
            {
                GeometryRevision = pending.GeometryRevision,
                TopologyGeneration = pending.TopologyGeneration,
                SkinningState = state,
                CoreIndicesRevision = state.CoreIndices?.Revision ?? 0UL,
                CoreWeightsRevision = state.CoreWeights?.Revision ?? 0UL,
                SpillHeadersRevision = state.SpillHeaders?.Revision ?? 0UL,
                SpillEntriesRevision = state.SpillEntries?.Revision ?? 0UL,
                CoreIndicesCount = state.CoreIndices?.ElementCount ?? 0u,
                CoreWeightsCount = state.CoreWeights?.ElementCount ?? 0u,
                SpillHeadersCount = state.SpillHeaders?.ElementCount ?? 0u,
                SpillEntriesCount = state.SpillEntries?.ElementCount ?? 0u,
                CoreIndicesReadable = UnsupportedGpuDeformationMeshPreparationWitness.IsReadable(state.CoreIndices),
                CoreWeightsReadable = UnsupportedGpuDeformationMeshPreparationWitness.IsReadable(state.CoreWeights),
                SpillHeadersReadable = UnsupportedGpuDeformationMeshPreparationWitness.IsReadable(state.SpillHeaders),
                SpillEntriesReadable = UnsupportedGpuDeformationMeshPreparationWitness.IsReadable(state.SpillEntries),
                Policy = pending.InputWitness.Policy,
            });
    }

    private void LogPendingMeshProgress(
        PendingGpuDeformationMeshPreparation pending,
        ulong renderFrame)
    {
        long now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(_lastMeshPreparationDiagnostic, now) <
            TimeSpan.FromSeconds(2))
            return;
        _lastMeshPreparationDiagnostic = now;
        Debug.RenderingWarningEvery(
            "Advanced.MeshPreparation.Pending",
            TimeSpan.FromSeconds(2),
            "[AdvancedMeshPreparation] pending mesh='{0}' stage={1} shape={2}/{3} vertex={4}/{5} " +
            "budgetFrame={6} renderFrame={7} budgetElapsedMs={8:F2} revision={9} records={10}/{11}.",
            pending.Mesh.Name ?? "<unnamed>", pending.Stage, pending.ShapeIndex,
            pending.ActiveBlendshapeCount, pending.VertexIndex, pending.VertexCount,
            _meshPreparationBudgetFrame, renderFrame,
            Stopwatch.GetElapsedTime(_meshPreparationBudgetStarted, now).TotalMilliseconds,
            pending.GeometryRevision, pending.PackedRecordCount, pending.RecordCount);
    }

    private bool AdvancePendingMesh(PendingGpuDeformationMeshPreparation pending)
    {
        int vertexCount = pending.Mesh.VertexCount;
        while (!OutOfMeshPreparationBudget())
        {
            if (pending.Stage < PendingGpuDeformationMeshPreparation.Spill)
            {
                AdvanceManagedMeshPreparation(pending);
                continue;
            }
            switch (pending.Stage)
            {
                case PendingGpuDeformationMeshPreparation.Spill:
                    if (pending.InputWitness.Policy == EAdvancedDeformationMeshPreparationPolicy.CanonicalSparseMorphs &&
                        !pending.Mesh.HasSkinning)
                    {
                        pending.InfluencesScratch = new AdvancedSkinInfluence[vertexCount];
                        pending.SpillScratch = [];
                        pending.Stage = PendingGpuDeformationMeshPreparation.Commit;
                        continue;
                    }
                    if (pending.SkinningState is null)
                    {
                        try
                        {
                            pending.Mesh.EnsureComputeSkinningBuffers();
                        }
                        catch (InvalidOperationException)
                        {
                            pending.Unsupported = true;
                            return false;
                        }
                        CaptureSkinningWitness(pending);
                        pending.InfluencesScratch = new AdvancedSkinInfluence[vertexCount];
                        if (pending.SkinningState?.CoreIndices is not
                            { IsDestroyed: false, ClientSideSource: not null } ||
                            pending.SkinningState.CoreWeights is not
                            { IsDestroyed: false, ClientSideSource: not null } ||
                            pending.SkinningState.SpillHeaders is
                            { IsDestroyed: true } ||
                            (pending.SpillCount != 0u &&
                             pending.SkinningState.SpillEntries is not
                             { IsDestroyed: false, ClientSideSource: not null }))
                        {
                            pending.Unsupported = true;
                            return false;
                        }
                    }
                    if (!MatchesSkinningWitness(pending))
                    {
                        _pendingMeshPreparation = null;
                        return false;
                    }
                    if (pending.VertexIndex >= pending.SpillCount)
                    {
                        pending.Stage = PendingGpuDeformationMeshPreparation.Influences;
                        pending.VertexIndex = 0;
                        continue;
                    }
                    pending.SpillScratch[pending.VertexIndex] =
                        ReadSpillInfluence(pending.SkinningState!, checked((uint)pending.VertexIndex));
                    pending.VertexIndex++;
                    break;

                case PendingGpuDeformationMeshPreparation.Influences:
                    if (!MatchesSkinningWitness(pending))
                    {
                        _pendingMeshPreparation = null;
                        return false;
                    }
                    if (pending.VertexIndex >= vertexCount)
                    {
                        pending.Stage = PendingGpuDeformationMeshPreparation.Commit;
                        continue;
                    }
                    pending.InfluencesScratch[pending.VertexIndex] =
                        ReadSkinInfluence(pending.SkinningState!, checked((uint)pending.VertexIndex));
                    pending.VertexIndex++;
                    break;

                default:
                    return true;
            }
        }
        return pending.Stage == PendingGpuDeformationMeshPreparation.Commit;
    }

    private static void AdvanceManagedMeshPreparation(
        PendingGpuDeformationMeshPreparation pending)
    {
        int vertexCount = pending.VertexCount;
        int blendshapeCount = pending.ActiveBlendshapeCount;
        switch (pending.Stage)
        {
            case PendingGpuDeformationMeshPreparation.CanonicalMorphCount:
            case PendingGpuDeformationMeshPreparation.CanonicalMorphPack:
                AdvanceCanonicalMorphPreparation(pending);
                return;
            case PendingGpuDeformationMeshPreparation.Count:
                if (blendshapeCount == 0 || pending.VertexIndex >= vertexCount)
                {
                    BeginBlendshapePacking(pending);
                    return;
                }
                CountBlendshapesAtVertex(pending, pending.VertexIndex++);
                return;

            case PendingGpuDeformationMeshPreparation.Pack:
                if (blendshapeCount == 0 || pending.VertexIndex >= vertexCount)
                {
                    pending.Stage = PendingGpuDeformationMeshPreparation.Vertices;
                    pending.VertexIndex = 0;
                    return;
                }
                PackBlendshapesAtVertex(pending, pending.VertexIndex++);
                return;

            case PendingGpuDeformationMeshPreparation.Vertices:
                if (pending.VertexIndex >= vertexCount)
                {
                    pending.Stage = PendingGpuDeformationMeshPreparation.Spill;
                    pending.VertexIndex = 0;
                    return;
                }
                uint canonicalIndex = checked((uint)pending.VertexIndex++);
                pending.VerticesScratch[canonicalIndex] = AdvancedPackedVertexCodec.Pack(
                    pending.Mesh, canonicalIndex, canonicalIndex);
                return;
        }
    }

    /// <summary>
    /// Counts one vertex's blendshape records per shape. The per-shape counts
    /// are held in the range scratch until <see cref="BeginBlendshapePacking"/>
    /// turns them into record offsets.
    /// </summary>
    private static void CountBlendshapesAtVertex(
        PendingGpuDeformationMeshPreparation pending,
        int vertexIndex)
    {
        XRMeshBlendshapeActiveListReader reader = pending.Blendshapes;
        reader.GetVertexEntries(vertexIndex, out int first, out int count);
        for (int entry = first; entry < first + count; entry++)
        {
            reader.ReadEntry(entry, out int shape, out Vector3 position, out Vector3 normal, out Vector3 tangent);
            if ((uint)shape >= (uint)pending.ActiveBlendshapeCount)
                continue;
            bool hasPosition = position.LengthSquared() > DeltaEpsilonSquared;
            bool hasNormal = normal.LengthSquared() > DeltaEpsilonSquared;
            bool hasTangent = tangent.LengthSquared() > DeltaEpsilonSquared;
            if (!hasPosition && !hasNormal && !hasTangent)
                continue;
            AdvancedBlendshapeRange range = pending.RangesScratch[shape];
            pending.RangesScratch[shape] = range with { RecordCount = range.RecordCount + 1u };
            pending.RecordCount++;
            pending.DeltaCount += checked((uint)(hasPosition ? 1 : 0) +
                (uint)(hasNormal ? 1 : 0) + (uint)(hasTangent ? 1 : 0));
        }
    }

    /// <summary>
    /// Converts the per-shape record counts into record offsets, so shapes keep
    /// their records contiguous and vertex-ordered while vertices are packed in
    /// one pass, and allocates the record and delta scratch.
    /// </summary>
    private static void BeginBlendshapePacking(PendingGpuDeformationMeshPreparation pending)
    {
        uint offset = 0u;
        for (int shape = 0; shape < pending.RangesScratch.Length; shape++)
        {
            uint count = pending.RangesScratch[shape].RecordCount;
            pending.RangesScratch[shape] = new AdvancedBlendshapeRange(offset, 0u, 0u, 0u);
            offset = checked(offset + count);
        }
        pending.RecordsScratch = new AdvancedBlendshapeSparseRecord[checked((int)pending.RecordCount)];
        pending.DeltasScratch = new Vector4[checked((int)pending.DeltaCount)];
        pending.Stage = PendingGpuDeformationMeshPreparation.Pack;
        pending.VertexIndex = 0;
    }

    private static void PackBlendshapesAtVertex(
        PendingGpuDeformationMeshPreparation pending,
        int vertexIndex)
    {
        XRMeshBlendshapeActiveListReader reader = pending.Blendshapes;
        reader.GetVertexEntries(vertexIndex, out int first, out int count);
        for (int entry = first; entry < first + count; entry++)
        {
            reader.ReadEntry(entry, out int shape, out Vector3 position, out Vector3 normal, out Vector3 tangent);
            if ((uint)shape >= (uint)pending.ActiveBlendshapeCount)
                continue;
            AdvancedBlendshapeRange range = pending.RangesScratch[shape];
            uint flags = range.AttributeFlags;
            uint p = AppendPendingDelta(pending, position, 1u, ref flags);
            uint n = AppendPendingDelta(pending, normal, 2u, ref flags);
            uint t = AppendPendingDelta(pending, tangent, 4u, ref flags);
            if ((p | n | t) == 0u)
                continue;
            pending.RecordsScratch[range.RecordOffset + range.RecordCount] =
                new AdvancedBlendshapeSparseRecord(checked((uint)vertexIndex), p, n, t);
            pending.RangesScratch[shape] = new AdvancedBlendshapeRange(
                range.RecordOffset, range.RecordCount + 1u, flags, 0u);
            pending.PackedRecordCount++;
        }
    }

    private static uint AppendPendingDelta(
        PendingGpuDeformationMeshPreparation pending,
        Vector3 delta,
        uint flag,
        ref uint flags)
    {
        if (!(delta.LengthSquared() > DeltaEpsilonSquared))
            return 0u;
        uint index = pending.PackedDeltaCount++;
        pending.DeltasScratch[index] = new Vector4(delta, 0.0f);
        flags |= flag;
        return index;
    }

    private static void CaptureSkinningWitness(
        PendingGpuDeformationMeshPreparation pending)
    {
        XRMeshSkinningBufferState state = pending.Mesh.GetSkinningBufferStateSnapshot();
        pending.SkinningState = state;
        pending.CoreIndicesRevision = state.CoreIndices?.Revision ?? 0UL;
        pending.CoreWeightsRevision = state.CoreWeights?.Revision ?? 0UL;
        pending.SpillHeadersRevision = state.SpillHeaders?.Revision ?? 0UL;
        pending.SpillEntriesRevision = state.SpillEntries?.Revision ?? 0UL;
        pending.SpillCount = state.HasSpillInfluences
            ? state.SpillEntries?.ElementCount ?? 0u : 0u;
        pending.SpillScratch = new AdvancedSpillInfluence[checked((int)pending.SpillCount)];
    }

    private static bool MatchesSkinningWitness(
        PendingGpuDeformationMeshPreparation pending)
    {
        XRMeshSkinningBufferState? state = pending.SkinningState;
        return state is not null &&
            ReferenceEquals(state, pending.Mesh.GetSkinningBufferStateSnapshot()) &&
            state.CoreIndices is { IsDestroyed: false, ClientSideSource: not null } indices &&
            state.CoreWeights is { IsDestroyed: false, ClientSideSource: not null } weights &&
            indices.ElementCount >= pending.VertexCount &&
            weights.ElementCount >= pending.VertexCount &&
            (state.SpillHeaders is null ||
             state.SpillHeaders is { IsDestroyed: false, ClientSideSource: not null } headers &&
             headers.ElementCount >= pending.VertexCount) &&
            (pending.SpillCount == 0u ||
             state.SpillEntries is { IsDestroyed: false, ClientSideSource: not null }) &&
            (state.CoreIndices?.Revision ?? 0UL) == pending.CoreIndicesRevision &&
            (state.CoreWeights?.Revision ?? 0UL) == pending.CoreWeightsRevision &&
            (state.SpillHeaders?.Revision ?? 0UL) == pending.SpillHeadersRevision &&
            (state.SpillEntries?.Revision ?? 0UL) == pending.SpillEntriesRevision &&
            (state.HasSpillInfluences ? state.SpillEntries?.ElementCount ?? 0u : 0u) ==
                pending.SpillCount;
    }

    private static unsafe AdvancedSpillInfluence ReadSpillInfluence(
        XRMeshSkinningBufferState state,
        uint index)
    {
        XRDataBuffer sourceBuffer = state.SpillEntries ??
            throw new InvalidOperationException("Canonical spill influences are unavailable.");
        uint packed = ((uint*)sourceBuffer.Address.Pointer)[index];
        return new AdvancedSpillInfluence(
            packed & 0xFFFFu, ((packed >> 16) & 0xFFu) / 255.0f);
    }

    private static unsafe AdvancedSkinInfluence ReadSkinInfluence(
        XRMeshSkinningBufferState state,
        uint vertexIndex)
    {
        XRDataBuffer indices = state.CoreIndices ??
            throw new InvalidOperationException("Canonical skinning indices are unavailable.");
        XRDataBuffer weights = state.CoreWeights ??
            throw new InvalidOperationException("Canonical skinning weights are unavailable.");
        byte* indexBytes = (byte*)indices.Address.Pointer;
        byte* weightBytes = (byte*)weights.Address.Pointer;
        uint elementByteOffset = vertexIndex * indices.ElementSize;
        uint bone0, bone1, bone2, bone3;
        if (indices.ComponentType == EComponentType.Byte)
        {
            byte* source = indexBytes + elementByteOffset;
            bone0 = source[0]; bone1 = source[1]; bone2 = source[2]; bone3 = source[3];
        }
        else
        {
            ushort* source = (ushort*)(indexBytes + elementByteOffset);
            bone0 = source[0]; bone1 = source[1]; bone2 = source[2]; bone3 = source[3];
        }
        byte* sourceWeights = weightBytes + vertexIndex * weights.ElementSize;
        uint header = state.SpillHeaders is XRDataBuffer headers
            ? ((uint*)headers.Address.Pointer)[vertexIndex] : 0u;
        return new AdvancedSkinInfluence
        {
            Bone0 = bone0, Bone1 = bone1, Bone2 = bone2, Bone3 = bone3,
            Weights = new Vector4(
                sourceWeights[0] / 255.0f, sourceWeights[1] / 255.0f,
                sourceWeights[2] / 255.0f, sourceWeights[3] / 255.0f),
            SpillOffset = header & 0x00FF_FFFFu,
            SpillCount = header >> 24,
        };
    }

    private bool TryCommitPendingMesh(
        PendingGpuDeformationMeshPreparation pending,
        out AdvancedGpuDeformationMeshSlice slice)
    {
        slice = default;
        if (((pending.InputWitness.Policy == EAdvancedDeformationMeshPreparationPolicy.AuthoredVertices ||
              pending.Mesh.HasSkinning) && !MatchesSkinningWitness(pending)) ||
            pending.RecordCount != pending.PackedRecordCount ||
            pending.DeltaCount != pending.PackedDeltaCount)
        {
            _pendingMeshPreparation = null;
            return false;
        }
        ulong payloadHash = HashPendingMeshPayload(pending);
        if (_staticGeneration.HasCpuMirror &&
            TryFindInternedMeshPayload(pending, payloadHash, out slice))
        {
            RememberMeshAlias(pending, in slice);
            return true;
        }
        if (!TryEnsureStaticInputsWritable())
            return false;
        if (TryFindInternedMeshPayload(pending, payloadHash, out slice))
        {
            RememberMeshAlias(pending, in slice);
            return true;
        }

        uint vertexCount = checked((uint)pending.Mesh.VertexCount);
        uint rangeCount = checked((uint)pending.ActiveBlendshapeCount);
        uint recordCount = pending.PackedRecordCount;
        uint deltaCount = pending.PackedDeltaCount - 1u;
        uint sourceBase = _sourceVertexCount;
        uint influenceBase = _skinInfluenceCount;
        uint spillBase = _spillInfluenceCount;
        uint rangeBase = _blendshapeRangeCount;
        uint recordBase = _blendshapeRecordCount;
        uint deltaBase = _blendshapeDeltaCount;
        uint requiredSource = checked(sourceBase + vertexCount);
        uint requiredInfluences = checked(influenceBase + vertexCount);
        uint requiredSpill = checked(spillBase + pending.SpillCount);
        uint requiredRanges = checked(rangeBase + rangeCount);
        uint requiredRecords = checked(recordBase + recordCount);
        uint requiredDeltas = checked(deltaBase + deltaCount);
        EnsureCpuCapacity(ref _sourceVertices, requiredSource);
        EnsureCpuCapacity(ref _skinInfluences, requiredInfluences);
        EnsureCpuCapacity(ref _spillInfluences, requiredSpill);
        EnsureCpuCapacity(ref _blendshapeRanges, requiredRanges);
        EnsureCpuCapacity(ref _blendshapeRecords, requiredRecords);
        EnsureCpuCapacity(ref _blendshapeDeltas, requiredDeltas);
        if (!TryEnsureStaticBufferCapacity(requiredSource, requiredInfluences,
                requiredSpill, requiredRanges, requiredRecords, requiredDeltas))
            return false;

        for (uint i = 0u; i < vertexCount; i++)
        {
            AdvancedDeformedVertex vertex = pending.VerticesScratch[i];
            vertex.SourceVertex = checked(vertex.SourceVertex + sourceBase);
            _sourceVertices[sourceBase + i] = vertex;
            AdvancedSkinInfluence influence = pending.InfluencesScratch[i];
            influence.SpillOffset = checked(influence.SpillOffset + spillBase);
            _skinInfluences[influenceBase + i] = influence;
        }
        pending.SpillScratch.AsSpan().CopyTo(
            _spillInfluences.AsSpan(checked((int)spillBase)));
        for (uint i = 0u; i < rangeCount; i++)
        {
            AdvancedBlendshapeRange range = pending.RangesScratch[i];
            _blendshapeRanges[rangeBase + i] = range with
            {
                RecordOffset = checked(range.RecordOffset + recordBase),
            };
        }
        for (uint i = 0u; i < recordCount; i++)
        {
            AdvancedBlendshapeSparseRecord record = pending.RecordsScratch[i];
            _blendshapeRecords[recordBase + i] = record with
            {
                PositionDelta = RebaseDelta(record.PositionDelta, deltaBase),
                NormalDelta = RebaseDelta(record.NormalDelta, deltaBase),
                TangentDelta = RebaseDelta(record.TangentDelta, deltaBase),
            };
        }
        pending.DeltasScratch.AsSpan(1, checked((int)deltaCount)).CopyTo(
            _blendshapeDeltas.AsSpan(checked((int)deltaBase)));

        _sourceVertexCount = requiredSource;
        _skinInfluenceCount = requiredInfluences;
        _spillInfluenceCount = requiredSpill;
        _blendshapeRangeCount = requiredRanges;
        _blendshapeRecordCount = requiredRecords;
        _blendshapeDeltaCount = requiredDeltas;
        slice = new AdvancedGpuDeformationMeshSlice(
            sourceBase, influenceBase, rangeBase, vertexCount, rangeCount,
            pending.TopologyGeneration);
        int entryIndex = _staticGeneration.PayloadEntries.Count;
        int priorHead = _staticGeneration.PayloadHashHeads.TryGetValue(
            payloadHash, out int head) ? head : -1;
        _staticGeneration.PayloadEntries.Add(new AdvancedGpuDeformationMeshPayloadEntry(
            payloadHash, slice, spillBase, pending.SpillCount,
            recordBase, recordCount, deltaBase, deltaCount, priorHead));
        _staticGeneration.PayloadHashHeads[payloadHash] = entryIndex;
        RememberMeshAlias(pending, in slice);
        return true;
    }

    private void RememberMeshAlias(
        PendingGpuDeformationMeshPreparation pending,
        in AdvancedGpuDeformationMeshSlice slice)
    {
        // Every published mesh slice has an input witness. Generation adoption
        // copies both maps together.
        _meshSlices[pending.Mesh] = slice;
        _staticGeneration.InputWitnesses[pending.Mesh] = pending.InputWitness;
        // A packed preparation of a mesh without skinning skips the skinning
        // stage, so no skinning state was captured. Record the current state.
        // Otherwise the alias witness never matches and the mesh is prepared again.
        bool capturedSkinning = pending.SkinningState is not null;
        XRMeshSkinningBufferState skinning = pending.SkinningState ??
            pending.Mesh.GetSkinningBufferStateSnapshot();
        _meshAliasWitnesses.Remove(pending.Mesh);
        _meshAliasWitnesses.Add(pending.Mesh,
            new AdvancedGpuDeformationMeshAliasWitness
            {
                Slice = slice,
                GeometryRevision = pending.GeometryRevision,
                SkinningState = skinning,
                CoreIndicesRevision = capturedSkinning
                    ? pending.CoreIndicesRevision : skinning.CoreIndices?.Revision ?? 0UL,
                CoreWeightsRevision = capturedSkinning
                    ? pending.CoreWeightsRevision : skinning.CoreWeights?.Revision ?? 0UL,
                SpillHeadersRevision = capturedSkinning
                    ? pending.SpillHeadersRevision : skinning.SpillHeaders?.Revision ?? 0UL,
                SpillEntriesRevision = capturedSkinning
                    ? pending.SpillEntriesRevision : skinning.SpillEntries?.Revision ?? 0UL,
                BlendshapeNames = pending.Names,
                BlendshapeCounts = pending.Blendshapes.IsValid
                    ? pending.Blendshapes.Counts : null,
                BlendshapeIndices = pending.Blendshapes.IsValid
                    ? pending.Blendshapes.Indices : null,
                BlendshapeDeltas = pending.Blendshapes.IsValid
                    ? pending.Blendshapes.Deltas : null,
                BlendshapeCountsRevision = pending.BlendshapeCountsRevision,
                BlendshapeIndicesRevision = pending.BlendshapeIndicesRevision,
                BlendshapeDeltasRevision = pending.BlendshapeDeltasRevision,
            });
    }

    private static ulong HashPendingMeshPayload(
        PendingGpuDeformationMeshPreparation pending)
    {
        const ulong offset = 14695981039346656037UL;
        ulong hash = offset;
        hash = MixPayloadHash(hash, pending.TopologyGeneration);
        hash = MixPayloadHash(hash, checked((ulong)pending.VertexCount));
        hash = MixPayloadHash(hash, pending.SpillCount);
        hash = MixPayloadHash(hash, checked((ulong)pending.ActiveBlendshapeCount));
        hash = MixPayloadHash(hash, pending.PackedRecordCount);
        hash = MixPayloadHash(hash, pending.PackedDeltaCount);
        hash = HashPayloadBytes(hash, MemoryMarshal.AsBytes(
            pending.VerticesScratch.AsSpan(0, pending.VertexCount)));
        hash = HashPayloadBytes(hash, MemoryMarshal.AsBytes(
            pending.InfluencesScratch.AsSpan(0, pending.VertexCount)));
        hash = HashPayloadBytes(hash, MemoryMarshal.AsBytes(
            pending.SpillScratch.AsSpan(0, checked((int)pending.SpillCount))));
        hash = HashPayloadBytes(hash, MemoryMarshal.AsBytes(
            pending.RangesScratch.AsSpan(0, pending.ActiveBlendshapeCount)));
        hash = HashPayloadBytes(hash, MemoryMarshal.AsBytes(
            pending.RecordsScratch.AsSpan(0, checked((int)pending.PackedRecordCount))));
        return HashPayloadBytes(hash, MemoryMarshal.AsBytes(
            pending.DeltasScratch.AsSpan(0, checked((int)pending.PackedDeltaCount))));
    }

    private static ulong MixPayloadHash(ulong hash, ulong value)
        => unchecked((hash ^ value) * 1099511628211UL);

    private static ulong HashPayloadBytes(ulong hash, ReadOnlySpan<byte> bytes)
    {
        for (int index = 0; index < bytes.Length; ++index)
            hash = unchecked((hash ^ bytes[index]) * 1099511628211UL);
        return hash;
    }

    private bool TryFindInternedMeshPayload(
        PendingGpuDeformationMeshPreparation pending,
        ulong hash,
        out AdvancedGpuDeformationMeshSlice slice)
    {
        if (_staticGeneration.PayloadHashHeads.TryGetValue(hash, out int entryIndex))
        {
            while (entryIndex >= 0)
            {
                AdvancedGpuDeformationMeshPayloadEntry entry =
                    _staticGeneration.PayloadEntries[entryIndex];
                if (PayloadMatches(pending, in entry))
                {
                    slice = entry.Slice;
                    return true;
                }
                entryIndex = entry.NextHashEntry;
            }
        }
        slice = default;
        return false;
    }

    private bool PayloadMatches(
        PendingGpuDeformationMeshPreparation pending,
        in AdvancedGpuDeformationMeshPayloadEntry entry)
    {
        AdvancedGpuDeformationMeshSlice slice = entry.Slice;
        if (slice.TopologyGeneration != pending.TopologyGeneration ||
            slice.VertexCount != pending.VertexCount ||
            slice.BlendshapeCount != pending.ActiveBlendshapeCount ||
            entry.SpillCount != pending.SpillCount ||
            entry.RecordCount != pending.PackedRecordCount ||
            entry.DeltaCount + 1u != pending.PackedDeltaCount ||
            (ulong)slice.SourceVertexOffset + slice.VertexCount > _sourceVertexCount ||
            (ulong)slice.BoneInfluenceOffset + slice.VertexCount > _skinInfluenceCount ||
            (ulong)entry.SpillBase + entry.SpillCount > _spillInfluenceCount ||
            (ulong)slice.BlendshapeRangeOffset + slice.BlendshapeCount > _blendshapeRangeCount ||
            (ulong)entry.RecordBase + entry.RecordCount > _blendshapeRecordCount ||
            (ulong)entry.DeltaBase + entry.DeltaCount > _blendshapeDeltaCount)
            return false;

        for (uint index = 0u; index < slice.VertexCount; ++index)
        {
            AdvancedDeformedVertex vertex =
                _sourceVertices[slice.SourceVertexOffset + index];
            vertex.SourceVertex -= slice.SourceVertexOffset;
            if (!SamePayloadBytes(in vertex,
                    in pending.VerticesScratch[index]))
                return false;

            AdvancedSkinInfluence influence =
                _skinInfluences[slice.BoneInfluenceOffset + index];
            influence.SpillOffset -= entry.SpillBase;
            if (!SamePayloadBytes(in influence,
                    in pending.InfluencesScratch[index]))
                return false;
        }
        if (!MemoryMarshal.AsBytes(_spillInfluences.AsSpan(
                checked((int)entry.SpillBase), checked((int)entry.SpillCount)))
            .SequenceEqual(MemoryMarshal.AsBytes(
                pending.SpillScratch.AsSpan(0, checked((int)entry.SpillCount)))))
            return false;

        for (uint index = 0u; index < slice.BlendshapeCount; ++index)
        {
            AdvancedBlendshapeRange stored =
                _blendshapeRanges[slice.BlendshapeRangeOffset + index];
            AdvancedBlendshapeRange local = stored with
            {
                RecordOffset = stored.RecordOffset - entry.RecordBase,
            };
            if (!SamePayloadBytes(in local,
                    in pending.RangesScratch[index]))
                return false;
        }
        for (uint index = 0u; index < entry.RecordCount; ++index)
        {
            AdvancedBlendshapeSparseRecord stored =
                _blendshapeRecords[entry.RecordBase + index];
            AdvancedBlendshapeSparseRecord local = stored with
            {
                PositionDelta = LocalDeltaIndex(stored.PositionDelta, entry.DeltaBase),
                NormalDelta = LocalDeltaIndex(stored.NormalDelta, entry.DeltaBase),
                TangentDelta = LocalDeltaIndex(stored.TangentDelta, entry.DeltaBase),
            };
            if (!SamePayloadBytes(in local,
                    in pending.RecordsScratch[index]))
                return false;
        }
        return SamePayloadBytes(in _blendshapeDeltas[0],
                   in pending.DeltasScratch[0]) &&
               MemoryMarshal.AsBytes(_blendshapeDeltas.AsSpan(
                       checked((int)entry.DeltaBase), checked((int)entry.DeltaCount)))
                   .SequenceEqual(MemoryMarshal.AsBytes(
                       pending.DeltasScratch.AsSpan(1, checked((int)entry.DeltaCount))));
    }

    private static uint LocalDeltaIndex(uint globalIndex, uint deltaBase)
        => globalIndex == 0u ? 0u : globalIndex - deltaBase + 1u;

    private static bool SamePayloadBytes<T>(in T left, in T right)
        where T : unmanaged
        => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.AsRef(in left), 1))
            .SequenceEqual(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.AsRef(in right), 1)));

    private static uint RebaseDelta(uint localIndex, uint deltaBase)
        => localIndex == 0u ? 0u : checked(deltaBase + localIndex - 1u);
}
