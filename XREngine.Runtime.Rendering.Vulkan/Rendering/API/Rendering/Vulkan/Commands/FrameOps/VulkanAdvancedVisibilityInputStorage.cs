namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Retained copy of the mutable advanced-preparation columns. Authoring leases
/// capture under the shared preparation lock; frame-plan storage then copies
/// only from that immutable lease and never revisits the live extractor.
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
    /// Captures the exact authoring generation while the shared preparation
    /// service prevents its extractor from advancing.
    /// </summary>
    internal bool TryCaptureAtAuthoring(
        in VulkanAdvancedVisibilityStageRequest request,
        out string failureReason)
    {
        Reset();
        if (!request.IsValid)
        {
            failureReason = "The advanced visibility authoring request is incomplete.";
            return false;
        }

        AdvancedPreparationPublication publication = request.Publication;
        int payloadCount = checked((int)publication.DrawCount);
        int indirectRangeCount = checked((int)publication.IndirectRangeCount);
        EnsureCapacity(ref _payloads, payloadCount, "payload");
        EnsureCapacity(ref _candidates, payloadCount, "candidate");
        EnsureCapacity(ref _producers, payloadCount, "producer");
        EnsureCapacity(
            ref _indirectRanges,
            indirectRangeCount,
            "indirect-range");
        EnsureCapacity(
            ref _indirectPayloadIndices,
            payloadCount,
            "indirect-payload-index");

        if (!AdvancedSharedPreparationService.Instance.TryCopyVisibilityColumns(
                request.Extractor,
                in publication,
                _payloads.AsSpan(0, payloadCount),
                _candidates.AsSpan(0, payloadCount),
                _producers.AsSpan(0, payloadCount),
                _indirectRanges.AsSpan(0, indirectRangeCount),
                _indirectPayloadIndices.AsSpan(0, payloadCount),
                out AdvancedIndirectPreparationResult indirect))
        {
            failureReason =
                "The advanced visibility publication changed before its authoring columns could be retained.";
            return false;
        }

        if (indirect.PayloadCount != publication.DrawCount ||
            indirect.RangeCount != publication.IndirectRangeCount)
        {
            Reset();
            failureReason =
                "The advanced visibility publication does not match its retained indirect column shape.";
            return false;
        }

        _familyRequest = request;
        Publication = publication;
        Indirect = indirect;
        _payloadCount = payloadCount;
        _candidateCount = payloadCount;
        _producerCount = payloadCount;
        _indirectRangeCount = indirectRangeCount;
        _indirectPayloadIndexCount = payloadCount;
        _captured = true;
        failureReason = "Ready";
        return true;
    }

    /// <summary>
    /// Copies the first stage in a visibility family from an immutable
    /// authoring lease and verifies that later stages address the same family.
    /// </summary>
    internal void CaptureOrValidate(
        in VulkanAdvancedVisibilityStageRequest request,
        VulkanAdvancedVisibilityInputStorage authoringInput)
    {
        ArgumentNullException.ThrowIfNull(authoringInput);
        if (_captured)
        {
            if (!MatchesRequest(in request) ||
                !authoringInput.MatchesRequest(in request))
            {
                throw new VulkanPlanPreconditionException(
                    "A frame-operation stream cannot retain more than one advanced visibility input family.");
            }

            return;
        }
        if (!authoringInput.MatchesRequest(in request))
        {
            throw new VulkanPlanPreconditionException(
                "The advanced visibility authoring lease does not match the frame-operation family.");
        }

        ReadOnlySpan<AdvancedVisibilityPayload> payloads =
            authoringInput.Payloads;
        ReadOnlySpan<AdvancedVisibilityCandidate> candidates =
            authoringInput.Candidates;
        ReadOnlySpan<EAdvancedGeometryProducer> producers =
            authoringInput.Producers;
        ReadOnlySpan<AdvancedIndirectRange> indirectRanges =
            authoringInput.IndirectRanges;
        ReadOnlySpan<int> indirectPayloadIndices =
            authoringInput.IndirectPayloadIndices;

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

        _familyRequest = request;
        Publication = authoringInput.Publication;
        Indirect = authoringInput.Indirect;
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
