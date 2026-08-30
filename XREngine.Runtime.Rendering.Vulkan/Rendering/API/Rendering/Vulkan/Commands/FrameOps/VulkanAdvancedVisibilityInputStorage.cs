namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frame-plan-owned copy of the mutable advanced-preparation columns. The
/// shared extractor is allowed to advance as soon as authoring completes, so
/// deferred readiness and recording must consume these retained columns.
/// </summary>
internal sealed class VulkanAdvancedVisibilityInputStorage
{
    private readonly bool _fixedCapacity;
    private readonly EVulkanAcceptedFrameLane _lane;
    private AdvancedVisibilityPayload[] _payloads;
    private AdvancedVisibilityCandidate[] _candidates;
    private EAdvancedGeometryProducer[] _producers;
    private AdvancedIndirectRange[] _indirectRanges;
    private int[] _indirectPayloadIndices;
    private VulkanAdvancedVisibilityStageRequest _familyRequest;
    private int _payloadCount;
    private int _candidateCount;
    private int _producerCount;
    private int _indirectRangeCount;
    private int _indirectPayloadIndexCount;
    private bool _captured;

    internal VulkanAdvancedVisibilityInputStorage(
        int drawCapacity,
        int indirectRangeCapacity,
        bool fixedCapacity,
        EVulkanAcceptedFrameLane lane)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(drawCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(indirectRangeCapacity);

        _fixedCapacity = fixedCapacity;
        _lane = lane;
        _payloads = new AdvancedVisibilityPayload[drawCapacity];
        _candidates = new AdvancedVisibilityCandidate[drawCapacity];
        _producers = new EAdvancedGeometryProducer[drawCapacity];
        _indirectRanges = new AdvancedIndirectRange[indirectRangeCapacity];
        _indirectPayloadIndices = new int[drawCapacity];
    }

    internal bool IsValid
        => _captured && Publication.PublicationGeneration != 0UL &&
           Publication.VisibilityContentGeneration != 0UL &&
           Publication.DrawCount == (uint)_payloadCount &&
           Indirect.PayloadCount == (uint)_payloadCount &&
           Indirect.RangeCount == (uint)_indirectRangeCount &&
           _candidateCount == _payloadCount &&
           _producerCount == _payloadCount &&
           _indirectPayloadIndexCount == _payloadCount;

    internal AdvancedPreparationPublication Publication { get; private set; }

    internal AdvancedIndirectPreparationResult Indirect { get; private set; }

    internal ReadOnlySpan<AdvancedVisibilityPayload> Payloads
        => _payloads.AsSpan(0, _payloadCount);

    internal ReadOnlySpan<AdvancedVisibilityCandidate> Candidates
        => _candidates.AsSpan(0, _candidateCount);

    internal ReadOnlySpan<EAdvancedGeometryProducer> Producers
        => _producers.AsSpan(0, _producerCount);

    internal ReadOnlySpan<AdvancedIndirectRange> IndirectRanges
        => _indirectRanges.AsSpan(0, _indirectRangeCount);

    internal ReadOnlySpan<int> IndirectPayloadIndices
        => _indirectPayloadIndices.AsSpan(0, _indirectPayloadIndexCount);

    internal void Reset()
    {
        _familyRequest = default;
        Publication = default;
        Indirect = default;
        _payloadCount = 0;
        _candidateCount = 0;
        _producerCount = 0;
        _indirectRangeCount = 0;
        _indirectPayloadIndexCount = 0;
        _captured = false;
    }

