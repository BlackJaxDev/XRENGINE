namespace XREngine.Rendering.Vulkan;

/// <summary>Exact fail-closed reason that prevents sealing a lane plan.</summary>
internal enum VulkanSubmissionPlanRejectionReason : byte
{
    None = 0,
    InvalidBinKey = 1,
    EmptyResourceManifest = 2,
    InvalidOutputPolicy = 3,
    GpuDrivenOutputPolicyRejected = 4,
    DiagnosticAttachedToZeroReadback = 5,
    CpuSafetyNetAttachedToZeroReadback = 6,
    DiagnosticsNotAllowed = 7,
    CpuSafetyNetNotAllowed = 8,
    SourceCountExceedsCapacity = 9,
    WorstCaseOutputExceedsCapacity = 10,
    OutputCapacityOverflow = 11,
    UnsupportedStrategy = 12,
    GpuLaneUnavailable = 13,
    GpuLaneBelowCrossover = 14,
    IndirectRangeUnresolved = 15,
    CompositeIndirectRange = 16,
    MeshletVisibilityAbiUnavailable = 17,
    OrderedExceptionCapacityExceeded = 18,
    RangeExecutionLaneMismatch = 19,
}

/// <summary>Exact downgrade recorded when the requested lane cannot be sealed.</summary>
internal enum VulkanSubmissionPlanDowngradeReason : byte
{
    None = 0,
    GpuDrivenOutputPolicyRejected = 1,
    IndirectCountUnsupported = 2,
    MeshletIndirectCountUnsupported = 3,
    IndirectCrossoverNotMet = 4,
    MeshletCrossoverNotMet = 5,
    RangeProducerRequiresIndexed = 6,
}
