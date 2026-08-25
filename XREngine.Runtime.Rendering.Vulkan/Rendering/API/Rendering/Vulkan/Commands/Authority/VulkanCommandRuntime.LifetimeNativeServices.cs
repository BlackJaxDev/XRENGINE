using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanCommandRuntime
{
    internal unsafe Result AllocateCommandBuffersWithLifetime(
        ref CommandBufferAllocateInfo allocateInfo,
        CommandBuffer* commandBuffers,
        string owner)
    {
        if (!DeviceContext.IsOperational)
            return Result.ErrorDeviceLost;

        lock (Pools.Gate)
        {
            Result result = Api.AllocateCommandBuffers(
                DeviceContext.Device,
                ref allocateInfo,
                commandBuffers);
            RuntimeEngine.Rendering.Stats.Vulkan.RecordVulkanAllocateCommandBuffersCall(
                allocateInfo.CommandBufferCount,
                result == Result.Success);
            if (result != Result.Success || commandBuffers is null)
                return result;

            try
            {
                for (int index = 0; index < allocateInfo.CommandBufferCount; index++)
                {
                    ResourceRuntime.RegisterAllocatedCommandBuffer(
                        commandBuffers[index],
                        allocateInfo.CommandPool,
                        allocateInfo.Level,
                        owner);
                }
                return result;
            }
            catch
            {
                Api.FreeCommandBuffers(
                    DeviceContext.Device,
                    allocateInfo.CommandPool,
                    allocateInfo.CommandBufferCount,
                    commandBuffers);
                for (int index = 0; index < allocateInfo.CommandBufferCount; index++)
                {
                    ResourceRuntime.CompleteCommandBufferDestruction(
                        commandBuffers[index]);
                    commandBuffers[index] = default;
                }
                throw;
            }
        }
    }

    internal unsafe Result AllocateCommandBufferWithLifetime(
        ref CommandBufferAllocateInfo allocateInfo,
        out CommandBuffer commandBuffer,
        string owner)
    {
        commandBuffer = default;
        fixed (CommandBuffer* commandBufferPointer = &commandBuffer)
            return AllocateCommandBuffersWithLifetime(
                ref allocateInfo,
                commandBufferPointer,
                owner);
    }

    internal unsafe void FreeCommandBuffersWithLifetime(
        int frameSlot,
        CommandPool commandPool,
        uint commandBufferCount,
        CommandBuffer* commandBuffers,
        string owner)
    {
        if (commandPool.Handle == 0 || commandBufferCount == 0 || commandBuffers is null)
            return;

        for (int index = 0; index < commandBufferCount; index++)
            FreeTrackedCommandBuffer(
                Api,
                DeviceContext.Device,
                ResourceRuntime,
                frameSlot,
                commandPool,
                ref commandBuffers[index],
                owner);
    }

    /// <summary>
    /// Releases a command buffer whose synchronous submission has completed,
    /// removing every command-owned registry before the native handle is freed.
    /// </summary>
    internal void FreeCompletedSynchronousCommandBuffer(
        CommandPool commandPool,
        ref CommandBuffer commandBuffer,
        string owner)
    {
        if (commandBuffer.Handle == 0)
            return;

        ulong handle = unchecked((ulong)commandBuffer.Handle);
        lock (Pools.Gate)
        {
            VulkanResourceLifetimeTracker tracker = ResourceRuntime.Lifetime.Tracker;
            lock (tracker.SyncRoot)
            {
                if (CommandBuffers.TrackingBatches.TryGetValue(
                        handle,
                        out VulkanCommandBufferTrackingBatch? batch))
                {
                    lock (batch)
                    {
                        if (batch.IsRecording || batch.QueuedSubmissionCount != 0)
                        {
                            throw new InvalidOperationException(
                                $"Cannot free synchronous command buffer 0x{handle:X} for {owner} while recording={batch.IsRecording}, queued={batch.QueuedSubmissionCount}.");
                        }

                        CommandBuffers.TrackingBatches.TryRemove(handle, out _);
                    }
                }

                if (tracker.CommandBufferLifetimes.TryGetValue(
                        handle,
                        out VulkanCommandBufferLifetimeRecord? lifetime) &&
                    lifetime.QueuedSubmissionCount != 0)
                {
                    throw new InvalidOperationException(
                        $"Cannot free synchronous command buffer 0x{handle:X} for {owner} while {lifetime.QueuedSubmissionCount} lifetime submission(s) remain queued.");
                }
            }

            RemoveCommandBufferState(commandBuffer);
            CommandBuffer releasing = commandBuffer;
            if (DeviceContext.IsOperational && commandPool.Handle != 0)
                Api.FreeCommandBuffers(
                    DeviceContext.Device,
                    commandPool,
                    1,
                    ref releasing);
            ResourceRuntime.CompleteSynchronousCommandBuffer(commandBuffer);
            commandBuffer = default;
        }
    }

    internal void QueueCommandPoolRetirementTracked(
        CommandPool commandPool,
        int frameSlot)
    {
        lock (Pools.Gate)
            ResourceRuntime.QueueCommandPoolRetirement(commandPool, frameSlot);
    }

    internal void EnsureImageViewAvailableForCommandRecording(
        CommandBuffer commandBuffer,
        ImageView imageView,
        string owner,
        ulong expectedGeneration = 0)
        => ResourceRuntime.EnsureImageViewAvailableForCommandRecording(
            commandBuffer,
            imageView,
            owner,
            expectedGeneration);

    internal unsafe void CopyImageToBufferTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        BufferImageCopy* regions)
    {
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, source.Handle);
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Buffer, destination.Handle);
        Api.CmdCopyImageToBuffer(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            regionCount,
            regions);
    }

    internal void CopyImageToBufferTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Silk.NET.Vulkan.Buffer destination,
        uint regionCount,
        ref BufferImageCopy region)
    {
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, source.Handle);
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Buffer, destination.Handle);
        Api.CmdCopyImageToBuffer(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            regionCount,
            ref region);
    }

    internal unsafe void CopyBufferToImageTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        BufferImageCopy* regions)
    {
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Buffer, source.Handle);
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, destination.Handle);
        Api.CmdCopyBufferToImage(
            commandBuffer,
            source,
            destination,
            destinationLayout,
            regionCount,
            regions);
    }

    internal void CopyBufferToImageTracked(
        CommandBuffer commandBuffer,
        Silk.NET.Vulkan.Buffer source,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ref BufferImageCopy region)
    {
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Buffer, source.Handle);
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, destination.Handle);
        Api.CmdCopyBufferToImage(
            commandBuffer,
            source,
            destination,
            destinationLayout,
            regionCount,
            ref region);
    }

    internal unsafe void ResolveImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageResolve* regions)
    {
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, source.Handle);
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, destination.Handle);
        Api.CmdResolveImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions);
    }

    internal unsafe void BlitImageTracked(
        CommandBuffer commandBuffer,
        Image source,
        ImageLayout sourceLayout,
        Image destination,
        ImageLayout destinationLayout,
        uint regionCount,
        ImageBlit* regions,
        Filter filter)
    {
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, source.Handle);
        PrimaryCommandEncoder.Track(commandBuffer, ObjectType.Image, destination.Handle);
        Api.CmdBlitImage(
            commandBuffer,
            source,
            sourceLayout,
            destination,
            destinationLayout,
            regionCount,
            regions,
            filter);
    }

    internal void RegisterDescriptorSet(
        DescriptorPool pool,
        DescriptorSet descriptorSet,
        bool usesUpdateAfterBind,
        string owner,
        uint setIndex,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings)
        => ResourceRuntime.DescriptorLifetime.RegisterDescriptorSet(
            pool,
            descriptorSet,
            usesUpdateAfterBind,
            owner,
            setIndex,
            reflectedBindings);

    internal void RegisterDescriptorSets(
        DescriptorPool pool,
        ReadOnlySpan<DescriptorSet> descriptorSets,
        bool usesUpdateAfterBind,
        string owner,
        IReadOnlyList<DescriptorBindingInfo>? reflectedBindings)
        => ResourceRuntime.DescriptorLifetime.RegisterDescriptorSets(
            pool,
            descriptorSets,
            usesUpdateAfterBind,
            owner,
            reflectedBindings);

    internal bool TryRecordQueueOwnershipTransfer(
        CommandBuffer commandBuffer,
        in VulkanQueueOwnershipTransferRequirement requirement)
        => TryRecordQueueOwnershipTransferRequirement(commandBuffer, requirement);

    internal bool TryRecordImageAccess(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        ImageLayout layout,
        PipelineStageFlags stageMask,
        AccessFlags accessMask,
        uint queueFamilyIndex)
        => TryRecordImageAccessDelta(
            commandBuffer,
            image,
            range,
            layout,
            stageMask,
            accessMask,
            queueFamilyIndex);

    internal bool TryRecordExternalImageOwnership(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        EVulkanExternalImageOwnership ownership)
        => TryRecordExternalImageOwnershipDelta(
            commandBuffer,
            image,
            range,
            ownership);

    internal bool TryGetPendingImageAccess(
        CommandBuffer commandBuffer,
        Image image,
        ImageSubresourceRange range,
        out VulkanImageAccessState state)
        => TryGetPendingImageAccessState(commandBuffer, image, range, out state);

    internal int ReleaseDescriptorRecordingReferences()
    {
        int transientPoolCount =
            ReleaseComputeTransientDescriptorReferencesForPhysicalResourceDestruction();
        int invalidatedChainCount =
            InvalidateCommandChainSecondaryCommandBuffersForDescriptorReferenceRelease();

        lock (Synchronization._vulkanImageLayoutLock)
        {
            Synchronization._trackedImageSubresourceStates.Clear();
            Synchronization._externalImageOwnershipByHandle.Clear();
            Synchronization._recordedImageLayoutsByCommandBuffer.Clear();
        }

        if (CommandBuffers.PrimaryOwners is not null)
        {
            for (int index = 0; index < CommandBuffers.PrimaryOwners.Length; index++)
            {
                CommandBuffers.PrimaryOwners[index].Dirty = true;
                CommandBuffers.PrimaryOwners[index].DirtyReason =
                    "descriptor references released";
            }
        }
        lock (CommandBuffers.OpenXrPrimaryOwnersGate)
        {
            foreach (PrimaryCommandArtifactOwner owner in
                     CommandBuffers.OpenXrPrimaryOwners.Values)
            {
                owner.Dirty = true;
                owner.DirtyReason = "descriptor references released";
            }
        }
        MarkCommandBuffersDirty("descriptor references released");
        return transientPoolCount + invalidatedChainCount;
    }
}