    /// <summary>
    /// Copies the first stage in a visibility family and verifies that later
    /// stages address the same authoring generation. The before/after checks
    /// make a concurrent extractor advance fail closed instead of publishing a
    /// torn retained image.
    /// </summary>
    internal void CaptureOrValidate(
        in VulkanAdvancedVisibilityStageRequest request)
    {
        if (_captured)
        {
            if (!MatchesRequest(in request))
            {
                throw new VulkanPlanPreconditionException(
                    "A frame-operation stream cannot retain more than one advanced visibility input family.");
            }

            return;
        }

        AdvancedPreparationExtractor extractor = request.Extractor;
        AdvancedPreparationPublication publication = request.Publication;
        if (!request.IsValid ||
            !extractor.MatchesPublication(in publication))
        {
            throw new VulkanPlanPreconditionException(
                "The advanced visibility extractor changed before its frame-owned input columns were retained.");
        }

        ReadOnlySpan<AdvancedVisibilityPayload> payloads =
            extractor.VisibilityPayloads;
        ReadOnlySpan<AdvancedVisibilityCandidate> candidates =
            extractor.VisibilityCandidates;
        ReadOnlySpan<EAdvancedGeometryProducer> producers =
            extractor.VisibilityProducers;
        ReadOnlySpan<AdvancedIndirectRange> indirectRanges =
            extractor.IndirectRanges;
        ReadOnlySpan<int> indirectPayloadIndices =
            extractor.IndirectPayloadIndices;
        AdvancedIndirectPreparationResult indirect = extractor.IndirectResult;

        if (publication.DrawCount != (uint)payloads.Length ||
            candidates.Length != payloads.Length ||
            producers.Length != payloads.Length ||
            indirectPayloadIndices.Length != payloads.Length ||
            publication.IndirectRangeCount != (uint)indirectRanges.Length ||
            indirect.PayloadCount != (uint)payloads.Length ||
            indirect.RangeCount != (uint)indirectRanges.Length)
        {
            throw new VulkanPlanPreconditionException(
                "The advanced visibility publication does not match its extractor column shape.");
        }

        EnsureCapacity(ref _payloads, payloads.Length, "payload");
        EnsureCapacity(ref _candidates, candidates.Length, "candidate");
        EnsureCapacity(ref _producers, producers.Length, "producer");
        EnsureCapacity(
            ref _indirectRanges,
            indirectRanges.Length,
            "indirect-range");
        EnsureCapacity(
            ref _indirectPayloadIndices,
            indirectPayloadIndices.Length,
            "indirect-payload-index");

        payloads.CopyTo(_payloads);
        candidates.CopyTo(_candidates);
        producers.CopyTo(_producers);
        indirectRanges.CopyTo(_indirectRanges);
        indirectPayloadIndices.CopyTo(_indirectPayloadIndices);

        if (!extractor.MatchesPublication(in publication))
        {
            throw new VulkanPlanPreconditionException(
                "The advanced visibility extractor changed while its frame-owned input columns were retained.");
        }

        _familyRequest = request;
        Publication = publication;
        Indirect = indirect;
        _payloadCount = payloads.Length;
        _candidateCount = candidates.Length;
        _producerCount = producers.Length;
        _indirectRangeCount = indirectRanges.Length;
        _indirectPayloadIndexCount = indirectPayloadIndices.Length;
        _captured = true;
    }

    internal bool MatchesRequest(
        in VulkanAdvancedVisibilityStageRequest request)
        => IsValid && _familyRequest.MatchesFamily(in request) &&
           Publication.Equals(request.Publication) &&
           Publication.VisibilityContentGeneration ==
               request.VisibilityContentGeneration;

    internal bool MatchesPublication(
        in AdvancedPreparationPublication publication,
        in AdvancedIndirectPreparationResult indirect)
        => IsValid && Publication.Equals(publication) &&
           Indirect.Equals(indirect);

    private void EnsureCapacity<T>(
        ref T[] values,
        int required,
        string column)
    {
        if (values.Length >= required)
            return;
        if (_fixedCapacity)
        {
            throw new VulkanAcceptedFramePlanCapacityException(
                _lane,
                values.Length,
                required,
                $"Advanced visibility {column} capacity was exhausted.");
        }

        Array.Resize(
            ref values,
            Math.Max(required, values.Length == 0 ? 4 : values.Length * 2));
    }
}
