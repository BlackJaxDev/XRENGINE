namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Fully lowered execution input for one frozen stable-bin header. GPU lanes
/// carry the exact set-1 argument/count byte offsets; CPU lanes deliberately
/// carry no synthetic GPU-count resource.
/// </summary>
internal readonly record struct VulkanStableBinSubmission(
    VulkanSealedBinSubmissionPlan Plan,
    VulkanPreparedStableBinHeader Header,
    VulkanAdvancedVisibilityResourceState VisibilityState,
    ulong IndexedArgumentOffset,
    ulong MeshArgumentOffset,
    ulong CountOffset,
    uint MaximumDrawCount);

/// <summary>Typed failure emitted before recording; no failure selects another lane.</summary>
internal enum VulkanStableBinSubmissionLoweringFailure : byte
{
    None = 0,
    InvalidPlan = 1,
    InvalidHeader = 2,
    VisibilityStateUnavailable = 3,
    IndirectArgumentCapacityExceeded = 4,
    OffsetOverflow = 5,
    UnsupportedStrategy = 6,
    ProducerLaneMismatch = 7,
}
