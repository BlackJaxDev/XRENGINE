using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanAdvancedVisibilityResourceRuntime
{
    private VulkanAdvancedDirectionalShadowResourceState[] _directionalShadowStates = [];

    /// <summary>Checks the total shadow output budget before any stream is written.</summary>
    internal bool TryPreflightDirectionalShadowBudget(
        in VulkanAdvancedVisibilityResourceState family,
        ulong requiredBytes,
        out string reason)
    {
        lock (_gate)
        {
            if (!family.IsValid || family.Reservation != _assignedReservation ||
                _resources.FrameDataArena is not { IsActive: true } arena ||
                !arena.TryCaptureReservedLaneCursor(family.FrameSlot,
                    EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    out ulong cursor) ||
                requiredBytes > StorageCapacityPerFrameSlot ||
                cursor > ulong.MaxValue - (StorageAlignment - 1u) ||
                ((cursor + StorageAlignment - 1u) & ~(StorageAlignment - 1u)) <
                    family.Payloads.Offset ||
                ((cursor + StorageAlignment - 1u) & ~(StorageAlignment - 1u)) -
                    family.Payloads.Offset > StorageCapacityPerFrameSlot - requiredBytes)
            {
                reason = "All directional shadow streams do not fit the accepted frame-slot arena.";
                return false;
            }
            reason = "Ready";
            return true;
        }
    }

    internal static bool TryGetDirectionalShadowRequiredBytes(
        uint cascadeCount,
        uint payloadCount,
        uint groupCount,
        uint rangeCount,
        out ulong requiredBytes)
    {
        requiredBytes = 0u;
        if (cascadeCount == 0u || payloadCount == 0u || groupCount == 0u ||
            rangeCount == 0u)
            return false;
        try
        {
            requiredBytes = AlignedStorageBytes(checked(cascadeCount * payloadCount * sizeof(uint))) +
                AlignedStorageBytes(checked(cascadeCount * groupCount * sizeof(uint))) +
                AlignedStorageBytes(checked(cascadeCount * rangeCount * sizeof(uint))) +
                AlignedStorageBytes(checked(cascadeCount * payloadCount * IndexedIndirectArgumentByteLength)) +
                AlignedStorageBytes(checked(cascadeCount * CounterByteLength));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reserves one immutable set-1 table and separate output streams for a
    /// directional shadow operation. A failed transaction restores the arena
    /// cursor before another operation can use the same frame slot.
    /// </summary>
    internal bool TryPrepareDirectionalShadowResources(
        in VulkanAdvancedVisibilityResourceState family,
        in VulkanAdvancedSceneLookupSegments lookupSegments,
        int operationKey,
        uint cascadeCount,
        out VulkanAdvancedDirectionalShadowResourceState shadow,
        out string reason)
    {
        shadow = default;
        lock (_gate)
        {
            if (!family.IsValid || family.Reservation != _assignedReservation ||
                operationKey < 0 || operationKey >= int.MaxValue / 2 ||
                cascadeCount == 0u ||
                cascadeCount > VulkanAdvancedDirectionalShadowLaneStorage.MaxCascadeCount ||
                family.PayloadCapacity == 0u || family.IndexedInstanceGroupCount == 0u ||
                _device is not { IsOperational: true } ||
                _resources.FrameDataArena is not { IsActive: true } arena ||
                _quarantinedFrameSlots[family.FrameSlot] ||
                !TryGetLateOperationSlotNoLock(family.FrameSlot,
                    family.FrameGeneration, int.MaxValue - operationKey,
                    out int operationSlot))
            {
                reason = "The directional shadow operation has no valid frame owner, indexed groups, or descriptor slot.";
                return false;
            }

            int descriptorIndex = checked(operationSlot *
                (int)(LateDescriptorSetsPerView * MaxLateVisibilityViews));
            if ((uint)descriptorIndex >= (uint)_lateDescriptorSets.Length ||
                _lateDescriptorSets[descriptorIndex].Handle == 0 ||
                (uint)descriptorIndex >= (uint)_directionalShadowStates.Length)
            {
                reason = "The directional shadow operation has no immutable set-1 descriptor table.";
                return false;
            }
            shadow = _directionalShadowStates[descriptorIndex];
            if (shadow.FrameGeneration == family.FrameGeneration)
            {
                if (shadow.OperationKey == operationKey &&
                    shadow.FrameSlot == family.FrameSlot &&
                    shadow.CascadeCount == cascadeCount &&
                    shadow.PayloadCount == family.PayloadCapacity &&
                    shadow.GroupCount == family.IndexedInstanceGroupCount &&
                    shadow.RangeCount == family.RangeCapacity && shadow.IsValid)
                {
                    reason = "Ready";
                    return true;
                }
                reason = "The directional shadow descriptor was sealed for another operation shape.";
                shadow = default;
                return false;
            }
            if (_lateDescriptorGenerations[descriptorIndex] == family.FrameGeneration)
            {
                reason = "The directional shadow descriptor is already owned by another sealed stage.";
                return false;
            }

            uint memberBytes;
            uint groupBytes;
            uint rangeBytes;
            uint argumentBytes;
            uint counterBytes;
            try
            {
                memberBytes = checked(cascadeCount * family.PayloadCapacity * sizeof(uint));
                groupBytes = checked(cascadeCount * family.IndexedInstanceGroupCount * sizeof(uint));
                rangeBytes = checked(cascadeCount * family.RangeCapacity * sizeof(uint));
                argumentBytes = checked(cascadeCount * family.PayloadCapacity * IndexedIndirectArgumentByteLength);
                counterBytes = checked(cascadeCount * CounterByteLength);
            }
            catch (OverflowException)
            {
                reason = "Directional shadow stream lengths exceed the 32-bit descriptor contract.";
                return false;
            }
            ulong totalBytes = AlignedStorageBytes(memberBytes) +
                AlignedStorageBytes(groupBytes) + AlignedStorageBytes(rangeBytes) +
                AlignedStorageBytes(argumentBytes) + AlignedStorageBytes(counterBytes);
            if (totalBytes > StorageCapacityPerFrameSlot ||
                !arena.TryCaptureReservedLaneCursor(family.FrameSlot,
                    EVulkanFrameDataLane.AdvancedVisibilityStorage, out ulong rollbackCursor))
            {
                reason = "The directional shadow streams exceed the bounded frame-slot arena.";
                return false;
            }

            if (!arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    memberBytes, StorageAlignment, out VulkanFrameDataSlice members) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    groupBytes, StorageAlignment, out VulkanFrameDataSlice groups) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    rangeBytes, StorageAlignment, out VulkanFrameDataSlice ranges) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    argumentBytes, StorageAlignment, out VulkanFrameDataSlice arguments) ||
                !arena.TryAllocate(family.FrameSlot, EVulkanFrameDataLane.AdvancedVisibilityStorage,
                    counterBytes, StorageAlignment, out VulkanFrameDataSlice counters) ||
                !TryClear(arena, members) || !TryClear(arena, groups) ||
                !TryClear(arena, ranges) || !TryClear(arena, arguments) ||
                !TryInitializeCounters(arena, counters, in lookupSegments, cascadeCount))
            {
                if (!TryRollbackFrameStorageTransaction(arena, family.FrameSlot,
                    rollbackCursor, out string rollbackReason))
                {
                    reason = rollbackReason;
                    return false;
                }
                reason = "The directional shadow stream allocation or initialization failed.";
                return false;
            }

            DescriptorSet descriptorSet = _lateDescriptorSets[descriptorIndex];
            VulkanAdvancedVisibilityResourceState descriptorState = family with
            {
                MeshPayloads = members,
                EarlyIndexedGroupCounts = groups,
                RangeCounts = ranges,
                IndirectArguments = arguments,
                Counters = counters,
            };
            if (!TryUpdateDescriptorSet(descriptorSet, in descriptorState, out reason))
            {
                if (!TryRollbackFrameStorageTransaction(arena, family.FrameSlot,
                    rollbackCursor, out string rollbackReason))
                    reason = rollbackReason;
                return false;
            }

            shadow = new(family.FrameSlot, family.FrameGeneration, operationKey,
                descriptorSet, members, groups, ranges, arguments, counters,
                cascadeCount, family.PayloadCapacity,
                family.IndexedInstanceGroupCount, family.RangeCapacity);
            _directionalShadowStates[descriptorIndex] = shadow;
            _lateDescriptorGenerations[descriptorIndex] = family.FrameGeneration;
            reason = "Ready";
            return true;
        }
    }
}
