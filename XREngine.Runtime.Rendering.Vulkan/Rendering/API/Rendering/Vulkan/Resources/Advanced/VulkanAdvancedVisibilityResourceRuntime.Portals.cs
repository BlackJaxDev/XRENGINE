using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedVisibilityResourceRuntime
{
    private const int PortalVisibilityOperationKey = int.MaxValue - 1;
    private VulkanAdvancedPortalVisibilityState[] _portalVisibilityStates = [];

    /// <summary>
    /// Computes the total aligned frame-data storage bytes required for a single-phase portal
    /// visibility segment pool covering <paramref name="portalViewCount"/> scheduled sub-views.
    /// </summary>
    internal static bool TryGetPortalVisibilityRequiredBytes(
        uint primaryViewCount,
        uint portalViewCount,
        uint payloadCapacity,
        uint groupCount,
        uint rangeCapacity,
        out ulong requiredBytes)
    {
        requiredBytes = 0u;
        if (primaryViewCount == 0u ||
            portalViewCount == 0u ||
            portalViewCount > (uint)AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame ||
            rangeCapacity == 0u)
            return false;

        try
        {
            uint bindingPayloadCapacity = Math.Max(1u, payloadCapacity);
            uint bindingGroupCount = Math.Max(1u, groupCount);
            uint totalViewCount = checked(primaryViewCount + portalViewCount);

            uint visibleIndexBytes = checked(portalViewCount * bindingPayloadCapacity * sizeof(uint));
            uint rangeCountBytes = checked(portalViewCount * rangeCapacity * sizeof(uint));
            uint indirectArgumentBytes = checked(portalViewCount * bindingPayloadCapacity * IndexedIndirectArgumentByteLength);
            uint meshArgumentBytes = checked(portalViewCount * bindingPayloadCapacity * MeshIndirectArgumentByteLength);
            uint meshPayloadBytes = checked(portalViewCount * bindingPayloadCapacity * sizeof(uint));
            uint groupCountBytes = checked(totalViewCount * bindingGroupCount * sizeof(uint));
            uint counterBytes = checked(totalViewCount * CounterByteLength);

            requiredBytes =
                AlignedStorageBytes(visibleIndexBytes) +
                AlignedStorageBytes(rangeCountBytes) +
                AlignedStorageBytes(indirectArgumentBytes) +
                AlignedStorageBytes(meshArgumentBytes) +
                AlignedStorageBytes(meshPayloadBytes) +
                AlignedStorageBytes(groupCountBytes) +
                AlignedStorageBytes(counterBytes);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Allocates and binds one single-phase set-1 portal visibility segment pool for
    /// <paramref name="portalViewCount"/> scheduled portal sub-views. Primary Hi-Z pyramids
    /// and meshlet occlusion history limits remain untouched.
    /// </summary>
    internal bool TryPreparePortalVisibilityResources(
        in VulkanAdvancedVisibilityResourceState family,
        in VulkanAdvancedSceneLookupSegments lookupSegments,
        uint portalViewCount,
        out VulkanAdvancedPortalVisibilityState portalState,
        out string reason)
    {
        portalState = default;
        lock (_gate)
        {
            if (!family.IsValid || family.Reservation != _assignedReservation ||
                portalViewCount == 0u ||
                portalViewCount > (uint)AdvancedPortalSchedulerOptions.HardMaxTotalPortalViewsPerFrame ||
                _device is not { IsOperational: true } ||
                _resources.FrameDataArena is not { IsActive: true } arena ||
                _quarantinedFrameSlots[family.FrameSlot] ||
                !TryGetLateOperationSlotNoLock(
                    family.FrameSlot,
                    family.FrameGeneration,
                    PortalVisibilityOperationKey,
                    out int operationSlot))
            {
                reason = "The portal visibility pool has no valid frame owner, view count, or descriptor slot.";
                return false;
            }

            int descriptorIndex = checked(operationSlot *
                (int)(LateDescriptorSetsPerView * MaxLateVisibilityViews));
            if ((uint)descriptorIndex >= (uint)_lateDescriptorSets.Length ||
                _lateDescriptorSets[descriptorIndex].Handle == 0 ||
                (uint)descriptorIndex >= (uint)_portalVisibilityStates.Length)
            {
                reason = "The portal visibility pool has no immutable set-1 descriptor table.";
                return false;
            }

            portalState = _portalVisibilityStates[descriptorIndex];
            if (portalState.FrameGeneration == family.FrameGeneration)
            {
                if (portalState.FrameSlot == family.FrameSlot &&
                    portalState.PrimaryViewCount == family.ViewCount &&
                    portalState.PortalViewCount == portalViewCount &&
                    portalState.PayloadCapacity == family.PayloadCapacity &&
                    portalState.RangeCapacity == family.RangeCapacity &&
                    portalState.IndexedInstanceGroupCount == family.IndexedInstanceGroupCount &&
                    portalState.IsValid)
                {
                    reason = "Ready";
                    return true;
                }

                reason = "The portal visibility descriptor was already sealed for a different view or payload shape.";
                portalState = default;
                return false;
            }

            if (_lateDescriptorGenerations[descriptorIndex] == family.FrameGeneration)
            {
                reason = "The portal visibility descriptor slot is already owned by another sealed stage.";
                return false;
            }

            uint bindingPayloadCapacity = Math.Max(1u, family.PayloadCapacity);
            uint bindingGroupCount = Math.Max(1u, family.IndexedInstanceGroupCount);
            uint totalViewCount;
            uint visibleIndexBytes;
            uint rangeCountBytes;
            uint indirectArgumentBytes;
            uint meshArgumentBytes;
            uint meshPayloadBytes;
            uint groupCountBytes;
            uint counterBytes;
            try
            {
                totalViewCount = checked(family.ViewCount + portalViewCount);
                visibleIndexBytes = checked(portalViewCount * bindingPayloadCapacity * sizeof(uint));
                rangeCountBytes = checked(portalViewCount * family.RangeCapacity * sizeof(uint));
                indirectArgumentBytes = checked(portalViewCount * bindingPayloadCapacity * IndexedIndirectArgumentByteLength);
                meshArgumentBytes = checked(portalViewCount * bindingPayloadCapacity * MeshIndirectArgumentByteLength);
                meshPayloadBytes = checked(portalViewCount * bindingPayloadCapacity * sizeof(uint));
                groupCountBytes = checked(totalViewCount * bindingGroupCount * sizeof(uint));
                counterBytes = checked(totalViewCount * CounterByteLength);
            }
            catch (OverflowException)
            {
                reason = "Portal visibility segment sizes exceed the 32-bit descriptor contract.";
                return false;
            }

            if (!TryGetPortalVisibilityRequiredBytes(
                    family.ViewCount,
                    portalViewCount,
                    family.PayloadCapacity,
                    family.IndexedInstanceGroupCount,
                    family.RangeCapacity,
                    out ulong totalBytes) ||
                totalBytes > StorageCapacityPerFrameSlot ||
                !arena.TryCaptureReservedLaneCursor(
                    family.FrameSlot,
                    EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    out ulong rollbackCursor))
            {
                reason = "The portal visibility segments exceed the bounded frame-slot arena.";
                return false;
            }

            if (!arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    visibleIndexBytes, StorageAlignment, out VulkanFrameDataSlice visibleIndices) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    rangeCountBytes, StorageAlignment, out VulkanFrameDataSlice rangeCounts) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    indirectArgumentBytes, StorageAlignment, out VulkanFrameDataSlice indirectArguments) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    meshArgumentBytes, StorageAlignment, out VulkanFrameDataSlice meshArguments) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    meshPayloadBytes, StorageAlignment, out VulkanFrameDataSlice meshPayloads) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    groupCountBytes, StorageAlignment, out VulkanFrameDataSlice earlyIndexedGroupCounts) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    counterBytes, StorageAlignment, out VulkanFrameDataSlice counters) ||
                !TryClear(arena, visibleIndices) ||
                !TryClear(arena, rangeCounts) ||
                !TryClear(arena, indirectArguments) ||
                !TryClear(arena, meshArguments) ||
                !TryClear(arena, meshPayloads) ||
                !TryClear(arena, earlyIndexedGroupCounts) ||
                !TryInitializeCounters(arena, counters, in lookupSegments, totalViewCount))
            {
                if (!TryRollbackFrameStorageTransaction(
                        arena,
                        family.FrameSlot,
                        rollbackCursor,
                        out string rollbackReason))
                {
                    reason = rollbackReason;
                    return false;
                }

                reason = "The portal visibility segment allocation or initialization failed.";
                return false;
            }

            DescriptorSet descriptorSet = _lateDescriptorSets[descriptorIndex];
            // Portal sub-views do not request geometry streaming pages or write primary history.
            VulkanAdvancedVisibilityResourceState descriptorState = family with
            {
                VisibleIndices = visibleIndices,
                RangeCounts = rangeCounts,
                IndirectArguments = indirectArguments,
                MeshArguments = meshArguments,
                MeshPayloads = meshPayloads,
                EarlyIndexedGroupCounts = earlyIndexedGroupCounts,
                Counters = counters,
                GeometryPageRequestsBuffer = _geometryFeedbackSentinelBuffer,
                GeometryPageRequestsByteLength = GeometryFeedbackSentinelBytes,
                GeometryPageFeedbackBitsBuffer = _geometryFeedbackSentinelBuffer,
                GeometryPageFeedbackBitsByteLength = GeometryFeedbackSentinelBytes,
            };
            if (!TryUpdateDescriptorSet(descriptorSet, in descriptorState, out reason))
            {
                if (!TryRollbackFrameStorageTransaction(
                        arena,
                        family.FrameSlot,
                        rollbackCursor,
                        out string rollbackReason))
                    reason = rollbackReason;
                return false;
            }

            portalState = new VulkanAdvancedPortalVisibilityState(
                family.FrameSlot,
                family.FrameGeneration,
                descriptorSet,
                visibleIndices,
                rangeCounts,
                indirectArguments,
                meshArguments,
                meshPayloads,
                earlyIndexedGroupCounts,
                counters,
                family.ViewCount,
                portalViewCount,
                family.PayloadCapacity,
                family.RangeCapacity,
                family.IndexedInstanceGroupCount);
            _portalVisibilityStates[descriptorIndex] = portalState;
            _lateDescriptorGenerations[descriptorIndex] = family.FrameGeneration;
            reason = "Ready";
            return true;
        }
    }
}
