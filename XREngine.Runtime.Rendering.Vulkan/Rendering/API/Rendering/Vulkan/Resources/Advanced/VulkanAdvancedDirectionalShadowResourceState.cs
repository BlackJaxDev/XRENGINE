using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Frozen GPU streams for one directional shadow operation.</summary>
internal readonly record struct VulkanAdvancedDirectionalShadowResourceState(
    int FrameSlot,
    ulong FrameGeneration,
    int OperationKey,
    DescriptorSet DescriptorSet,
    VulkanFrameDataSlice Members,
    VulkanFrameDataSlice GroupCounts,
    VulkanFrameDataSlice RangeCounts,
    VulkanFrameDataSlice IndexedArguments,
    VulkanFrameDataSlice Counters,
    uint CascadeCount,
    uint PayloadCount,
    uint GroupCount,
    uint RangeCount)
{
    internal bool IsValid => FrameSlot >= 0 && FrameGeneration != 0u && OperationKey >= 0 &&
        DescriptorSet.Handle != 0 && Members.IsValid && GroupCounts.IsValid &&
        RangeCounts.IsValid && IndexedArguments.IsValid && Counters.IsValid &&
        CascadeCount != 0u && PayloadCount != 0u && GroupCount != 0u && RangeCount != 0u;
}
