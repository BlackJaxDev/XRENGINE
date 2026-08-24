namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Constant-time identity for a sealed command-chain schedule publication.
/// Native artifact invalidation is validated separately before a cache hit is
/// admitted.
/// </summary>
internal readonly record struct CommandChainScheduleCacheIdentity(
    int StaticOperationCount,
    int DynamicOperationCount,
    ulong StaticOperationSignature,
    ulong DynamicOperationSignature,
    ulong ResourcePlanRevision,
    ulong ResourceVersionSignature,
    ulong DescriptorVersionSignature,
    VulkanRecordedRenderTargetSnapshot RecordingTarget)
{
    internal bool IsReusable =>
        ResourceVersionSignature != 0UL &&
        DescriptorVersionSignature != 0UL &&
        RecordingTarget.IsComplete;

    /// <summary>
    /// Describes the first cache field that changed. This is restricted to the
    /// opt-in recording diagnostic path because formatting allocates.
    /// </summary>
    internal string DescribeFirstMismatch(
        in CommandChainScheduleCacheIdentity current)
    {
        if (StaticOperationCount != current.StaticOperationCount)
            return $"StaticOperationCount {StaticOperationCount}->{current.StaticOperationCount}";
        if (DynamicOperationCount != current.DynamicOperationCount)
            return $"DynamicOperationCount {DynamicOperationCount}->{current.DynamicOperationCount}";
        if (StaticOperationSignature != current.StaticOperationSignature)
            return $"StaticOperationSignature 0x{StaticOperationSignature:X}->0x{current.StaticOperationSignature:X}";
        if (DynamicOperationSignature != current.DynamicOperationSignature)
            return $"DynamicOperationSignature 0x{DynamicOperationSignature:X}->0x{current.DynamicOperationSignature:X}";
        if (ResourcePlanRevision != current.ResourcePlanRevision)
            return $"ResourcePlanRevision {ResourcePlanRevision}->{current.ResourcePlanRevision}";
        if (ResourceVersionSignature != current.ResourceVersionSignature)
            return $"ResourceVersionSignature 0x{ResourceVersionSignature:X}->0x{current.ResourceVersionSignature:X}";
        if (DescriptorVersionSignature != current.DescriptorVersionSignature)
            return $"DescriptorVersionSignature 0x{DescriptorVersionSignature:X}->0x{current.DescriptorVersionSignature:X}";
        if (RecordingTarget != current.RecordingTarget)
        {
            VulkanRecordedRenderTargetSnapshot currentTarget = current.RecordingTarget;
            return $"RecordingTarget {RecordingTarget.DescribeFirstMismatch(in currentTarget)}";
        }

        return "<none>";
    }
}
