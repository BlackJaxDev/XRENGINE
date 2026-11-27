using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable single-phase set-1 visibility segment allocation for scheduled portal and
/// mirror sub-views in one frame slot. Portal sub-views share the primary family's read-only
/// payloads, candidates, producers, range metadata, and geometry slices, while owning
/// disjoint indirect, range-count, and counter ranges without Hi-Z pyramids or meshlet history.
/// </summary>
internal readonly record struct VulkanAdvancedPortalVisibilityState(
    int FrameSlot,
    ulong FrameGeneration,
    DescriptorSet DescriptorSet,
    VulkanFrameDataSlice VisibleIndices,
    VulkanFrameDataSlice RangeCounts,
    VulkanFrameDataSlice IndirectArguments,
    VulkanFrameDataSlice MeshArguments,
    VulkanFrameDataSlice MeshPayloads,
    VulkanFrameDataSlice EarlyIndexedGroupCounts,
    VulkanFrameDataSlice Counters,
    uint PrimaryViewCount,
    uint PortalViewCount,
    uint PayloadCapacity,
    uint RangeCapacity,
    uint IndexedInstanceGroupCount)
{
    /// <summary>
    /// True when the portal segment pool owns valid frame-slot storage, descriptor state,
    /// and bounded view counts.
    /// </summary>
    internal bool IsValid
        => FrameSlot >= 0 &&
           FrameGeneration != 0u &&
           DescriptorSet.Handle != 0 &&
           VisibleIndices.IsValid &&
           RangeCounts.IsValid &&
           IndirectArguments.IsValid &&
           MeshArguments.IsValid &&
           MeshPayloads.IsValid &&
           EarlyIndexedGroupCounts.IsValid &&
           Counters.IsValid &&
           PrimaryViewCount != 0u &&
           PortalViewCount != 0u &&
           PortalViewCount <= (uint)AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame &&
           RangeCapacity != 0u &&
           IndexedInstanceGroupCount <= PayloadCapacity;

    /// <summary>
    /// Computes the disjoint payload and range segment offsets assigned to one scheduled
    /// portal slot index (<c>0 .. PortalViewCount - 1</c>).
    /// </summary>
    internal bool TryGetPortalViewSegment(
        uint portalSlotIndex,
        out uint payloadBase,
        out uint rangeBase)
    {
        payloadBase = 0u;
        rangeBase = 0u;
        if (portalSlotIndex >= PortalViewCount)
            return false;

        try
        {
            payloadBase = checked(portalSlotIndex * PayloadCapacity);
            rangeBase = checked(portalSlotIndex * RangeCapacity);
            return true;
        }
        catch (OverflowException)
        {
            payloadBase = 0u;
            rangeBase = 0u;
            return false;
        }
    }

    /// <summary>
    /// Creates a visibility resource state view that directs indirect lowering and raster
    /// submission to this portal segment pool while preserving the primary family's read-only
    /// geometry and payload bindings.
    /// </summary>
    internal VulkanAdvancedVisibilityResourceState CreateRasterResourceState(
        in VulkanAdvancedVisibilityResourceState primaryFamily)
        => primaryFamily with
        {
            DescriptorSet = DescriptorSet,
            VisibleIndices = VisibleIndices,
            RangeCounts = RangeCounts,
            IndirectArguments = IndirectArguments,
            MeshArguments = MeshArguments,
            MeshPayloads = MeshPayloads,
            EarlyIndexedGroupCounts = EarlyIndexedGroupCounts,
            Counters = Counters,
            ViewCount = PortalViewCount,
            IndirectArgumentCapacity = checked(PortalViewCount * PayloadCapacity),
        };
}
