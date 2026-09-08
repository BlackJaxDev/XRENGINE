using XREngine.Rendering.Commands;

namespace XREngine.Rendering.OpenGL;

/// <summary>
/// Renderer-owned immutable copy of the mutable Advanced preparation columns.
/// A GL stage captures this at its authoring boundary and must never revisit the
/// shared extractor while executing GPU work.  The fixed buffers are supplied
/// by the owning runtime during renderer initialization, so capture performs no
/// managed allocation on a frame path.
/// </summary>
internal sealed class OpenGLAdvancedVisibilityInputStorage
{
    internal const uint CounterWordsPerView = 38u;
    private const uint PersistentStateRecordByteLength = 32u;
    private readonly AdvancedVisibilityPayload[] _payloads;
    private readonly AdvancedVisibilityCandidate[] _candidates;
    private readonly EAdvancedGeometryProducer[] _producers;
    private readonly AdvancedIndirectRange[] _indirectRanges;
    private readonly int[] _indirectPayloadIndices;
    private readonly AdvancedDeformedArenaSlice[] _deformationSlices;
    private readonly AdvancedPreparedDrawDeformationRecord[] _preparedDeformations;
    private readonly byte[] _preparedDeformationWrites;
    private readonly uint[] _rangeIndices;
    private readonly uint[] _rangeOffsets;
    private readonly uint[] _counters;
    private int _payloadCount;
    private int _indirectRangeCount;
    private int _preparedDeformationCount;
    private uint _viewCount;
    private AdvancedPreparationPublication _publication;
    private AdvancedIndirectPreparationResult _indirect;
    private AdvancedGpuDeformationPublication _deformation;
    private bool _captured;

