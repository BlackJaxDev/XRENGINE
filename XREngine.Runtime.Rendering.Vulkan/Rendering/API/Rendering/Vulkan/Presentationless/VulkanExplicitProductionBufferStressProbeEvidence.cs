namespace XREngine.Rendering.Vulkan;

/// <summary>Cold-path state transition evidence for one requested native buffer stress probe.</summary>
public sealed record VulkanExplicitProductionBufferStressProbeEvidence
{
    public EVulkanExplicitProductionBufferStressCheckpoint Checkpoint { get; init; }
    public uint RequestedByteSize { get; init; }
    public VulkanNativeBufferDiagnosticDescription OldBinding { get; init; }
    public VulkanNativeBufferDiagnosticDescription NewBinding { get; init; }
    public bool OldBindingFrozenByLogicalPlan { get; init; }
    public ulong LogicalPlanNativeBufferBindingRevision { get; init; }
    public ulong NativeBufferBindingRevisionAfterGrowth { get; init; }
    public bool LogicalPacketRejectedBeforeAcquire { get; init; }
    public bool AcquisitionAvoided { get; init; }
    public bool RetryRequired { get; init; }
    public bool OldBindingRecordedByFrozenFrame { get; init; }
    public bool GrowthAttempted { get; init; }
    public bool GrowthObserved { get; init; }
    public bool SubmissionAllowed { get; init; }
    public bool GpuOverlapObserved { get; init; }
    public bool PrematureReclamationObserved { get; init; }
    public bool ReclamationObservedAfterCompletion { get; init; }
    public ulong RecordedCommandBufferHandle { get; init; }
    public bool RecordedFrameSlotReused { get; init; }
    public VulkanExplicitProductionSubmissionReceipt SlotReuseSubmission { get; init; }
    public VulkanExplicitProductionSubmissionReceipt Submission { get; init; }
    public VulkanNativeBufferLifetimeDiagnostic BeforeGrowth { get; init; }
    public VulkanNativeBufferLifetimeDiagnostic AfterGrowth { get; init; }
    public VulkanNativeBufferLifetimeDiagnostic LatestLifetime { get; init; }
    public VulkanNativeBufferDescriptorOwnerDiagnostic[] OldDescriptorOwners { get; init; } = [];
    public bool RecordedRetentionProven { get; init; }
    public string? Failure { get; init; }
}
