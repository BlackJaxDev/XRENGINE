namespace XREngine.Rendering.WebGPU;

/// <summary>Sealed preparation columns copied under the shared preparation authority's lock.</summary>
internal sealed class WebGpuAdvancedVisibilityInputStorage
{
    private AdvancedVisibilityPayload[] _payloads = [];
    private AdvancedVisibilityCandidate[] _candidates = [];
    private EAdvancedGeometryProducer[] _producers = [];
    private AdvancedIndirectRange[] _ranges = [];
    private int[] _indices = [];
    private AdvancedDeformedArenaSlice[] _slices = [];
    private int _count;

    internal ReadOnlySpan<AdvancedVisibilityPayload> Payloads => _payloads.AsSpan(0, _count);
    internal Span<AdvancedVisibilityPayload> MutablePayloads => _payloads.AsSpan(0, _count);
    internal Span<AdvancedVisibilityCandidate> MutableCandidates => _candidates.AsSpan(0, _count);
    internal ReadOnlySpan<AdvancedVisibilityCandidate> Candidates => _candidates.AsSpan(0, _count);
    internal ReadOnlySpan<EAdvancedGeometryProducer> Producers => _producers.AsSpan(0, _count);
    internal ReadOnlySpan<AdvancedDeformedArenaSlice> DeformationSlices => _slices.AsSpan(0, _count);
    internal AdvancedGpuDeformationPublication Deformation { get; private set; }
    internal uint CurrentByteCount { get; private set; }
    internal uint PreviousByteCount { get; private set; }

    internal bool TryCapture(in AdvancedVisibilityStageBackendRequest request, out string reason)
    {
        int count = checked((int)request.Publication.DrawCount);
        int rangeCount = checked((int)request.Publication.IndirectRangeCount);
        if (count > AdvancedPreparationOptions.Default.MaximumDraws || rangeCount > AdvancedPreparationOptions.Default.MaximumIndirectRanges)
            throw new NotSupportedException("WebGPU.Advanced.PreparationCapacity: the captured canonical publication exceeds bounded native input capacity.");
        if (_payloads.Length < count)
        {
            Array.Resize(ref _payloads, count);
            Array.Resize(ref _candidates, count);
            Array.Resize(ref _producers, count);
            Array.Resize(ref _indices, count);
            Array.Resize(ref _slices, count);
        }
        if (_ranges.Length < rangeCount) Array.Resize(ref _ranges, rangeCount);
        AdvancedPreparationPublication publication = request.Publication;
        if (!AdvancedSharedPreparationService.Instance.TryCopyVisibilityInputs(request.Extractor, in publication,
            _payloads.AsSpan(0, count), _candidates.AsSpan(0, count), _producers.AsSpan(0, count),
            _ranges.AsSpan(0, rangeCount), _indices.AsSpan(0, count), _slices.AsSpan(0, count),
            out AdvancedIndirectPreparationResult indirect, out AdvancedGpuDeformationPublication deformation) ||
            indirect.PayloadCount != publication.DrawCount || indirect.RangeCount != publication.IndirectRangeCount)
        {
            reason = "WebGPU.Advanced.PreparationStale: the exact canonical input columns could not be retained.";
            return false;
        }
        uint currentBytes = 0, previousBytes = 0;
        for (int index = 0; index < count; index++)
        {
            if (!_payloads[index].Skinned) continue;
            AdvancedDeformedArenaSlice slice = _slices[index];
            currentBytes = Math.Max(currentBytes, checked((uint)(slice.CurrentByteOffset + (ulong)slice.VertexCount * slice.VertexStride)));
            if (deformation.PreviousOutputValid)
                previousBytes = Math.Max(previousBytes, checked((uint)(slice.PreviousByteOffset + (ulong)slice.VertexCount * slice.VertexStride)));
        }
        _count = count;
        Deformation = deformation;
        CurrentByteCount = currentBytes;
        PreviousByteCount = previousBytes;
        reason = string.Empty;
        return true;
    }
}