    internal OpenGLAdvancedVisibilityInputStorage(int drawCapacity, int indirectRangeCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(drawCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(indirectRangeCapacity);
        _payloads = new AdvancedVisibilityPayload[drawCapacity];
        _candidates = new AdvancedVisibilityCandidate[drawCapacity];
        _producers = new EAdvancedGeometryProducer[drawCapacity];
        _indirectRanges = new AdvancedIndirectRange[indirectRangeCapacity];
        _indirectPayloadIndices = new int[drawCapacity];
        _deformationSlices = new AdvancedDeformedArenaSlice[drawCapacity];
        _preparedDeformations = new AdvancedPreparedDrawDeformationRecord[drawCapacity];
        _preparedDeformationWrites = new byte[drawCapacity];
        _rangeIndices = new uint[drawCapacity];
        _rangeOffsets = new uint[indirectRangeCapacity];
        _counters = new uint[checked((int)(CounterWordsPerView * RenderFrameViewSet.MaxViewCount))];
    }

    internal bool IsValid
        => _captured && _publication.FrameId != 0u &&
           _publication.PublicationGeneration != 0u &&
           _publication.VisibilityContentGeneration != 0u &&
           _publication.DrawCount == (uint)_payloadCount &&
           _indirect.PayloadCount == (uint)_payloadCount &&
           _indirect.RangeCount == (uint)_indirectRangeCount &&
           _deformation.FrameId == _publication.FrameId &&
           (_publication.DeformationJobCount == 0u ||
            (_publication.AggregateDispatchExecuted &&
             _deformation.JobCount == _publication.DeformationJobCount));

    internal AdvancedPreparationPublication Publication => _publication;
    internal AdvancedIndirectPreparationResult Indirect => _indirect;
    internal AdvancedGpuDeformationPublication Deformation => _deformation;
    internal ReadOnlySpan<AdvancedVisibilityPayload> Payloads => _payloads.AsSpan(0, _payloadCount);
    internal ReadOnlySpan<AdvancedVisibilityCandidate> Candidates => _candidates.AsSpan(0, _payloadCount);
    internal ReadOnlySpan<EAdvancedGeometryProducer> Producers => _producers.AsSpan(0, _payloadCount);
    internal ReadOnlySpan<AdvancedIndirectRange> IndirectRanges => _indirectRanges.AsSpan(0, _indirectRangeCount);
    internal ReadOnlySpan<int> IndirectPayloadIndices => _indirectPayloadIndices.AsSpan(0, _payloadCount);
    internal ReadOnlySpan<AdvancedDeformedArenaSlice> DeformationSlices => _deformationSlices.AsSpan(0, _payloadCount);
    internal ReadOnlySpan<AdvancedPreparedDrawDeformationRecord> PreparedDeformations
        => _preparedDeformations.AsSpan(0, _preparedDeformationCount);
    internal ReadOnlySpan<uint> RangeIndices => _rangeIndices.AsSpan(0, _payloadCount);
    internal ReadOnlySpan<uint> RangeOffsets => _rangeOffsets.AsSpan(0, _indirectRangeCount);
    internal ReadOnlySpan<uint> Counters => _counters.AsSpan(0, checked((int)(CounterWordsPerView * _viewCount)));
    internal uint ViewCount => _viewCount;
    internal uint PersistentStateByteLength => checked((uint)_payloadCount * _viewCount * PersistentStateRecordByteLength);

    internal bool TryCapture(in AdvancedVisibilityStageBackendRequest request, out string reason)
    {
        Reset();
        if (!request.IsValid)
        {
            reason = request.GetInvalidReason() ?? "The Advanced visibility request is incomplete.";
            return false;
        }

        uint viewCount = (uint)request.Views.ViewCount;
        int payloadCount = checked((int)request.Publication.DrawCount);
        int rangeCount = checked((int)request.Publication.IndirectRangeCount);
        if (viewCount is 0u or > RenderFrameViewSet.MaxViewCount ||
            payloadCount > _payloads.Length || rangeCount > _indirectRanges.Length)
        {
            reason = "The GL Advanced visibility snapshot capacity is smaller than the canonical publication.";
            return false;
        }

        AdvancedPreparationPublication publication = request.Publication;
        if (!AdvancedSharedPreparationService.Instance.TryCopyVisibilityInputs(
                request.Extractor,
                in publication,
                _payloads.AsSpan(0, payloadCount),
                _candidates.AsSpan(0, payloadCount),
                _producers.AsSpan(0, payloadCount),
                _indirectRanges.AsSpan(0, rangeCount),
                _indirectPayloadIndices.AsSpan(0, payloadCount),
                _deformationSlices.AsSpan(0, payloadCount),
                out AdvancedIndirectPreparationResult indirect,
                out AdvancedGpuDeformationPublication deformation))
        {
            reason = "The canonical Advanced publication changed before GL could retain its input columns.";
            return false;
        }

        if (indirect.PayloadCount != publication.DrawCount ||
            indirect.RangeCount != publication.IndirectRangeCount ||
            (publication.DeformationJobCount != 0u &&
             (!publication.AggregateDispatchExecuted ||
              deformation.FrameId != publication.FrameId ||
              deformation.JobCount != publication.DeformationJobCount)))
        {
            Reset();
            reason = "The retained GL Advanced columns do not match the canonical publication shape.";
            return false;
        }
        if (!TryBuildRangeMetadata(payloadCount, rangeCount, out reason))
        {
            Reset();
            return false;
        }

        _publication = publication;
        _indirect = indirect;
        _deformation = deformation;
        _payloadCount = payloadCount;
        _indirectRangeCount = rangeCount;
        _viewCount = viewCount;
        _captured = true;
        reason = "Ready";
        return true;
    }

    internal void Reset()
    {
        _publication = default;
        _indirect = default;
        _deformation = default;
        _payloadCount = 0;
        _indirectRangeCount = 0;
        _preparedDeformationCount = 0;
        _viewCount = 0u;
        _captured = false;
    }

    /// <summary>
    /// Rewrites the canonical ordered ranges into the lookup image consumed by
    /// GPU indirect expansion. The shader indexes this image by payload, not
    /// by the extractor's ordered submission position.
    /// </summary>
    private bool TryBuildRangeMetadata(int payloadCount, int rangeCount, out string reason)
    {
        if (rangeCount == 0)
        {
            reason = payloadCount == 0 ? "Ready" : "The GL Advanced publication has payloads without submission ranges.";
            return payloadCount == 0;
        }

        Span<uint> indices = _rangeIndices.AsSpan(0, payloadCount);
        indices.Fill(uint.MaxValue);
        for (int rangeIndex = 0; rangeIndex < rangeCount; ++rangeIndex)
        {
            AdvancedIndirectRange range = _indirectRanges[rangeIndex];
            uint rangeEnd;
            try { rangeEnd = checked(range.FirstPayloadIndex + range.PayloadCapacity); }
            catch (OverflowException)
            {
                reason = "The GL Advanced indirect range overflows its ordered payload span.";
                return false;
            }
            if (rangeEnd > (uint)payloadCount)
            {
                reason = "The GL Advanced indirect range exceeds the sealed payload span.";
                return false;
            }

            _rangeOffsets[rangeIndex] = range.FirstPayloadIndex;
            for (uint orderedIndex = range.FirstPayloadIndex; orderedIndex < rangeEnd; ++orderedIndex)
            {
                int payloadIndex = _indirectPayloadIndices[(int)orderedIndex];
                if ((uint)payloadIndex >= (uint)payloadCount || indices[payloadIndex] != uint.MaxValue)
                {
                    reason = "The GL Advanced indirect ranges do not provide one unique payload owner.";
                    return false;
                }
                indices[payloadIndex] = (uint)rangeIndex;
            }
        }
        if (indices.Contains(uint.MaxValue))
        {
            reason = "The GL Advanced indirect ranges omit a sealed payload.";
            return false;
        }

        reason = "Ready";
        return true;
    }

    internal bool TryInitializeCounters(
        OpenGLAdvancedSceneTableUploader sceneUploader,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(sceneUploader);
        if (!_captured || _viewCount == 0u)
        {
            reason = "The GL Advanced counter image has no captured visibility family.";
            return false;
        }

        Span<uint> counters = _counters.AsSpan(0, checked((int)(CounterWordsPerView * _viewCount)));
        counters.Clear();
        return sceneUploader.TryInitializeVisibilityCounters(counters, _viewCount, out reason);
    }

    /// <summary>
    /// Builds the per-canonical-draw deformation sidecar from this sealed input
    /// image. The visibility shaders use this sidecar as their only authority
    /// for selecting aggregate deformation outputs and temporal history.
    /// </summary>
    internal bool TryBuildPreparedDeformations(
        Commands.AdvancedGpuScenePublicationSnapshot snapshot,
        out string reason)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsValid || snapshot.Draws.Sequence != _publication.ScenePublication.Sequence ||
            snapshot.Geometry.Sequence != _publication.ScenePublication.Sequence)
        {
            reason = "The GL deformation sidecar does not have the sealed canonical scene publication.";
            return false;
        }

