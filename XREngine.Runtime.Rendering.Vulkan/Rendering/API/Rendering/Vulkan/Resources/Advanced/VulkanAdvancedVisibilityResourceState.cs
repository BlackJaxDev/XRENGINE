using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable set-1 visibility producer allocation for one frame slot.
/// The ranges are frame-owned and may only be consumed by the matching
/// submission generation.
/// </summary>
internal readonly record struct VulkanAdvancedVisibilityResourceState(
    int FrameSlot,
    ulong FrameGeneration,
    DescriptorSet DescriptorSet,
    VulkanFrameDataSlice Payloads,
    VulkanFrameDataSlice Candidates,
    VkBufferHandle PersistentStateBuffer,
    ulong PersistentStateByteLength,
    ulong PersistentStateTopologyGeneration,
    ulong PersistentStateContentGeneration,
    VulkanFrameDataSlice DeferredIndices,
    VulkanFrameDataSlice VisibleIndices,
    VulkanFrameDataSlice Producers,
    VulkanFrameDataSlice RangeIndices,
    VulkanFrameDataSlice RangeOffsets,
    VulkanFrameDataSlice RangeCounts,
    VulkanFrameDataSlice Counters,
    VulkanFrameDataSlice IndirectArguments,
    VulkanFrameDataSlice MeshArguments,
    VulkanFrameDataSlice MeshPayloads,
    VulkanAdvancedVisibilityGeometrySlices Geometry,
    VulkanFrameDataSlice LateVisibleIndices,
    VulkanFrameDataSlice LateRangeCounts,
    VulkanFrameDataSlice LateIndirectArguments,
    VulkanFrameDataSlice LateMeshArguments,
    VulkanFrameDataSlice LateMeshPayloads,
    uint ViewCount,
    uint PayloadCapacity,
    uint RangeCapacity,
    uint IndirectArgumentCapacity)
{
    internal bool IsValid
        => FrameSlot >= 0 && FrameGeneration != 0u &&
           DescriptorSet.Handle != 0 && Payloads.IsValid && Candidates.IsValid &&
           PersistentStateBuffer.Handle != 0 && PersistentStateByteLength != 0u &&
           PersistentStateTopologyGeneration != 0u &&
           PersistentStateContentGeneration != 0u &&
           DeferredIndices.IsValid && VisibleIndices.IsValid &&
           Producers.IsValid && RangeIndices.IsValid && RangeOffsets.IsValid &&
           RangeCounts.IsValid && Counters.IsValid && IndirectArguments.IsValid &&
           MeshArguments.IsValid && MeshPayloads.IsValid &&
           Geometry.IsValid &&
           LateVisibleIndices.IsValid && LateRangeCounts.IsValid &&
           LateIndirectArguments.IsValid && LateMeshArguments.IsValid &&
           LateMeshPayloads.IsValid &&
           ViewCount != 0u && PayloadCapacity != 0u && RangeCapacity != 0u &&
           IndirectArgumentCapacity == (ulong)ViewCount * PayloadCapacity;

    /// <summary>
    /// Returns the exact contiguous set-1 segment assigned to one canonical
    /// view. Every GPU-owned producer stream uses this convention: payload
    /// streams advance by <see cref="PayloadCapacity"/>, while range counts
    /// advance by <see cref="RangeCapacity"/>.
    /// </summary>
    internal bool TryGetViewSegment(
        uint viewIndex,
        out uint payloadBase,
        out uint rangeBase)
    {
        payloadBase = 0u;
        rangeBase = 0u;
        if (viewIndex >= ViewCount)
            return false;

        try
        {
            payloadBase = checked(viewIndex * PayloadCapacity);
            rangeBase = checked(viewIndex * RangeCapacity);
            return true;
        }
        catch (OverflowException)
        {
            payloadBase = 0u;
            rangeBase = 0u;
            return false;
        }
    }
}
