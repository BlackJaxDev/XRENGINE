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
    public static AdvancedFrameSlotUploadArenaOptions Default
        => new(
            AdvancedFrameSlotContract.DefaultSlotCount,
            new AdvancedFrameUploadCapacityProfile(
                InstanceBytes: 4u * 1024u * 1024u,
                ViewBytes: 64u * 1024u,
                DeformationJobBytes: 2u * 1024u * 1024u,
                LightBytes: 512u * 1024u,
                MaterialBytes: 2u * 1024u * 1024u),
            new AdvancedFrameUploadCapacityProfile(
                InstanceBytes: 512u * 1024u,
                ViewBytes: 16u * 1024u,
                DeformationJobBytes: 512u * 1024u,
                LightBytes: 128u * 1024u,
                MaterialBytes: 512u * 1024u),
            DefaultAlignmentBytes: 16u,
            MaxDirtyRangesPerStream: 8,
            OverflowGenerationCount: 3,
            RetiredGenerationCapacity: 3);
}
