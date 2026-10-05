namespace XREngine.Rendering;

/// <summary>
/// Fixed bounds and initial capacities for an <see cref="AdvancedFrameSlotUploadArena"/>.
/// </summary>
public readonly record struct AdvancedFrameSlotUploadArenaOptions(
    uint SlotCount,
    AdvancedFrameUploadCapacityProfile InitialCapacity,
    AdvancedFrameUploadCapacityProfile OverflowCapacity,
    uint DefaultAlignmentBytes,
    int MaxDirtyRangesPerStream,
    int OverflowGenerationCount,
    int RetiredGenerationCapacity)
{
    /// <summary>
    /// Only the deformation-job stream has a producer today. The instance, view,
    /// light and material streams start small and grow at a frame boundary on
    /// their first high-water mark if a producer appears, instead of pinning
    /// about 32 MB across the slots and overflow generations up front.
    /// </summary>
    public static AdvancedFrameSlotUploadArenaOptions Default
        => new(
            AdvancedFrameSlotContract.DefaultSlotCount,
            new AdvancedFrameUploadCapacityProfile(
                InstanceBytes: 4u * 1024u,
                ViewBytes: 4u * 1024u,
                DeformationJobBytes: 2u * 1024u * 1024u,
                LightBytes: 4u * 1024u,
                MaterialBytes: 4u * 1024u),
            new AdvancedFrameUploadCapacityProfile(
                InstanceBytes: 4u * 1024u,
                ViewBytes: 4u * 1024u,
                DeformationJobBytes: 512u * 1024u,
                LightBytes: 4u * 1024u,
                MaterialBytes: 4u * 1024u),
            DefaultAlignmentBytes: 16u,
            MaxDirtyRangesPerStream: 8,
            OverflowGenerationCount: 3,
            RetiredGenerationCapacity: 3);
}
