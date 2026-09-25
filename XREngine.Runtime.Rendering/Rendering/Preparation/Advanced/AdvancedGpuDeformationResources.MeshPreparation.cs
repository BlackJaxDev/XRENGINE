using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
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
            mesh.Vertices.Length != mesh.VertexCount)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        PendingGpuDeformationMeshPreparation pending = CreatePendingMesh(mesh, 0u, 0UL);
        uint steps = 0u;
        while (pending.Stage < PendingGpuDeformationMeshPreparation.Spill)
        {
            if ((steps++ & 0xFFFu) == 0u)
                cancellationToken.ThrowIfCancellationRequested();
            AdvanceManagedMeshPreparation(pending);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!MatchesPendingSource(pending) ||
            pending.RecordCount != pending.PackedRecordCount ||
            pending.DeltaCount != pending.PackedDeltaCount)
            throw new InvalidOperationException(
                "Imported deformation payload changed during exclusive mesh preparation.");

        ImportedGpuDeformationMeshPayload payload = new()
        {
            GeometryRevision = pending.GeometryRevision,
            VertexCount = pending.VertexCount,
            SourceVertices = pending.SourceVertices,
            Names = pending.Names,
            ActiveBlendshapeCount = pending.ActiveBlendshapeCount,
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
        if (_meshSlices.TryGetValue(mesh, out slice) &&
            slice.TopologyGeneration == topologyGeneration)
            return AdvancedGpuDeformationMeshPreparationStatus.Ready;
        if (_unsupportedMeshPreparationSources.TryGetValue(mesh, out var unsupported) &&
            unsupported.GeometryRevision == mesh.GeometryRevision &&
            unsupported.TopologyGeneration == topologyGeneration)
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
            (pending.Unsupported || !MatchesPendingSource(pending) ||
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
            if (mesh.VertexCount <= 0 || !CanReadCanonicalVertices(mesh))
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
                    renderFrame, out pending))
                pending = CreatePendingMesh(mesh, topologyGeneration, renderFrame);
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

        if (!MatchesPendingSource(pending))
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
        return AdvancedGpuDeformationMeshPreparationStatus.Ready;
    }

    private static bool MatchesPendingSource(PendingGpuDeformationMeshPreparation pending)
    {
        XRMesh mesh = pending.Mesh;
        return pending.GeometryRevision == mesh.GeometryRevision &&
            pending.VertexCount == mesh.VertexCount &&
            CanReadCanonicalVertices(mesh) &&
            ReferenceEquals(pending.SourceVertices, mesh.Vertices) &&
            ReferenceEquals(pending.Names, mesh.BlendshapeNames) &&
            pending.ActiveBlendshapeCount == (mesh.Vertices.Length == mesh.VertexCount
                ? mesh.BlendshapeNames.Length : 0);
    }

    private static PendingGpuDeformationMeshPreparation CreatePendingMesh(
        XRMesh mesh,
        uint topologyGeneration,
        ulong renderFrame)
    {
        int vertexCount = mesh.VertexCount;
        int blendshapeCount = mesh.Vertices.Length == vertexCount
            ? mesh.BlendshapeNames.Length : 0;
        return new PendingGpuDeformationMeshPreparation
        {
            Mesh = mesh,
            TopologyGeneration = topologyGeneration,
            GeometryRevision = mesh.GeometryRevision,
            VertexCount = vertexCount,
            SourceVertices = mesh.Vertices,
            Names = mesh.BlendshapeNames,
            ActiveBlendshapeCount = blendshapeCount,
            LastOwnerVisitFrame = renderFrame,
            SourceIndices = new int[checked(vertexCount * blendshapeCount)],
            VerticesScratch = new AdvancedDeformedVertex[vertexCount],
            RangesScratch = new AdvancedBlendshapeRange[blendshapeCount],
        };
    }

    private static bool TryCreatePendingFromImportedPayload(
        XRMesh mesh,
        uint topologyGeneration,
        ulong renderFrame,
        out PendingGpuDeformationMeshPreparation pending)
    {
        if (!ImportedMeshPayloads.TryGetValue(mesh, out var payload))
        {
            pending = null!;
            return false;
        }
        if (payload.GeometryRevision == mesh.GeometryRevision &&
            payload.VertexCount == mesh.VertexCount &&
            ReferenceEquals(payload.SourceVertices, mesh.Vertices) &&
            ReferenceEquals(payload.Names, mesh.BlendshapeNames) &&
            payload.ActiveBlendshapeCount == mesh.BlendshapeNames.Length)
        {
            pending = new PendingGpuDeformationMeshPreparation
            {
                Mesh = mesh,
                TopologyGeneration = topologyGeneration,
                GeometryRevision = payload.GeometryRevision,
                VertexCount = payload.VertexCount,
                SourceVertices = payload.SourceVertices,
                Names = payload.Names,
                ActiveBlendshapeCount = payload.ActiveBlendshapeCount,
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
        _unsupportedMeshPreparationSources.Remove(pending.Mesh);
        _unsupportedMeshPreparationSources.Add(pending.Mesh,
            new UnsupportedGpuDeformationMeshPreparationWitness
            {
                GeometryRevision = pending.GeometryRevision,
                TopologyGeneration = pending.TopologyGeneration,
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
            case PendingGpuDeformationMeshPreparation.Index:
                if (blendshapeCount == 0 || pending.VertexIndex >= vertexCount)
                {
                    pending.Stage = PendingGpuDeformationMeshPreparation.Count;
                    pending.VertexIndex = 0;
                    return;
                }
                IndexBlendshapesAtVertex(pending, pending.VertexIndex++);
                return;

            case PendingGpuDeformationMeshPreparation.Count:
                if (pending.ShapeIndex >= blendshapeCount)
                {
                    pending.RecordsScratch = new AdvancedBlendshapeSparseRecord[
                        checked((int)pending.RecordCount)];
                    pending.DeltasScratch = new Vector4[checked((int)pending.DeltaCount)];
                    pending.Stage = PendingGpuDeformationMeshPreparation.Pack;
                    pending.ShapeIndex = 0;
                    pending.VertexIndex = 0;
                    return;
                }
                CountBlendshapeAtVertex(pending, pending.ShapeIndex, pending.VertexIndex);
                AdvanceShapeVertex(pending, vertexCount);
                return;

            case PendingGpuDeformationMeshPreparation.Pack:
                if (pending.ShapeIndex >= blendshapeCount)
                {
                    pending.SourceIndices = [];
                    pending.Stage = PendingGpuDeformationMeshPreparation.Vertices;
                    pending.VertexIndex = 0;
                    return;
                }
                PackBlendshapeAtVertex(pending, pending.ShapeIndex, pending.VertexIndex);
                AdvanceShapeVertex(pending, vertexCount);
                return;

            case PendingGpuDeformationMeshPreparation.Vertices:
                if (pending.VertexIndex >= vertexCount)
                {
                    pending.Stage = PendingGpuDeformationMeshPreparation.Spill;
                    pending.VertexIndex = 0;
                    return;
                }
                int canonicalIndex = pending.VertexIndex++;
                pending.VerticesScratch[canonicalIndex] = PackCanonicalVertex(
                    pending.Mesh, checked((uint)canonicalIndex), checked((uint)canonicalIndex));
                return;
        }
    }

    private static void AdvanceShapeVertex(
        PendingGpuDeformationMeshPreparation pending,
        int vertexCount)
    {
        if (++pending.VertexIndex < vertexCount)
            return;
        pending.VertexIndex = 0;
        pending.ShapeIndex++;
    }

    private static void IndexBlendshapesAtVertex(
        PendingGpuDeformationMeshPreparation pending,
        int vertexIndex)
    {
        List<(string name, VertexData data)>? shapes =
            pending.SourceVertices[vertexIndex].Blendshapes;
        Dictionary<string, int> first = pending.FirstNameIndices;
        first.Clear();
        int firstNull = -1;
        if (shapes is not null)
        {
            first.EnsureCapacity(shapes.Count);
            for (int i = 0; i < shapes.Count; i++)
            {
                string name = shapes[i].name;
                if (name is null)
                {
                    if (firstNull < 0)
                        firstNull = i;
                }
                else
                    first.TryAdd(name, i);
            }
        }
        int vertexCount = pending.SourceVertices.Length;
        for (int shapeIndex = 0; shapeIndex < pending.ActiveBlendshapeCount; shapeIndex++)
        {
            string name = pending.Names[shapeIndex];
            int sourceIndex = -1;
            if (shapes is not null)
            {
                if ((uint)shapeIndex < (uint)shapes.Count &&
                    string.Equals(shapes[shapeIndex].name, name, StringComparison.Ordinal))
                    sourceIndex = shapeIndex;
                else if (name is null)
                    sourceIndex = firstNull;
                else if (first.TryGetValue(name, out int match))
                    sourceIndex = match;
            }
            pending.SourceIndices[checked(shapeIndex * vertexCount + vertexIndex)] = sourceIndex;
        }
    }

    private static void CountBlendshapeAtVertex(
        PendingGpuDeformationMeshPreparation pending,
        int shapeIndex,
        int vertexIndex)
    {
        Vertex source = pending.SourceVertices[vertexIndex];
        int sourceIndex = pending.SourceIndices[
            checked(shapeIndex * pending.SourceVertices.Length + vertexIndex)];
        if (!TryGetIndexedBlendshapeData(source, sourceIndex, out VertexData data))
            return;
        GetBlendshapeDeltas(source, data, out Vector3 position,
            out Vector3 normal, out Vector3 tangent);
        bool hasPosition = position.LengthSquared() > DeltaEpsilonSquared;
        bool hasNormal = normal.LengthSquared() > DeltaEpsilonSquared;
        bool hasTangent = tangent.LengthSquared() > DeltaEpsilonSquared;
        if (!hasPosition && !hasNormal && !hasTangent)
            return;
        pending.RecordCount++;
        pending.DeltaCount += checked((uint)(hasPosition ? 1 : 0) +
            (uint)(hasNormal ? 1 : 0) + (uint)(hasTangent ? 1 : 0));
    }

    private static void PackBlendshapeAtVertex(
        PendingGpuDeformationMeshPreparation pending,
        int shapeIndex,
        int vertexIndex)
    {
        if (vertexIndex == 0)
            pending.RangesScratch[shapeIndex] = new AdvancedBlendshapeRange(
                pending.PackedRecordCount, 0u, 0u, 0u);
        Vertex source = pending.SourceVertices[vertexIndex];
        int sourceIndex = pending.SourceIndices[
            checked(shapeIndex * pending.SourceVertices.Length + vertexIndex)];
        if (TryGetIndexedBlendshapeData(source, sourceIndex, out VertexData data))
        {
            GetBlendshapeDeltas(source, data, out Vector3 position,
                out Vector3 normal, out Vector3 tangent);
            AdvancedBlendshapeRange range = pending.RangesScratch[shapeIndex];
            uint flags = range.AttributeFlags;
            uint p = AppendPendingDelta(pending, position, 1u, ref flags);
            uint n = AppendPendingDelta(pending, normal, 2u, ref flags);
            uint t = AppendPendingDelta(pending, tangent, 4u, ref flags);
            if ((p | n | t) != 0u)
            {
                pending.RecordsScratch[pending.PackedRecordCount++] =
                    new AdvancedBlendshapeSparseRecord(checked((uint)vertexIndex), p, n, t);
                pending.RangesScratch[shapeIndex] = new AdvancedBlendshapeRange(
                    range.RecordOffset, range.RecordCount + 1u, flags, 0u);
            }
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
        if (!MatchesSkinningWitness(pending) ||
            pending.RecordCount != pending.PackedRecordCount ||
            pending.DeltaCount != pending.PackedDeltaCount)
        {
            _pendingMeshPreparation = null;
            return false;
        }
        if (!TryEnsureStaticInputsWritable())
            return false;

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
        _meshSlices[pending.Mesh] = slice;
        return true;
    }

    private static uint RebaseDelta(uint localIndex, uint deltaBase)
        => localIndex == 0u ? 0u : checked(deltaBase + localIndex - 1u);
}
