using System.Threading;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>Renderer-free image-state services used by late UI command recording.</summary>
internal sealed partial class VulkanCommandRuntime
{
    internal void SeedRecordedImageLayoutState(
        CommandBuffer commandBuffer,
        CommandBuffer predecessor)
    {
        if (commandBuffer.Handle == 0 || predecessor.Handle == 0)
            return;

        ulong commandBufferHandle = unchecked((ulong)commandBuffer.Handle);
        ulong predecessorHandle = unchecked((ulong)predecessor.Handle);
        lock (Synchronization._vulkanImageLayoutLock)
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    predecessorHandle,
                    out VulkanRecordedImageLayoutState? predecessorState))
            {
                return;
            }

            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    commandBufferHandle,
                    out VulkanRecordedImageLayoutState? recorded))
            {
                recorded = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = CommandBuffers.ResolveRecordingGeneration(commandBuffer),
                };
                Synchronization._recordedImageLayoutsByCommandBuffer[commandBufferHandle] = recorded;
            }

            recorded.EntrySubresources.Clear();
            recorded.SecondaryDescriptorRequirements.Clear();
            recorded.SecondaryDescriptorImagePayloadGenerations.Clear();
            recorded.QueueOwnershipTransfers.Clear();
            recorded.EntryStateIncomplete = predecessorState.EntryStateIncomplete;
            recorded.EntryStateFailure = predecessorState.EntryStateFailure;
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in
                     predecessorState.TouchedSubresources)
            {
                recorded.EntrySubresources[pair.Key] = pair.Value;
            }
        }
    }

    internal void TransitionSecondaryDescriptorImagesForExecution(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        CommandBuffer primary,
        CommandBuffer secondary)
    {
        if (primary.Handle == 0 || secondary.Handle == 0)
            return;

        lock (Synchronization.SecondaryDescriptorRequirementScratchGate)
        {
            Dictionary<VulkanTrackedImageSubresource, VulkanImageAccessState> requirements =
                Synchronization._secondaryDescriptorRequirementScratch;
            requirements.Clear();
            try
            {
                lock (Synchronization._vulkanImageLayoutLock)
                {
                    if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                            unchecked((ulong)secondary.Handle),
                            out VulkanRecordedImageLayoutState? secondaryState))
                    {
                        return;
                    }

                    foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> requirement in
                             secondaryState.SecondaryDescriptorRequirements)
                    {
                        if (!requirements.TryGetValue(requirement.Key, out VulkanImageAccessState existing))
                        {
                            requirements.Add(requirement.Key, requirement.Value);
                            continue;
                        }

                        if (existing.Layout != requirement.Value.Layout ||
                            (existing.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                             requirement.Value.QueueFamilyIndex != Vk.QueueFamilyIgnored &&
                             existing.QueueFamilyIndex != requirement.Value.QueueFamilyIndex) ||
                            (existing.ResourceGeneration != 0 && requirement.Value.ResourceGeneration != 0 &&
                             existing.ResourceGeneration != requirement.Value.ResourceGeneration) ||
                            existing.ExpectedDescriptorLayout != requirement.Value.ExpectedDescriptorLayout ||
                            existing.ExternalOwnership != requirement.Value.ExternalOwnership)
                        {
                            throw new InvalidOperationException(
                                $"Secondary command buffer 0x{secondary.Handle:X} publishes an incompatible descriptor image requirement for 0x{requirement.Key.ImageHandle:X}.");
                        }

                        requirements[requirement.Key] = existing with
                        {
                            StageMask = existing.StageMask | requirement.Value.StageMask,
                            AccessMask = existing.AccessMask | requirement.Value.AccessMask,
                            Serial = Math.Max(existing.Serial, requirement.Value.Serial),
                        };
                    }
                }

                foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> requirement in requirements)
                {
                    VulkanTrackedImageSubresource key = requirement.Key;
                    VulkanImageAccessState required = requirement.Value;
                    ulong generation = encoder.ResourceRuntime.GetPublishedGeneration(ObjectType.Image, key.ImageHandle);
                    if (required.ResourceGeneration != 0 && generation != required.ResourceGeneration)
                    {
                        throw new InvalidOperationException(
                            $"Secondary command-buffer run requires image 0x{key.ImageHandle:X} generation {required.ResourceGeneration}, but generation {generation} is published.");
                    }

                    Image image = new(key.ImageHandle);
                    ImageSubresourceRange range = new()
                    {
                        AspectMask = key.Aspect,
                        BaseMipLevel = key.MipLevel,
                        LevelCount = 1,
                        BaseArrayLayer = key.ArrayLayer,
                        LayerCount = 1,
                    };
                    if (!TryGetRecordedImageAccessState(primary, image, in range, out VulkanImageAccessState prior))
                    {
                        if (generation == 0)
                            continue;
                        prior = VulkanImageAccessState.Undefined with { ResourceGeneration = generation };
                    }

                    if (prior.Layout == required.Layout &&
                        prior.QueueFamilyIndex == required.QueueFamilyIndex &&
                        prior.ResourceGeneration == required.ResourceGeneration)
                    {
                        continue;
                    }
                    if (prior.QueueFamilyIndex != required.QueueFamilyIndex ||
                        prior.ResourceGeneration != required.ResourceGeneration)
                    {
                        throw new InvalidOperationException(
                            $"Secondary command-buffer run cannot establish image 0x{key.ImageHandle:X} descriptor entry state.");
                    }

                    EmitImageTransition(
                        encoder,
                        telemetry,
                        primary,
                        image,
                        in range,
                        prior,
                        required);
                }
            }
            finally
            {
                requirements.Clear();
            }
        }
    }

    /// <summary>
    /// Merges the already-frozen secondary image journal into its executing
    /// primary. The primary's pending barriers are published first so the
    /// secondary entry contract observes their post-transition state.
    /// </summary>
    internal void MergeSecondaryImageStatesForExecution(
        CommandBuffer primary,
        CommandBuffer secondary,
        VulkanFrameTelemetry telemetry)
    {
        if (primary.Handle == 0 || secondary.Handle == 0)
            return;

        if (CommandBuffers.TrackingBatches.TryGetValue(
                unchecked((ulong)primary.Handle),
                out VulkanCommandBufferTrackingBatch? primaryBatch))
        {
            _ = FlushImageAccessBatch(primary, primaryBatch, telemetry);
        }

        lock (Synchronization._vulkanImageLayoutLock)
        {
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)primary.Handle),
                    out VulkanRecordedImageLayoutState? primaryState))
            {
                primaryState = new VulkanRecordedImageLayoutState
                {
                    RecordingGeneration = CommandBuffers.ResolveRecordingGeneration(primary),
                };
                Synchronization._recordedImageLayoutsByCommandBuffer[
                    unchecked((ulong)primary.Handle)] = primaryState;
            }
            if (!Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(
                    unchecked((ulong)secondary.Handle),
                    out VulkanRecordedImageLayoutState? secondaryState))
            {
                return;
            }

            if (secondaryState.EntryStateIncomplete)
            {
                primaryState.EntryStateIncomplete = true;
                if (!primaryState.EntryStateFailure.RequiresRecording)
                    primaryState.EntryStateFailure = secondaryState.EntryStateFailure;
            }
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in
                     secondaryState.EntrySubresources)
            {
                if (!primaryState.Subresources.ContainsKey(pair.Key) &&
                    !primaryState.EntrySubresources.ContainsKey(pair.Key))
                {
                    primaryState.EntrySubresources[pair.Key] = pair.Value;
                }
            }
            foreach (KeyValuePair<VulkanTrackedImageSubresource, VulkanImageAccessState> pair in
                     secondaryState.TouchedSubresources)
            {
                primaryState.Subresources[pair.Key] = pair.Value;
            }
            primaryState.QueueOwnershipTransfers.AddRange(secondaryState.QueueOwnershipTransfers);
            primaryState.RefreshTouchedSubresources();
        }
    }

    internal void EmitImageTransition(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        ImageLayout oldLayout,
        ImageLayout newLayout)
    {
        VulkanImageAccessState prior;
        if (!TryGetRecordedImageAccessState(commandBuffer, image, in range, out prior))
        {
            ulong generation = encoder.ResourceRuntime.GetPublishedGeneration(ObjectType.Image, image.Handle);
            prior = ResolveOverlayImageAccessState(oldLayout, range.AspectMask, generation, 0);
        }
        VulkanImageAccessState next = ResolveOverlayImageAccessState(
            newLayout,
            range.AspectMask,
            prior.ResourceGeneration,
            unchecked((ulong)Interlocked.Increment(ref telemetry._vulkanImageLayoutTransitionSerial)));
        EmitImageTransition(encoder, telemetry, commandBuffer, image, in range, prior, next);
    }

    private static unsafe void EmitImageTransition(
        VulkanTrackedCommandEncoder encoder,
        VulkanFrameTelemetry telemetry,
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        in VulkanImageAccessState prior,
        in VulkanImageAccessState next)
    {
        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = (AccessFlags)(ulong)prior.AccessMask,
            DstAccessMask = (AccessFlags)(ulong)next.AccessMask,
            OldLayout = prior.Layout,
            NewLayout = next.Layout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        encoder.PipelineBarrier(
            commandBuffer,
            (PipelineStageFlags)(ulong)prior.StageMask,
            (PipelineStageFlags)(ulong)next.StageMask,
            DependencyFlags.None,
            0,
            null,
            0,
            null,
            1,
            &barrier);
        encoder.RecordImageAccess(commandBuffer, image, in range, in next);
    }

    private bool TryGetRecordedImageAccessState(
        CommandBuffer commandBuffer,
        Image image,
        in ImageSubresourceRange range,
        out VulkanImageAccessState state)
    {
        state = VulkanImageAccessState.Undefined;
        ulong handle = unchecked((ulong)commandBuffer.Handle);
        if (handle == 0 || image.Handle == 0)
            return false;

        if (CommandBuffers.TrackingBatches.TryGetValue(handle, out VulkanCommandBufferTrackingBatch? batch))
        {
            lock (batch)
                if (batch.LatestImageAccessStates.TryGet(image.Handle, range, out state))
                    return true;
        }

        lock (Synchronization._vulkanImageLayoutLock)
        {
            Synchronization._recordedImageLayoutsByCommandBuffer.TryGetValue(handle, out VulkanRecordedImageLayoutState? recorded);
            return TryGetRecordedImageAccessStateNoLock(recorded, image, in range, out state);
        }
    }

    private bool TryGetRecordedImageAccessStateNoLock(
        VulkanRecordedImageLayoutState? recorded,
        Image image,
        in ImageSubresourceRange range,
        out VulkanImageAccessState state)
    {
        state = VulkanImageAccessState.Undefined;
        VulkanImageAccessState? combined = null;
        ImageSubresourceRange requestedRange = range;
        uint levels = Math.Max(requestedRange.LevelCount, 1u);
        uint layers = Math.Max(requestedRange.LayerCount, 1u);
        for (uint levelOffset = 0; levelOffset < levels; levelOffset++)
        for (uint layerOffset = 0; layerOffset < layers; layerOffset++)
        {
            uint mip = requestedRange.BaseMipLevel + levelOffset;
            uint layer = requestedRange.BaseArrayLayer + layerOffset;
            if (!MergeRecordedAspect(ImageAspectFlags.ColorBit) ||
                !MergeRecordedAspect(ImageAspectFlags.DepthBit) ||
                !MergeRecordedAspect(ImageAspectFlags.StencilBit))
            {
                return false;
            }

            bool MergeRecordedAspect(ImageAspectFlags aspect)
            {
                if ((requestedRange.AspectMask & aspect) == 0)
                    return true;
                VulkanTrackedImageSubresource key = new(image.Handle, mip, layer, aspect);
                VulkanImageAccessState candidate;
                if (recorded is not null &&
                    (recorded.Subresources.TryGetValue(key, out candidate) ||
                     recorded.EntrySubresources.TryGetValue(key, out candidate)))
                {
                    // The command buffer has already established this state.
                }
                else if (Synchronization._trackedImageSubresourceStates.TryGetValue(
                             key,
                             out VulkanImageSubresourceState? submitted))
                {
                    candidate = submitted.Submitted;
                }
                else
                {
                    return false;
                }

                if (!combined.HasValue)
                {
                    combined = candidate;
                    return true;
                }
                return combined.Value.Layout == candidate.Layout &&
                    combined.Value.QueueFamilyIndex == candidate.QueueFamilyIndex &&
                    combined.Value.ResourceGeneration == candidate.ResourceGeneration;
            }
        }

        if (!combined.HasValue)
            return false;
        state = combined.Value;
        return state.Layout != ImageLayout.Undefined;
    }

    private static VulkanImageAccessState ResolveOverlayImageAccessState(
        ImageLayout layout,
        ImageAspectFlags aspect,
        ulong generation,
        ulong serial)
    {
        (PipelineStageFlags2 stages, AccessFlags2 access, ImageLayout descriptorLayout) = layout switch
        {
            ImageLayout.ColorAttachmentOptimal => (
                PipelineStageFlags2.ColorAttachmentOutputBit,
                AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit,
                ImageLayout.Undefined),
            ImageLayout.PresentSrcKhr => (
                PipelineStageFlags2.BottomOfPipeBit,
                AccessFlags2.MemoryReadBit,
                ImageLayout.Undefined),
            ImageLayout.TransferDstOptimal => (
                PipelineStageFlags2.TransferBit,
                AccessFlags2.TransferWriteBit,
                ImageLayout.Undefined),
            ImageLayout.General => (
                PipelineStageFlags2.AllCommandsBit,
                AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
                ImageLayout.General),
            _ => (
                PipelineStageFlags2.TopOfPipeBit,
                AccessFlags2.None,
                ImageLayout.Undefined),
        };
        return new VulkanImageAccessState(
            layout,
            stages,
            access,
            Vk.QueueFamilyIgnored,
            descriptorLayout,
            serial,
            generation);
    }
}
