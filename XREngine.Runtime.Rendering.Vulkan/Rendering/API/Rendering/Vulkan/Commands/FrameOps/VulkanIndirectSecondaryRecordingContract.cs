namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Frozen producer and buffer-identity contract required before an indirect
/// draw may be recorded into a reusable secondary command buffer.
/// </summary>
internal readonly record struct VulkanIndirectSecondaryRecordingContract(
    EVulkanIndirectSecondaryEligibility Eligibility,
    ulong IndirectBufferIdentity,
    ulong ParameterBufferIdentity,
    uint DrawCount,
    uint Stride,
    nuint ByteOffset,
    nuint CountByteOffset,
    bool UseCount)
{
    public bool IsEligible =>
        Eligibility ==
        EVulkanIndirectSecondaryEligibility.EligibleProducerComplete;
}
