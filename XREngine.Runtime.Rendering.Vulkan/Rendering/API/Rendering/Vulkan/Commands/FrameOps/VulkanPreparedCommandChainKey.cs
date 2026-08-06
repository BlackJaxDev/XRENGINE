namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Native state sealed from prepared mesh draws immediately before a scheduled
/// secondary is selected for reuse. Missing prepared state is deliberately not
/// comparable so it cannot authorize reuse.
/// </summary>
internal readonly record struct VulkanPreparedCommandChainKey(
    ulong PipelineIdentity,
    ulong DescriptorSetIdentity,
    int DescriptorSetCount,
    RecordedPacketKey RecordedPacketKey,
    bool IsComplete)
{
    internal static readonly VulkanPreparedCommandChainKey Incomplete = default;
}
