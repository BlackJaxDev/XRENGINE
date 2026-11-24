using Silk.NET.Vulkan;
using XREngine.Rendering.Compute;
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
    VulkanFrameDataSlice IndexedInstanceGroups,
    VulkanFrameDataSlice EarlyIndexedGroupCounts,
    VulkanAdvancedVisibilityGeometrySlices Geometry,
    VulkanFrameDataSlice LateVisibleIndices,
    VulkanFrameDataSlice LateRangeCounts,
    VulkanFrameDataSlice LateIndirectArguments,
    VulkanFrameDataSlice LateMeshArguments,
    VulkanFrameDataSlice LateMeshPayloads,
    VulkanFrameDataSlice LateIndexedGroupCounts,
    VulkanFrameDataSlice BoundsRoutes,
    VulkanNativeBufferRange BoundsAtlas,
    VulkanNativeBufferRange BoundsMetadata,
    PhysicsChainGpuOutputPageToken BoundsPageToken,
    uint ViewCount,
    uint PayloadCapacity,
    uint RangeCapacity,
    uint IndexedInstanceGroupCount,
    uint IndirectArgumentCapacity,
    AdvancedVisibilityFamilyReservation Reservation = default,
    VkBufferHandle MeshletHistoryBuffer = default,
    ulong MeshletHistoryByteLength = 0u,
    VulkanNativeBufferRange SkinPalettes = default,
    VkBufferHandle GeometryPageRequestsBuffer = default,
    ulong GeometryPageRequestsByteLength = 0u,
    VkBufferHandle GeometryPageFeedbackBitsBuffer = default,
    ulong GeometryPageFeedbackBitsByteLength = 0u)
{
    /// <summary>Byte length of one <c>VkDrawIndexedIndirectCommand</c> in the argument streams.</summary>
    internal const uint IndexedIndirectArgumentByteLength = 20u;

    /// <summary>
    /// Byte length of one late visibility test dispatch record: a
    /// <c>VkDispatchIndirectCommand</c> and one padding word.
    /// </summary>
    internal const uint LateDispatchArgumentByteLength = 16u;

    internal bool IsValid
    {
        get
        {
            bool descriptorBackingValid = Reservation.IsValid && FrameSlot >= 0 &&
                FrameGeneration != 0u && DescriptorSet.Handle != 0 &&
                Payloads.IsValid && Candidates.IsValid &&
                PersistentStateBuffer.Handle != 0 && PersistentStateByteLength != 0u &&
                PersistentStateTopologyGeneration != 0u &&
                PersistentStateContentGeneration != 0u &&
                MeshletHistoryBuffer.Handle != 0 && MeshletHistoryByteLength != 0u &&
                DeferredIndices.IsValid && VisibleIndices.IsValid && Producers.IsValid &&
                RangeIndices.IsValid && RangeOffsets.IsValid && RangeCounts.IsValid &&
                Counters.IsValid && IndirectArguments.IsValid && MeshArguments.IsValid &&
                MeshPayloads.IsValid && Geometry.IsValid && LateVisibleIndices.IsValid &&
                IndexedInstanceGroups.IsValid && EarlyIndexedGroupCounts.IsValid &&
                LateRangeCounts.IsValid && LateIndirectArguments.IsValid &&
                LateMeshArguments.IsValid && LateMeshPayloads.IsValid &&
                LateIndexedGroupCounts.IsValid &&
                BoundsRoutes.IsValid && ViewCount != 0u &&
                RangeCapacity != 0u &&
                GeometryPageRequestsBuffer.Handle != 0 && GeometryPageRequestsByteLength != 0u &&
                GeometryPageFeedbackBitsBuffer.Handle != 0 && GeometryPageFeedbackBitsByteLength != 0u;
            if (!descriptorBackingValid)
                return false;
            if (IndexedInstanceGroupCount > PayloadCapacity)
                return false;

            // Empty families bind one-element backing ranges but retain zero as
            // their logical payload and indirect-argument capacities.
            if (PayloadCapacity == 0u)
                return IndirectArgumentCapacity == 0u;

            return IndirectArgumentCapacity == (ulong)ViewCount * PayloadCapacity;
        }
    }

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

    /// <summary>
    /// True when every view segment can be addressed from the view 0 segment
    /// with the strides that the multi-view preparation shaders use
    /// (<c>VisibilityPreparationViews.glslinc</c>): payload streams advance by
    /// <see cref="PayloadCapacity"/>, and range counters advance by the length
    /// of the shared range offset table, which must equal
    /// <see cref="RangeCapacity"/>.
    /// </summary>
    internal bool SupportsMultiViewPreparation
        => ViewCount != 0u &&
           TryGetViewSegment(ViewCount - 1u, out _, out _) &&
           RangeOffsets.Length == (ulong)RangeCapacity * sizeof(uint);

    /// <summary>
    /// Returns the buffer offset of the late visibility test dispatch record of
    /// one view. <c>BuildVisibilityIndirect.comp</c> writes one record for each
    /// view after the indexed arguments of all views, so the late test can
    /// dispatch over the live early and deferred counts instead of
    /// <see cref="PayloadCapacity"/>.
    /// </summary>
    internal bool TryGetLateDispatchArgumentOffset(uint viewIndex, out ulong bufferOffset)
    {
        bufferOffset = 0u;
        if (viewIndex >= ViewCount || !IndirectArguments.IsValid)
            return false;

        try
        {
            ulong recordOffset = checked(
                (ulong)ViewCount * PayloadCapacity * IndexedIndirectArgumentByteLength +
                (ulong)viewIndex * LateDispatchArgumentByteLength);
            if (checked(recordOffset + LateDispatchArgumentByteLength) > IndirectArguments.Length)
                return false;
            bufferOffset = checked(IndirectArguments.Offset + recordOffset);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
