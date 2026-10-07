using XREngine.Rendering.Commands;

namespace XREngine.Rendering;

/// <summary>Builds one frozen submission strategy from retained visibility payloads.</summary>
public sealed class AdvancedVisibilityInputVariant
{
    private readonly AdvancedIndirectRangePlanner _ranges;
    private readonly AdvancedIndexedInstanceGroupPlanner _groups;

    public AdvancedVisibilityInputVariant(int maximumPayloads, int maximumRanges)
    {
        _ranges = new AdvancedIndirectRangePlanner(maximumPayloads, maximumRanges);
        _groups = new AdvancedIndexedInstanceGroupPlanner(maximumPayloads, maximumRanges);
    }

    public bool TryBuild(
        Span<AdvancedVisibilityPayload> payloads,
        Span<EAdvancedGeometryProducer> producers,
        Span<AdvancedIndirectRange> ranges,
        Span<int> indirectPayloadIndices,
        Span<AdvancedIndexedInstanceGroup> groups,
        BackendReadyFramePackage package,
        in AdvancedPreparationPublication publication,
        out AdvancedIndirectPreparationResult indirect)
    {
        indirect = default;
        if (package.State != EBackendReadyFramePackageState.Published ||
            package.CanonicalScenePublication.DatabaseEpoch !=
                publication.ScenePublication.DatabaseEpoch ||
            package.CanonicalScenePublication.Sequence !=
                publication.ScenePublication.Sequence ||
            !package.TryGetCanonicalPublicationSnapshot(
                out AdvancedGpuScenePublicationSnapshot snapshot) ||
            payloads.Length != publication.DrawCount ||
            producers.Length != payloads.Length ||
            indirectPayloadIndices.Length != payloads.Length ||
            groups.Length < payloads.Length)
            return false;

        indirect = _ranges.Build(payloads, 0u, 0u, 20u, 4u,
            package.SubmissionResolution.Resolved);
        if (indirect.RangeCount > (uint)ranges.Length)
            return false;
        _ranges.ProducersByPayload.CopyTo(producers);
        _ranges.Ranges.CopyTo(ranges);
        _ranges.PayloadIndices.CopyTo(indirectPayloadIndices);
        _groups.Build(payloads, _ranges.ProducersByPayload,
            _ranges.RangeIndicesByPayload, _ranges.Ranges, snapshot);
        _groups.Groups.CopyTo(groups);
        return true;
    }

    public int GroupCount => _groups.Groups.Length;
}