        ReadOnlySpan<AdvancedDrawRecord> canonicalDraws = snapshot.Draws.PhysicalRecords;
        if (canonicalDraws.IsEmpty || canonicalDraws.Length > _preparedDeformations.Length)
        {
            reason = "The canonical draw image exceeds the GL deformation-sidecar capacity.";
            return false;
        }

        Span<AdvancedPreparedDrawDeformationRecord> overlays =
            _preparedDeformations.AsSpan(0, canonicalDraws.Length);
        Span<byte> writes = _preparedDeformationWrites.AsSpan(0, canonicalDraws.Length);
        overlays.Clear();
        writes.Clear();
        for (int payloadIndex = 0; payloadIndex < _payloadCount; ++payloadIndex)
        {
            AdvancedVisibilityPayload payload = _payloads[payloadIndex];
            if (!payload.Draw.IsValid)
                continue;
            if (!snapshot.Draws.TryGetDenseIndex(payload.Draw, out uint drawDenseIndex) ||
                drawDenseIndex >= (uint)canonicalDraws.Length)
            {
                reason = $"Canonical GL visibility payload {payloadIndex} has no exact draw row.";
                return false;
            }

            AdvancedDrawRecord canonicalDraw = canonicalDraws[checked((int)drawDenseIndex)];
            if (canonicalDraw.Geometry != payload.Geometry || !payload.Geometry.IsValid ||
                !snapshot.Geometry.TryGet(payload.Geometry, out AdvancedGeometryRecord geometry))
            {
                reason = $"Canonical GL visibility payload {payloadIndex} has no immutable geometry association.";
                return false;
            }

            bool canonicalRangeMatches = (geometry.Source is EAdvancedGeometrySource.Static or EAdvancedGeometrySource.MeshletLocal) &&
                geometry.CurrentVertexData.IsValid && geometry.IndexData.IsValid &&
                geometry.CurrentVertexData.ElementStride == 64u &&
                geometry.IndexData.ElementStride == sizeof(uint) &&
                payload.FirstIndex == geometry.IndexBase && payload.IndexCount == geometry.IndexCount &&
                payload.VertexCount == geometry.VertexCount &&
                (payload.Skinned || payload.GeometryOffsets.VertexOffset == geometry.VertexBase);
            if (!canonicalRangeMatches)
            {
                reason = $"Canonical GL visibility payload {payloadIndex} does not match immutable topology.";
                return false;
            }

            AdvancedPreparedDrawDeformationRecord overlay;
            if (payload.Skinned)
            {
                AdvancedDeformedArenaSlice slice = _deformationSlices[payloadIndex];
                bool offsetsFit = slice.VertexStride == 64u && slice.VertexCount == payload.VertexCount &&
                    slice.CurrentFrameSlot == _deformation.CurrentFrameSlot &&
                    slice.PreviousFrameSlot == _deformation.PreviousFrameSlot &&
                    slice.CurrentVertexOffset == payload.GeometryOffsets.VertexOffset &&
                    slice.PreviousVertexOffset == payload.GeometryOffsets.PreviousVertexOffset &&
                    (ulong)slice.CurrentVertexOffset * slice.VertexStride <= _deformation.CurrentVertices.Length &&
                    (ulong)slice.VertexCount * slice.VertexStride <= _deformation.CurrentVertices.Length - (ulong)slice.CurrentVertexOffset * slice.VertexStride &&
                    (ulong)slice.PreviousVertexOffset * slice.VertexStride <= _deformation.PreviousVertices.Length &&
                    (ulong)slice.VertexCount * slice.VertexStride <= _deformation.PreviousVertices.Length - (ulong)slice.PreviousVertexOffset * slice.VertexStride;
                if (payload.ForceCpuDiagnostic || _deformation.JobCount == 0u || !slice.Owner.IsValid ||
                    canonicalDraw.Deformation != slice.Owner || !offsetsFit)
                {
                    reason = $"Canonical GL visibility payload {payloadIndex} has no exact GPU deformation output.";
                    return false;
                }

                EAdvancedPreparedDrawDeformationFlags flags =
                    EAdvancedPreparedDrawDeformationFlags.Active |
                    EAdvancedPreparedDrawDeformationFlags.TemporalStatePresent;
                if (_deformation.PreviousOutputValid && slice.HasValidVelocity &&
                    payload.TemporalReason == EAdvancedVelocityValidityReason.Valid)
                    flags |= EAdvancedPreparedDrawDeformationFlags.PreviousValid;
                flags = (EAdvancedPreparedDrawDeformationFlags)
                    AdvancedReconstructionTemporalFlags.PackVelocityReason((uint)flags, payload.TemporalReason);
                overlay = new(payload.Geometry, slice.Owner, slice.CurrentVertexOffset,
                    slice.PreviousVertexOffset, slice.VertexCount, flags);
            }
            else
            {
                EAdvancedPreparedDrawDeformationFlags flags =
                    (EAdvancedPreparedDrawDeformationFlags)AdvancedReconstructionTemporalFlags.PackVelocityReason(
                        (uint)EAdvancedPreparedDrawDeformationFlags.TemporalStatePresent,
                        payload.TemporalReason);
                overlay = new(payload.Geometry, canonicalDraw.Deformation, geometry.VertexBase,
                    geometry.VertexBase, payload.VertexCount, flags);
            }

            int overlayIndex = checked((int)drawDenseIndex);
            if (writes[overlayIndex] != 0 && overlays[overlayIndex] != overlay)
            {
                reason = $"Canonical GL draw row {drawDenseIndex} resolves to conflicting deformation slices.";
                return false;
            }
            overlays[overlayIndex] = overlay;
            writes[overlayIndex] = 1;
        }

        _preparedDeformationCount = canonicalDraws.Length;
        reason = "Ready";
        return true;
    }
}
