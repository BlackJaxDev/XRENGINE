using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    internal bool TryCaptureBufferAllocationIdentity(
        XRDataBuffer owner,
        out GpuBufferAllocationIdentity identity)
    {
        identity = default;
        if (owner is null ||
            WrapperLookup.GetOrCreate(owner, generateNow: false) is not VkDataBuffer buffer ||
            !buffer.TryCaptureComputeBufferSnapshot(
                allowSynchronousUpload: false,
                out VulkanComputeBufferBinding snapshot) ||
            !TryCaptureNativeBufferRange(
                owner,
                0u,
                snapshot.Range,
                BufferUsageFlags.StorageBufferBit,
                out VulkanNativeBufferRange range,
                out _))
        {
            return false;
        }

        identity = new GpuBufferAllocationIdentity(
            owner,
            range.Buffer.Handle,
            range.Offset,
            range.Length,
            range.LifetimeSlot.Index,
            range.NativeGeneration,
            (ulong)range.Usage);
        if (range.Length != snapshot.Range)
        {
            identity = default;
            return false;
        }

        return true;
    }

    internal bool TryCaptureNativeBufferRange(XRDataBuffer owner, ulong offset, ulong length, BufferUsageFlags requiredUsage, out VulkanNativeBufferRange range, out string reason)
    {
        range = default;
        if (owner is null || length == 0u || WrapperLookup.GetOrCreate(owner, generateNow: false) is not VkDataBuffer buffer ||
            !buffer.TryCaptureComputeBufferSnapshot(allowSynchronousUpload: false, out VulkanComputeBufferBinding snapshot) ||
            offset > snapshot.Range || length > snapshot.Range - offset ||
            (snapshot.UsageFlags & requiredUsage) != requiredUsage)
        {
            reason = "The deformation output has no published native buffer range with the required usage and capacity.";
            return false;
        }
        VulkanResourceLifetimeKey key = new(ObjectType.Buffer, snapshot.Buffer.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        using (VulkanFrameLockScope.Enter(tracker.SyncRoot, EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (!tracker.TryGetResourceSlotNoLock(key, out VulkanResourceSlotHandle slot) ||
                !tracker.TryResolvePublishedResourceSlotNoLock(slot, out VulkanResourceLifetimeRecord record) ||
                record.Key != key || record.Generation != slot.Generation || record.PublishedGeneration != slot.Generation)
            {
                reason = "The deformation output native buffer lost its published lifetime generation.";
                return false;
            }
            range = new VulkanNativeBufferRange(owner, snapshot.Buffer, offset, length, slot, slot.Generation, snapshot.UsageFlags);
        }
        reason = "Ready";
        return true;
    }

    /// <summary>
    /// Reports whether no command buffer still uses <paramref name="owner"/>: no queued reference
    /// and its last use on each queue domain at or below that domain's completion frontier. When
    /// only the frontier holds it, the method sweeps the unobserved submissions once
    /// (<see cref="SweepLifetimeSubmissionCompletions"/>) and checks again, because the frontier can
    /// lag the GPU until each submission's owner reports its completion.
    /// </summary>
    internal EGpuBufferContentReuseStatus QueryBufferContentReuse(
        XRDataBuffer owner)
    {
        EGpuBufferContentReuseStatus status = EvaluateBufferContentReuse(owner, out _, out _);
        if (status != EGpuBufferContentReuseStatus.PendingCompletion ||
            SweepLifetimeSubmissionCompletions() == 0)
            return status;
        return EvaluateBufferContentReuse(owner, out _, out _);
    }

    /// <summary>
    /// Diagnostics only: the reuse status of <paramref name="owner"/> and, when it is still
    /// pending, a description of the graphics completion frontier that holds it (see
    /// <see cref="VulkanResourceLifetimeTracker.DescribeGraphicsFrontierNoLock"/>). Allocates.
    /// </summary>
    internal string DescribeBufferContentReuse(XRDataBuffer owner)
    {
        EGpuBufferContentReuseStatus status = EvaluateBufferContentReuse(owner, out ulong lastGraphics, out int queued);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        using (VulkanFrameLockScope.Enter(tracker.SyncRoot, EVulkanFrameWaitReason.ResourceLifetimeLock))
            return $"status={status} " + tracker.DescribeGraphicsFrontierNoLock(lastGraphics, queued);
    }

    private EGpuBufferContentReuseStatus EvaluateBufferContentReuse(
        XRDataBuffer owner,
        out ulong bufferLastGraphicsSequence,
        out int bufferQueuedReferences)
    {
        bufferLastGraphicsSequence = 0UL;
        bufferQueuedReferences = 0;
        if (Lifetime.Tracker.DeviceLost)
            return EGpuBufferContentReuseStatus.DeviceLost;
        if (owner is null ||
            WrapperLookup.GetOrCreate(owner, generateNow: false) is not
                VkDataBuffer buffer ||
            !buffer.TryCaptureComputeBufferSnapshot(
                allowSynchronousUpload: false,
                out VulkanComputeBufferBinding snapshot))
        {
            return EGpuBufferContentReuseStatus.Unsupported;
        }

        VulkanResourceLifetimeKey key =
            new(ObjectType.Buffer, snapshot.Buffer.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        using (VulkanFrameLockScope.Enter(
            tracker.SyncRoot,
            EVulkanFrameWaitReason.ResourceLifetimeLock))
        {
            if (tracker.DeviceLost)
                return EGpuBufferContentReuseStatus.DeviceLost;
            if (!tracker.TryGetResourceSlotNoLock(
                    key,
                    out VulkanResourceSlotHandle slot) ||
                !tracker.TryResolvePublishedResourceSlotNoLock(
                    slot,
                    out VulkanResourceLifetimeRecord record) ||
                record.Key != key ||
                record.Generation != slot.Generation ||
                record.PublishedGeneration != slot.Generation)
            {
                return EGpuBufferContentReuseStatus.Superseded;
            }
            bufferLastGraphicsSequence = record.Pins.LastGraphicsSequence;
            bufferQueuedReferences = record.Pins.QueuedReferenceCount;
            if (record.Pins.HasQueuedReferences)
                return EGpuBufferContentReuseStatus.AwaitingSubmission;
            if (record.Pins.LastGraphicsSequence >
                    tracker.CompletedGraphicsSequence ||
                record.Pins.LastTransferSequence >
                    tracker.CompletedTransferSequence ||
                record.Pins.LastOtherSequence >
                    tracker.CompletedOtherSequence)
            {
                return EGpuBufferContentReuseStatus.PendingCompletion;
            }
        }

        return EGpuBufferContentReuseStatus.Ready;
    }
    internal bool TryValidateNativeBufferRange(in VulkanNativeBufferRange range, out string reason)
    {
        if (!range.IsValid || WrapperLookup.GetOrCreate(range.Owner, generateNow: false) is not VkDataBuffer buffer ||
            buffer.BufferHandle is not Silk.NET.Vulkan.Buffer native || native.Handle != range.Buffer.Handle ||
            buffer.AllocatedByteSize < range.Offset + range.Length ||
            (buffer.LastUsageFlags & range.Usage) != range.Usage)
        {
            reason = "The retained native buffer range is no longer backed by its captured owner allocation.";
            return false;
        }
        VulkanResourceLifetimeKey key = new(ObjectType.Buffer, range.Buffer.Handle);
        VulkanResourceLifetimeTracker tracker = Lifetime.Tracker;
        using (VulkanFrameLockScope.Enter(tracker.SyncRoot, EVulkanFrameWaitReason.ResourceLifetimeLock))
            if (!tracker.TryResolvePublishedResourceSlotNoLock(range.LifetimeSlot, out VulkanResourceLifetimeRecord record) ||
                record.Key != key || record.Generation != range.NativeGeneration || record.PublishedGeneration != range.NativeGeneration)
            {
                reason = "The retained native buffer range has been superseded.";
                return false;
            }
        reason = "Ready";
        return true;
    }
}
